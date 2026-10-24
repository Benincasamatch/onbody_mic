namespace OnbodyMic.Domain;

public enum SessionPhase
{
    Unconfigured = 0,
    ConfiguredMuted = 1,
    ReadyMuted = 2,
    Running = 3,
    FaultedMuted = 4,
    EmergencyMuted = 5,
}
