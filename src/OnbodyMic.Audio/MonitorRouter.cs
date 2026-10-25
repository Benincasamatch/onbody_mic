namespace OnbodyMic.Audio;

/// <summary>
/// Local monitor router feeding the monitor output device. ClipOnly taps the
/// clip player (send mode only); FullEndpoint drains a bounded ring filled by
/// the actual virtual capture endpoint. Preview playbacks are routed here as
/// the local audition while automatic monitor state is suspended by the engine.
/// The router has no edge back into the send mixer — FullEndpoint frames
/// cannot be re-sent (docs/02 §4 no-loopback rule).
/// </summary>
public sealed class MonitorRouter
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _capacity;
    private readonly int _highWaterFrames;
    private int _head;
    private int _count;

    private long _fullEndpointPushed;
    private long _fullEndpointDropped;
    private long _framesEmitted;

    private const int PlayerChunkFrames = 2048;
    private readonly float[] _playerScratch = new float[PlayerChunkFrames];

    public MonitorRouter(ClipFeed feed, int capacityFrames, int highWaterFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        Feed = feed;
        _ring = new float[capacityFrames];
        _capacity = capacityFrames;
        _highWaterFrames = Math.Min(highWaterFrames, capacityFrames);
    }

    public ClipFeed Feed { get; }

    private ClipPlayer Player => Feed.Player;

    /// <summary>Current monitor route state provider (immutable snapshot).</summary>
    public required Func<MonitorRouteState> StateProvider { get; init; }

    public required Func<RouteGains> GainsProvider { get; init; }

    /// <summary>Frames pushed by the FullEndpoint capture adapter (capture thread).</summary>
    public void PushFullEndpoint(ReadOnlySpan<float> frames)
    {
        lock (_gate)
        {
            _fullEndpointPushed += frames.Length;
            var overflow = _count + frames.Length - _capacity;
            if (overflow > 0)
            {
                _head = (_head + overflow) % _capacity;
                _count -= overflow;
                _fullEndpointDropped += overflow;
            }

            for (var i = 0; i < frames.Length; i++)
            {
                _ring[(_head + _count) % _capacity] = frames[i];
                _count++;
            }
        }
    }

    /// <summary>Fills the destination with monitor audio; silence when disabled or starving.</summary>
    public int Read(Span<float> destination)
    {
        var frames = destination.Length;
        destination.Clear();
        var state = StateProvider();
        var gains = GainsProvider();
        var emitted = 0;

        var previewing = Player.Active is { Mode: ClipPlaybackMode.Preview };
        if (previewing)
        {
            // local audition replaces monitor content while the engine holds
            // automatic monitoring suspended
            emitted = ReadPlayer(destination);
        }
        else if (state.Enabled && state.Mode == MonitorMode.ClipOnly)
        {
            // tap of the authorized send-side pulls: exactly what is being
            // sent, never a second consumer on the player's playhead
            emitted = Feed.ReadTap(destination);
        }
        else if (state.Enabled && state.Mode == MonitorMode.FullEndpoint)
        {
            lock (_gate)
            {
                // trim slow drift so the ring stays near the target watermark
                var excess = _count - _highWaterFrames;
                if (excess > 0)
                {
                    _head = (_head + excess) % _capacity;
                    _count -= excess;
                    _fullEndpointDropped += excess;
                }

                emitted = Math.Min(frames, _count);
                for (var i = 0; i < emitted; i++)
                {
                    destination[i] = _ring[(_head + i) % _capacity];
                }

                _head = (_head + emitted) % _capacity;
                _count -= emitted;
            }
        }

        if (emitted > 0)
        {
            var gain = gains.Monitor;
            for (var i = 0; i < frames; i++)
            {
                destination[i] = Math.Clamp(destination[i] * gain, -1f, 1f);
            }

            _framesEmitted += emitted;
        }

        return emitted;
    }

    /// <summary>Pulls the player in chunks through a reusable buffer (player API is array-based).</summary>
    private int ReadPlayer(Span<float> destination)
    {
        var emitted = 0;
        while (emitted < destination.Length)
        {
            var chunk = Math.Min(PlayerChunkFrames, destination.Length - emitted);
            var taken = Player.Read(_playerScratch, chunk);
            _playerScratch.AsSpan(0, taken).CopyTo(destination[emitted..]);
            emitted += taken;
            if (taken < chunk)
            {
                break;
            }
        }

        return emitted;
    }

    /// <summary>Clears buffered frames (monitor off, mode switch, emergency).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _fullEndpointDropped += _count;
            _head = 0;
            _count = 0;
        }

        Feed.ClearTap();
    }

    public MonitorRouterStats Stats()
    {
        lock (_gate)
        {
            return new MonitorRouterStats(_fullEndpointPushed, _fullEndpointDropped, _count, _framesEmitted);
        }
    }
}

public readonly record struct MonitorRouterStats(
    long FullEndpointPushedFrames,
    long FullEndpointDroppedFrames,
    int QueuedFrames,
    long FramesEmitted);
