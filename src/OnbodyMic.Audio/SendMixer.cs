namespace OnbodyMic.Audio;

/// <summary>
/// Pull-mode send mixer: fills a whole output period per call. Each feed is
/// checked against the current gate snapshot; allowed feeds are summed with
/// their route gain, denied feeds are drained so their buffered frames can
/// never leak out later. The final mix is hard-limited to [-1, 1] with
/// clipping counted (basic peak protection per docs/02 §9.1).
/// </summary>
public sealed class SendMixer
{
    private readonly List<ISendFeed> _feeds = [];
    private readonly float[] _scratch;

    /// <summary>Gate snapshot provider; returns an immutable snapshot cheaply. Swapped only while the pump is stopped.</summary>
    public required Func<SendGateSnapshot> SnapshotProvider { get; set; }

    public required Func<RouteGains> GainsProvider { get; set; }

    public required SendGate Gate { get; init; }

    public SendMixer(int maxPeriodFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPeriodFrames, 1);
        _scratch = new float[maxPeriodFrames];
    }


    public IReadOnlyList<ISendFeed> Feeds => _feeds;

    public void Add(ISendFeed feed) => _feeds.Add(feed);

    /// <summary>Single-writer counters (output pump thread); readers take a racy snapshot.</summary>
    public long FramesMixed { get; private set; }

    public long ClippedSamples { get; private set; }

    public long UnderrunFrames { get; private set; }

    public long DeniedPulls { get; private set; }

    public float Peak { get; private set; }

    public void Read(Span<float> destination)
    {
        destination.Clear();
        var frames = destination.Length;
        var snapshot = SnapshotProvider();
        var gains = GainsProvider();

        foreach (var feed in _feeds)
        {
            var ticket = feed.Ticket;
            if (ticket is null || !Gate.Decide(ticket, snapshot).Allowed)
            {
                feed.Drain(frames);
                DeniedPulls++;
                continue;
            }

            var available = feed.Pull(_scratch, frames);
            if (available < frames)
            {
                UnderrunFrames += frames - available;
            }

            var gain = feed.Kind switch
            {
                AudioKind.Microphone => gains.Mic,
                AudioKind.Application => gains.App,
                AudioKind.Clip => gains.Clip,
                _ => 0f,
            };

            for (var i = 0; i < available; i++)
            {
                destination[i] += _scratch[i] * gain;
            }
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var value = destination[i];
            var magnitude = Math.Abs(value);
            if (magnitude > Peak)
            {
                Peak = magnitude;
            }

            if (magnitude > 1f)
            {
                destination[i] = Math.Clamp(value, -1f, 1f);
                ClippedSamples++;
            }
        }

        FramesMixed += frames;
    }
}
