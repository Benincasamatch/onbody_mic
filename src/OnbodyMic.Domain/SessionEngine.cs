namespace OnbodyMic.Domain;

/// Platform-neutral session state machine implementing the safety baseline
/// from docs/06: startup silent, explicit enables, latching emergency mute,
/// epoch-guarded routing, and single-clip replacement.
public sealed class SessionEngine
{
    private long _nextPlaybackEpoch = 1;
    private readonly Dictionary<string, SourceState> _sources = [];
    private bool _monitorResumeEligible;

    private sealed record SourceState(long Epoch, bool Authorized);

    public SessionEngine()
    {
        Gate = new SendGate();
        Switches = SessionSwitches.AllOff;
    }

    public SendGate Gate { get; }

    public long SessionEpoch { get; private set; }

    public long MonitorEpoch { get; private set; }

    public SessionSwitches Switches { get; private set; }

    public MonitorMode MonitorMode { get; private set; } = MonitorMode.ClipOnly;

    public ActiveClip? Clip { get; private set; }

    public bool EmergencyLatched { get; private set; }

    public bool DevicesValidated { get; private set; }

    public string? LastPresetId { get; private set; }

    public bool MonitorSuspendedForPreview { get; private set; }

    public SessionPhase Phase
    {
        get
        {
            if (EmergencyLatched)
            {
                return SessionPhase.EmergencyMuted;
            }

            if (!DevicesValidated)
            {
                return LastPresetId is null ? SessionPhase.Unconfigured : SessionPhase.ConfiguredMuted;
            }

            return Switches.AnySendEnabled || Clip is { Mode: ClipPlaybackMode.Send }
                ? SessionPhase.Running
                : SessionPhase.ReadyMuted;
        }
    }

    public SendGateSnapshot Snapshot() => new(SessionEpoch, EmergencyLatched, Switches, Clip);

    public CommandResult ValidateDevices()
    {
        DevicesValidated = true;
        return CommandResult.Ok;
    }

    public CommandResult SetMicSend(bool enabled)
    {
        if (enabled && EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        Switches = Switches with { MicSendEnabled = enabled };
        return CommandResult.Ok;
    }

    public CommandResult SetExtrasSend(bool enabled)
    {
        if (enabled && EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        if (!enabled)
        {
            // Extras off stops the app sub-route and any outbound clip, but
            // never touches the microphone or a pure-local preview.
            Switches = Switches with { ExtrasSendEnabled = false, AppSendEnabled = false };
            if (Clip is { Mode: ClipPlaybackMode.Send })
            {
                Clip = null;
            }
        }
        else
        {
            Switches = Switches with { ExtrasSendEnabled = true };
        }

        return CommandResult.Ok;
    }

    public CommandResult SetAppSend(bool enabled)
    {
        if (enabled && EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        if (enabled && !Switches.ExtrasSendEnabled)
        {
            return CommandResult.Reject("extras-disabled");
        }

        Switches = Switches with { AppSendEnabled = enabled };
        return CommandResult.Ok;
    }

    public CommandResult SetMonitor(bool enabled, MonitorMode mode)
    {
        if (EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        if (MonitorSuspendedForPreview)
        {
            // An explicit monitor command during preview replaces the
            // suspended state and voids any automatic resume.
            MonitorSuspendedForPreview = false;
            _monitorResumeEligible = false;
        }

        MonitorMode = mode;
        Switches = Switches with { MonitorEnabled = enabled };
        MonitorEpoch++;
        return CommandResult.Ok;
    }

    public CommandResult AuthorizeSource(string sourceId, AudioKind kind)
    {
        if (EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        if (kind == AudioKind.Application && !Switches.ExtrasSendEnabled)
        {
            return CommandResult.Reject("extras-disabled");
        }

        _sources[sourceId] = new SourceState(_sources.TryGetValue(sourceId, out var existing) ? existing.Epoch : 1, true);
        return CommandResult.Ok;
    }

    public SourceTicket? CreateTicket(string sourceId, AudioKind kind)
    {
        if (!_sources.TryGetValue(sourceId, out var state) || !state.Authorized)
        {
            return null;
        }

        return new SourceTicket(sourceId, kind, SessionEpoch, state.Epoch);
    }

    /// Single-source failure bumps only that source's epoch; every other
    /// authorized source keeps producing valid tickets.
    public CommandResult SourceFailed(string sourceId)
    {
        if (_sources.TryGetValue(sourceId, out var state))
        {
            _sources[sourceId] = state with { Epoch = state.Epoch + 1, Authorized = false };
        }

        return CommandResult.Ok;
    }

    public CommandResult PlayClip(string clipId)
    {
        if (EmergencyLatched || !Switches.ExtrasSendEnabled)
        {
            return CommandResult.Reject("extras-disabled-or-emergency");
        }

        StartClip(clipId, ClipPlaybackMode.Send);
        return CommandResult.Ok;
    }

    public CommandResult PreviewClip(string clipId)
    {
        if (EmergencyLatched)
        {
            return CommandResult.Reject("emergency-latched");
        }

        StartClip(clipId, ClipPlaybackMode.Preview);
        if (Switches.MonitorEnabled)
        {
            MonitorSuspendedForPreview = true;
            _monitorResumeEligible = true;
            Switches = Switches with { MonitorEnabled = false };
        }
        else
        {
            MonitorSuspendedForPreview = false;
            _monitorResumeEligible = false;
        }

        return CommandResult.Ok;
    }

    public CommandResult CompleteClip()
    {
        if (Clip is not { } clip)
        {
            return CommandResult.Reject("no-clip");
        }

        if (clip.Mode == ClipPlaybackMode.Preview && MonitorSuspendedForPreview && _monitorResumeEligible)
        {
            Switches = Switches with { MonitorEnabled = true };
        }

        ClearClip();
        return CommandResult.Ok;
    }

    public CommandResult StopClip()
    {
        if (Clip is null)
        {
            return CommandResult.Reject("no-clip");
        }

        ClearClip();
        return CommandResult.Ok;
    }

    public CommandResult EmergencyMute()
    {
        EmergencyLatched = true;
        Switches = SessionSwitches.AllOff;
        ClearClip();
        InvalidateAllSources();
        SessionEpoch++;
        return CommandResult.Ok;
    }

    public CommandResult UnlockEmergency()
    {
        if (!EmergencyLatched)
        {
            return CommandResult.Reject("not-latched");
        }

        EmergencyLatched = false;
        Switches = SessionSwitches.AllOff;
        return CommandResult.Ok;
    }

    public CommandResult SwitchPreset(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return CommandResult.Reject("invalid-preset");
        }

        // Full teardown happens first; the id is only committed when the new
        // preset validates, so a failure leaves the old config but muted.
        Switches = SessionSwitches.AllOff;
        ClearClip();
        InvalidateAllSources();
        SessionEpoch++;
        DevicesValidated = false;
        LastPresetId = presetId;
        return CommandResult.Ok;
    }

    private void StartClip(string clipId, ClipPlaybackMode mode)
    {
        // One player, one slot: any new target replaces the old one and
        // invalidates an automatic monitor resume from a previous preview.
        if (Clip is { Mode: ClipPlaybackMode.Preview })
        {
            MonitorSuspendedForPreview = false;
            _monitorResumeEligible = false;
        }

        Clip = new ActiveClip(clipId, _nextPlaybackEpoch++, mode);
    }

    private void ClearClip()
    {
        Clip = null;
        MonitorSuspendedForPreview = false;
        _monitorResumeEligible = false;
    }

    private void InvalidateAllSources()
    {
        foreach (var key in _sources.Keys.ToList())
        {
            _sources[key] = new SourceState(_sources[key].Epoch + 1, false);
        }
    }
}
