namespace OnbodyMic.Media;

public sealed record ClipOrigin(string Kind, string? ParentClipId, string OriginalPath, int StartMs, int EndMs);

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
    long ImportUnixMs,
    ClipOrigin? Origin = null,
    float[]? Peaks = null)
{
    /// <summary>Waveform min/max bins (<c>[min0, max0, …]</c>) computed at import; empty for legacy entries.</summary>
    public float[] WaveformPeaks => Peaks ?? [];

    public bool Equals(ClipItem? other) =>
        other is not null &&
        ClipId == other.ClipId && DisplayName == other.DisplayName &&
        ManagedAssetPath == other.ManagedAssetPath && OriginalImportPath == other.OriginalImportPath &&
        DurationMs == other.DurationMs && Format == other.Format && Favorite == other.Favorite &&
        GroupId == other.GroupId && Gain == other.Gain && ImportUnixMs == other.ImportUnixMs &&
        Equals(Origin, other.Origin) && WaveformPeaks.AsSpan().SequenceEqual(other.WaveformPeaks);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ClipId); hash.Add(DisplayName); hash.Add(ManagedAssetPath); hash.Add(OriginalImportPath);
        hash.Add(DurationMs); hash.Add(Format); hash.Add(Favorite); hash.Add(GroupId);
        hash.Add(Gain); hash.Add(ImportUnixMs); hash.Add(Origin);
        foreach (var peak in WaveformPeaks) hash.Add(peak);
        return hash.ToHashCode();
    }
}

public sealed record ClipGroup(string GroupId, string Name, string? ParentGroupId = null);

public sealed record FolderImportFile(string Path, string? GroupId);

public sealed record FolderImportPlan(FolderImportFile[] Files, string[] RootGroupIds);

public enum ClipSortOrder
{
    Name,
    ImportTime,
    LibraryOrder,
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
