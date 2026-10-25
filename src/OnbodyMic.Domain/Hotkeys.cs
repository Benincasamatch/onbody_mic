namespace OnbodyMic.Domain;

/// <summary>
/// Global hotkey actions (docs/01 §5.8 first-release set). Preset selection
/// joins when presets exist (P3); no preview hotkey — auditions are explicit
/// local actions, never a send-hotkey fallback.
/// </summary>
public enum HotkeyAction
{
    PlayClip = 0,
    StopClip = 1,
    ToggleExtrasSend = 2,
    ToggleMicSend = 3,
    EmergencyMute = 4,
    ToggleMonitor = 5,
}

public static class HotkeyActionNames
{
    /// <summary>Storage names in settings.json; stable across releases.</summary>
    public static string ToName(HotkeyAction action) => action switch
    {
        HotkeyAction.PlayClip => "playClip",
        HotkeyAction.StopClip => "stopClip",
        HotkeyAction.ToggleExtrasSend => "toggleExtrasSend",
        HotkeyAction.ToggleMicSend => "toggleMicSend",
        HotkeyAction.EmergencyMute => "emergencyMute",
        HotkeyAction.ToggleMonitor => "toggleMonitor",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static bool TryParse(string? name, out HotkeyAction action)
    {
        switch (name)
        {
            case "playClip": action = HotkeyAction.PlayClip; return true;
            case "stopClip": action = HotkeyAction.StopClip; return true;
            case "toggleExtrasSend": action = HotkeyAction.ToggleExtrasSend; return true;
            case "toggleMicSend": action = HotkeyAction.ToggleMicSend; return true;
            case "emergencyMute": action = HotkeyAction.EmergencyMute; return true;
            case "toggleMonitor": action = HotkeyAction.ToggleMonitor; return true;
            default: action = default; return false;
        }
    }
}

/// <summary>Modifier flags; bit values match the Win32 MOD_* constants.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A global key combination. <see cref="Key"/> is a Win32 virtual-key code,
/// carried as a plain encoding table so shared code can parse/compare/store
/// gestures while platform adapters do the OS registration (docs/06 §9).
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    public bool IsUnbound => Key == 0;

    public override string ToString()
    {
        if (IsUnbound)
        {
            return string.Empty;
        }

        var text = string.Empty;
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            text += "Ctrl+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            text += "Alt+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            text += "Shift+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            text += "Win+";
        }

        return text + KeyName(Key);
    }

    /// <summary>Parses "Ctrl+Alt+F7" style text; empty/whitespace yields the unbound gesture.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "super": modifiers |= HotkeyModifiers.Win; break;
                default: return false;
            }
        }

        if (!TryKey(parts[^1], out var key))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    private static string KeyName(int key) => Keys.TryGetValue(key, out var name) ? name : $"VK(0x{key:X2})";

    private static bool TryKey(string text, out int key)
    {
        key = 0;
        var normalized = text.ToLowerInvariant();
        if (normalized.Length == 1)
        {
            var c = normalized[0];
            if (c is >= 'a' and <= 'z')
            {
                key = char.ToUpperInvariant(c); // VK_A..VK_Z = 0x41..0x5A
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                key = c;
                return true;
            }

            return false;
        }

        return KeysByNorm.TryGetValue(normalized, out key);
    }

    private static readonly Dictionary<int, string> Keys = BuildKeyNames();

    private static readonly Dictionary<string, int> KeysByNorm = Keys.ToDictionary(p => p.Value.ToLowerInvariant(), p => p.Key);

    private static Dictionary<int, string> BuildKeyNames()
    {
        var keys = new Dictionary<int, string>
        {
            [0x08] = "Back",
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x13] = "Pause",
            [0x20] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x2C] = "PrintScreen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x5B] = "LWin",
            [0x5C] = "RWin",
            [0x6A] = "NumMultiply",
            [0x6B] = "NumAdd",
            [0x6D] = "NumSubtract",
            [0x6E] = "NumDecimal",
            [0x6F] = "NumDivide",
            [0x90] = "NumLock",
            [0x91] = "ScrollLock",
            [0xA0] = "LShift",
            [0xA1] = "RShift",
            [0xA2] = "LCtrl",
            [0xA3] = "RCtrl",
            [0xA4] = "LAlt",
            [0xA5] = "RAlt",
            [0xBA] = "Semicolon",
            [0xBB] = "Equal",
            [0xBC] = "Comma",
            [0xBD] = "Minus",
            [0xBE] = "Period",
            [0xBF] = "Slash",
            [0xC0] = "Grave",
            [0xDB] = "BracketLeft",
            [0xDC] = "Backslash",
            [0xDD] = "BracketRight",
            [0xDE] = "Quote",
        };
        for (var d = 0; d <= 9; d++)
        {
            keys[0x60 + d] = $"Num{d}";
        }

        for (var f = 1; f <= 24; f++)
        {
            keys[0x6F + f] = $"F{f}"; // F1 = 0x70
        }

        for (var l = 'a'; l <= 'z'; l++)
        {
            keys[char.ToUpperInvariant(l)] = char.ToUpperInvariant(l).ToString();
        }

        for (var d = '0'; d <= '9'; d++)
        {
            keys[d] = d.ToString();
        }

        return keys;
    }
}

/// <summary>A registered binding: what the gesture does and (for clip plays) which stable clip id it targets.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, string? TargetId, HotkeyGesture Gesture)
{
    public bool Matches(HotkeyAction action, string? targetId) => Action == action && TargetId == targetId;

    public string Describe() => Action switch
    {
        HotkeyAction.PlayClip => $"play-clip:{TargetId}",
        HotkeyAction.StopClip => "stop-clip",
        HotkeyAction.ToggleExtrasSend => "toggle-extras-send",
        HotkeyAction.ToggleMicSend => "toggle-mic-send",
        HotkeyAction.EmergencyMute => "emergency-mute",
        HotkeyAction.ToggleMonitor => "toggle-monitor",
        _ => Action.ToString(),
    };
}

/// <summary>One hotkey activation as delivered by the OS backend; raised on the backend's pump thread.</summary>
public readonly record struct HotkeyTrigger(HotkeyBinding Binding, long TimestampTick);
