namespace OnbodyMic.Persistence;

using System.Text.Json;
using System.Text.Json.Serialization;
using OnbodyMic.Domain;

/// <summary>A load-time rejection: the entry stays on disk but is not activated (docs/01 §5.8 — never silently swap actions).</summary>
public readonly record struct SettingsIssue(string Section, string Key, string Reason);

/// <summary>Thrown when the settings file exists but cannot be trusted; the file is left untouched.</summary>
public sealed class SettingsLoadException(string reason) : Exception(reason);

public sealed class UiPreferencesData
{
    public bool StartMinimized { get; set; }

    /// <summary>"name" | "importTime"; anything else loads as the default with an issue.</summary>
    public string ClipSort { get; set; } = "name";
}

public sealed class GlobalSettingsData
{
    public int SchemaVersion { get; set; } = 1;

    public string? LastPresetId { get; set; }

    public string? LibraryRoot { get; set; }

    /// <summary>Action storage name -> gesture text; null/empty gesture = unbound.</summary>
    public Dictionary<string, string?> GlobalHotkeys { get; set; } = [];

    /// <summary>Stable clip id -> gesture text (docs/06 §4.1: presets reference clip ids, never paths).</summary>
    public Dictionary<string, string?> ClipHotkeys { get; set; } = [];

    public Dictionary<string, string?> PresetHotkeys { get; set; } = [];

    public UiPreferencesData Ui { get; set; } = new();
}

/// <summary>Loaded settings plus the bindings that survived validation.</summary>
public sealed class GlobalSettings(GlobalSettingsData data, IReadOnlyList<HotkeyBinding> bindings, IReadOnlyList<SettingsIssue> issues)
{
    public GlobalSettingsData Data { get; } = data;

    /// <summary>Validated bindings (parseable gesture, known action, no duplicate gesture).</summary>
    public IReadOnlyList<HotkeyBinding> Bindings { get; } = bindings;

    public IReadOnlyList<SettingsIssue> Issues { get; } = issues;

    public static GlobalSettings Default() => new(new GlobalSettingsData(), [], []);
}

public sealed class GlobalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public GlobalSettingsStore(string path) => _path = path;

    /// <summary>Missing file yields defaults; a present but corrupt/unknown-schema file throws (docs/06 §7: never silently run on bad data).</summary>
    public GlobalSettings Load()
    {
        if (!File.Exists(_path))
        {
            return GlobalSettings.Default();
        }

        GlobalSettingsData data;
        try
        {
            data = JsonSerializer.Deserialize<GlobalSettingsData>(File.ReadAllText(_path), JsonOptions)
                ?? throw new SettingsLoadException("empty settings document");
        }
        catch (JsonException ex)
        {
            throw new SettingsLoadException($"settings json invalid: {ex.Message}");
        }

        if (data.SchemaVersion != 1)
        {
            throw new SettingsLoadException($"unsupported settings schemaVersion: {data.SchemaVersion}");
        }

        return Validate(data);
    }

    public void Save(GlobalSettingsData data)
    {
        data.SchemaVersion = 1;
        AtomicFile.WriteText(_path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static GlobalSettings Validate(GlobalSettingsData data)
    {
        var issues = new List<SettingsIssue>();
        var bindings = new List<HotkeyBinding>();
        var seenGestures = new HashSet<HotkeyGesture>();
        foreach (var (actionName, gestureText) in data.GlobalHotkeys)
        {
            if (!HotkeyActionNames.TryParse(actionName, out var action))
            {
                issues.Add(new SettingsIssue("globalHotkeys", actionName, "unknown action (kept, not activated)"));
                continue;
            }

            if (!AddBinding(bindings, seenGestures, issues, "globalHotkeys", actionName, action, targetId: null, gestureText))
            {
                continue;
            }
        }

        foreach (var (clipId, gestureText) in data.ClipHotkeys)
        {
            // A dangling clip id is kept and reported, not dropped: the user
            // sees the broken binding and decides (docs/01 §5.8 标红).
            AddBinding(bindings, seenGestures, issues, "clipHotkeys", clipId, HotkeyAction.PlayClip, clipId, gestureText);
        }

        foreach (var (presetId, _) in data.PresetHotkeys)
        {
            if (string.IsNullOrWhiteSpace(presetId))
            {
                issues.Add(new SettingsIssue("presetHotkeys", presetId, "empty preset id"));
            }
        }

        if (data.Ui.ClipSort is not ("name" or "importTime"))
        {
            issues.Add(new SettingsIssue("ui", "clipSort", $"unknown sort '{data.Ui.ClipSort}', using 'name'"));
            data.Ui.ClipSort = "name";
        }

        return new GlobalSettings(data, bindings, issues);
    }

    private static bool AddBinding(
        List<HotkeyBinding> bindings,
        HashSet<HotkeyGesture> seenGestures,
        List<SettingsIssue> issues,
        string section,
        string key,
        HotkeyAction action,
        string? targetId,
        string? gestureText)
    {
        if (!HotkeyGesture.TryParse(gestureText, out var gesture))
        {
            issues.Add(new SettingsIssue(section, key, $"unparseable gesture '{gestureText}'"));
            return false;
        }

        if (gesture.IsUnbound)
        {
            // An explicit null/empty entry means unbound; nothing to activate.
            return false;
        }

        if (!seenGestures.Add(gesture))
        {
            issues.Add(new SettingsIssue(section, key, $"duplicate gesture '{gesture}' (kept, not activated)"));
            return false;
        }

        bindings.Add(new HotkeyBinding(action, targetId, gesture));
        return true;
    }
}
