namespace OnbodyMic.Platform.Windows;

using System.Runtime.InteropServices;

/// <summary>
/// Public low-level WASAPI building blocks (own COM interop, exact vtable control) so callers
/// can run event-driven render/capture pumps on MMCSS-boosted threads — NAudio's threads are
/// normal-priority and miss 10ms deadlines under load. IIDs verified against mingw-w64 headers.
/// </summary>
public static class WindowsLowLevelAudio
{
    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public const int ShareModeShared = 0;
    public const int ShareModeExclusive = 1;
    public const int StreamFlagsEventCallback = 0x00040000;
    public const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    public const int StreamFlagsSrcDefaultQuality = 0x08000000;

    public static IAudioClientCom ActivateClient(string endpointId)
    {
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        var deviceEnumerator = (IMMDeviceEnumeratorCom)Activator.CreateInstance(enumeratorType!)!;
        deviceEnumerator.GetDevice(endpointId, out var device);
        using var releaseDevice = new ComReleaser(device);
        var iid = IidIAudioClient;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out var client));
        return (IAudioClientCom)client;
    }

    /// <summary>18-byte plain WAVEFORMATEX (PCM16) on the unmanaged heap; caller frees.</summary>
    public static IntPtr CreateWaveFormatPcm16(int rate, int channels)
    {
        var ptr = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(ptr, 0, 1);
        Marshal.WriteInt16(ptr, 2, (short)channels);
        Marshal.WriteInt32(ptr, 4, rate);
        Marshal.WriteInt32(ptr, 8, rate * channels * 2);
        Marshal.WriteInt16(ptr, 12, (short)(channels * 2));
        Marshal.WriteInt16(ptr, 14, 16);
        Marshal.WriteInt16(ptr, 16, 0);
        return ptr;
    }

    public static IntPtr CreateEventHandle()
        => NativeMethods.CreateEvent(IntPtr.Zero, manualReset: false, initialState: false, null);

    public static int WaitForEvent(IntPtr handle, int milliseconds)
        => NativeMethods.WaitForSingleObject(handle, milliseconds);

    public static bool CloseEventHandle(IntPtr handle)
        => NativeMethods.CloseHandle(handle);

    private sealed class ComReleaser(object comObject) : IDisposable
    {
        public void Dispose() => _ = Marshal.ReleaseComObject(comObject);
    }
}

/// <summary>MMCSS thread registration scope: boost scheduling for real-time audio loops.</summary>
public sealed class MmcssScope(string taskName = "Pro Audio") : IDisposable
{
    private IntPtr _handle;

    public void Acquire()
    {
        var index = 0u;
        _handle = AvSetMmThreadCharacteristics(taskName, ref index);
        if (_handle == IntPtr.Zero)
        {
            throw new COMException("AvSetMmThreadCharacteristics failed", Marshal.GetHRForLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            _ = AvRevertMmThreadCharacteristics(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("avrt.dll")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioRenderClientCom
{
    [PreserveSig]
    int GetBuffer(int framesRequested, out IntPtr data);

    [PreserveSig]
    int ReleaseBuffer(int framesWritten, int flags);
}
