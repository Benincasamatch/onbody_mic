namespace OnbodyMic.Media;

/// <summary>One soundboard entry. The stable <see cref="ClipId"/> is the only thing presets, hotkeys and the cache reference (docs/06 §4.3).</summary>
public sealed record ClipItem(
    string ClipId,
    string DisplayName,
    string ManagedAssetPath,
    string? OriginalImportPath,
    int DurationMs,
    string Format,
    bool Favorite,
    string? GroupId,
    float Gain,
    long ImportUnixMs);

public sealed record ClipGroup(string GroupId, string Name);

public enum ClipSortOrder
{
    Name,
    ImportTime,
}

/// <summary>Import limits; the duration bound matches the decoder's full-predecode contract (docs/01 §5.3).</summary>
public sealed record ClipLibraryLimits(int MaxFileBytes, int MaxDurationSeconds)
{
    public static ClipLibraryLimits Default { get; } = new(32 * 1024 * 1024, 60);
}

public enum ImportOutcome
{
    Imported,
    Duplicate,
}

/// <summary>Duplicate imports surface the existing entry; the caller decides whether to force a second copy (docs/01 §5.3).</summary>
public readonly record struct ImportResult(ImportOutcome Outcome, ClipItem Item, ClipItem? ExistingDuplicate)
{
    public static ImportResult FromDuplicate(ClipItem existing) => new(ImportOutcome.Duplicate, existing, existing);
}

/// <summary>Import/validation failure with a user-presentable reason (T14: clear error, no crash, nothing half-imported).</summary>
public sealed class ClipImportException(string reason) : Exception(reason);

/// <summary>The managed index exists but cannot be trusted; the file is left untouched and the library refuses to operate.</summary>
public sealed class ClipLibraryLoadException(string reason) : Exception(reason);
