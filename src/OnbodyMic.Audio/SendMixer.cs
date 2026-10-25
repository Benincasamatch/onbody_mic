namespace OnbodyMic.Audio;

/// <summary>
/// Pull-mode send mixer: fills a whole output period per call. Each feed is
/// checked against the current gate snapshot; allowed feeds are summed with
/// their route gain, denied feeds are drained so their buffered frames can
/// never leak out later. The final mix is hard-limited to [-1, 1] with
/// clipping counted (basic peak protection per docs/02 §9.1).
/// Partial pulls (capture starvation, clip end) get a short fade-out and the
/// next pull fades back in, so a starving feed produces an inaudible dip
/// instead of a hard step. Post-mix hard steps are counted separately as a
/// forensic indicator: a pop with HardSteps unchanged did not originate here.
/// </summary>
public sealed class SendMixer
{
    private const int FadeFrames = 128; // ~2.9 ms @ 44.1 kHz
    private const float HardStepThreshold = 0.5f;
    private const float LimiterKnee = 0.8f; // soft-knee threshold

    private readonly List<ISendFeed> _feeds = [];
    private readonly HashSet<ISendFeed> _shortFeeds = [];
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

    /// <summary>Counted post-mix discontinuities larger than any legitimate in-program transition.</summary>
    public long HardSteps { get; private set; }

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
                _shortFeeds.Add(feed); // fade in when re-authorized
                continue;
            }

            var wasShort = _shortFeeds.Contains(feed);
            var available = feed.Pull(_scratch, frames);
            if (available < frames)
            {
                UnderrunFrames += frames - available;
                _shortFeeds.Add(feed);
            }
            else
            {
                _shortFeeds.Remove(feed);
            }

            if (available > 0)
            {
                if (wasShort)
                {
                    var ramp = Math.Min(FadeFrames, available);
                    for (var i = 0; i < ramp; i++)
                    {
                        _scratch[i] *= i / (float)ramp;
                    }
                }

                if (available < frames)
                {
                    var tail = Math.Min(FadeFrames, available);
                    for (var j = 0; j < tail; j++)
                    {
                        _scratch[available - tail + j] *= (tail - j) / (float)tail;
                    }
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
        }

        for (var i = 0; i < destination.Length; i++)
        {
            if (i > 0
                && Math.Abs(destination[i] - destination[i - 1]) > HardStepThreshold
                && Math.Abs(destination[i]) > 0.005f
                && Math.Abs(destination[i - 1]) > 0.005f)
            {
                HardSteps++;
            }

            var value = destination[i];
            var magnitude = Math.Abs(value);
            if (magnitude > Peak)
            {
                Peak = magnitude;
            }

            if (magnitude > LimiterKnee)
            {
                // soft knee: continuous and flat-saturating at 1.0, so an
                // over-summing mix saturates gently instead of hard-clipping
                // into sharp corners (P1.1 KOOK retest: hard limiter corners
                // were themselves audible as crunch during loud speech+fx)
                var overshoot = (magnitude - LimiterKnee) / (1f - LimiterKnee);
                var limited = LimiterKnee + ((1f - LimiterKnee) * MathF.Tanh(overshoot));
                destination[i] = MathF.Sign(value) * limited;
                ClippedSamples++;
            }
        }

        FramesMixed += frames;
    }
}
