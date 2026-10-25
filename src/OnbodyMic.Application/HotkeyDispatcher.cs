namespace OnbodyMic.Application;

using OnbodyMic.Domain;

/// <summary>Outcome of resolving a stable clip id against the managed library.</summary>
public readonly record struct ClipResolution(bool Known, bool Available, string? Path, float Gain)
{
    public static ClipResolution Unknown { get; } = new(false, false, null, 1f);
}

/// <summary>
/// OS global-hotkey backend (IHotkeyService, docs/06 §3): registers bindings,
/// rejects conflicts before activation, and reports activations on its own
/// pump thread. Backends must suppress OS key auto-repeat so a held key
/// fires exactly once per press.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>Registers (replacing any binding for the same action+target); fails on gesture conflict or OS rejection.</summary>
    CommandResult Register(HotkeyBinding binding);

    /// <summary>Removes a binding; succeeding when absent (cancellation is idempotent, docs/06 §6).</summary>
    CommandResult Unregister(HotkeyAction action, string? targetId);

    IReadOnlyList<HotkeyBinding> Active { get; }

    /// <summary>Raised on the backend's pump thread for each activation.</summary>
    event Action<HotkeyTrigger>? Triggered;
}

/// <summary>
/// Single dispatch point from hotkey activations to session commands
/// (docs/06 §6: buttons and hotkeys share the same commands). While
/// suspended for hotkey editing, every action except the emergency mute is
/// ignored (docs/01 §5.4).
/// </summary>
public sealed class HotkeyDispatcher : IDisposable
{
    private readonly AudioSession _session;
    private readonly IHotkeyService _service;
    private readonly Func<string, ClipResolution>? _clipResolver;
    private readonly Func<string, bool>? _presetExists;
    private readonly Func<string, PresetApplyResult>? _presetApplier;
    private volatile bool _suspended;

    public HotkeyDispatcher(
        AudioSession session,
        IHotkeyService service,
        Func<string, ClipResolution>? clipResolver = null,
        Func<string, bool>? presetExists = null,
        Func<string, PresetApplyResult>? presetApplier = null)
    {
        _session = session;
        _service = service;
        _clipResolver = clipResolver;
        _presetExists = presetExists;
        _presetApplier = presetApplier;
        _service.Triggered += OnTriggered;
    }

    /// <summary>True while hotkey editing suppresses normal triggers.</summary>
    public bool Suspended => _suspended;

    public event Action<SessionNotice>? Noticed;

    public void Suspend() => _suspended = true;

    public void Resume() => _suspended = false;

    /// <summary>Binds after validating the target; the service rejects gesture conflicts.</summary>
    public CommandResult Bind(HotkeyBinding binding)
    {
        if (binding.Gesture.IsUnbound)
        {
            return CommandResult.Reject("gesture-required");
        }

        if (binding.Action == HotkeyAction.PlayClip)
        {
            if (string.IsNullOrWhiteSpace(binding.TargetId))
            {
                return CommandResult.Reject("clip-id-required");
            }

            var resolution = _clipResolver?.Invoke(binding.TargetId) ?? ClipResolution.Unknown;
            if (!resolution.Known)
            {
                return CommandResult.Reject($"unknown-clip:{binding.TargetId}");
            }

            if (!resolution.Available)
            {
                return CommandResult.Reject($"clip-unavailable:{binding.TargetId}");
            }
        }
        else if (binding.Action == HotkeyAction.SelectPreset)
        {
            if (string.IsNullOrWhiteSpace(binding.TargetId))
            {
                return CommandResult.Reject("preset-id-required");
            }

            if (_presetExists?.Invoke(binding.TargetId) == false)
            {
                return CommandResult.Reject($"unknown-preset:{binding.TargetId}");
            }
        }
        else if (binding.TargetId is not null)
        {
            return CommandResult.Reject("target-not-supported");
        }

        return _service.Register(binding);
    }

    public CommandResult Unbind(HotkeyAction action, string? targetId) => _service.Unregister(action, targetId);

    public IReadOnlyList<HotkeyBinding> Active => _service.Active;

    private void OnTriggered(HotkeyTrigger trigger)
    {
        var binding = trigger.Binding;
        try
        {
            if (_suspended && binding.Action != HotkeyAction.EmergencyMute)
            {
                Notice($"hotkey ignored (editing): {binding.Describe()}");
                return;
            }

            switch (binding.Action)
            {
                case HotkeyAction.PlayClip:
                    DispatchPlay(binding.TargetId!);
                    break;
                case HotkeyAction.StopClip:
                    _ = _session.StopClip();
                    break;
                case HotkeyAction.ToggleExtrasSend:
                    var extras = _session.Status().Switches.ExtrasSendEnabled;
                    Report(_session.EnableExtras(!extras), "toggle-extras");
                    break;
                case HotkeyAction.ToggleMicSend:
                    var mic = _session.Status().Switches.MicSendEnabled;
                    Report(_session.EnableMic(!mic), "toggle-mic");
                    break;
                case HotkeyAction.EmergencyMute:
                    Report(_session.EmergencyMute(), "emergency-mute");
                    break;
                case HotkeyAction.ToggleMonitor:
                    var status = _session.Status();
                    Report(_session.SetMonitor(!status.Switches.MonitorEnabled, status.MonitorMode), "toggle-monitor");
                    break;
                case HotkeyAction.SelectPreset:
                    if (_presetApplier is null)
                    {
                        Notice("preset hotkeys unavailable: no preset manager wired");
                        break;
                    }

                    if (_presetExists?.Invoke(binding.TargetId!) == false)
                    {
                        Notice($"hotkey preset missing: {binding.TargetId}");
                        break;
                    }

                    Report(_presetApplier(binding.TargetId!), "select-preset");
                    break;
            }
        }
        catch (Exception ex)
        {
            // a dispatch failure must never kill the backend pump thread
            Notice($"hotkey dispatch failed: {ex.Message}");
        }
    }

    private void DispatchPlay(string clipId)
    {
        if (_clipResolver is not null)
        {
            var resolution = _clipResolver(clipId);
            if (!resolution.Known || !resolution.Available)
            {
                // hotkeys target stable library ids; a dangling or unavailable
                // entry is refused, never forwarded as a raw path decode
                Notice($"hotkey clip unavailable: {clipId}");
                return;
            }
        }

        var result = _session.PlayClip(clipId);
        if (!result.Accepted)
        {
            // docs/01 §5.4: rejected, not queued — the user must see why
            Notice($"hotkey play rejected ({result.Rejection}): {clipId}");
        }
    }

    private void Report(CommandResult result, string action)
    {
        if (!result.Accepted)
        {
            Notice($"hotkey {action} rejected: {result.Rejection}");
        }
    }

    private void Report(PresetApplyResult result, string action)
    {
        if (!result.Accepted)
        {
            Notice($"hotkey {action} rejected: {result.Rejection}");
        }
    }

    private void Notice(string message) => Noticed?.Invoke(new SessionNotice(SessionNoticeKind.Info, message));

    public void Dispose() => _service.Triggered -= OnTriggered;
}
