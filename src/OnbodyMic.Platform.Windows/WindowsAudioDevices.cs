namespace OnbodyMic.Platform.Windows;

using NAudio.CoreAudioApi;

public static class WindowsAudioDevices
{
    public static IReadOnlyList<AudioEndpointInfo> List(bool activeOnly = true)
    {
        using var enumerator = new MMDeviceEnumerator();
        var states = DeviceState.Active | DeviceState.Disabled | DeviceState.NotPresent | DeviceState.Unplugged;

        var result = new List<AudioEndpointInfo>();
        foreach (DataFlow flow in Enum.GetValues<DataFlow>())
        {
            if (flow == DataFlow.All)
            {
                continue;
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, states))
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

    public static MMDevice OpenById(string id)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            return enumerator.GetDevice(id);
        }
        catch
        {
            enumerator.Dispose();
            throw;
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
