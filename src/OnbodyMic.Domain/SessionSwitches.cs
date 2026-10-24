namespace OnbodyMic.Domain;

public readonly record struct SessionSwitches(
    bool MicSendEnabled,
    bool ExtrasSendEnabled,
    bool AppSendEnabled,
    bool MonitorEnabled)
{
    public static SessionSwitches AllOff { get; } = new(false, false, false, false);

    public bool AnySendEnabled => MicSendEnabled || ExtrasSendEnabled || AppSendEnabled;
}
