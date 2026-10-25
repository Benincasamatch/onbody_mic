using OnbodyMic.Domain;

namespace OnbodyMic.Application;

/// <summary>
/// Composition hooks: the session owns all safety logic; concrete device
/// adapters are injected here so tests run on fakes (docs/06 §8).
/// </summary>
public sealed class AudioSessionOptions
{
    /// <summary>Pre-decoded sample cache keyed by stable clip id; null disables preloading.</summary>
    public IClipSampleStore? SampleStore { get; init; }

    /// <summary>Resolves stable clip ids to managed assets and per-clip gain; null keeps raw-path playback.</summary>
    public Func<string, ClipResolution>? ClipResolver { get; init; }

    /// <summary>Full-predecode clip decoder used on cache misses.</summary>
    public required IClipDecoder ClipDecoder { get; init; }

    /// <summary>Boundary ramp for clip start/stop/replace/natural end; 0 disables (exact-value tests).</summary>
    public int ClipBoundaryFadeFrames { get; init; } = ClipPlayer.DefaultFadeFrames;

    /// <summary>Initial device endpoints; presets replace this at apply time. Null leaves the session unconfigured.</summary>
    public PresetDeviceConfig? InitialDevices { get; init; }

    /// <summary>Checks configured endpoints against live hardware; missing devices become route issues, never substitutions (B05).</summary>
    public Func<PresetDeviceConfig, IReadOnlyList<RouteIssue>>? DeviceValidator { get; init; }

    /// <summary>Creates the microphone adapter for the given endpoint id bound to the mic channel.</summary>
    public required Func<string, SourceChannel, IAudioSourceAdapter> MicAdapterFactory { get; init; }

    /// <summary>Creates the application capture adapter; the binding is the verified process identity.</summary>
    public required Func<SourceChannel, AppBinding, IAudioSourceAdapter> AppAdapterFactory { get; init; }

    /// <summary>Creates the virtual output pump for the given render endpoint id (exclusive writer of the cable).</summary>
    public required Func<string, IVirtualAudioOutput> OutputFactory { get; init; }

    /// <summary>Creates the local monitor output device for the given endpoint id.</summary>
    public required Func<string, IMonitorOutput> MonitorOutputFactory { get; init; }

    /// <summary>Creates the FullEndpoint capture adapter for the given cable capture endpoint id; null when unavailable.</summary>
    public Func<string, MonitorRouter, IMonitorCaptureAdapter>? MonitorCaptureFactory { get; init; }

    public RouteGains InitialGains { get; init; } = RouteGains.Unity;

    public int MicQueueFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(250));

    public int AppQueueFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(250));

    public int MonitorRingFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(300));

    public int MonitorHighWaterFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(150));

    /// <summary>Largest single pull the output pump may request (scratch sizing).</summary>
    public int MaxPeriodFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(200));

    /// <summary>Queue watermark the adaptive pull ratio holds for live capture channels (drift absorption).</summary>
    public int MicAdaptiveTargetFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(60));

    public int AppAdaptiveTargetFrames { get; init; } = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(60));

    /// <summary>Master switch for adaptive pull; disable for exact-value deterministic tests.</summary>
    public bool AdaptiveRate { get; init; } = true;

    /// <summary>Upper bound for the enable-time wait on first captured frames; enables anyway on timeout (silence, not failure).</summary>
    public int SourcePrerollTimeoutMs { get; init; } = 150;
}
