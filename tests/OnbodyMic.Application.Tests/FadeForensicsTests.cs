namespace OnbodyMic.Application.Tests;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using Xunit;

/// <summary>
/// In-vitro forensics for the p2smoke half-rate/doubled-duration anomaly:
/// drives the session exactly like the smoke does (default boundary fades,
/// cache preloaded, period pumps) and asserts the emitted loud material
/// matches the clip length exactly — no doubling, no missing first plays.
/// </summary>
public sealed class FadeForensicsTests : IDisposable
{
    private const int Period = 441; // 10 ms

    private readonly FakeDecoder _decoder = new();
    private readonly FakeSampleStore _store = new();
    private readonly FakeSourceAdapter _mic = new();
    private readonly FakeSourceAdapter _app = new();
    private readonly FakeVirtualOutput _output = new();
    private readonly FakeMonitorOutput _monitorOutput = new();
    private readonly AudioSession _session;

    public FadeForensicsTests()
    {
        _session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = _decoder,
            SampleStore = _store,
            ClipResolver = id => new ClipResolution(true, true, $"assets/{id}.wav", 1f),
            MicAdapterFactory = channel =>
            {
                _mic.Channel = channel;
                return _mic;
            },
            AppAdapterFactory = (channel, _) =>
            {
                _app.Channel = channel;
                return _app;
            },
            OutputFactory = () => _output,
            MonitorOutputFactory = () => _monitorOutput,
            SourcePrerollTimeoutMs = 20,
            AdaptiveRate = false,
            // DEFAULT fades: the smoke configuration under suspicion
        });
        Assert.True(_session.AttachOutput().Accepted);
    }

    public void Dispose() => _session.Dispose();

    [Fact]
    public void Ten_presses_each_emit_exactly_one_clip_of_material()
    {
        Assert.True(_session.EnableExtras(true).Accepted);
        var clipFrames = 22050; // 500 ms
        _decoder.Generator = _ => FakeDecoder.CreateTone(0.4f, clipFrames);

        var emitted = new List<float>();
        for (var press = 0; press < 10; press++)
        {
            Assert.True(_session.PlayClip("clip-a").Accepted);
            for (var p = 0; p < 65; p++)
            {
                emitted.AddRange(_output.Pump(Period));
            }
        }

        var loudRuns = new List<int>();
        var run = 0;
        foreach (var value in emitted)
        {
            if (Math.Abs(value) > 0.05)
            {
                run++;
            }
            else if (run > 0)
            {
                loudRuns.Add(run);
                run = 0;
            }
        }

        if (run > 0)
        {
            loudRuns.Add(run);
        }

        Assert.Equal(10, loudRuns.Count);
        Assert.All(loudRuns, length => Assert.InRange(length, clipFrames - Period, clipFrames + Period));
    }

    [Fact]
    public void First_play_is_audible_immediately_after_enable()
    {
        Assert.True(_session.EnableExtras(true).Accepted);
        _decoder.Generator = _ => FakeDecoder.CreateTone(0.4f, 4410);

        Assert.True(_session.PlayClip("clip-a").Accepted);
        var first = _output.Pump(Period);

        Assert.All(first, value => Assert.True(Math.Abs(value) <= 0.4f + 1e-6f));
        Assert.Contains(first, value => Math.Abs(value) > 0.05); // audible material in the very first period
    }
}
