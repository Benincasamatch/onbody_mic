namespace OnbodyMic.Application;

/// <summary>
/// Composition hooks: the session owns all safety logic; concrete device
/// adapters are injected here so tests run on fakes (docs/06 §8).
/// </summary>
public sealed class AudioSessionOptions
{
    public required IClipDecoder ClipDecoder { get; init; }

    /// <summary>Creates the microphone adapter bound to the mic channel.</summary>
    public required Func<SourceChannel, IAudioSourceAdapter> MicAdapterFactory { get; init; }

    /// <summary>Creates the application capture adapter; the binding is the verified process identity.</summary>
    public required Func<SourceChannel, AppBinding, IAudioSourceAdapter> AppAdapterFactory { get; init; }

    /// <summary>Creates the virtual output pump (exclusive writer of the cable render endpoint).</summary>
    public required Func<IVirtualAudioOutput> OutputFactory { get; init; }

    /// <summary>Creates the local monitor output device.</summary>
    public required Func<IMonitorOutput> MonitorOutputFactory { get; init; }

    /// <summary>Creates the FullEndpoint capture adapter; null when the capability is unavailable.</summary>
    public Func<MonitorRouter, IMonitorCaptureAdapter>? MonitorCaptureFactory { get; init; }

    public RouteGains InitialGains { get; init; } = RouteGains.Unity;

    public int MicQueueFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(250));

    public int AppQueueFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(250));

    public int MonitorRingFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(300));

    public int MonitorHighWaterFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(150));

    /// <summary>Largest single pull the output pump may request (scratch sizing).</summary>
    public int MaxPeriodFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(200));
}
