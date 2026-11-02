namespace OnbodyMic.Application.Tests;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using Xunit;

public sealed class SessionLifecycleRegressionTests
{
    [Fact]
    public async Task Slow_hotkey_play_does_not_block_emergency_on_the_same_pump()
    {
        using var decoding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoder = new FakeDecoder { Generator = _ =>
        {
            decoding.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new float[2048];
        }};
        using var rig = new Rig(decoder);
        rig.Session.AttachOutput();
        rig.Session.EnableExtras(true);
        using var hotkeys = new FakeHotkeyService();
        using var dispatcher = new HotkeyDispatcher(rig.Session, hotkeys);
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+F7", out var gesture));
        var pump = Task.Run(() =>
        {
            hotkeys.Fire(new HotkeyBinding(HotkeyAction.PlayClip, "slow", gesture));
            Assert.True(decoding.Wait(TimeSpan.FromSeconds(3)));
            hotkeys.Fire(new HotkeyBinding(HotkeyAction.EmergencyMute, null, gesture));
        });
        try
        {
            Assert.Same(pump, await Task.WhenAny(pump, Task.Delay(2000)));
            await pump;
            Assert.Equal(SessionPhase.EmergencyMuted, rig.Session.Status().Phase);
        }
        finally { release.Set(); }
        await dispatcher.LastPlayback;
        Assert.Null(rig.Session.Status().Clip);
    }

    [Fact]
    public async Task Rapid_uncached_requests_decode_serially_and_only_the_latest_starts()
    {
        using var decoding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoder = new FakeDecoder { Generator = path =>
        {
            if (path == "first")
            {
                decoding.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return new float[2048];
        }};
        using var rig = new Rig(decoder);
        rig.Session.AttachOutput();
        rig.Session.EnableExtras(true);
        var first = rig.Session.PlayClipAsync("first");
        Task<CommandResult>? second = null;
        Task<CommandResult>? last = null;
        try
        {
            Assert.True(decoding.Wait(TimeSpan.FromSeconds(3)));
            second = rig.Session.PlayClipAsync("superseded");
            last = rig.Session.PlayClipAsync("last");
        }
        finally { release.Set(); }
        Assert.False((await first).Accepted);
        Assert.False((await second!).Accepted);
        Assert.True((await last!).Accepted);
        Assert.Equal(2, decoder.Calls);
        Assert.Equal("last", rig.Session.Status().Clip?.ClipId);
    }

    [Fact]
    public async Task Applying_a_preset_cancels_pending_decode_even_after_send_reopens()
    {
        using var decoding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoder = new FakeDecoder { Generator = _ =>
        {
            decoding.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new float[2048];
        }};
        using var rig = new Rig(decoder);
        rig.Session.AttachOutput();
        rig.Session.EnableExtras(true);
        var play = rig.Session.PlayClipAsync("old");
        try
        {
            Assert.True(decoding.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(rig.Session.ApplyPreset(Preset).Accepted);
            Assert.True(rig.Session.EnableExtras(true).Accepted);
        }
        finally { release.Set(); }
        Assert.Equal("clip-start-cancelled", (await play).Rejection);
        Assert.Null(rig.Session.Status().Clip);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Slow_decode_does_not_block_safety_commands_or_start_after_cancellation(bool emergency)
    {
        using var decoding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoder = new FakeDecoder { Generator = _ =>
        {
            decoding.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Enumerable.Repeat(.5f, 2048).ToArray();
        }};
        using var rig = new Rig(decoder);
        rig.Session.AttachOutput();
        rig.Session.EnableExtras(true);
        var playback = Task.Run(() => rig.Session.PlayClip("slow"));
        Task<CommandResult>? stop = null;
        try
        {
            Assert.True(decoding.Wait(TimeSpan.FromSeconds(3)));
            stop = Task.Run(() => emergency ? rig.Session.EmergencyMute() : rig.Session.StopClip());
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(1500)));
            Assert.True((await stop).Accepted);
            Assert.False(rig.Session.Status().ClipLoading);
        }
        finally { release.Set(); }
        Assert.False((await playback).Accepted);
        Assert.Null(rig.Session.Status().Clip);
        Assert.All(rig.Outputs.Last().Pump(32), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Emergency_mute_discards_old_material_before_the_next_send()
    {
        var decoder = new FakeDecoder { Generator = path => path == "silence"
            ? new float[2048] : Enumerable.Repeat(.8f, 2048).ToArray() };
        using var rig = new Rig(decoder, fadeFrames: ClipPlayer.DefaultFadeFrames);
        rig.Session.AttachOutput();
        rig.Session.EnableExtras(true);
        rig.Session.PlayClip("old");
        Assert.Contains(rig.Outputs.Last().Pump(256), sample => sample > 0);
        rig.Session.EmergencyMute();
        rig.Session.UnlockEmergency();
        rig.Session.EnableExtras(true);
        rig.Session.PlayClip("silence");
        Assert.All(rig.Outputs.Last().Pump(256), sample => Assert.Equal(0, sample));
    }

    private static PresetDeviceConfig Devices => new("mic", "render", "capture", "headphones", null);
    private static PresetConfig Preset => new("next", "Next", Devices, 1, 1, 1,
        new MonitorPreferences(MonitorMode.ClipOnly, 1), [], false, null);

    [Fact]
    public void Preview_from_startup_is_audible_without_enabling_monitor_or_send()
    {
        using var rig = new Rig();
        Assert.True(rig.Session.PreviewClip("preview").Accepted);
        Assert.NotNull(rig.Monitors.LastOrDefault());
        Assert.Contains(rig.Monitors.Last().Pump(32), sample => sample > 0);
        var levels = rig.Session.TakeLevels();
        Assert.True(levels.Clip > 0);
        Assert.Equal(0, levels.Mic);
        Assert.Equal(0, levels.Mix);
        Assert.Equal(0, rig.Session.TakeLevels().Clip);
        Assert.Equal(SessionSwitches.AllOff, rig.Session.Status().Switches);
        rig.Session.AttachOutput();
        Assert.All(rig.Outputs.Last().Pump(32), sample => Assert.Equal(0, sample));
        rig.Session.StopClip();
        Assert.True(rig.Monitors.Last().Disposed);
    }

    [Fact]
    public void Preview_reports_missing_headphones_instead_of_a_silent_success()
    {
        using var rig = new Rig(monitorFails: true);
        Assert.False(rig.Session.PreviewClip("preview").Accepted);
        Assert.Null(rig.Session.Status().Clip);
        Assert.Equal(SessionSwitches.AllOff, rig.Session.Status().Switches);
    }

    [Fact]
    public async Task Old_completion_must_not_end_the_replacement_clip()
    {
        using var decoding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoder = new FakeDecoder
        {
            Generator = path =>
            {
                if (path == "second")
                {
                    decoding.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                }
                return FakeDecoder.CreateTone(0.25f, 64);
            },
        };
        using var rig = new Rig(decoder);
        rig.Session.AttachOutput();
        rig.Session.EnableExtras(true);
        rig.Session.PlayClip("first");
        var replacement = Task.Run(() => rig.Session.PlayClip("second"));
        try
        {
            Assert.True(decoding.Wait(TimeSpan.FromSeconds(5)));
            rig.Outputs.Last().Pump(64); // completion queues behind the decoding command
        }
        finally { release.Set(); }
        Assert.True((await replacement).Accepted);
        Assert.Equal("second", rig.Session.Status().Clip?.ClipId);
        Assert.Contains(rig.Outputs.Last().Pump(32), sample => sample > 0);
    }

    [Fact]
    public void Fault_from_replaced_output_must_not_kill_the_new_output()
    {
        using var rig = new Rig();
        rig.Session.AttachOutput();
        var old = rig.Outputs.Last();
        rig.Session.ApplyPreset(Preset);
        old.RaiseFault();
        Assert.False(rig.Session.Status().OutputLost);
        Assert.False(rig.Outputs.Last().Disposed);
    }

    [Fact]
    public void Applying_a_preset_revokes_the_previous_live_process_binding()
    {
        using var rig = new Rig();
        rig.Session.BindApplication(new AppBinding(42, "old app"));
        rig.Session.ApplyPreset(Preset);
        Assert.Null(rig.Session.Status().AppBinding);
        rig.Session.EnableExtras(true);
        Assert.Equal("no-app-bound", rig.Session.EnableApp(true).Rejection);
    }

    [Fact]
    public void Validation_failure_returns_a_rejection_and_leaves_audio_safely_muted()
    {
        using var rig = new Rig(validator: _ => throw new IOException("device scan failed"));
        rig.Session.AttachOutput();
        rig.Session.EnableExtras(true);
        var result = rig.Session.ApplyPreset(Preset);
        Assert.False(result.Accepted);
        Assert.Equal(SessionSwitches.AllOff, rig.Session.Status().Switches);
        Assert.All(rig.Outputs.Last().Pump(32), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Clearing_configuration_disposes_every_path_and_revokes_the_live_binding()
    {
        var mic = new FakeSourceAdapter();
        var app = new FakeSourceAdapter();
        var output = new FakeVirtualOutput();
        var monitor = new FakeMonitorOutput();
        var capture = new FakeMonitorCapture();
        using var session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = new FakeDecoder(),
            InitialDevices = Devices,
            AdaptiveRate = false,
            SourcePrerollTimeoutMs = 0,
            MicAdapterFactory = (_, channel) => { mic.Channel = channel; return mic; },
            AppAdapterFactory = (channel, _) => { app.Channel = channel; return app; },
            OutputFactory = _ => output,
            MonitorOutputFactory = _ => monitor,
            MonitorCaptureFactory = (_, _) => capture,
        });
        Assert.True(session.ApplyPreset(Preset).Accepted);
        Assert.True(session.EnableMic(true).Accepted);
        Assert.True(session.EnableExtras(true).Accepted);
        Assert.True(session.BindApplication(new AppBinding(42, "selected process")).Accepted);
        Assert.True(session.EnableApp(true).Accepted);
        Assert.True(session.SetMonitor(true, MonitorMode.FullEndpoint).Accepted);
        Assert.True(session.PreviewClip("preview").Accepted);

        Assert.True(session.ClearConfiguration().Accepted);

        var status = session.Status();
        Assert.Equal(SessionPhase.Unconfigured, status.Phase);
        Assert.Equal(SessionSwitches.AllOff, status.Switches);
        Assert.Null(status.Devices);
        Assert.Null(status.LastPresetId);
        Assert.Null(status.AppBinding);
        Assert.Null(status.Clip);
        Assert.False(status.MonitorSuspendedForPreview);
        Assert.False(status.DevicesValidated);
        Assert.True(mic.Disposed);
        Assert.True(app.Disposed);
        Assert.True(output.Disposed);
        Assert.True(monitor.Disposed);
        Assert.True(capture.Disposed);
        Assert.Equal("no-device-config", session.AttachOutput().Rejection);
        Assert.Equal("no-device-config", session.EnableMic(true).Rejection);
    }

    [Fact]
    public async Task Clearing_configuration_cancels_a_decode_before_another_configuration_can_send()
    {
        using var decoding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoder = new FakeDecoder { Generator = _ =>
        {
            decoding.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return FakeDecoder.CreateTone(.5f, 2048);
        }};
        using var rig = new Rig(decoder);
        rig.Session.ApplyPreset(Preset);
        rig.Session.EnableExtras(true);
        var previousOutput = rig.Outputs.Last();
        var playback = rig.Session.PlayClipAsync("deleted-configuration");
        try
        {
            Assert.True(decoding.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(rig.Session.ClearConfiguration().Accepted);
            Assert.True(previousOutput.Disposed);
            Assert.Null(rig.Session.Status().Devices);
            Assert.Null(rig.Session.Status().LastPresetId);
            Assert.True(rig.Session.ApplyPreset(Preset with { PresetId = "replacement" }).Accepted);
            Assert.True(rig.Session.EnableExtras(true).Accepted);
            previousOutput.RaiseFault();
            Assert.False(rig.Session.Status().OutputLost);
        }
        finally { release.Set(); }
        Assert.Equal("clip-start-cancelled", (await playback).Rejection);
        Assert.Null(rig.Session.Status().Clip);
        Assert.All(rig.Outputs.Last().Pump(32), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Late_adapter_callbacks_after_disposal_are_harmless()
    {
        var rig = new Rig();
        rig.Session.AttachOutput();
        rig.Session.Dispose();
        Assert.Null(Record.Exception(() => rig.Outputs.Last().RaiseFault()));
    }

    private sealed class Rig : IDisposable
    {
        public List<FakeVirtualOutput> Outputs { get; } = [];
        public List<FakeMonitorOutput> Monitors { get; } = [];
        public AudioSession Session { get; }

        public Rig(IClipDecoder? decoder = null, bool monitorFails = false,
            Func<PresetDeviceConfig, IReadOnlyList<RouteIssue>>? validator = null, int fadeFrames = 0)
        {
            Session = new AudioSession(new AudioSessionOptions
            {
                ClipDecoder = decoder ?? new FakeDecoder(),
                ClipBoundaryFadeFrames = fadeFrames,
                InitialDevices = Devices,
                DeviceValidator = validator,
                AdaptiveRate = false,
                SourcePrerollTimeoutMs = 0,
                MicAdapterFactory = (_, channel) => new FakeSourceAdapter { Channel = channel },
                AppAdapterFactory = (channel, _) => new FakeSourceAdapter { Channel = channel },
                OutputFactory = _ => { var output = new FakeVirtualOutput(); Outputs.Add(output); return output; },
                MonitorOutputFactory = _ =>
                {
                    if (monitorFails) throw new IOException("headphones unavailable");
                    var output = new FakeMonitorOutput(); Monitors.Add(output); return output;
                },
                MonitorCaptureFactory = (_, _) => new FakeMonitorCapture(),
            });
        }

        public void Dispose() => Session.Dispose();
    }
}
