namespace OnbodyMic.Domain.Tests;

using Xunit;

public sealed class SessionEngineTests
{
    [Fact]
    public void Startup_is_fully_muted()
    {
        var engine = new SessionEngine();

        Assert.Equal(SessionPhase.Unconfigured, engine.Phase);
        Assert.Equal(SessionSwitches.AllOff, engine.Switches);
        Assert.Null(engine.Clip);
    }

    [Fact]
    public void EnableApp_is_rejected_while_extras_off()
    {
        var engine = Ready();

        var result = engine.SetAppSend(true);

        Assert.False(result.Accepted);
        Assert.False(engine.Switches.AppSendEnabled);
    }

    [Fact]
    public void DisableExtras_stops_app_and_send_clip_but_keeps_mic()
    {
        var engine = Ready();
        engine.SetMicSend(true);
        engine.SetExtrasSend(true);
        engine.SetAppSend(true);
        engine.PlayClip("airhorn");
        var micTicket = Authorize(engine, "mic", AudioKind.Microphone);

        engine.SetExtrasSend(false);

        Assert.False(engine.Switches.ExtrasSendEnabled);
        Assert.False(engine.Switches.AppSendEnabled);
        Assert.Null(engine.Clip);
        Assert.True(engine.Switches.MicSendEnabled);
        Assert.True(engine.Gate.Decide(micTicket!, engine.Snapshot()).Allowed);
    }

    [Fact]
    public void Emergency_latches_and_unlock_keeps_everything_off()
    {
        var engine = Ready();
        engine.SetMicSend(true);
        engine.SetExtrasSend(true);

        engine.EmergencyMute();
        Assert.Equal(SessionPhase.EmergencyMuted, engine.Phase);
        Assert.Equal(SessionSwitches.AllOff, engine.Switches);

        var unlock = engine.UnlockEmergency();
        Assert.True(unlock.Accepted);
        Assert.Equal(SessionSwitches.AllOff, engine.Switches);
        Assert.Equal(SessionPhase.ReadyMuted, engine.Phase);
    }

    [Fact]
    public void Emergency_rejects_play_and_preview()
    {
        var engine = Ready();
        engine.SetExtrasSend(true);
        engine.EmergencyMute();

        Assert.False(engine.PlayClip("a").Accepted);
        Assert.False(engine.PreviewClip("a").Accepted);
    }

    [Fact]
    public void New_clip_replaces_old_and_stale_frames_are_denied()
    {
        var engine = Ready();
        engine.SetExtrasSend(true);
        engine.PlayClip("first");
        var oldTicket = ClipTicket(engine);

        engine.PlayClip("second");
        var snapshot = engine.Snapshot();

        Assert.Equal("second", engine.Clip!.ClipId);
        Assert.Equal(SendDenialReason.StalePlaybackEpoch, engine.Gate.Decide(oldTicket, snapshot).Reason);
        Assert.True(engine.Gate.Decide(ClipTicket(engine), snapshot).Allowed);
    }

    [Fact]
    public void Same_clip_replays_from_start()
    {
        var engine = Ready();
        engine.SetExtrasSend(true);
        engine.PlayClip("airhorn");
        var first = ClipTicket(engine);

        engine.PlayClip("airhorn");

        Assert.Equal("airhorn", engine.Clip!.ClipId);
        Assert.Equal(SendDenialReason.StalePlaybackEpoch, engine.Gate.Decide(first, engine.Snapshot()).Reason);
    }

    [Fact]
    public void Preview_never_reaches_the_send_gate()
    {
        var engine = Ready();
        engine.SetExtrasSend(true);
        engine.PreviewClip("airhorn");

        var snapshot = engine.Snapshot();

        Assert.Equal(ClipPlaybackMode.Preview, engine.Clip!.Mode);
        Assert.Equal(SendDenialReason.ClipNotSending, engine.Gate.Decide(ClipTicket(engine), snapshot).Reason);
    }

    [Fact]
    public void Preview_pauses_monitor_and_resumes_after_normal_completion()
    {
        var engine = Ready();
        engine.SetMonitor(true, MonitorMode.ClipOnly);
        engine.PreviewClip("airhorn");

        Assert.False(engine.Switches.MonitorEnabled);
        Assert.True(engine.MonitorSuspendedForPreview);

        engine.CompleteClip();
        Assert.True(engine.Switches.MonitorEnabled);
        Assert.False(engine.MonitorSuspendedForPreview);
    }

    [Fact]
    public void StopClip_cancels_automatic_monitor_resume()
    {
        var engine = Ready();
        engine.SetMonitor(true, MonitorMode.ClipOnly);
        engine.PreviewClip("airhorn");
        engine.StopClip();

        Assert.False(engine.Switches.MonitorEnabled);
    }

    [Fact]
    public void SwitchPreset_invalidates_old_session_frames_and_updates_last_preset()
    {
        var engine = Ready();
        engine.SetMicSend(true);
        var micTicket = Authorize(engine, "mic", AudioKind.Microphone);

        engine.SwitchPreset("preset-game");

        Assert.Equal("preset-game", engine.LastPresetId);
        Assert.Equal(SessionSwitches.AllOff, engine.Switches);
        Assert.Equal(SendDenialReason.StaleSessionEpoch, engine.Gate.Decide(micTicket!, engine.Snapshot()).Reason);
    }

    [Fact]
    public void SwitchPreset_rejects_blank_id_without_side_effects()
    {
        var engine = Ready();
        engine.SetMicSend(true);

        var result = engine.SwitchPreset(" ");

        Assert.False(result.Accepted);
        Assert.Equal("preset-default", engine.LastPresetId);
        Assert.True(engine.Switches.MicSendEnabled);
    }

    [Fact]
    public void SourceFailure_bumps_only_that_source()
    {
        var engine = Ready();
        engine.SetMicSend(true);
        engine.SetExtrasSend(true);
        var micTicket = Authorize(engine, "mic", AudioKind.Microphone);
        Authorize(engine, "app", AudioKind.Application);
        var appTicket = engine.CreateTicket("app", AudioKind.Application);

        engine.SourceFailed("app");

        Assert.Null(engine.CreateTicket("app", AudioKind.Application));
        Assert.NotNull(engine.CreateTicket("mic", AudioKind.Microphone));
        Assert.True(engine.Gate.Decide(micTicket!, engine.Snapshot()).Allowed);
        Assert.Null(appTicket is null ? null : engine.CreateTicket("app", AudioKind.Application));
    }

    [Fact]
    public void EmergencyMute_cuts_off_existing_tickets()
    {
        var engine = Ready();
        engine.SetMicSend(true);
        var micTicket = Authorize(engine, "mic", AudioKind.Microphone);

        engine.EmergencyMute();

        Assert.Equal(SendDenialReason.EmergencyLatched, engine.Gate.Decide(micTicket!, engine.Snapshot()).Reason);
    }

    [Fact]
    public void OutputFailed_closes_all_send_but_keeps_monitor_and_latch()
    {
        var engine = Ready();
        engine.SetMicSend(true);
        engine.SetExtrasSend(true);
        engine.SetAppSend(true);
        engine.SetMonitor(true, MonitorMode.FullEndpoint);
        var micTicket = Authorize(engine, "mic", AudioKind.Microphone);
        var appTicket = Authorize(engine, "app", AudioKind.Application);

        engine.OutputFailed();

        Assert.Equal(SessionPhase.OutputLost, engine.Phase);
        Assert.False(engine.Switches.MicSendEnabled);
        Assert.False(engine.Switches.ExtrasSendEnabled);
        Assert.False(engine.Switches.AppSendEnabled);
        // monitoring path survives a virtual-output failure
        Assert.True(engine.Switches.MonitorEnabled);
        Assert.Equal(SendDenialReason.StaleSessionEpoch, engine.Gate.Decide(micTicket!, engine.Snapshot()).Reason);
        Assert.Equal(SendDenialReason.StaleSessionEpoch, engine.Gate.Decide(appTicket!, engine.Snapshot()).Reason);

        engine.ValidateDevices();
        Assert.NotEqual(SessionPhase.OutputLost, engine.Phase);
    }

    private static SessionEngine Ready()
    {
        var engine = new SessionEngine();
        engine.SwitchPreset("preset-default");
        engine.ValidateDevices();
        return engine;
    }

    private static SourceTicket Authorize(SessionEngine engine, string id, AudioKind kind)
    {
        var accepted = engine.AuthorizeSource(id, kind);
        Assert.True(accepted.Accepted);
        return engine.CreateTicket(id, kind)!;
    }

    private static SourceTicket ClipTicket(SessionEngine engine) =>
        new("clip", AudioKind.Clip, engine.SessionEpoch, engine.Clip!.PlaybackEpoch);
}
