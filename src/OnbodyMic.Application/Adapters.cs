namespace OnbodyMic.Application;

/// <summary>Fills a whole output period; returns the frames written.</summary>
public delegate int PullFrames(Span<float> destination);

/// <summary>Receives captured engine-format frames; valid only during the call.</summary>
public delegate void PushFrames(ReadOnlySpan<float> frames);

/// <summary>Stable description of the captured application (docs/02 §8.3: identity, not PID alone).</summary>
public readonly record struct AppBinding(uint ProcessId, string Description);

/// <summary>
/// A live input source bound to its channel. Start throws on permission/device
/// failure so the caller can reject the enable; Ended fires with a null reason
/// when the source ends normally (e.g. target process exited) and with the
/// fault text otherwise.
/// </summary>
public interface IAudioSourceAdapter : IDisposable
{
    void Start();

    event Action<string?>? Ended;
}

/// <summary>Exclusive pump writing the virtual render endpoint; pulls gated mixer output.</summary>
public interface IVirtualAudioOutput : IDisposable
{
    void Start(PullFrames pull);

    void Stop();

    event Action<Exception>? Faulted;
}

/// <summary>Local monitor output device (headphones).</summary>
public interface IMonitorOutput : IDisposable
{
    void Start(PullFrames pull);

    void Stop();

    event Action<Exception>? Faulted;
}

/// <summary>Reads the actual virtual capture endpoint for FullEndpoint monitoring; never feeds the send mixer.</summary>
public interface IMonitorCaptureAdapter : IDisposable
{
    void Start(PushFrames push);

    void Stop();

    event Action<Exception>? Faulted;
}
