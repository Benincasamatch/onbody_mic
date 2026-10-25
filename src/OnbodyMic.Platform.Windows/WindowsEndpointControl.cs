namespace OnbodyMic.Platform.Windows;

using System.Runtime.InteropServices;

/// <summary>
/// Enables/disables MMDevice endpoints via the undocumented IPolicyConfig COM interface.
/// This is the same call mmsys.cpl makes when the user clicks 启用/禁用; it is per-user
/// and needs no elevation. Signature follows the widely replicated vtable layout used by
/// SoundSwitch/AudioEndPointLibrary — if Windows ever drops the interface, the HRESULT
/// surfaces directly to the caller.
/// </summary>
public static class WindowsEndpointControl
{
    public static void SetEnabled(string endpointId, bool enabled)
    {
        var config = new IPolicyConfig();
        Marshal.ThrowExceptionForHR(config.SetEndpointVisibility(endpointId, enabled ? 1 : 0));
    }

    /// <summary>
    /// Sets the endpoint shared-mode engine format (mmsys.cpl 高级 -> 默认格式) to float32 PCM.
    /// The Steam driver bridges render->capture without conversion, so both sides must run
    /// the same rate or audio is pitch-shifted by the rate ratio.
    /// </summary>
    public static void SetDeviceFormat(string endpointId, int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 192000 || channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "rate must be 8000..192000 and channels 1..2");
        }

        var format = new WaveFormatExtensible
        {
            FormatTag = unchecked((short)0xFFFE),
            Channels = (short)channels,
            SampleRate = sampleRate,
            BlockAlign = (short)(channels * 4),
            BitsPerSample = 32,
            CbSize = 22,
            ValidBitsPerSample = 32,
            ChannelMask = channels == 1 ? 0x4 : 0x3,
            SubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71"),
        };
        format.AvgBytesPerSec = sampleRate * format.BlockAlign;

        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
            Marshal.StructureToPtr(format, pointer, false);
            var config = new IPolicyConfig();
            var hr = config.SetDeviceFormat(endpointId, pointer, pointer);
            if (hr != 0)
            {
                // Some builds reject SetDeviceFormat on the base IPolicyConfig while accepting
                // the IPolicyConfigVista variant that exposes the same vtable.
                var vista = new IPolicyConfigVista();
                hr = vista.SetDeviceFormat(endpointId, pointer, pointer);
            }

            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveFormatExtensible
{
    public short FormatTag;
    public short Channels;
    public int SampleRate;
    public int AvgBytesPerSec;
    public short BlockAlign;
    public short BitsPerSample;
    public short CbSize;
    public short ValidBitsPerSample;
    public int ChannelMask;
    public Guid SubFormat;
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class CPolicyConfig;

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), CoClass(typeof(CPolicyConfig))]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mixFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr deviceFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr defaultPeriodValue, IntPtr minPeriodValue);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultDevice, IntPtr key, IntPtr value);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultDevice, IntPtr key, IntPtr value);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

[ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), CoClass(typeof(CPolicyConfig))]
internal interface IPolicyConfigVista
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mixFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr deviceFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr defaultPeriodValue, IntPtr minPeriodValue);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultDevice, IntPtr key, IntPtr value);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultDevice, IntPtr key, IntPtr value);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}
