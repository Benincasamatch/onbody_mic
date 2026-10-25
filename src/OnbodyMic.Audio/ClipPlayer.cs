namespace OnbodyMic.Audio;

/// <summary>
/// Single-slot clip player: a new target replaces the old one; triggering the
/// same clip again restarts from the beginning (ReplaceAndRestartSame).
/// Send-mode playbacks are pulled through the mixer only while the send gate
/// authorizes the current playback epoch; preview playbacks are pulled by the
/// monitor router and can never reach the send mixer (no ticket is issued).
/// Playback boundaries (start, stop, replace, natural end) run through short
/// linear ramps so abrupt real-world material never produces hard steps —
/// the P2 pop reports traced back to boundary steps that ramped test tones
/// had masked.
/// </summary>
public sealed class ClipPlayer
{
    public const int DefaultFadeFrames = 128; // ~2.9 ms @ 44.1 kHz, matches the P1.1 fade granularity

    private readonly object _gate = new();
    private readonly int _fadeFrames;
    private float[] _samples = [];
    private int _position;
    private ActiveClip? _active;
    private long _completedPlaybacks;
    private long _replacedPlaybacks;
    private float _gain = 1f;
    private int _fadeInRemaining;
    private int _fadeOutRemaining;
    private bool _stopping;

    // crossfade source: the replaced playback keeps decaying for one fade
    // window while the new one fades in (equal-power style overlap)
    private float[] _crossfadeSamples = [];
    private int _crossfadePosition;
    private int _crossfadeRemaining;

    public ClipPlayer(int fadeFrames = DefaultFadeFrames)
    {
        _fadeFrames = Math.Max(0, fadeFrames);
    }

    public event Action<ActiveClip>? PlaybackCompleted;

    public ActiveClip? Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public long CompletedPlaybacks => _completedPlaybacks;

    public void Start(string clipId, float[] samples, long playbackEpoch, ClipPlaybackMode mode, float gain = 1f)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                _replacedPlaybacks++;
                if (_fadeFrames > 0 && _position < _samples.Length)
                {
                    _crossfadeSamples = _samples;
                    _crossfadePosition = _position;
                    _crossfadeRemaining = _fadeFrames;
                }
                else
                {
                    _crossfadeRemaining = 0;
                }
            }

            _samples = samples;
            _position = 0;
            _gain = gain;
            _fadeInRemaining = _fadeFrames;
            _fadeOutRemaining = 0;
            _stopping = false;
            _active = new ActiveClip(clipId, playbackEpoch, mode);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_active is null)
            {
                return;
            }

            if (_fadeFrames == 0)
            {
                ClearAll();
                return;
            }

            // fade the current material out over one window instead of
            // stepping to silence; state clears when the fade exhausts
            _stopping = true;
            _fadeOutRemaining = _fadeFrames;
            if (_crossfadeRemaining > _fadeFrames)
            {
                _crossfadeRemaining = _fadeFrames;
            }
        }
    }

    /// <summary>Reads up to <paramref name="frames"/> frames and advances the playhead.</summary>
    public int Read(float[] destination, int frames)
    {
        ActiveClip? finished = null;
        int taken;
        lock (_gate)
        {
            if (_active is null)
            {
                // a crossfade tail can still sound after the new clip ended
                Array.Clear(destination, 0, frames);
                taken = 0;
                if (_crossfadeRemaining > 0)
                {
                    taken = Math.Min(frames, _crossfadeRemaining);
                    EmitCrossfade(destination, taken);
                }

                return taken;
            }

            taken = Math.Min(frames, _samples.Length - _position);
            var boundaryActive = _fadeInRemaining > 0
                || _stopping
                || _crossfadeRemaining > 0
                || _samples.Length - _position <= _fadeFrames + taken;
            if (!boundaryActive && _gain == 1f)
            {
                Array.Copy(_samples, _position, destination, 0, taken);
            }
            else
            {
                for (var i = 0; i < taken; i++)
                {
                    var multiplier = _gain;
                    if (_fadeInRemaining > 0)
                    {
                        multiplier *= (float)(_fadeFrames - _fadeInRemaining) / _fadeFrames;
                        _fadeInRemaining--;
                    }

                    var tail = _samples.Length - (_position + i);
                    if (tail < _fadeFrames)
                    {
                        multiplier *= (float)tail / _fadeFrames;
                    }

                    if (_stopping && _fadeOutRemaining > 0)
                    {
                        multiplier *= (float)_fadeOutRemaining-- / _fadeFrames;
                    }

                    destination[i] = _samples[_position + i] * multiplier;
                }
            }

            if (_crossfadeRemaining > 0 && taken > 0)
            {
                EmitCrossfade(destination, taken);
            }

            if (taken < frames)
            {
                Array.Clear(destination, taken, frames - taken);
            }

            _position += taken;
            if (_position >= _samples.Length || (_stopping && _fadeOutRemaining <= 0))
            {
                if (!_stopping)
                {
                    finished = _active;
                    _completedPlaybacks++;
                }

                ClearAll();
            }
        }

        if (finished is not null)
        {
            PlaybackCompleted?.Invoke(finished);
        }

        return taken;
    }

    /// <summary>Adds the decaying crossfade tail into the first <paramref name="frames"/> destination slots.</summary>
    private void EmitCrossfade(float[] destination, int frames)
    {
        var count = Math.Min(frames, _crossfadeRemaining);
        for (var i = 0; i < count; i++)
        {
            var index = _crossfadePosition + i;
            if (index >= _crossfadeSamples.Length)
            {
                _crossfadeRemaining = 0;
                return;
            }

            destination[i] += _crossfadeSamples[index] * (float)(_crossfadeRemaining - i) / _fadeFrames;
        }

        _crossfadePosition += count;
        _crossfadeRemaining -= count;
    }

    /// <summary>Snapshot of player state for metrics output.</summary>
    public ClipPlayerStats Stats()
    {
        lock (_gate)
        {
            return new ClipPlayerStats(
                _active is not null,
                _active?.Mode,
                _position,
                _samples.Length,
                _completedPlaybacks,
                _replacedPlaybacks);
        }
    }

    private void ClearAll()
    {
        _active = null;
        _position = 0;
        _samples = [];
        _gain = 1f;
        _fadeInRemaining = 0;
        _fadeOutRemaining = 0;
        _stopping = false;
    }
}

public readonly record struct ClipPlayerStats(
    bool Playing,
    ClipPlaybackMode? Mode,
    int PositionFrames,
    int LengthFrames,
    long CompletedPlaybacks,
    long ReplacedPlaybacks);

/// <summary>
/// The clip player's send-side feed. The ticket is only set for send-mode
/// playbacks; previews deliberately carry no ticket, so the mixer denies and
/// drains them — structural guarantee that auditions never leave the machine.
/// Authorized pulls are copied into a bounded tap so the ClipOnly monitor can
/// observe exactly what is being sent without pulling the playhead a second
/// time (P2 pop report: mixer and monitor used to advance the single
/// playhead together, each consuming half of the material).
/// </summary>
public sealed class ClipFeed : ISendFeed
{
    private readonly object _gate = new();
    private readonly float[] _tap;
    private readonly int _tapCapacity;
    private readonly int _tapHighWater;
    private int _tapHead;
    private int _tapCount;
    private SourceTicket? _ticket;

    public ClipFeed(ClipPlayer player, int tapCapacityFrames, int tapHighWaterFrames)
    {
        Player = player;
        _tapCapacity = Math.Max(1, tapCapacityFrames);
        _tapHighWater = Math.Min(_tapCapacity, Math.Max(1, tapHighWaterFrames));
        _tap = new float[_tapCapacity];
    }

    public ClipPlayer Player { get; }

    public string SourceId => "clip";

    public AudioKind Kind => AudioKind.Clip;

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

    public void SetTicket(SourceTicket? ticket)
    {
        lock (_gate)
        {
            _ticket = ticket;
        }
    }

    public int Pull(float[] destination, int frames)
    {
        var taken = Player.Read(destination, frames);
        lock (_gate)
        {
            for (var i = 0; i < taken; i++)
            {
                _tap[(_tapHead + _tapCount) % _tapCapacity] = destination[i];
                if (_tapCount < _tapCapacity)
                {
                    _tapCount++;
                }
                else
                {
                    _tapHead = (_tapHead + 1) % _tapCapacity;
                }
            }
        }

        return taken;
    }

    public int Drain(int frames) => 0; // nothing buffered: the playhead only advances when authorized

    /// <summary>Reads the send-side tap for ClipOnly monitoring; trims to the watermark like the endpoint ring.</summary>
    public int ReadTap(Span<float> destination)
    {
        lock (_gate)
        {
            var excess = _tapCount - _tapHighWater;
            if (excess > 0)
            {
                _tapHead = (_tapHead + excess) % _tapCapacity;
                _tapCount -= excess;
            }

            var emitted = Math.Min(destination.Length, _tapCount);
            for (var i = 0; i < emitted; i++)
            {
                destination[i] = _tap[(_tapHead + i) % _tapCapacity];
            }

            _tapHead = (_tapHead + emitted) % _tapCapacity;
            _tapCount -= emitted;
            return emitted;
        }
    }

    public void ClearTap()
    {
        lock (_gate)
        {
            _tapHead = 0;
            _tapCount = 0;
        }
    }
}
