namespace OnbodyMic.Media;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnbodyMic.Persistence;

/// <summary>
/// Managed soundboard library (IClipLibrary role, docs/06 §3): imports are
/// validated (decodable, bounded duration/size), copied into a managed store
/// under a stable id, and all metadata mutations persist atomically. The
/// original file is never modified, moved or deleted.
/// </summary>
public sealed class ClipLibrary : IDisposable
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly string _indexPath;
    private readonly IClipDecoder _decoder;
    private readonly ClipLibraryLimits _limits;
    private readonly object _gate = new();
    private List<ClipItem> _items;
    private List<ClipGroup> _groups;

    /// <summary>Raised after every persisted mutation (list refresh hook).</summary>
    public event Action? Changed;

    private ClipLibrary(string root, IClipDecoder decoder, ClipLibraryLimits limits, List<ClipItem> items, List<ClipGroup> groups)
    {
        _root = root;
        _indexPath = Path.Combine(root, "index.json");
        _decoder = decoder;
        _limits = limits;
        _items = items;
        _groups = groups;
    }

    public string Root => _root;

    /// <summary>Loads the index; missing root starts empty. A corrupt index throws and never writes.</summary>
    public static ClipLibrary Open(string root, IClipDecoder decoder, ClipLibraryLimits? limits = null)
    {
        limits ??= ClipLibraryLimits.Default;
        var indexPath = Path.Combine(root, "index.json");
        if (!File.Exists(indexPath))
        {
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            return new ClipLibrary(root, decoder, limits, [], []);
        }

        LibraryDocument document;
        try
        {
            document = JsonSerializer.Deserialize<LibraryDocument>(File.ReadAllText(indexPath), JsonOptions)
                ?? throw new ClipLibraryLoadException("empty index document");
        }
        catch (JsonException ex)
        {
            throw new ClipLibraryLoadException($"index json invalid: {ex.Message}");
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            throw new ClipLibraryLoadException($"unsupported index schemaVersion: {document.SchemaVersion}");
        }

        var items = new List<ClipItem>(document.Items.Count);
        var seenIds = new HashSet<string>();
        foreach (var dto in document.Items)
        {
            if (string.IsNullOrWhiteSpace(dto.ClipId) || !seenIds.Add(dto.ClipId))
            {
                throw new ClipLibraryLoadException($"duplicate or empty clipId: '{dto.ClipId}'");
            }

            if (string.IsNullOrWhiteSpace(dto.DisplayName))
            {
                throw new ClipLibraryLoadException($"item {dto.ClipId}: empty displayName");
            }

            if (string.IsNullOrWhiteSpace(dto.ManagedAssetPath) || !IsSafeRelativePath(dto.ManagedAssetPath))
            {
                throw new ClipLibraryLoadException($"item {dto.ClipId}: unsafe managedAssetPath '{dto.ManagedAssetPath}'");
            }

            if (dto.DurationMs < 0)
            {
                throw new ClipLibraryLoadException($"item {dto.ClipId}: negative duration");
            }

            if (dto.Format is not ("wav" or "mp3"))
            {
                throw new ClipLibraryLoadException($"item {dto.ClipId}: unsupported format '{dto.Format}'");
            }

            if (!float.IsFinite(dto.Gain) || dto.Gain is < 0f or > 4f)
            {
                throw new ClipLibraryLoadException($"item {dto.ClipId}: gain out of range");
            }

            if (dto.ImportUnixMs <= 0)
            {
                throw new ClipLibraryLoadException($"item {dto.ClipId}: invalid import time");
            }

            items.Add(new ClipItem(
                dto.ClipId,
                dto.DisplayName,
                dto.ManagedAssetPath,
                dto.OriginalImportPath,
                dto.DurationMs,
                dto.Format,
                dto.Favorite,
                dto.GroupId,
                dto.Gain,
                dto.ImportUnixMs));
        }

        var groups = new List<ClipGroup>(document.Groups.Count);
        var seenGroups = new HashSet<string>();
        foreach (var dto in document.Groups)
        {
            if (string.IsNullOrWhiteSpace(dto.GroupId) || !seenGroups.Add(dto.GroupId))
            {
                throw new ClipLibraryLoadException($"duplicate or empty groupId: '{dto.GroupId}'");
            }

            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                throw new ClipLibraryLoadException($"group {dto.GroupId}: empty name");
            }

            groups.Add(new ClipGroup(dto.GroupId, dto.Name));
        }

        var knownGroups = groups.Select(g => g.GroupId).ToHashSet();
        foreach (var item in items.Where(i => i.GroupId is not null && !knownGroups.Contains(i.GroupId!)).ToList())
        {
            // Dangling group references degrade to ungrouped; entries stay intact.
            items[items.IndexOf(item)] = item with { GroupId = null };
        }

        return new ClipLibrary(root, decoder, limits, items, groups);
    }

    public ClipItem? Find(string clipId)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => i.ClipId == clipId);
        }
    }

    public bool IsAvailable(string clipId) => Find(clipId) is { } item && File.Exists(AssetAbsolutePath(item));

    public IReadOnlyList<ClipItem> ItemsSnapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    public IReadOnlyList<ClipGroup> GroupsSnapshot()
    {
        lock (_gate)
        {
            return _groups.ToArray();
        }
    }

    public string AssetAbsolutePath(ClipItem item) => Path.GetFullPath(Path.Combine(_root, item.ManagedAssetPath));

    /// <summary>Filter by name substring, optional favorites-only and group; sorted by name or import time (docs/01 §5.3).</summary>
    public IReadOnlyList<ClipItem> Search(string? query = null, ClipSortOrder sort = ClipSortOrder.Name, bool favoritesOnly = false, string? groupId = null)
    {
        lock (_gate)
        {
            IEnumerable<ClipItem> items = _items;
            if (!string.IsNullOrWhiteSpace(query))
            {
                items = items.Where(i => i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            if (favoritesOnly)
            {
                items = items.Where(i => i.Favorite);
            }

            if (groupId is not null)
            {
                items = items.Where(i => i.GroupId == groupId);
            }

            items = sort switch
            {
                ClipSortOrder.Name => items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase),
                ClipSortOrder.ImportTime => items.OrderByDescending(i => i.ImportUnixMs),
                _ => items,
            };
            return items.ToArray();
        }
    }

    /// <summary>Prefill order: favorites first, newest first within each class (docs/01 §5.3 常用预解码).</summary>
    public IReadOnlyList<ClipItem> PrefillCandidates()
    {
        lock (_gate)
        {
            return _items
                .OrderByDescending(i => i.Favorite)
                .ThenByDescending(i => i.ImportUnixMs)
                .ToArray();
        }
    }

    public ImportResult Import(string sourcePath, string? displayName = null, bool allowDuplicate = false)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new ClipImportException($"file not found: {sourcePath}");
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3"))
        {
            throw new ClipImportException($"unsupported format: {extension}（首批支持 wav/mp3）");
        }

        var fileBytes = new FileInfo(fullPath).Length;
        if (fileBytes > _limits.MaxFileBytes)
        {
            throw new ClipImportException($"file exceeds {_limits.MaxFileBytes} bytes");
        }

        DecodedClip decoded;
        try
        {
            decoded = _decoder.Decode(fullPath);
        }
        catch (Exception ex)
        {
            throw new ClipImportException($"decode failed: {ex.Message}");
        }

        var duration = TimeSpan.FromSeconds((double)decoded.Samples.Length / EngineFormat.Rate);
        if (duration.TotalSeconds > _limits.MaxDurationSeconds)
        {
            throw new ClipImportException($"clip exceeds {_limits.MaxDurationSeconds}s bound（实测 {duration.TotalSeconds:F1}s）");
        }

        string hash;
        using (var stream = File.OpenRead(fullPath))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }

        lock (_gate)
        {
            var duplicate = _items.FirstOrDefault(i => i.Format == extension.TrimStart('.') && ContentHashMatches(i, hash));
            if (duplicate is not null && !allowDuplicate)
            {
                return ImportResult.FromDuplicate(duplicate);
            }

            var clipId = NewClipId(_items);
            var assetPath = $"assets/{clipId}{extension}";
            var assetAbsolute = Path.Combine(_root, assetPath);
            Directory.CreateDirectory(Path.Combine(_root, "assets"));
            CopyVerified(fullPath, assetAbsolute);

            var item = new ClipItem(
                clipId,
                displayName is { Length: > 0 } ? displayName.Trim() : Path.GetFileNameWithoutExtension(fullPath),
                assetPath,
                fullPath,
                (int)duration.TotalMilliseconds,
                extension.TrimStart('.'),
                Favorite: false,
                GroupId: null,
                Gain: 1f,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            try
            {
                _items.Add(item);
                SaveIndex();
            }
            catch
            {
                _items.Remove(item);
                TryDeleteFile(assetAbsolute);
                throw;
            }

            Changed?.Invoke();
            return new ImportResult(ImportOutcome.Imported, item, null);
        }
    }

    public bool Rename(string clipId, string displayName)
    {
        var name = displayName.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        return MutateItem(clipId, item => item with { DisplayName = name });
    }

    public bool SetFavorite(string clipId, bool favorite) => MutateItem(clipId, item => item with { Favorite = favorite });

    public bool SetGain(string clipId, float gain)
    {
        if (!float.IsFinite(gain) || gain is < 0f or > 4f)
        {
            return false;
        }

        return MutateItem(clipId, item => item with { Gain = gain });
    }

    /// <summary>Moves the clip into a group; null leaves it ungrouped (docs/01 §5.3 single-level groups).</summary>
    public bool SetGroup(string clipId, string? groupId)
    {
        lock (_gate)
        {
            if (groupId is not null && _groups.All(g => g.GroupId != groupId))
            {
                return false;
            }
        }

        return MutateItem(clipId, item => item with { GroupId = groupId });
    }

    public ClipGroup? CreateGroup(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_groups.Any(g => g.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var group = new ClipGroup(NewGroupId(_groups), trimmed);
            try
            {
                _groups.Add(group);
                SaveIndex();
            }
            catch
            {
                _groups.Remove(group);
                throw;
            }

            Changed?.Invoke();
            return group;
        }
    }

    public bool RenameGroup(string groupId, string name)
    {
        var trimmed = name.Trim();
        lock (_gate)
        {
            if (trimmed.Length == 0 || _groups.Any(g => g.GroupId != groupId && g.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var index = _groups.FindIndex(g => g.GroupId == groupId);
            if (index < 0)
            {
                return false;
            }

            var renamed = _groups[index] with { Name = trimmed };
            _groups[index] = renamed;
            try
            {
                SaveIndex();
            }
            catch
            {
                _groups[index] = _groups[index] with { Name = name };
                throw;
            }

            Changed?.Invoke();
            return true;
        }
    }

    /// <summary>Deletes the group; member clips move to ungrouped, never deleted (docs/06 §4.3). Returns the moved count.</summary>
    public int DeleteGroup(string groupId)
    {
        lock (_gate)
        {
            if (_groups.RemoveAll(g => g.GroupId == groupId) == 0)
            {
                return 0;
            }

            var moved = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].GroupId == groupId)
                {
                    _items[i] = _items[i] with { GroupId = null };
                    moved++;
                }
            }

            SaveIndex();
            Changed?.Invoke();
            return moved;
        }
    }

    public bool Delete(string clipId)
    {
        ClipItem? removed;
        lock (_gate)
        {
            removed = _items.FirstOrDefault(i => i.ClipId == clipId);
            if (removed is null)
            {
                return false;
            }

            _items.Remove(removed);
            try
            {
                SaveIndex();
            }
            catch
            {
                _items.Add(removed);
                throw;
            }
        }

        // The user's original file is never touched; only the managed copy goes away.
        TryDeleteFile(AssetAbsolutePath(removed));
        Changed?.Invoke();
        return true;
    }

    private bool MutateItem(string clipId, Func<ClipItem, ClipItem> mutate)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.ClipId == clipId);
            if (index < 0)
            {
                return false;
            }

            var updated = mutate(_items[index]);
            if (updated == _items[index])
            {
                return true;
            }

            var original = _items[index];
            _items[index] = updated;
            try
            {
                SaveIndex();
            }
            catch
            {
                _items[index] = original;
                throw;
            }

            Changed?.Invoke();
            return true;
        }
    }

    private void SaveIndex()
    {
        var document = new LibraryDocument
        {
            SchemaVersion = SchemaVersion,
            Items = [.. _items.Select(i => new ClipItemDto
            {
                ClipId = i.ClipId,
                DisplayName = i.DisplayName,
                ManagedAssetPath = i.ManagedAssetPath,
                OriginalImportPath = i.OriginalImportPath,
                DurationMs = i.DurationMs,
                Format = i.Format,
                Favorite = i.Favorite,
                GroupId = i.GroupId,
                Gain = i.Gain,
                ImportUnixMs = i.ImportUnixMs,
            })],
            Groups = [.. _groups.Select(g => new ClipGroupDto { GroupId = g.GroupId, Name = g.Name })],
        };
        AtomicFile.WriteText(_indexPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    private bool ContentHashMatches(ClipItem item, string hash)
    {
        try
        {
            using var stream = File.OpenRead(AssetAbsolutePath(item));
            return Convert.ToHexString(SHA256.HashData(stream)) == hash;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyVerified(string source, string destination)
    {
        var temp = destination + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: true);
            if (new FileInfo(temp).Length != new FileInfo(source).Length)
            {
                throw new IOException("asset copy verify failed");
            }

            if (File.Exists(destination))
            {
                File.Delete(temp);
                throw new IOException("asset already exists");
            }

            File.Move(temp, destination);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // a locked asset file must not break the index update
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NewClipId(IEnumerable<ClipItem> items)
    {
        string id;
        do
        {
            id = "clip-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        }
        while (items.Any(i => i.ClipId == id));
        return id;
    }

    private static string NewGroupId(IEnumerable<ClipGroup> groups)
    {
        string id;
        do
        {
            id = "group-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        }
        while (groups.Any(g => g.GroupId == id));
        return id;
    }

    private static bool IsSafeRelativePath(string path)
    {
        // "assets/<id>.wav" style only: never rooted, never escaping the root.
        return !Path.IsPathRooted(path) && !path.Contains("..");
    }

    /// <summary>No unmanaged resources today; kept for caller-owned lifetimes.</summary>
    public void Dispose()
    {
    }

    private sealed class LibraryDocument
    {
        public int SchemaVersion { get; set; }

        public List<ClipItemDto> Items { get; set; } = [];

        public List<ClipGroupDto> Groups { get; set; } = [];
    }

    private sealed class ClipItemDto
    {
        public string ClipId { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public string ManagedAssetPath { get; set; } = "";

        public string? OriginalImportPath { get; set; }

        public int DurationMs { get; set; }

        public string Format { get; set; } = "";

        public bool Favorite { get; set; }

        public string? GroupId { get; set; }

        public float Gain { get; set; } = 1f;

        public long ImportUnixMs { get; set; }
    }

    private sealed class ClipGroupDto
    {
        public string GroupId { get; set; } = "";

        public string Name { get; set; } = "";
    }
}
