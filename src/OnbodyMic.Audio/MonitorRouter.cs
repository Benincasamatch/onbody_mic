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
    private readonly int _primFrames;
    private readonly int _targetFrames;

    // absolute stream coordinates: ring slot = coordinate % capacity
    private long _writePos;
    private double _readPos;
    private bool _primed;

    private long _fullEndpointPushed;
    private long _fullEndpointDropped;
    private long _framesEmitted;
    private long _fullEndpointStarved;

    private const int PlayerChunkFrames = 2048;
    private readonly float[] _playerScratch = new float[PlayerChunkFrames];

    public MonitorRouter(ClipFeed feed, int capacityFrames, int highWaterFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        Feed = feed;
        _ring = new float[capacityFrames];
        _capacity = capacityFrames;
        _highWaterFrames = Math.Min(highWaterFrames, capacityFrames);
        // pre-roll + adaptive consumption (P3.2): the capture endpoint and the
        // monitor output device run on independent clocks with unsynchronized
        // pump cadences — consuming from an empty ring produces continuous
        // silence/splice bursts that no gain setting can hide
        _primFrames = Math.Max(1, Math.Min(_highWaterFrames / 4, capacityFrames / 4));
        _targetFrames = Math.Max(_primFrames, _highWaterFrames / 2);
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
            var overflow = _writePos + frames.Length - _readPos - _capacity;
            if (overflow > 0)
            {
                // pathological backlog: dump everything down to the target water
                // level in one splice, then let the adaptive ratio hold it —
                // better than a continuous drip of dropped frames
                var drop = (long)Math.Ceiling(_writePos + frames.Length - _readPos - _targetFrames);
                if (drop > 0)
                {
                    _readPos += drop;
                    _fullEndpointDropped += drop;
                    _primed = true;
                }
            }

            foreach (var frame in frames)
            {
                _ring[_writePos % _capacity] = frame;
                _writePos++;
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
                if (!_primed)
                {
                    if (_writePos - _readPos >= _primFrames)
                    {
                        // start from the newest window: pre-roll exists so the
                        // listener hears "now", not the accumulated backlog
                        _primed = true;
                        _readPos = Math.Max(_readPos, _writePos - _targetFrames);
                    }
                    else
                    {
                        _fullEndpointStarved += frames;
                    }
                }

                if (_primed)
                {
                    // adaptive rate (P1.1 mechanism): hold the ring near the
                    // target water level by stretching/squeezing ±1%; absorbs
                    // the ppm-scale drift between capture and monitor output
                    // clocks without queue growth or starvation
                    var available = _writePos - _readPos;
                    var error = available - _targetFrames;
                    var ratio = 1.0 + Math.Clamp(error / _targetFrames * 0.01, -0.01, 0.01);
                    while (emitted < frames && _writePos - _readPos >= 1)
                    {
                        var index0 = (int)(_readPos % _capacity);
                        var frac = _readPos - Math.Floor(_readPos);
                        var index1 = (index0 + 1) % _capacity;
                        destination[emitted] = _ring[index0] + ((_ring[index1] - _ring[index0]) * (float)frac);
                        _readPos += ratio;
                        emitted++;
                    }

                    if (emitted < frames)
                    {
                        _fullEndpointStarved += frames - emitted;
                    }
                }
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
            _writePos = 0;
            _readPos = 0;
            _primed = false;
        }
    }

    public MonitorRouterStats Stats()
    {
        lock (_gate)
        {
            return new MonitorRouterStats(
                _fullEndpointPushed,
                _fullEndpointDropped,
                _primed ? (int)(_writePos - _readPos) : 0,
                _framesEmitted,
                _fullEndpointStarved);
        }
    }
}

public readonly record struct MonitorRouterStats(
    long FullEndpointPushedFrames,
    long FullEndpointDroppedFrames,
    int QueuedFrames,
    long FramesEmitted,
    long FullEndpointStarvedFrames = 0);
