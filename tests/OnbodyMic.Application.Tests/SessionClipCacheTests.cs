namespace OnbodyMic.Application.Tests;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using Xunit;

/// <summary>P2 preloading: the session serves stable-id plays from the cache, decodes on miss, and applies per-clip gain.</summary>
public sealed class SessionClipCacheTests : IDisposable
{
    private const int Period = 441;

    private readonly FakeDecoder _decoder = new();
    private readonly FakeSampleStore _store = new();
    private readonly Dictionary<string, ClipResolution> _resolutions = [];
    private readonly FakeSourceAdapter _mic = new();
    private readonly FakeSourceAdapter _app = new();
    private readonly FakeVirtualOutput _output = new();
    private readonly FakeMonitorOutput _monitorOutput = new();
    private readonly AudioSession _session;

    public SessionClipCacheTests()
    {
        _session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = _decoder,
            SampleStore = _store,
            ClipResolver = id => _resolutions.TryGetValue(id, out var resolution) ? resolution : ClipResolution.Unknown,
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
        });
        Assert.True(_session.AttachOutput().Accepted);
        _resolutions["clip-a"] = new ClipResolution(true, true, "assets/clip-a.wav", 1f);
    }

    public void Dispose() => _session.Dispose();

    private static void Accept(CommandResult result) => Assert.True(result.Accepted, result.Rejection);

    [Fact]
    public void Cache_miss_decodes_then_stores_under_the_stable_id()
    {
        Accept(_session.EnableExtras(true));

        Accept(_session.PlayClip("clip-a"));

        Assert.Equal(1, _decoder.Calls);
        Assert.Equal("assets/clip-a.wav", _decoder.LastPath);
        Assert.Equal(["clip-a"], _store.StoredIds);
    }

    [Fact]
    public void Cache_hit_plays_without_decoding()
    {
        _store.Store("clip-a", FakeDecoder.CreateTone(0.25f, 4410));
        Accept(_session.EnableExtras(true));

        Accept(_session.PlayClip("clip-a"));

        Assert.Equal(0, _decoder.Calls);
        var mixed = _output.Pump(Period);
        Assert.All(mixed, value => Assert.Equal(0.25f, value, 4));
    }

    [Fact]
    public void Known_but_missing_asset_is_rejected_without_decoding()
    {
        _resolutions["clip-a"] = _resolutions["clip-a"] with { Available = false };
        Accept(_session.EnableExtras(true));

        var result = _session.PlayClip("clip-a");

        Assert.False(result.Accepted);
        Assert.Contains("clip-unavailable", result.Rejection);
        Assert.Equal(0, _decoder.Calls);
        Assert.Null(_session.Status().Clip);
    }

    [Fact]
    public void Per_clip_gain_scales_the_playback()
    {
        _store.Store("clip-a", FakeDecoder.CreateTone(0.5f, 4410));
        _resolutions["clip-a"] = _resolutions["clip-a"] with { Gain = 0.5f };
        Accept(_session.EnableExtras(true));

        Accept(_session.PlayClip("clip-a"));

        var mixed = _output.Pump(Period);
        Assert.All(mixed, value => Assert.Equal(0.25f, value, 4));
    }

    [Fact]
    public void Ad_hoc_path_plays_decode_but_do_not_pollute_the_cache()
    {
        Accept(_session.EnableExtras(true));

        Accept(_session.PlayClip("some/where/raw.wav"));

        Assert.Equal(1, _decoder.Calls);
        Assert.Empty(_store.StoredIds); // raw paths never evict managed clips
        var mixed = _output.Pump(Period);
        Assert.All(mixed, value => Assert.Equal(0.5f, value, 4));
    }
}
