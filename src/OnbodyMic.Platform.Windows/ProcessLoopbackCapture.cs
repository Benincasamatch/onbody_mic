namespace OnbodyMic.Platform.Windows;

using System.Runtime.InteropServices;
using NAudio.Wave;

/// <summary>
/// Per-process tree loopback capture (Windows build 20348+), using Microsoft's
/// ApplicationLoopback activation and shared-mode PCM16 format. Never falls back
/// to endpoint/system loopback.
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
    private readonly Lock _gate = new();
    private IAudioClientCom? _audioClient;
    private IAudioCaptureClientCom? _captureClient;
    private IntPtr _sampleReadyEvent;
    private Thread? _captureThread;
    private byte[] _scratch = [];
    private volatile bool _running;
    private bool _started;
    private bool _disposed;

    public event Action<Exception>? Faulted;
    public WaveFormat WaveFormat { get; }

    public ProcessLoopbackCapture(int rate = 44100, int channels = 2)
    {
        WaveFormat = new WaveFormat(rate, 16, channels);
    }

    /// <summary>Borrowed PCM16 buffer, valid only during the callback. Count excludes unused capacity.</summary>
    public event Action<byte[], int>? DataAvailable;

    public void Start(uint targetProcessId, bool includeProcessTree)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("capture already started");
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                throw new PlatformNotSupportedException("process-tree audio capture requires Windows build 20348 or newer");
            ArgumentOutOfRangeException.ThrowIfZero(targetProcessId);
            _started = true;
            var handler = new ActivationHandler();
            var activationParams = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = targetProcessId,
                    ProcessLoopbackMode = includeProcessTree
                        ? ProcessLoopbackMode.IncludeTargetProcessTree : ProcessLoopbackMode.ExcludeTargetProcessTree,
                },
            };
            var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlobHeader>());
            var formatPtr = BuildWaveFormatPointer();
            var stage = "activation";
            try
            {
                Marshal.StructureToPtr(activationParams, paramsPtr, false);
                Marshal.StructureToPtr(new PropVariantBlobHeader
                {
                    Vt = 0x41, BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(), BlobData = paramsPtr,
                }, variantPtr, false);
                Marshal.ThrowExceptionForHR(NativeMethods.ActivateAudioInterfaceAsync(
                    VirtualAudioDeviceProcessLoopback, in IidIAudioClient, variantPtr, handler, out var operation));
                if (operation != IntPtr.Zero) Marshal.Release(operation);
                handler.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _audioClient = handler.TakeClient();
                stage = "initialization";
                Marshal.ThrowExceptionForHR(_audioClient.Initialize(0,
                    AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback | AudclntStreamflagsAutoConvertPcm,
                    0, 0, formatPtr, IntPtr.Zero));
                Marshal.ThrowExceptionForHR(_audioClient.GetBufferSize(out var bufferFrames));
                _scratch = new byte[checked((int)bufferFrames * WaveFormat.BlockAlign)];
                var captureIid = IidIAudioCaptureClient;
                Marshal.ThrowExceptionForHR(_audioClient.GetService(ref captureIid, out var service));
                _captureClient = (IAudioCaptureClientCom)service;
                _sampleReadyEvent = NativeMethods.CreateEvent(IntPtr.Zero, false, false, null);
                if (_sampleReadyEvent == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Marshal.ThrowExceptionForHR(_audioClient.SetEventHandle(_sampleReadyEvent));
                stage = "start";
                Marshal.ThrowExceptionForHR(_audioClient.Start());
                _running = true;
                _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "procloop" };
                _captureThread.Start();
            }
            catch (Exception ex)
            {
                _running = false;
                ReleaseResources();
                throw new InvalidOperationException($"process-tree capture {stage} failed for PID {targetProcessId}: {ex.Message} (0x{ex.HResult:X8})", ex);
            }
            finally
            {
                handler.Abandon();
                Marshal.FreeHGlobal(formatPtr);
                Marshal.FreeHGlobal(variantPtr);
                Marshal.FreeHGlobal(paramsPtr);
            }
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            _running = false;
            thread = _captureThread;
            if (_sampleReadyEvent != IntPtr.Zero) NativeMethods.SetEvent(_sampleReadyEvent);
        }
        // Never join the callback thread itself, or release COM/handles while it is in use.
        if (thread is not null && thread != Thread.CurrentThread) thread.Join();
        lock (_gate) { _ = _audioClient?.Stop(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        if (_captureThread == Thread.CurrentThread) return; // CaptureLoop's finally releases after its last callback.
        lock (_gate) { ReleaseResources(); }
    }

    private void ReleaseResources()
    {
        _ = _audioClient?.Stop();
        if (_captureClient is not null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }
        if (_audioClient is not null)
        {
            Marshal.ReleaseComObject(_audioClient);
            _audioClient = null;
        }
        if (_sampleReadyEvent != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_sampleReadyEvent);
            _sampleReadyEvent = IntPtr.Zero;
        }
    }

    private void CaptureLoop()
    {
        try
        {
            while (_running)
            {
                var wait = NativeMethods.WaitForSingleObject(_sampleReadyEvent, 200);
                if (wait == 258) continue; // WAIT_TIMEOUT
                if (wait != 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                while (_running)
                {
                    Marshal.ThrowExceptionForHR(_captureClient!.GetNextPacketSize(out var frames));
                    if (frames == 0) break;
                    Marshal.ThrowExceptionForHR(_captureClient.GetBuffer(out var data, out var packetFrames, out var flags, out _, out _));
                    var bytes = 0;
                    try
                    {
                        bytes = checked((int)packetFrames * WaveFormat.BlockAlign);
                        if (bytes > _scratch.Length) throw new InvalidOperationException("capture packet exceeds the negotiated buffer size");
                        if ((flags & AudclntBufferflagsSilent) != 0) _scratch.AsSpan(0, bytes).Clear();
                        else
                        {
                            if (data == IntPtr.Zero && bytes != 0) throw new InvalidOperationException("capture returned null non-silent audio data");
                            Marshal.Copy(data, _scratch, 0, bytes);
                        }
                    }
                    finally { Marshal.ThrowExceptionForHR(_captureClient.ReleaseBuffer(packetFrames)); }
                    DataAvailable?.Invoke(_scratch, bytes);
                }
            }
        }
        catch (Exception ex)
        {
            if (_running) Faulted?.Invoke(new InvalidOperationException($"process-tree capture failed: {ex.Message} (0x{ex.HResult:X8})", ex));
        }
        finally
        {
            _running = false;
            lock (_gate) { if (_disposed) ReleaseResources(); }
        }
    }

    private IntPtr BuildWaveFormatPointer()
    {
        var blockAlign = (short)WaveFormat.BlockAlign;
        var ptr = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(ptr, 0, 1);
        Marshal.WriteInt16(ptr, 2, (short)WaveFormat.Channels);
        Marshal.WriteInt32(ptr, 4, WaveFormat.SampleRate);
        Marshal.WriteInt32(ptr, 8, WaveFormat.SampleRate * blockAlign);
        Marshal.WriteInt16(ptr, 12, blockAlign);
        Marshal.WriteInt16(ptr, 14, 16);
        Marshal.WriteInt16(ptr, 16, 0);
        return ptr;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandlerCom, IAgileObjectCom
    {
        private readonly Lock _gate = new();
        private IAudioClientCom? _client;
        private bool _abandoned;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAudioClientCom TakeClient()
        {
            lock (_gate)
            {
                var client = _client ?? throw new InvalidOperationException("activation returned no audio client");
                _client = null;
                return client;
            }
        }

        public void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
                if (_client is not null) Marshal.ReleaseComObject(_client);
                _client = null;
            }
        }

        public int ActivateCompleted(IntPtr operationPtr)
        {
            IActivateAudioInterfaceAsyncOperationCom? operation = null;
            IntPtr iface = IntPtr.Zero;
            try
            {
                operation = (IActivateAudioInterfaceAsyncOperationCom)Marshal.GetObjectForIUnknown(operationPtr);
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var hr, out iface));
                Marshal.ThrowExceptionForHR(hr);
                if (iface == IntPtr.Zero) throw new COMException("activation returned no interface");
                var client = (IAudioClientCom)Marshal.GetObjectForIUnknown(iface);
                lock (_gate)
                {
                    if (_abandoned) Marshal.ReleaseComObject(client);
                    else _client = client;
                }
                Completion.TrySetResult();
            }
            catch (Exception ex) { Completion.TrySetException(ex); }
            finally
            {
                if (iface != IntPtr.Zero) Marshal.Release(iface);
                if (operation is not null) Marshal.ReleaseComObject(operation);
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
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}

[ComImport, ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObjectCom { }

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
public interface IAudioClientCom
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
public interface IAudioCaptureClientCom
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frames);

    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}
