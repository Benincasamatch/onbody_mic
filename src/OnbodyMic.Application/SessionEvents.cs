namespace OnbodyMic.Application;

public enum SessionNoticeKind
{
    Info = 0,
    SourceEnded = 1,
    SourceFaulted = 2,
    OutputFaulted = 3,
    MonitorFaulted = 4,
    ClipCompleted = 5,
}

/// <summary>Desensitized notice for UI/logging: no paths, device ids or audio content.</summary>
public readonly record struct SessionNotice(SessionNoticeKind Kind, string Message);

/// <summary>Consistent view of the session for display (read on the command thread).</summary>
public sealed record SessionStatus(
    SessionPhase Phase,
    SessionSwitches Switches,
    bool EmergencyLatched,
    bool OutputLost,
    bool DevicesValidated,
    string? LastPresetId,
    AppBinding? AppBinding,
    ActiveClip? Clip,
    bool MonitorSuspendedForPreview,
    MonitorMode MonitorMode,
    RouteGains Gains,
    PresetDeviceConfig? Devices);

/// <summary>Desensitized counters for the metrics panel.</summary>
public sealed record SessionMetrics(
    SourceChannelStats Mic,
    SourceChannelStats App,
    ClipPlayerStats Clip,
    MonitorRouterStats Monitor,
    long MixerFramesMixed,
    long MixerClippedSamples,
    long MixerUnderrunFrames,
    long MixerDeniedPulls,
    long MixerHardSteps,
    float MixerPeak,
    double MicRatio,
    double AppRatio);
