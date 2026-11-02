namespace OnbodyMic.EngineHost;

using OnbodyMic.Application;
using OnbodyMic.Domain;
using OnbodyMic.Platform.Windows;

internal readonly record struct QueuedHotkeyTrigger(HotkeyTrigger Trigger, long Generation);

/// <summary>The OS pump only posts intent; the existing dispatcher runs on the host's ordered control lane, never an import/decode worker.</summary>
internal sealed class QueuedHotkeyService : IHotkeyService
{
    private const HotkeyModifiers NoRepeat = (HotkeyModifiers)0x4000;
    private readonly WindowsHotkeyService _windows = new();
    private readonly Action<QueuedHotkeyTrigger> _enqueue;
    private long _generation;

    public QueuedHotkeyService(Action<QueuedHotkeyTrigger> enqueue)
    {
        _enqueue = enqueue;
        _windows.Triggered += OnTriggered;
    }

    public event Action<HotkeyTrigger>? Triggered;
    public long Generation => Volatile.Read(ref _generation);

    public CommandResult Register(HotkeyBinding binding)
    {
        var result = _windows.Register(binding with
        {
            // MOD_NOREPEAT is a RegisterHotKey flag, not a gesture modifier exposed in settings or IPC.
            Gesture = binding.Gesture with { Modifiers = binding.Gesture.Modifiers | NoRepeat },
        });
        if (result.Accepted) Interlocked.Increment(ref _generation);
        return result;
    }

    public CommandResult Unregister(HotkeyAction action, string? targetId)
    {
        var result = _windows.Unregister(action, targetId);
        if (result.Accepted) Interlocked.Increment(ref _generation);
        return result;
    }

    public IReadOnlyList<HotkeyBinding> Active => _windows.Active.Select(PublicBinding).ToArray();

    public void Deliver(HotkeyTrigger trigger) => Triggered?.Invoke(trigger);

    private void OnTriggered(HotkeyTrigger trigger) => _enqueue(new QueuedHotkeyTrigger(
        trigger with { Binding = PublicBinding(trigger.Binding) }, Volatile.Read(ref _generation)));

    private static HotkeyBinding PublicBinding(HotkeyBinding binding) => binding with
    {
        Gesture = binding.Gesture with { Modifiers = binding.Gesture.Modifiers & ~NoRepeat },
    };

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        _windows.Triggered -= OnTriggered;
        _windows.Dispose();
    }
}

internal static class HotkeyBindings
{
    public static string[] Actions { get; } =
    [
        "stopClip", "toggleExtrasSend", "toggleMicSend", "emergencyMute", "toggleMonitor",
    ];

    public static HotkeyData Apply(SettingsData settings, HotkeyDispatcher dispatcher)
    {
        var issues = new List<SettingsIssueData>();
        foreach (var active in dispatcher.Active)
        {
            var result = dispatcher.Unbind(active.Action, active.TargetId);
            if (!result.Accepted)
                issues.Add(new SettingsIssueData(Section(active.Action), active.TargetId ?? HotkeyActionNames.ToName(active.Action),
                    result.Rejection ?? "hotkey-unregistration-rejected"));
        }

        var seen = new HashSet<HotkeyGesture>();
        foreach (var (key, text) in settings.GlobalHotkeys)
        {
            if (!HotkeyActionNames.TryParse(key, out var action))
            {
                issues.Add(new SettingsIssueData("globalHotkeys", key, "unknown action (kept, not activated)"));
                continue;
            }
            Bind("globalHotkeys", key, action, null, text);
        }
        foreach (var (id, text) in settings.ClipHotkeys)
            Bind("clipHotkeys", id, HotkeyAction.PlayClip, id, text);
        foreach (var (id, text) in settings.PresetHotkeys)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add(new SettingsIssueData("presetHotkeys", id, "empty preset id"));
                continue;
            }
            Bind("presetHotkeys", id, HotkeyAction.SelectPreset, id, text);
        }

        return new HotkeyData(dispatcher.Active.Select(binding => new HotkeyBindingData(
            HotkeyActionNames.ToName(binding.Action), binding.TargetId, binding.Gesture.ToString())).ToArray(),
            issues.ToArray(), Actions);

        void Bind(string section, string key, HotkeyAction action, string? target, string? text)
        {
            if (!HotkeyGesture.TryParse(text, out var gesture))
            {
                issues.Add(new SettingsIssueData(section, key, $"unparseable gesture '{text}'"));
                return;
            }
            if (gesture.IsUnbound) return;
            if (!seen.Add(gesture))
            {
                issues.Add(new SettingsIssueData(section, key, $"duplicate gesture '{gesture}' (kept, not activated)"));
                return;
            }
            var result = dispatcher.Bind(new HotkeyBinding(action, target, gesture));
            if (!result.Accepted)
                issues.Add(new SettingsIssueData(section, key, result.Rejection ?? "hotkey-registration-rejected"));
        }
    }

    private static string Section(HotkeyAction action) => action switch
    {
        HotkeyAction.PlayClip => "clipHotkeys",
        HotkeyAction.SelectPreset => "presetHotkeys",
        _ => "globalHotkeys",
    };
}
