namespace OnbodyMic.Platform.Windows;

using System.Runtime.InteropServices;
using NAudio.Wave;

/// <summary>
/// Per-process loopback capture (Windows 10 2004+). Port of the Microsoft ApplicationLoopback
/// sample: ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_PARAMS (VT_BLOB) against the
/// VAD\Process_Loopback virtual device, then an event-driven IAudioCaptureClient loop.
/// IIDs and vtable order verified against mingw-w64 mmdeviceapi.h and NAudio main
/// (IUnknown-based, no IInspectable slots).
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntStreamflagsEventCallback = 0x00040000;
    private const int AudclntStreamflagsAutoConvertPcm = unchecked((int)0x80000000);
    private const int AudclntBufferflagsSilent = 0x2;

    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private IAudioClientCom? _audioClient;
    private IAudioCaptureClientCom? _captureClient;
    private IntPtr _sampleReadyEvent;
    private Thread? _captureThread;
    private volatile bool _running;

    public WaveFormat WaveFormat { get; } = new(44100, 16, 2);

    /// <summary>Raw captured frames (16-bit PCM 44.1kHz stereo) as they arrive.</summary>
    public event Action<byte[], int>? DataAvailable;

    public void Start(uint targetProcessId, bool includeProcessTree)
    {
        if (_audioClient is not null)
        {
            throw new InvalidOperationException("capture already started");
        }

        var handler = new ActivationHandler();
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = targetProcessId,
                ProcessLoopbackMode = includeProcessTree
                    ? ProcessLoopbackMode.IncludeTargetProcessTree
                    : ProcessLoopbackMode.ExcludeTargetProcessTree,
            },
        };

        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlobHeader>());
        var formatPtr = BuildWaveFormatPointer();
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, fDeleteOld: false);
            Marshal.StructureToPtr(new PropVariantBlobHeader
            {
                Vt = 0x41, // VT_BLOB
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = paramsPtr,
            }, variantPtr, fDeleteOld: false);

            Marshal.ThrowExceptionForHR(NativeMethods.ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback, in IidIAudioClient, variantPtr, handler, out var operationPtr));
            _ = Marshal.Release(operationPtr);

            if (!handler.Completion.Task.Wait(5000))
            {
                throw new TimeoutException("process loopback activation timed out");
            }

            _audioClient = (IAudioClientCom)handler.Completion.Task.Result;
            Marshal.ThrowExceptionForHR(_audioClient.Initialize(
                0, AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback | AudclntStreamflagsAutoConvertPcm,
                0, 0, formatPtr, IntPtr.Zero));
            var captureIid = IidIAudioCaptureClient;
            Marshal.ThrowExceptionForHR(_audioClient.GetService(ref captureIid, out var service));
            _captureClient = (IAudioCaptureClientCom)service;

            _sampleReadyEvent = NativeMethods.CreateEvent(IntPtr.Zero, manualReset: false, initialState: false, null);
            Marshal.ThrowExceptionForHR(_audioClient.SetEventHandle(_sampleReadyEvent));
            Marshal.ThrowExceptionForHR(_audioClient.Start());

            _running = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "procloop" };
            _captureThread.Start();
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join(500);
        _ = _audioClient?.Stop();
    }

    public void Dispose()
    {
        Stop();
        if (_sampleReadyEvent != IntPtr.Zero)
        {
            _ = NativeMethods.CloseHandle(_sampleReadyEvent);
            _sampleReadyEvent = IntPtr.Zero;
        }

        if (_audioClient is not null)
        {
            _ = Marshal.ReleaseComObject(_audioClient);
            _audioClient = null;
        }
    }

    private void CaptureLoop()
    {
        while (_running)
        {
            if (NativeMethods.WaitForSingleObject(_sampleReadyEvent, 200) != 0)
            {
                continue;
            }

            while (_running)
            {
                Marshal.ThrowExceptionForHR(_captureClient!.GetNextPacketSize(out var frames));
                if (frames == 0)
                {
                    break;
                }

                Marshal.ThrowExceptionForHR(_captureClient.GetBuffer(
                    out var data, out var packetFrames, out var flags, out _, out _));
                var bytes = (int)(packetFrames * WaveFormat.BlockAlign);
                var buffer = new byte[bytes];
                if (data != IntPtr.Zero && (flags & AudclntBufferflagsSilent) == 0)
                {
                    Marshal.Copy(data, buffer, 0, bytes);
                }

                Marshal.ThrowExceptionForHR(_captureClient.ReleaseBuffer(packetFrames));
                DataAvailable?.Invoke(buffer, bytes);
            }
        }
    }

    private static IntPtr BuildWaveFormatPointer()
    {
        var ptr = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(ptr, 0, 1);        // WAVE_FORMAT_PCM
        Marshal.WriteInt16(ptr, 2, 2);        // channels
        Marshal.WriteInt32(ptr, 4, 44100);    // samples per second
        Marshal.WriteInt32(ptr, 8, 44100 * 4);// avg bytes per second
        Marshal.WriteInt16(ptr, 12, 4);       // block align
        Marshal.WriteInt16(ptr, 14, 16);      // bits per sample
        Marshal.WriteInt16(ptr, 16, 0);       // cbSize
        return ptr;
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandlerCom
    {
        public TaskCompletionSource<object> Completion { get; } = new();

        public int ActivateCompleted(IntPtr operationPtr)
        {
            try
            {
                var operation = (IActivateAudioInterfaceAsyncOperationCom)Marshal.GetObjectForIUnknown(operationPtr);
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var hr, out var iface));
                if (hr != 0)
                {
                    Completion.SetException(Marshal.GetExceptionForHR(hr) ?? new COMException($"activation failed: 0x{hr:X8}", hr));
                }
                else if (iface == IntPtr.Zero)
                {
                    Completion.SetException(new COMException("activation returned no interface"));
                }
                else
                {
                    Completion.SetResult(Marshal.GetObjectForIUnknown(iface));
                }
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }

            return 0;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1,
}

/// <summary>Sequential head of a VT_BLOB PROPVARIANT; pointer aligned per platform.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlobHeader
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public uint BlobSize;
    public IntPtr BlobData;
}

internal static class NativeMethods
{
    [DllImport("mmdevapi.dll")]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid iid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandlerCom completionHandler,
        out IntPtr operation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandlerCom
{
    [PreserveSig]
    int ActivateCompleted(IntPtr operation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperationCom
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClientCom
{
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrameCount);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint padding);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr format);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClientCom
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frames);

    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}
