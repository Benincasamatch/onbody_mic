namespace OnbodyMic.Application.Tests;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using Xunit;

public sealed class AudioSessionTests
{
    private const int Period = 441; // 10 ms @ 44.1 kHz

    [Fact]
    public void Startup_is_fully_muted_and_pump_emits_silence()
    {
        var env = SessionEnv.Create();
        Accept(env.Session.AttachOutput());

        var status = env.Session.Status();
        Assert.Equal(SessionSwitches.AllOff, status.Switches);
        Assert.Null(status.Clip);
        Assert.Null(status.LastPresetId);

        var sent = env.Output.Fake.Pump(Period);
        Assert.All(sent, value => Assert.Equal(0f, value));
        env.Dispose();
    }

    [Fact]
    public void App_enable_is_rejected_while_extras_off()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();

        var result = env.Session.EnableApp(true);

        Assert.False(result.Accepted);
        Assert.Equal("extras-disabled", result.Rejection);
        Assert.False(env.Session.Status().Switches.AppSendEnabled);
        env.Dispose();
    }

    [Fact]
    public void Mic_app_and_clip_all_reach_the_output_when_enabled()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.BindApplication(new AppBinding(42, "test-app")));
        Accept(env.Session.EnableApp(true));
        env.Mic.Push(Filled(Period, 0.1f));
        env.App.Push(Filled(Period, 0.2f));
        Accept(env.Session.PlayClip("clip.wav"));
        env.Output.Fake.Pump(Period); // consume the queued mic/app backlog

        env.Mic.Push(Filled(Period, 0.1f));
        env.App.Push(Filled(Period, 0.2f));
        var mixed = env.Output.Fake.Pump(Period);

        Assert.Equal(0.8f, mixed[0], 4); // 0.1 + 0.2 + 0.5 clip tone
        Assert.All(mixed, value => Assert.True(value > 0.7f && value <= 1f));
        env.Dispose();
    }

    [Fact]
    public void Extras_off_stops_app_capture_and_send_clip_but_mic_keeps_sending()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.BindApplication(new AppBinding(42, "test-app")));
        Accept(env.Session.EnableApp(true));
        Accept(env.Session.PlayClip("clip.wav"));
        env.Mic.Push(Filled(Period, 0.1f)); // keep the mic feed non-starved for the exact-value check
        env.Output.Fake.Pump(Period);

        Accept(env.Session.EnableExtras(false));

        var status = env.Session.Status();
        Assert.False(status.Switches.ExtrasSendEnabled);
        Assert.False(status.Switches.AppSendEnabled);
        Assert.True(status.Switches.MicSendEnabled);
        Assert.True(env.App.Disposed); // app capture stopped (docs/06 §1.1)

        env.Mic.Push(Filled(Period, 0.1f));
        env.App.Push(Filled(Period, 0.2f)); // late push from a revoked source is dropped
        var mixed = env.Output.Fake.Pump(Period);

        Assert.Equal(0.1f, mixed[0], 4); // only mic
        Assert.All(mixed, value => Assert.Equal(0.1f, value, 4));
        env.Dispose();
    }

    [Fact]
    public void Preview_is_audible_locally_but_never_reaches_the_virtual_output()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.SetMonitor(true, MonitorMode.ClipOnly));

        Accept(env.Session.PreviewClip("clip.wav"));

        var status = env.Session.Status();
        Assert.True(status.MonitorSuspendedForPreview);

        var sent = env.Output.Fake.Pump(Period);
        Assert.All(sent, value => Assert.Equal(0f, value)); // nothing leaves the machine

        var monitored = env.MonitorOutput.Fake.Pump(Period);
        Assert.All(monitored, value => Assert.Equal(0.5f, value, 4)); // audition audible locally
        env.Dispose();
    }

    [Fact]
    public void Emergency_mute_latches_stops_everything_and_rejects_new_plays()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.SetMonitor(true, MonitorMode.ClipOnly));
        env.Mic.Push(Filled(Period, 0.3f));
        env.Output.Fake.Pump(Period);

        Accept(env.Session.EmergencyMute());

        var status = env.Session.Status();
        Assert.Equal(SessionPhase.EmergencyMuted, status.Phase);
        Assert.True(status.EmergencyLatched);
        Assert.Equal(SessionSwitches.AllOff, status.Switches);
        Assert.True(env.MonitorOutput.Fake.Disposed); // listening stopped
        var sent = env.Output.Fake.Pump(Period);
        Assert.All(sent, value => Assert.Equal(0f, value));

        Assert.False(env.Session.PreviewClip("clip.wav").Accepted);
        Assert.False(env.Session.PlayClip("clip.wav").Accepted);
        Assert.False(env.Session.EnableMic(true).Accepted);

        Accept(env.Session.UnlockEmergency());
        status = env.Session.Status();
        Assert.False(status.EmergencyLatched);
        Assert.Equal(SessionSwitches.AllOff, status.Switches); // unlock keeps everything off
        env.Dispose();
    }

    [Fact]
    public void Frames_buffered_during_mute_are_not_replayed_after_reenable()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));

        env.Mic.Push(Filled(Period, 0.1f)); // before mute
        Accept(env.Session.EnableMic(false));
        env.Output.Fake.Pump(Period); // muted period: queued frames drained
        env.Mic.Push(Filled(Period, 0.2f)); // during mute
        env.Output.Fake.Pump(Period);

        Accept(env.Session.EnableMic(true));
        var replayed = env.Output.Fake.Pump(Period);
        Assert.All(replayed, value => Assert.Equal(0f, value)); // nothing old comes back

        // first live pull after starvation ramps in; assert the steady pull is exact
        env.Mic.Push(Filled(Period, 0.3f)); // fresh input after re-enable
        env.Output.Fake.Pump(Period);
        env.Mic.Push(Filled(Period, 0.3f));
        var live = env.Output.Fake.Pump(Period);
        Assert.All(live, value => Assert.Equal(0.3f, value, 4));
        env.Dispose();
    }

    [Fact]
    public void Adaptive_rate_keeps_a_deep_queue_pull_clean_end_to_end()
    {
        // separate session with adaptive ON: a slow producer (drift) must not
        // starve full pulls once the queue sits at the watermark
        var mic = new FakeSourceAdapter();
        var output = new OutputSlot();
        using var session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = new FakeDecoder(),
            MicAdapterFactory = channel =>
            {
                mic.Channel = channel;
                return mic;
            },
            AppAdapterFactory = (_, _) => throw new InvalidOperationException("unused"),
            OutputFactory = output.Create,
            MonitorOutputFactory = () => throw new InvalidOperationException("unused"),
            AdaptiveRate = true,
            SourcePrerollTimeoutMs = 20,
        });
        session.AttachOutput();

        var watermark = EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(60));
        mic.OnStart = () => mic.Push(Filled(watermark, 0.5f));
        Accept(session.EnableMic(true));

        for (var period = 0; period < 800; period++)
        {
            mic.Push(Filled(439, 0.5f)); // producer 0.45% slow: ratio must settle below 1
            var mixed = output.Fake.Pump(Period);
            Assert.All(mixed, value => Assert.Equal(0.5f, value, 3));
        }

        Assert.Equal(0, session.Metrics().MixerUnderrunFrames);
        Assert.True(session.Metrics().MicRatio < 1.0);
    }

    [Fact]
    public void App_exit_closes_only_the_app_path_mic_keeps_sending()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.BindApplication(new AppBinding(42, "test-app")));
        Accept(env.Session.EnableApp(true));
        env.App.RaiseEnded(null); // target process exited normally

        Assert.True(WaitUntil(() => env.Session.Status().Switches.AppSendEnabled == false));
        var status = env.Session.Status();
        Assert.True(status.Switches.MicSendEnabled); // mic unaffected (T40: no cross-kill)
        Assert.True(env.App.Disposed);

        env.Mic.Push(Filled(Period, 0.4f));
        var mixed = env.Output.Fake.Pump(Period);
        Assert.All(mixed, value => Assert.Equal(0.4f, value, 4));
        env.Dispose();
    }

    [Fact]
    public void Mic_device_loss_closes_mic_but_app_and_clip_continue()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.BindApplication(new AppBinding(42, "test-app")));
        Accept(env.Session.EnableApp(true));
        env.Mic.RaiseEnded("device invalidated");

        Assert.True(WaitUntil(() => env.Session.Status().Switches.MicSendEnabled == false));
        var status = env.Session.Status();
        Assert.True(status.Switches.AppSendEnabled); // app unaffected (T19)
        Assert.True(env.Mic.Disposed);

        env.App.Push(Filled(Period, 0.25f));
        var mixed = env.Output.Fake.Pump(Period);
        Assert.All(mixed, value => Assert.Equal(0.25f, value, 4));
        env.Dispose();
    }

    [Fact]
    public void Output_fault_closes_all_send_and_requires_explicit_reattach()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        env.Mic.Push(Filled(Period, 0.3f));
        Assert.NotEqual(0f, env.Output.Fake.Pump(Period)[0]);

        env.Output.Fake.RaiseFault();

        Assert.True(WaitUntil(() => env.Session.Status().OutputLost));
        var status = env.Session.Status();
        Assert.Equal(SessionPhase.OutputLost, status.Phase);
        Assert.Equal(SessionSwitches.AllOff with { MonitorEnabled = false }, status.Switches);
        Assert.True(env.Output.Fake.Disposed);
        Assert.False(env.Session.EnableMic(true).Accepted); // rejects while output is lost

        Accept(env.Session.AttachOutput()); // explicit re-attach
        Accept(env.Session.EnableMic(true));
        env.Mic.Push(Filled(Period, 0.3f));
        Assert.NotEqual(0f, env.Output.Fake.Pump(Period)[0]);
        env.Dispose();
    }

    [Fact]
    public void Gain_changes_apply_only_to_their_own_path()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.BindApplication(new AppBinding(42, "test-app")));
        Accept(env.Session.EnableApp(true));
        env.Mic.Push(Filled(Period, 0.2f));
        env.App.Push(Filled(Period, 0.2f));
        env.Output.Fake.Pump(Period);

        Accept(env.Session.SetGains(new RouteGains(0.5f, 2f, 1f, 1f)));

        env.Mic.Push(Filled(Period, 0.2f));
        env.App.Push(Filled(Period, 0.2f));
        var mixed = env.Output.Fake.Pump(Period);
        Assert.Equal(0.5f, mixed[0], 4); // mic 0.2*0.5 + app 0.2*2 = 0.5
        env.Dispose();
    }

    [Fact]
    public void ClipOnly_monitor_contains_no_mic_or_app_audio()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableMic(true));
        Accept(env.Session.EnableExtras(true));
        Accept(env.Session.BindApplication(new AppBinding(42, "test-app")));
        Accept(env.Session.EnableApp(true));
        Accept(env.Session.SetMonitor(true, MonitorMode.ClipOnly));
        Accept(env.Session.PlayClip("clip.wav"));
        env.Mic.Push(Filled(Period, 0.1f));
        env.App.Push(Filled(Period, 0.2f));
        env.Output.Fake.Pump(Period);

        var monitored = env.MonitorOutput.Fake.Pump(Period);

        Assert.All(monitored, value => Assert.Equal(0.5f, value, 4)); // clip tone only, no mic/app
        env.Dispose();
    }

    [Fact]
    public void FullEndpoint_monitor_reads_capture_and_does_not_feed_the_send_mixer()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.SetMonitor(true, MonitorMode.FullEndpoint));
        env.MonitorCapture.Fake.Push?.Invoke(Filled(Period, 0.7f));

        var monitored = env.MonitorOutput.Fake.Pump(Period);
        Assert.All(monitored[..Period], value => Assert.Equal(0.7f, value, 4));

        var sent = env.Output.Fake.Pump(Period); // structural no-loopback: nothing sent
        Assert.All(sent, value => Assert.Equal(0f, value));
        env.Dispose();
    }

    [Fact]
    public void Preview_finish_resumes_monitor_only_when_untouched()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.SetMonitor(true, MonitorMode.ClipOnly));
        env.MonitorOutput.Fake.Pump(Period);

        Accept(env.Session.PreviewClip("clip.wav"));
        Assert.True(env.Session.Status().MonitorSuspendedForPreview);
        env.MonitorOutput.Fake.Pump(4410); // drain the whole audition to its natural end
        Assert.True(WaitUntil(() => env.Session.Status().Clip is null));

        var status = env.Session.Status();
        Assert.False(status.MonitorSuspendedForPreview);
        Assert.True(status.Switches.MonitorEnabled); // automatic resume after normal completion
        env.Dispose();
    }

    [Fact]
    public void StopClip_cancels_automatic_monitor_resume()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.SetMonitor(true, MonitorMode.ClipOnly));

        Accept(env.Session.PreviewClip("clip.wav"));
        env.MonitorOutput.Fake.Pump(Period); // audition partially consumed

        Accept(env.Session.StopClip());

        var status = env.Session.Status();
        Assert.False(status.Switches.MonitorEnabled); // resume token voided
        Assert.Null(status.Clip);
        env.Dispose();
    }

    [Fact]
    public void App_requires_explicit_binding_before_enable()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableExtras(true));

        var result = env.Session.EnableApp(true);
        Assert.False(result.Accepted);
        Assert.Equal("no-app-bound", result.Rejection);

        Accept(env.Session.BindApplication(new AppBinding(7, "player")));
        Accept(env.Session.EnableApp(true));
        Assert.True(env.Session.Status().Switches.AppSendEnabled);

        // rebinding while sending is refused (T08: explicit re-bind flow)
        Assert.False(env.Session.BindApplication(new AppBinding(9, "other")).Accepted);
        env.Dispose();
    }

    [Fact]
    public void Decode_failure_rejects_play_without_touching_safety_state()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        Accept(env.Session.EnableExtras(true));
        env.Decoder.Generator = _ => throw new InvalidDataException("corrupt");

        var result = env.Session.PlayClip("bad.wav");
        Assert.False(result.Accepted);
        Assert.StartsWith("decode-failed", result.Rejection);

        var status = env.Session.Status();
        Assert.Null(status.Clip);
        Assert.True(status.Switches.ExtrasSendEnabled); // still armed, just no clip
        env.Dispose();
    }

    // ---- helpers ----

    private static void Accept(CommandResult result) => Assert.True(result.Accepted, result.Rejection);

    private static float[] Filled(int frames, float value)
    {
        var samples = new float[frames];
        Array.Fill(samples, value);
        return samples;
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    [Fact]
    public void Enable_mic_with_preroll_available_flips_switch_immediately()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();
        env.Mic.OnStart = () => env.Mic.Push(new float[EngineFormat.TimeToFrames(TimeSpan.FromMilliseconds(50))]);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Accept(env.Session.EnableMic(true));
        watch.Stop();

        var status = env.Session.Status();
        Assert.True(status.Switches.MicSendEnabled);
        Assert.True(watch.ElapsedMilliseconds < 100, $"enable took {watch.ElapsedMilliseconds}ms; pre-roll wait should not time out");
        env.Dispose();
    }

    [Fact]
    public void Enable_mic_without_frames_still_succeeds_after_bounded_wait()
    {
        var env = SessionEnv.Create();
        env.Session.AttachOutput();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Accept(env.Session.EnableMic(true));
        watch.Stop();

        var status = env.Session.Status();
        Assert.True(status.Switches.MicSendEnabled); // timeout path: silence, not failure
        Assert.True(watch.ElapsedMilliseconds >= 20, "bounded pre-roll wait should have run");
        env.Dispose();
    }

    /// <summary>Wires a session with fake adapters; each test reads the instances it needs.</summary>
    private sealed class SessionEnv : IDisposable
    {
        public readonly AudioSession Session;
        public readonly FakeDecoder Decoder;
        public readonly FakeSourceAdapter Mic;
        public readonly FakeSourceAdapter App;
        public readonly OutputSlot Output;
        public readonly MonitorOutputSlot MonitorOutput;
        public readonly MonitorCaptureSlot MonitorCapture;

        private SessionEnv(
            AudioSession session,
            FakeDecoder decoder,
            FakeSourceAdapter mic,
            FakeSourceAdapter app,
            OutputSlot output,
            MonitorOutputSlot monitorOutput,
            MonitorCaptureSlot monitorCapture)
        {
            Session = session;
            Decoder = decoder;
            Mic = mic;
            App = app;
            Output = output;
            MonitorOutput = monitorOutput;
            MonitorCapture = monitorCapture;
        }

        public static SessionEnv Create()
        {
            var decoder = new FakeDecoder();
            var mic = new FakeSourceAdapter();
            var app = new FakeSourceAdapter();
            var output = new OutputSlot();
            var monitorOutput = new MonitorOutputSlot();
            var monitorCapture = new MonitorCaptureSlot();

            var session = new AudioSession(new AudioSessionOptions
            {
                ClipDecoder = decoder,
                MicAdapterFactory = channel =>
                {
                    mic.Channel = channel;
                    return mic;
                },
                AppAdapterFactory = (channel, _) =>
                {
                    app.Channel = channel;
                    return app;
                },
                OutputFactory = output.Create,
                MonitorOutputFactory = monitorOutput.Create,
                MonitorCaptureFactory = router => monitorCapture.Create(),
                SourcePrerollTimeoutMs = 20,
                AdaptiveRate = false,
            });
            return new SessionEnv(session, decoder, mic, app, output, monitorOutput, monitorCapture);
        }

        public void Dispose() => Session.Dispose();
    }

    public sealed class OutputSlot
    {
        public FakeVirtualOutput Fake { get; private set; } = new();

        public FakeVirtualOutput Create()
        {
            Fake = new FakeVirtualOutput();
            return Fake;
        }
    }

    public sealed class MonitorOutputSlot
    {
        public FakeMonitorOutput Fake { get; private set; } = new();

        public FakeMonitorOutput Create()
        {
            Fake = new FakeMonitorOutput();
            return Fake;
        }
    }

    public sealed class MonitorCaptureSlot
    {
        public FakeMonitorCapture Fake { get; private set; } = new();

        public FakeMonitorCapture Create()
        {
            Fake = new FakeMonitorCapture();
            return Fake;
        }
    }
}
