namespace OnbodyMic.Audio.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class MonitorRouterTests
{
    [Fact]
    public void Disabled_router_emits_silence()
    {
        var (router, _) = Build(new MonitorRouteState(false, MonitorMode.ClipOnly), RouteGains.Unity);
        var output = new float[4];
        Assert.Equal(0, router.Read(output));
        Assert.All(output, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void ClipOnly_taps_only_the_send_clip_player()
    {
        var player = new ClipPlayer();
        player.Start("clip-a", new float[] { 0.5f, 0.5f }, playbackEpoch: 1, ClipPlaybackMode.Send);
        var (router, _) = Build(new MonitorRouteState(true, MonitorMode.ClipOnly), RouteGains.Unity, player);

        var output = new float[2];
        var emitted = router.Read(output);

        Assert.Equal(2, emitted);
        Assert.Equal(new float[] { 0.5f, 0.5f }, output);
    }

    [Fact]
    public void Preview_audition_routes_locally_even_while_monitor_suspended()
    {
        // engine semantics: preview suspends MonitorEnabled, but the audition
        // itself must still be audible on the local monitor device
        var player = new ClipPlayer();
        player.Start("clip-a", new float[] { 0.25f }, playbackEpoch: 1, ClipPlaybackMode.Preview);
        var (router, _) = Build(new MonitorRouteState(false, MonitorMode.ClipOnly), RouteGains.Unity, player);

        var output = new float[1];
        Assert.Equal(1, router.Read(output));
        Assert.Equal(0.25f, output[0], 5);
    }

    [Fact]
    public void FullEndpoint_reads_pushed_frames_and_applies_monitor_gain()
    {
        var (router, _) = Build(new MonitorRouteState(true, MonitorMode.FullEndpoint), new RouteGains(1, 1, 1, 0.5f));
        router.PushFullEndpoint(new float[] { 0.4f, 0.8f });

        var output = new float[2];
        var emitted = router.Read(output);

        Assert.Equal(2, emitted);
        Assert.Equal(0.2f, output[0], 5);
        Assert.Equal(0.4f, output[1], 5);
    }

    [Fact]
    public void FullEndpoint_ring_is_bounded_and_trims_drift()
    {
        var capacity = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(300));
        var player = new ClipPlayer();
        var router = new MonitorRouter(player, capacity, capacity / 2)
        {
            StateProvider = () => new MonitorRouteState(true, MonitorMode.FullEndpoint),
            GainsProvider = () => RouteGains.Unity,
        };

        // push beyond capacity -> oldest dropped
        router.PushFullEndpoint(new float[capacity + 10]);
        var stats = router.Stats();
        Assert.Equal(capacity, stats.QueuedFrames);
        Assert.Equal(10, stats.FullEndpointDroppedFrames);

        // a read trims back toward the high-water mark before emitting
        var output = new float[32];
        router.Read(output);
        stats = router.Stats();
        Assert.True(stats.QueuedFrames <= capacity / 2 + 32);
    }

    [Fact]
    public void FullEndpoint_starvation_emits_silence()
    {
        var (router, _) = Build(new MonitorRouteState(true, MonitorMode.FullEndpoint), RouteGains.Unity);
        var output = new float[4];
        Assert.Equal(0, router.Read(output));
        Assert.All(output, value => Assert.Equal(0f, value));
    }

    private static (MonitorRouter Router, ClipPlayer Player) Build(
        MonitorRouteState state,
        RouteGains gains,
        ClipPlayer? player = null)
    {
        var clipPlayer = player ?? new ClipPlayer();
        var router = new MonitorRouter(clipPlayer, 4410, 2205)
        {
            StateProvider = () => state,
            GainsProvider = () => gains,
        };
        return (router, clipPlayer);
    }
}
