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
/// </summary>
public sealed class SourceChannel : ISendFeed
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private SourceTicket? _ticket;

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
                _pushedFrames, _pulledFrames, _droppedFrames, _deniedFrames, _count, _ticket is not null);
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
    bool Authorized);
