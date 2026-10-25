namespace OnbodyMic.Platform.Windows;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Lists active WASAPI sessions (capture clients) on an endpoint. NAudio does not wrap
/// IAudioSessionControl2, so this is a minimal own interop chain:
/// IMMDeviceEnumerator -> IMMDevice::Activate(IAudioSessionManager2) -> enumerator.
/// </summary>
public static class WindowsEndpointSessions
{
    public sealed record SessionInfo(int ProcessId, string ProcessName, string State);

    public static List<SessionInfo> List(string endpointId)
    {
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        var deviceEnumerator = (IMMDeviceEnumeratorCom)Activator.CreateInstance(enumeratorType!)!;
        deviceEnumerator.GetDevice(endpointId, out var device);
        using var releaseDevice = new ComReleaser(device);

        var managerIid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        device.Activate(ref managerIid, 23, IntPtr.Zero, out object manager);
        using var releaseManager = new ComReleaser(manager);
        var manager2 = (IAudioSessionManager2)manager;

        manager2.GetSessionEnumerator(out var enumerator);
        using var releaseEnumerator = new ComReleaser(enumerator);
        enumerator.GetCount(out var count);

        var result = new List<SessionInfo>();
        for (var i = 0; i < count; i++)
        {
            enumerator.GetSession(i, out var session);
            using var releaseSession = new ComReleaser(session);
            var control2 = (IAudioSessionControl2)session;
            control2.GetProcessId(out var pid);
            control2.GetState(out var state);
            var name = "(unknown)";
            try
            {
                name = Process.GetProcessById(pid).ProcessName;
            }
            catch
            {
                // exited or access denied; keep the placeholder
            }

            result.Add(new SessionInfo(pid, name, state switch
            {
                1 => "Active",
                2 => "Expired",
                _ => "Inactive",
            }));
        }

        return result;
    }

    private sealed class ComReleaser(object comObject) : IDisposable
    {
        public void Dispose() => _ = Marshal.ReleaseComObject(comObject);
    }
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumeratorCom
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(IntPtr audioSessionGuid, int flags, out IntPtr session);

    [PreserveSig]
    int GetSimpleAudioVolume(IntPtr audioSessionGuid, int flags, out IntPtr volume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr notification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetSession(int index, out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr notification);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr notification);

    // IAudioSessionControl2
    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string instanceIdentifier);

    [PreserveSig]
    int GetProcessId(out int processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference(int optOut);
}
