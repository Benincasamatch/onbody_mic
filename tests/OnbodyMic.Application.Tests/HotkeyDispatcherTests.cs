namespace OnbodyMic.Application.Tests;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using Xunit;

/// <summary>
/// P2 hotkey semantics: single dispatch through the session, reject-never-
/// queue when extras is closed (docs/01 §5.4), editing suspension with the
/// emergency exception, and binding validation.
/// </summary>
public sealed class HotkeyDispatcherTests : IDisposable
{
    private const int Period = 441;

    private readonly FakeDecoder _decoder = new();
    private readonly FakeSampleStore _store = new();
    private readonly Dictionary<string, ClipResolution> _resolutions = [];
    private readonly FakeSourceAdapter _mic = new();
    private readonly FakeSourceAdapter _app = new();
    private readonly OutputSlot _output = new();
    private readonly MonitorOutputSlot _monitorOutput = new();
    private readonly AudioSession _session;
    private readonly FakeHotkeyService _hotkeys = new();
    private readonly HotkeyDispatcher _dispatcher;
    private readonly List<SessionNotice> _notices = [];

    public HotkeyDispatcherTests()
    {
        _session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = _decoder,
            ClipBoundaryFadeFrames = 0, // exact-value assertions
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
            OutputFactory = _output.Create,
            MonitorOutputFactory = _monitorOutput.Create,
            SourcePrerollTimeoutMs = 20,
            AdaptiveRate = false,
        });
        _dispatcher = new HotkeyDispatcher(_session, _hotkeys, id => _resolutions.TryGetValue(id, out var resolution) ? resolution : ClipResolution.Unknown);
        _dispatcher.Noticed += notice => _notices.Add(notice);
        Accept(_session.AttachOutput());
        _resolutions["clip-a"] = new ClipResolution(true, true, "assets/clip-a.wav", 1f);
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _session.Dispose();
    }

    private static void Accept(CommandResult result) => Assert.True(result.Accepted, result.Rejection);

    private static HotkeyBinding Play(string clipId, string gesture = "Ctrl+Alt+F7") => Parse(gesture) is { } g
        ? new HotkeyBinding(HotkeyAction.PlayClip, clipId, g)
        : throw new InvalidOperationException("bad gesture");

    private static HotkeyGesture? Parse(string gesture) => HotkeyGesture.TryParse(gesture, out var parsed) ? parsed : null;

    [Fact]
    public void Play_hotkey_with_extras_closed_is_rejected_without_decoding_or_queueing()
    {
        _hotkeys.Fire(Play("clip-a"));

        Assert.Null(_session.Status().Clip);
        Assert.Equal(0, _decoder.Calls);
        Assert.Contains(_notices, n => n.Message.Contains("extras-disabled-or-emergency"));
        // nothing was queued: enabling extras later must not suddenly play
        Accept(_session.EnableExtras(true));
        Assert.Null(_session.Status().Clip);
    }

    [Fact]
    public void Play_hotkey_with_extras_open_plays_the_clip_through_the_send_gate()
    {
        Accept(_session.EnableExtras(true));

        _hotkeys.Fire(Play("clip-a"));

        var status = _session.Status();
        Assert.NotNull(status.Clip);
        Assert.Equal("clip-a", status.Clip!.ClipId);
        var mixed = _output.Fake.Pump(Period);
        Assert.All(mixed, value => Assert.Equal(0.5f, value, 4));
    }

    [Fact]
    public void Rapid_retrigger_replaces_the_running_clip_in_order()
    {
        Accept(_session.EnableExtras(true));

        _hotkeys.Fire(Play("clip-a"));
        _hotkeys.Fire(Play("clip-a"));

        Assert.Equal(1, _session.Metrics().Clip.ReplacedPlaybacks);
        Assert.NotNull(_session.Status().Clip);
    }

    [Fact]
    public void Stop_hotkey_stops_only_the_clip_extras_stays_on()
    {
        Accept(_session.EnableExtras(true));
        _hotkeys.Fire(Play("clip-a"));
        _hotkeys.Fire(new HotkeyBinding(HotkeyAction.StopClip, null, Parse("Ctrl+Alt+F8")!.Value));

        var status = _session.Status();
        Assert.Null(status.Clip); // T12: only the clip stops
        Assert.True(status.Switches.ExtrasSendEnabled);
    }

    [Fact]
    public void Toggle_hotkeys_flip_their_own_switch_only()
    {
        var extras = new HotkeyBinding(HotkeyAction.ToggleExtrasSend, null, Parse("Ctrl+Alt+F9")!.Value);
        _hotkeys.Fire(extras);
        Assert.True(_session.Status().Switches.ExtrasSendEnabled);

        _hotkeys.Fire(extras);
        Assert.False(_session.Status().Switches.ExtrasSendEnabled);

        var mic = new HotkeyBinding(HotkeyAction.ToggleMicSend, null, Parse("Ctrl+Alt+F10")!.Value);
        _hotkeys.Fire(mic);
        Assert.True(_session.Status().Switches.MicSendEnabled);
        Assert.False(_session.Status().Switches.ExtrasSendEnabled); // mic did not drag extras along
    }

    [Fact]
    public void Emergency_hotkey_still_fires_while_suspended_and_play_is_ignored()
    {
        Accept(_session.EnableExtras(true));
        _dispatcher.Suspend();

        _hotkeys.Fire(Play("clip-a"));
        Assert.Null(_session.Status().Clip); // suspended: playback trigger ignored
        Assert.Contains(_notices, n => n.Message.Contains("editing"));

        var emergency = new HotkeyBinding(HotkeyAction.EmergencyMute, null, Parse("Ctrl+Alt+F11")!.Value);
        _hotkeys.Fire(emergency); // emergency is exempt from suspension (docs/01 §5.4)
        Assert.Equal(SessionPhase.EmergencyMuted, _session.Status().Phase);

        _dispatcher.Resume();
        _hotkeys.Fire(Play("clip-a")); // still rejected: latch outlives suspension
        Assert.Null(_session.Status().Clip);
    }

    [Fact]
    public void Bind_validates_clip_targets_and_gesture_conflicts()
    {
        Assert.False(_dispatcher.Bind(new HotkeyBinding(HotkeyAction.PlayClip, "clip-missing", Parse("Ctrl+Alt+1")!.Value)).Accepted);

        _resolutions["clip-a"] = _resolutions["clip-a"] with { Available = false };
        Assert.False(_dispatcher.Bind(Play("clip-a", "Ctrl+Alt+2")).Accepted);

        _resolutions["clip-a"] = _resolutions["clip-a"] with { Available = true };
        Accept(_dispatcher.Bind(Play("clip-a", "Ctrl+Alt+3")));

        // same gesture for another target conflicts; rebinding the same target replaces
        Assert.False(_dispatcher.Bind(Play("clip-b-unknown", "Ctrl+Alt+3")).Accepted);
        Accept(_dispatcher.Bind(Play("clip-a", "Ctrl+Alt+4")));
        var binding = _dispatcher.Active.Single(b => b.TargetId == "clip-a");
        Assert.Equal("Ctrl+Alt+4", binding.Gesture.ToString());
    }

    [Fact]
    public void Unbind_removes_the_registration()
    {
        Accept(_dispatcher.Bind(Play("clip-a", "Ctrl+Alt+3")));
        Accept(_dispatcher.Unbind(HotkeyAction.PlayClip, "clip-a"));
        Assert.DoesNotContain(_dispatcher.Active, b => b.TargetId == "clip-a");
    }

    [Fact]
    public void Dangling_clip_binding_fired_is_refused_not_decoded_as_a_path()
    {
        Accept(_session.EnableExtras(true));
        _dispatcher.Bind(Play("clip-a", "Ctrl+Alt+3"));
        _resolutions.Remove("clip-a"); // the library entry vanished after binding

        _hotkeys.Fire(Play("clip-a", "Ctrl+Alt+3"));

        Assert.Null(_session.Status().Clip);
        Assert.Equal(0, _decoder.Calls);
        Assert.Contains(_notices, n => n.Message.Contains("unavailable"));
    }

    [Fact]
    public void Monitor_toggle_hotkey_switches_monitoring()
    {
        var monitor = new HotkeyBinding(HotkeyAction.ToggleMonitor, null, Parse("Ctrl+Alt+F12")!.Value);
        _hotkeys.Fire(monitor);
        Assert.True(_session.Status().Switches.MonitorEnabled);

        _hotkeys.Fire(monitor);
        Assert.False(_session.Status().Switches.MonitorEnabled);
    }
}

sealed class OutputSlot
{
    public FakeVirtualOutput Fake { get; private set; } = new();

    public FakeVirtualOutput Create()
    {
        Fake = new();
        return Fake;
    }
}

sealed class MonitorOutputSlot
{
    public FakeMonitorOutput Fake { get; private set; } = new();

    public FakeMonitorOutput Create()
    {
        Fake = new();
        return Fake;
    }
}
