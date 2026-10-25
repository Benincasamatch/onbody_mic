namespace OnbodyMic.Platform.Windows;

using NAudio.CoreAudioApi;

public static class WindowsAudioDevices
{
    private static MMDeviceEnumerator? _enumerator;

    /// <summary>
    /// One process-wide NAudio enumerator, created before any pump activates
    /// the coclass through raw interop: a second wrapper type for the same
    /// COM singleton fails its coclass cast (P1 constraint, devlog 2026-10-26).
    /// </summary>
    private static MMDeviceEnumerator Enumerator => _enumerator ??= new MMDeviceEnumerator();

    public static IReadOnlyList<AudioEndpointInfo> List(bool activeOnly = true)
    {
        var states = DeviceState.Active | DeviceState.Disabled | DeviceState.NotPresent | DeviceState.Unplugged;

        var result = new List<AudioEndpointInfo>();
        foreach (DataFlow flow in Enum.GetValues<DataFlow>())
        {
            if (flow == DataFlow.All)
            {
                continue;
            }

            foreach (var device in Enumerator.EnumerateAudioEndPoints(flow, states))
            {
                using var _ = device;
                if (activeOnly && device.State != DeviceState.Active)
                {
                    continue;
                }

                result.Add(new AudioEndpointInfo(
                    device.ID,
                    device.FriendlyName,
                    flow.ToString(),
                    device.State.ToString()));
            }
        }

        return result;
    }

    public static string? DefaultDeviceId(bool capture)
    {
        try
        {
            using var device = Enumerator.GetDefaultAudioEndpoint(
                capture ? DataFlow.Capture : DataFlow.Render, Role.Multimedia);
            return device?.ID;
        }
        catch
        {
            return null;
        }
    }

    public static MMDevice? OpenById(string id)
    {
        try
        {
            return Enumerator.GetDevice(id);
        }
        catch
        {
            return null;
        }
    }

    public static MMDevice? FindByIndex(int index, bool capture)
    {
        var flowName = capture ? "Capture" : "Render";
        var endpoints = List(activeOnly: true)
            .Select((endpoint, i) => (endpoint, i))
            .Where(x => x.endpoint.DataFlow == flowName)
            .ToList();

        if (index < 0 || index >= endpoints.Count)
        {
            return null;
        }

        return OpenById(endpoints[index].endpoint.Id);
    }
}
