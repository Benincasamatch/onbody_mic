namespace OnbodyMic.Application;

using OnbodyMic.Domain;

/// <summary>Route a preset refers to; used to report per-route health without leaking device ids.</summary>
public enum PresetRoute
{
    Microphone = 0,
    VirtualOutput = 1,
    VirtualCapture = 2,
    Monitor = 3,
    Application = 4,
}

/// <summary>Non-fatal per-route problem found while applying a preset (missing device, dead app path...).</summary>
public readonly record struct RouteIssue(PresetRoute Route, string Reason);

public enum PresetLoadStatus
{
    Loaded = 0,
    NotFound = 1,
    Corrupt = 2,
}

/// <summary>Outcome of loading one preset file: loaded config, absent, or present-but-untrusted (file untouched).</summary>
public sealed record PresetLoadResult
{
    public PresetLoadStatus Status { get; }

    public PresetConfig? Config { get; }

    /// <summary>Presentable reason when <see cref="Status"/> is <see cref="PresetLoadStatus.Corrupt"/>.</summary>
    public string? Reason { get; }

    private PresetLoadResult(PresetLoadStatus status, PresetConfig? config, string? reason)
    {
        Status = status;
        Config = config;
        Reason = reason;
    }

    public static PresetLoadResult Loaded(PresetConfig config) => new(PresetLoadStatus.Loaded, config, null);

    public static PresetLoadResult NotFound() => new(PresetLoadStatus.NotFound, null, null);

    public static PresetLoadResult Corrupt(string reason) => new(PresetLoadStatus.Corrupt, null, reason);
}

/// <summary>One line of the preset list; a corrupt file stays visible instead of vanishing (T25).</summary>
public readonly record struct PresetSummary(string PresetId, string Name, bool Corrupt);

/// <summary>
/// Versioned preset persistence (IPresetStore role from docs/06 §3): atomic
/// writes with backups, corrupt files rejected without modification, never
/// stores runtime session state.
/// </summary>
public interface IPresetStore
{
    IReadOnlyList<PresetSummary> List();

    PresetLoadResult Load(string presetId);

    /// <summary>Structural validation then atomic save; a rejected config is not written.</summary>
    CommandResult Save(PresetConfig config);

    bool Exists(string presetId);

    /// <summary>Deletes the preset file only; callers handle hotkey cleanup and active-preset teardown.</summary>
    CommandResult Delete(string presetId);

    CommandResult Rename(string presetId, string newName);

    /// <summary>Restores from the atomic-write backup after validating it; both files stay untouched on failure.</summary>
    CommandResult RestoreBackup(string presetId);

    /// <summary>Generates a fresh unique preset id.</summary>
    string NewId();
}

/// <summary>Where the last successfully applied preset id persists (global settings); the value is config, never runtime state.</summary>
public interface ILastPresetSink
{
    string? LastPresetId { get; }

    void SetLastPreset(string? presetId);
}

/// <summary>Result of applying a preset to the live session.</summary>
public readonly record struct PresetApplyResult(
    bool Accepted,
    string? Rejection,
    IReadOnlyList<RouteIssue> Issues,
    string? OutputAttachRejection)
{
    public static PresetApplyResult Reject(string reason) => new(false, reason, [], null);
}
