namespace OnbodyMic.Audio;

/// <summary>
/// A send-side feed the mixer can pull from: either a live capture channel or
/// the single clip player. Pull advances the stream; drain discards without
/// emitting (the gate-denial path, so revoked audio is never replayed later).
/// </summary>
public interface ISendFeed
{
    string SourceId { get; }

    AudioKind Kind { get; }

    SourceTicket? Ticket { get; }

    int Pull(float[] destination, int frames);

    int Drain(int frames);
}

/// <summary>
/// One bounded per-source channel of engine-format frames. Capture threads push,
/// the send mixer pulls or drains under a short lock. Frames pushed while
/// unauthorized are dropped; frames still buffered when authorization, a switch
/// or the session epoch is revoked are drained by the consumer — never replayed.
/// Capture and render run on different device clocks (observed ~400 ppm drift in
/// P1 KOOK testing); with adaptive rate enabled the consumer nudges its pull
/// ratio (±1%, linear interpolation) to hold the queue at the target watermark,
/// which is the docs/02 §9.2 "micro adaptive resampling" answer to drift.
/// </summary>
public sealed class SourceChannel : ISendFeed
{
    public const double MaxRatioAdjust = 0.01;

    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private SourceTicket? _ticket;
    private double _ratio = 1.0;
    private bool _adaptive;
    private int _targetQueued;

    private long _pushedFrames;
    private long _pulledFrames;
    private long _droppedFrames;
    private long _deniedFrames;

    public SourceChannel(string sourceId, AudioKind kind, int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        SourceId = sourceId;
        Kind = kind;
        _ring = new float[capacityFrames];
        _capacity = capacityFrames;
    }

    public string SourceId { get; }

    public AudioKind Kind { get; }

    /// <summary>Current consumer rate (1.0 = exact engine rate); below 1 when the producer clock is slow.</summary>
    public double Ratio
    {
        get
        {
            lock (_gate)
            {
                return _ratio;
            }
        }
    }

    public SourceTicket? Ticket
    {
        get
        {
            lock (_gate)
            {
                return _ticket;
            }
        }
    }

    /// <summary>Sets the authorization ticket; null revokes it and discards buffered frames.</summary>
    public void SetTicket(SourceTicket? ticket)
    {
        lock (_gate)
        {
            _ticket = ticket;
            if (ticket is null && _count > 0)
            {
                _droppedFrames += _count;
                _head = 0;
                _count = 0;
            }
        }
    }

    /// <summary>Enables watermark-driven adaptive pull rate for this channel.</summary>
    public void EnableAdaptiveRate(int targetQueuedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetQueuedFrames, 1);
        lock (_gate)
        {
            _adaptive = true;
            _targetQueued = targetQueuedFrames;
        }
    }

    public void Push(ReadOnlySpan<float> frames)
    {
        lock (_gate)
        {
            _pushedFrames += frames.Length;
            if (_ticket is null)
            {
                _droppedFrames += frames.Length;
                return;
            }

            if (frames.Length >= _capacity)
            {
                // keep only the newest window that fits
                frames[^_capacity..].CopyTo(_ring);
                _droppedFrames += frames.Length - _capacity;
                _head = 0;
                _count = _capacity;
                return;
            }

            var overflow = _count + frames.Length - _capacity;
            if (overflow > 0)
            {
                _head = (_head + overflow) % _capacity;
                _count -= overflow;
                _droppedFrames += overflow;
            }

            for (var i = 0; i < frames.Length; i++)
            {
                _ring[(_head + _count) % _capacity] = frames[i];
                _count++;
            }
        }
    }

    public int Pull(float[] destination, int frames)
    {
        lock (_gate)
        {
            if (_adaptive)
            {
                UpdateRatioLocked();
            }

            if (_ratio == 1.0)
            {
                return PullExactLocked(destination, frames);
            }

            return PullResampledLocked(destination, frames);
        }
    }

    private void UpdateRatioLocked()
    {
        var error = (_count - _targetQueued) / (double)_targetQueued;
        var target = 1.0 + (Math.Clamp(error, -1.0, 1.0) * MaxRatioAdjust);
        _ratio += (target - _ratio) * 0.05;
        _ratio = Math.Clamp(_ratio, 1.0 - MaxRatioAdjust, 1.0 + MaxRatioAdjust);
    }

    private int PullExactLocked(float[] destination, int frames)
    {
        var take = Math.Min(frames, _count);
        for (var i = 0; i < take; i++)
        {
            destination[i] = _ring[(_head + i) % _capacity];
        }

        _head = (_head + take) % _capacity;
        _count -= take;
        _pulledFrames += take;
        return take;
    }

    private int PullResampledLocked(float[] destination, int frames)
    {
        if (_count < 2)
        {
            return 0;
        }

        var maxOut = (int)((_count - 1) / _ratio);
        var outFrames = Math.Min(frames, maxOut);
        var consumed = (int)Math.Ceiling(outFrames * _ratio);
        for (var i = 0; i < outFrames; i++)
        {
            var position = i * _ratio;
            var whole = (int)position;
            var fraction = position - whole;
            var a = _ring[(_head + whole) % _capacity];
            var b = _ring[(_head + whole + 1) % _capacity];
            destination[i] = a + ((b - a) * (float)fraction);
        }

        _head = (_head + consumed) % _capacity;
        _count -= consumed;
        _pulledFrames += consumed;
        return outFrames;
    }

    public int Drain(int frames)
    {
        lock (_gate)
        {
            var discard = Math.Min(frames, _count);
            _head = (_head + discard) % _capacity;
            _count -= discard;
            _deniedFrames += discard;
            return discard;
        }
    }

    public int QueuedFrames
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Cumulative counters for desensitized metrics output.</summary>
    public SourceChannelStats Stats()
    {
        lock (_gate)
        {
            return new SourceChannelStats(
                _pushedFrames, _pulledFrames, _droppedFrames, _deniedFrames, _count, _ticket is not null, _ratio);
        }
    }
}

/// <summary>Snapshot of one source channel's counters; identities and paths are not included.</summary>
public readonly record struct SourceChannelStats(
    long PushedFrames,
    long PulledFrames,
    long DroppedFrames,
    long DeniedFrames,
    int QueuedFrames,
    bool Authorized,
    double Ratio);
