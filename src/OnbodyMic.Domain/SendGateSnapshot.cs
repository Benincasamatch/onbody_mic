namespace OnbodyMic.Domain;

public sealed record SendGateSnapshot(
    long SessionEpoch,
    bool EmergencyLatched,
    SessionSwitches Switches,
    ActiveClip? Clip);
