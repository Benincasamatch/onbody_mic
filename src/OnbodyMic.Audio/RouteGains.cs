namespace OnbodyMic.Audio;

/// <summary>
/// Immutable route gains applied by the consumers (send mixer and monitor
/// router) per snapshot; changing gains never touches safety state.
/// </summary>
public readonly record struct RouteGains(float Mic, float App, float Clip, float Monitor)
{
    public static RouteGains Unity { get; } = new(1f, 1f, 1f, 1f);
}

/// <summary>Current monitor routing state as seen by the monitor output thread.</summary>
public readonly record struct MonitorRouteState(bool Enabled, MonitorMode Mode);
