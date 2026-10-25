namespace OnbodyMic.Audio.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class SendMixerTests
{
    private const int Epoch = 1;

    [Fact]
    public void Denied_feed_is_drained_and_never_replayed()
    {
        var mic = new SourceChannel("mic", AudioKind.Microphone, 100);
        mic.SetTicket(new SourceTicket("mic", AudioKind.Microphone, Epoch, 1));
        mic.Push(new float[] { 0.5f, 0.5f, 0.5f });

        var switches = SessionSwitches.AllOff with { MicSendEnabled = false };
        var mixer = Build(switches, clip: null);
        mixer.Add(mic);

        var output = new float[3];
        mixer.Read(output);

        Assert.All(output, value => Assert.Equal(0f, value));
        Assert.Equal(3, mic.Stats().DeniedFrames);
        Assert.Equal(0, mic.QueuedFrames);

        // re-enabling must not bring the muted-era frames back
        var enabled = SessionSwitches.AllOff with { MicSendEnabled = true };
        mixer.SnapshotProvider = () => Snapshot(enabled, null);
        mixer.Read(output);
        Assert.All(output, value => Assert.Equal(0f, value));
        Assert.Equal(0, mic.Stats().PulledFrames);
    }

    [Fact]
    public void Allowed_feeds_mix_with_independent_gains()
    {
        var mic = new SourceChannel("mic", AudioKind.Microphone, 100);
        mic.SetTicket(new SourceTicket("mic", AudioKind.Microphone, Epoch, 1));
        mic.Push(new float[] { 0.25f, 0.25f });
        var app = new SourceChannel("app", AudioKind.Application, 100);
        app.SetTicket(new SourceTicket("app", AudioKind.Application, Epoch, 1));
        app.Push(new float[] { 0.5f, 0.5f });

        var switches = SessionSwitches.AllOff with { MicSendEnabled = true, ExtrasSendEnabled = true, AppSendEnabled = true };
        var mixer = Build(switches, clip: null, gains: new RouteGains(2f, 0.5f, 1f, 1f));
        mixer.Add(mic);
        mixer.Add(app);

        var output = new float[2];
        mixer.Read(output);

        // mic 0.25*2 + app 0.5*0.5
        Assert.Equal(0.75f, output[0], 5);
        Assert.Equal(0.75f, output[1], 5);
    }

    [Fact]
    public void Stale_session_epoch_frames_are_denied()
    {
        var mic = new SourceChannel("mic", AudioKind.Microphone, 100);
        mic.SetTicket(new SourceTicket("mic", AudioKind.Microphone, Epoch, 1));
        mic.Push(new float[] { 0.5f });

        var switches = SessionSwitches.AllOff with { MicSendEnabled = true };
        var mixer = Build(switches, clip: null, sessionEpoch: Epoch + 1);
        mixer.Add(mic);

        var output = new float[1];
        mixer.Read(output);
        Assert.Equal(0f, output[0]);
        Assert.Equal(1, mic.Stats().DeniedFrames);
    }

    [Fact]
    public void Mix_is_hard_limited_and_clipping_counted()
    {
        var mic = new SourceChannel("mic", AudioKind.Microphone, 100);
        mic.SetTicket(new SourceTicket("mic", AudioKind.Microphone, Epoch, 1));
        mic.Push(new float[] { 0.9f, -0.9f });

        var app = new SourceChannel("app", AudioKind.Application, 100);
        app.SetTicket(new SourceTicket("app", AudioKind.Application, Epoch, 1));
        app.Push(new float[] { 0.9f, -0.9f });

        var switches = SessionSwitches.AllOff with { MicSendEnabled = true, ExtrasSendEnabled = true, AppSendEnabled = true };
        var mixer = Build(switches, clip: null);
        mixer.Add(mic);
        mixer.Add(app);

        var output = new float[2];
        mixer.Read(output);

        Assert.Equal(1f, output[0]);
        Assert.Equal(-1f, output[1]);
        Assert.Equal(2, mixer.ClippedSamples);
        Assert.Equal(1.8f, mixer.Peak, 5);
    }

    [Fact]
    public void Underrun_counts_authorized_but_empty_feeds()
    {
        var mic = new SourceChannel("mic", AudioKind.Microphone, 100);
        mic.SetTicket(new SourceTicket("mic", AudioKind.Microphone, Epoch, 1));

        var switches = SessionSwitches.AllOff with { MicSendEnabled = true };
        var mixer = Build(switches, clip: null);
        mixer.Add(mic);

        var output = new float[10];
        mixer.Read(output);
        Assert.Equal(10, mixer.UnderrunFrames);
        Assert.All(output, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Clip_feed_without_ticket_never_reaches_output()
    {
        var player = new ClipPlayer();
        var feed = new ClipFeed(player);
        player.Start("clip-a", new float[] { 0.5f, 0.5f, 0.5f }, playbackEpoch: 1, ClipPlaybackMode.Preview);

        var switches = SessionSwitches.AllOff with { ExtrasSendEnabled = true };
        var mixer = Build(switches, clip: null);
        mixer.Add(feed);

        var output = new float[3];
        mixer.Read(output);
        Assert.All(output, value => Assert.Equal(0f, value));
        // the mixer must not advance a preview playhead it is not authorized to pull
        Assert.Equal(0, player.Stats().PositionFrames);
    }

    internal static SendGateSnapshot Snapshot(SessionSwitches switches, ActiveClip? clip) =>
        new(Epoch, EmergencyLatched: false, switches, clip);

    private static SendMixer Build(
        SessionSwitches switches,
        ActiveClip? clip,
        RouteGains? gains = null,
        long sessionEpoch = Epoch)
    {
        var snapshot = new SendGateSnapshot(sessionEpoch, false, switches, clip);
        var routeGains = gains ?? RouteGains.Unity;
        return new SendMixer(64)
        {
            Gate = new SendGate(),
            SnapshotProvider = () => snapshot,
            GainsProvider = () => routeGains,
        };
    }
}
