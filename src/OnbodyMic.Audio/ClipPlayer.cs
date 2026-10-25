namespace OnbodyMic.Audio;

/// <summary>
/// Single-slot clip player: a new target replaces the old one; triggering the
/// same clip again restarts from the beginning (ReplaceAndRestartSame).
/// Send-mode playbacks are pulled through the mixer only while the send gate
/// authorizes the current playback epoch; preview playbacks are pulled by the
/// monitor router and can never reach the send mixer (no ticket is issued).
/// </summary>
public sealed class ClipPlayer
{
    private readonly object _gate = new();
    private float[] _samples = [];
    private int _position;
    private ActiveClip? _active;
    private long _completedPlaybacks;
    private long _replacedPlaybacks;
    private float _gain = 1f;

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
            }

            _samples = samples;
            _position = 0;
            _gain = gain;
            _active = new ActiveClip(clipId, playbackEpoch, mode);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _active = null;
            _position = 0;
            _samples = [];
            _gain = 1f;
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
                Array.Clear(destination, 0, frames);
                return 0;
            }

            taken = Math.Min(frames, _samples.Length - _position);
            Array.Copy(_samples, _position, destination, 0, taken);
            if (_gain != 1f)
            {
                for (var i = 0; i < taken; i++)
                {
                    destination[i] *= _gain;
                }
            }

            if (taken < frames)
            {
                Array.Clear(destination, taken, frames - taken);
            }

            _position += taken;
            if (_position >= _samples.Length)
            {
                finished = _active;
                _active = null;
                _position = 0;
                _samples = [];
                _completedPlaybacks++;
            }
        }

        if (finished is not null)
        {
            PlaybackCompleted?.Invoke(finished);
        }

        return taken;
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
/// </summary>
public sealed class ClipFeed : ISendFeed
{
    private readonly object _gate = new();
    private SourceTicket? _ticket;

    public ClipFeed(ClipPlayer player) => Player = player;

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

    public int Pull(float[] destination, int frames) => Player.Read(destination, frames);

    public int Drain(int frames) => 0; // nothing buffered: the playhead only advances when authorized
}
