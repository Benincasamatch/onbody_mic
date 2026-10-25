namespace OnbodyMic.Audio.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class MonitorRouterTests
{
    [Fact]
    public void Disabled_router_emits_silence()
    {
        var (router, _, _) = Build(new MonitorRouteState(false, MonitorMode.ClipOnly), RouteGains.Unity);
        var output = new float[4];
        Assert.Equal(0, router.Read(output));
        Assert.All(output, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void ClipOnly_taps_the_authorized_send_pulls_without_double_pulling()
    {
        var player = new ClipPlayer(fadeFrames: 0);
        player.Start("clip-a", new float[] { 0.5f, 0.5f }, playbackEpoch: 1, ClipPlaybackMode.Send);
        var (router, _, feed) = Build(new MonitorRouteState(true, MonitorMode.ClipOnly), RouteGains.Unity, player);

        var mixed = new float[2];
        Assert.Equal(2, feed.Pull(mixed, 2)); // the mixer's authorized pull fills the tap

        var output = new float[2];
        var emitted = router.Read(output);

        Assert.Equal(2, emitted);
        Assert.Equal(new float[] { 0.5f, 0.5f }, output); // monitor heard exactly what was sent

        // regression (P2 pop report): the monitor read must not have advanced
        // the playhead — the next mixer pull sees the clip exhausted
        var next = new float[2];
        Assert.Equal(0, feed.Pull(next, 2));
        Assert.All(next, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Preview_audition_routes_locally_even_while_monitor_suspended()
    {
        // engine semantics: preview suspends MonitorEnabled, but the audition
        // itself must still be audible on the local monitor device
        var player = new ClipPlayer(fadeFrames: 0);
        player.Start("clip-a", new float[] { 0.25f }, playbackEpoch: 1, ClipPlaybackMode.Preview);
        var (router, _, _) = Build(new MonitorRouteState(false, MonitorMode.ClipOnly), RouteGains.Unity, player);

        var output = new float[1];
        Assert.Equal(1, router.Read(output));
        Assert.Equal(0.25f, output[0], 5);
    }

    [Fact]
    public void FullEndpoint_prerolls_then_reads_pushed_frames_with_monitor_gain()
    {
        var (router, _, _) = Build(new MonitorRouteState(true, MonitorMode.FullEndpoint), new RouteGains(1, 1, 1, 0.5f));
        // pre-roll: the router stays silent until the prim threshold is met
        var prim = 551; // Build: min(highWater/4, capacity/4)
        router.PushFullEndpoint(new float[prim]);
        var preroll = new float[2];
        _ = router.Read(preroll);
        Assert.All(preroll, v => Assert.Equal(0f, v)); // pre-roll stays silent

        // keep the stream alive; once primed the monitor hears the frames
        // through the monitor gain (adaptive interpolation, ±1%)
        router.PushFullEndpoint(new float[] { 0.4f, 0.8f });
        float[]? heard = null;
        var output = new float[2];
        for (var i = 0; i < 400 && heard is null; i++)
        {
            router.PushFullEndpoint(new float[] { 0.4f, 0.8f, 0.4f, 0.8f });
            var emitted = router.Read(output);
            if (emitted > 0 && output.Take(emitted).Any(v => v > 0.01f))
            {
                heard = output[..emitted];
            }
        }

        Assert.NotNull(heard);
        Assert.All(heard!, v => Assert.InRange(v, 0.1f, 0.45f));
    }

    [Fact]
    public void FullEndpoint_ring_is_bounded_and_adapts_to_clock_drift()
    {
        var capacity = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(300));
        var feed = new ClipFeed(new ClipPlayer(fadeFrames: 0), capacity, capacity / 2);
        var router = new MonitorRouter(feed, capacity, capacity / 2)
        {
            StateProvider = () => new MonitorRouteState(true, MonitorMode.FullEndpoint),
            GainsProvider = () => RouteGains.Unity,
        };

        // push beyond capacity -> the backlog dumps to the target water level
        router.PushFullEndpoint(new float[capacity + 10]);
        var stats = router.Stats();
        Assert.Equal(capacity / 4, stats.QueuedFrames);
        Assert.True(stats.FullEndpointDroppedFrames >= 10);

        // steady state: production matches consumption, the ±1% adaptive ratio
        // holds the water level — the backlog dump must not repeat
        var output = new float[32];
        for (var i = 0; i < 500; i++)
        {
            router.PushFullEndpoint(new float[32]);
            router.Read(output);
        }

        stats = router.Stats();
        var droppedAfterWarmup = stats.FullEndpointDroppedFrames;
        for (var i = 0; i < 500; i++)
        {
            router.PushFullEndpoint(new float[32]);
            router.Read(output);
        }

        stats = router.Stats();
        Assert.Equal(droppedAfterWarmup, stats.FullEndpointDroppedFrames);
        Assert.True(stats.QueuedFrames > 0, "ring ran dry under matched rates");
        Assert.True(stats.FullEndpointStarvedFrames < 32 * 500 * 0.1,
            $"starved too much under matched rates: {stats.FullEndpointStarvedFrames}");
    }

    [Fact]
    public void FullEndpoint_starvation_emits_silence()
    {
        var (router, _, _) = Build(new MonitorRouteState(true, MonitorMode.FullEndpoint), RouteGains.Unity);
        var output = new float[4];
        Assert.Equal(0, router.Read(output));
        Assert.All(output, value => Assert.Equal(0f, value));
    }

    private static (MonitorRouter Router, ClipPlayer Player, ClipFeed Feed) Build(
        MonitorRouteState state,
        RouteGains gains,
        ClipPlayer? player = null)
    {
        var clipPlayer = player ?? new ClipPlayer(fadeFrames: 0);
        var feed = new ClipFeed(clipPlayer, 4410, 2205);
        var router = new MonitorRouter(feed, 4410, 2205)
        {
            StateProvider = () => state,
            GainsProvider = () => gains,
        };
        return (router, clipPlayer, feed);
    }
}
