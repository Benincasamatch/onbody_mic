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
    private const int SchemaVersion = 2;

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

        if (document.SchemaVersion is not (1 or SchemaVersion))
        {
            throw new ClipLibraryLoadException($"unsupported index schemaVersion: {document.SchemaVersion}");
        }

        if (document.Items is null || document.Groups is null)
        {
            throw new ClipLibraryLoadException("index items and groups must be arrays");
        }

        var items = new List<ClipItem>(document.Items.Count);
        var seenIds = new HashSet<string>();
        foreach (var dto in document.Items)
        {
            if (dto is null)
            {
                throw new ClipLibraryLoadException("index contains a null item");
            }

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

            if (dto.Origin is not null)
            {
                if (document.SchemaVersion == 1)
                    throw new ClipLibraryLoadException($"item {dto.ClipId}: origin requires schemaVersion 2");
                if (ValidateOrigin(dto.Origin, dto.ClipId) is { } reason)
                    throw new ClipLibraryLoadException($"item {dto.ClipId}: {reason}");
                if (dto.Format != "wav" || dto.OriginalImportPath != dto.Origin.OriginalPath)
                    throw new ClipLibraryLoadException($"item {dto.ClipId}: inconsistent trimmed origin");
            }

            if (dto.Peaks is not null && (dto.Peaks.Length == 0 || dto.Peaks.Length % 2 != 0 || dto.Peaks.Length > WaveformPeaks.MaxBins * 2))
                throw new ClipLibraryLoadException($"item {dto.ClipId}: invalid waveform peaks");
            if (dto.Peaks is not null && dto.Peaks.Any(peak => !float.IsFinite(peak) || peak is < -1f or > 1f))
                throw new ClipLibraryLoadException($"item {dto.ClipId}: waveform peaks out of range");

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
                dto.ImportUnixMs,
                dto.Origin,
                dto.Peaks));
        }

        var groups = new List<ClipGroup>(document.Groups.Count);
        var seenGroups = new HashSet<string>();
        foreach (var dto in document.Groups)
        {
            if (dto is null)
            {
                throw new ClipLibraryLoadException("index contains a null group");
            }

            if (string.IsNullOrWhiteSpace(dto.GroupId) || !seenGroups.Add(dto.GroupId))
            {
                throw new ClipLibraryLoadException($"duplicate or empty groupId: '{dto.GroupId}'");
            }

            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                throw new ClipLibraryLoadException($"group {dto.GroupId}: empty name");
            }

            groups.Add(new ClipGroup(dto.GroupId, dto.Name, dto.ParentGroupId));
        }

        var groupsById = groups.ToDictionary(g => g.GroupId);
        var validatedGroups = new HashSet<string>();
        foreach (var group in groups)
        {
            var chain = new HashSet<string>();
            var current = group;
            while (!validatedGroups.Contains(current.GroupId))
            {
                if (!chain.Add(current.GroupId))
                    throw new ClipLibraryLoadException($"group hierarchy contains a cycle at '{current.GroupId}'");
                if (current.ParentGroupId is null) break;
                if (!groupsById.TryGetValue(current.ParentGroupId, out var parent))
                    throw new ClipLibraryLoadException($"group {current.GroupId}: missing parent '{current.ParentGroupId}'");
                current = parent;
            }
            validatedGroups.UnionWith(chain);
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

    public string AssetAbsolutePath(ClipItem item) => ResolveManagedPath(item.ManagedAssetPath);

    private string ResolveManagedPath(string relativePath) => Path.GetFullPath(Path.Combine(_root,
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)));

    /// <summary>Filter by name, favorites and a group's subtree; library order retains persisted insertion order.</summary>
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
                var descendants = GroupSubtree(groupId);
                items = items.Where(i => i.GroupId is not null && descendants.Contains(i.GroupId));
            }

            items = sort switch
            {
                ClipSortOrder.Name => items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase),
                ClipSortOrder.ImportTime => items.OrderByDescending(i => i.ImportUnixMs),
                ClipSortOrder.LibraryOrder => items,
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

    public ImportResult Import(string sourcePath, string? displayName = null, bool allowDuplicate = false, string? groupId = null)
        => ImportCore(sourcePath, displayName, allowDuplicate, groupId, origin: null);

    /// <summary>Imports an independently owned exported WAV, retaining its source provenance rather than the temporary export path.</summary>
    public ImportResult ImportSelection(string exportedWavPath, ClipOrigin origin, string displayName, bool allowDuplicate = false, string? groupId = null)
    {
        if (origin is null) throw new ClipImportException("selection origin is required");
        if (ValidateOrigin(origin) is { } reason) throw new ClipImportException(reason);
        if (string.IsNullOrWhiteSpace(displayName)) throw new ClipImportException("selection display name must not be empty");
        origin = origin with { OriginalPath = Path.GetFullPath(origin.OriginalPath) };
        return ImportCore(exportedWavPath, displayName, allowDuplicate, groupId, origin);
    }

    private ImportResult ImportCore(string sourcePath, string? displayName, bool allowDuplicate, string? groupId, ClipOrigin? origin)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            throw new ClipImportException($"file not found: {sourcePath}");

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3"))
            throw new ClipImportException($"unsupported format: {extension}（首批支持 wav/mp3）");
        if (origin is not null && extension != ".wav")
            throw new ClipImportException("trimmed selection must be exported as WAV");
        if (new FileInfo(fullPath).Length > _limits.MaxFileBytes)
            throw new ClipImportException($"file exceeds {_limits.MaxFileBytes} bytes");

        var assetsDirectory = Path.Combine(_root, "assets");
        Directory.CreateDirectory(assetsDirectory);
        var stagedPath = Path.Combine(assetsDirectory, $".import-{Guid.NewGuid():N}{extension}");
        string? cleanupPath = stagedPath;
        FileStream? reservation = null;
        try
        {
            CopyVerified(fullPath, stagedPath);
            if (new FileInfo(stagedPath).Length > _limits.MaxFileBytes)
                throw new ClipImportException($"file exceeds {_limits.MaxFileBytes} bytes");

            DecodedClip decoded;
            try
            {
                decoded = _decoder.Decode(stagedPath);
            }
            catch (Exception ex)
            {
                throw new ClipImportException($"decode failed: {ex.Message}");
            }

            var duration = TimeSpan.FromSeconds((double)decoded.Samples.Length / EngineFormat.Rate);
            if (duration.TotalSeconds > _limits.MaxDurationSeconds)
                throw new ClipImportException($"clip exceeds {_limits.MaxDurationSeconds}s bound（实测 {duration.TotalSeconds:F1}s）");

            // Reserve the staged copy against new readers/writers while allowing our rename and rollback deletion.
            reservation = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(reservation));
            lock (_gate)
            {
                var duplicate = _items.FirstOrDefault(i => i.Format == extension.TrimStart('.') && ContentHashMatches(i, hash));
                if (duplicate is not null && !allowDuplicate)
                    return ImportResult.FromDuplicate(duplicate);

                if (groupId is not null && _groups.All(g => g.GroupId != groupId))
                    throw new ClipImportException($"group not found: {groupId}");

                var clipId = NewClipId(_items);
                var assetPath = $"assets/{clipId}{extension}";
                var assetAbsolute = ResolveManagedPath(assetPath);
                File.Move(stagedPath, assetAbsolute);
                cleanupPath = assetAbsolute;
                var item = new ClipItem(
                    clipId,
                    string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(fullPath) : displayName.Trim(),
                    assetPath,
                    origin?.OriginalPath ?? fullPath,
                    (int)duration.TotalMilliseconds,
                    extension.TrimStart('.'),
                    Favorite: false,
                    GroupId: groupId,
                    Gain: 1f,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    origin,
                    WaveformPeaks.Compute(decoded.Samples));

                try
                {
                    _items.Add(item);
                    SaveIndex();
                }
                catch
                {
                    _items.Remove(item);
                    throw;
                }

                cleanupPath = null;
                reservation.Dispose();
                reservation = null;
                Changed?.Invoke();
                return new ImportResult(ImportOutcome.Imported, item, null);
            }
        }
        finally
        {
            try
            {
                if (cleanupPath is not null) TryDeleteFile(cleanupPath);
            }
            finally
            {
                reservation?.Dispose();
            }
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

    /// <summary>Moves the clip into a group; null leaves it ungrouped.</summary>
    public bool SetGroup(string clipId, string? groupId)
    {
        lock (_gate)
        {
            if (groupId is not null && _groups.All(g => g.GroupId != groupId))
            {
                return false;
            }

            return MutateItem(clipId, item => item with { GroupId = groupId });
        }
    }

    /// <summary>Plans mixed file/folder input in input order, persisting folder groups as one atomic mutation.</summary>
    public FolderImportPlan PrepareFolderImport(IReadOnlyList<string> paths)
    {
        var entries = new List<FolderEntry>();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seenPaths = new HashSet<string>(pathComparer);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ClipImportException("import path must not be empty");
            AddFolderEntry(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), -1, entries, seenPaths);
        }

        lock (_gate)
        {
            var originalGroups = _groups;
            var groupIds = new string?[entries.Count];
            var files = new List<FolderImportFile>();
            var roots = new List<string>();
            try
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var parentId = entry.ParentIndex < 0 ? null : groupIds[entry.ParentIndex];
                    if (!entry.IsDirectory)
                    {
                        files.Add(new FolderImportFile(entry.Path, parentId));
                        continue;
                    }

                    var name = new DirectoryInfo(entry.Path).Name;
                    if (name.Length == 0) name = entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var group = _groups.FirstOrDefault(g => g.ParentGroupId == parentId &&
                        g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (group is null)
                    {
                        group = new ClipGroup(NewGroupId(_groups), name, parentId);
                        if (ReferenceEquals(_groups, originalGroups)) _groups = new List<ClipGroup>(_groups);
                        InsertGroup(group, naturalSiblingOrder: true);
                    }
                    groupIds[i] = group.GroupId;
                    if (entry.ParentIndex < 0 && !roots.Contains(group.GroupId)) roots.Add(group.GroupId);
                }
                if (_groups.Count != originalGroups.Count) SaveIndex();
            }
            catch
            {
                _groups = originalGroups;
                throw;
            }

            if (_groups.Count != originalGroups.Count) Changed?.Invoke();
            return new FolderImportPlan(files.ToArray(), roots.ToArray());
        }
    }

    private readonly record struct FolderEntry(string Path, int ParentIndex, bool IsDirectory);

    private static void AddFolderEntry(string path, int parentIndex, List<FolderEntry> entries, HashSet<string> seenPaths)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == (FileAttributes.Directory | FileAttributes.ReparsePoint) ||
            !seenPaths.Add(path)) return;
        if ((attributes & FileAttributes.Directory) == 0)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                entries.Add(new FolderEntry(path, parentIndex, false));
            return;
        }

        var directoryIndex = entries.Count;
        entries.Add(new FolderEntry(path, parentIndex, true));
        var children = Directory.GetFileSystemEntries(path);
        Array.Sort(children, NaturalNameComparer.Instance);
        foreach (var child in children) AddFolderEntry(child, directoryIndex, entries, seenPaths);
    }

    private HashSet<string> GroupSubtree(string groupId)
    {
        var children = _groups.Where(g => g.ParentGroupId is not null).ToLookup(g => g.ParentGroupId!);
        var descendants = new HashSet<string> { groupId };
        var pending = new Stack<string>();
        pending.Push(groupId);
        while (pending.TryPop(out var parent))
        {
            foreach (var child in children[parent])
                if (descendants.Add(child.GroupId)) pending.Push(child.GroupId);
        }
        return descendants;
    }

    private void InsertGroup(ClipGroup group, bool naturalSiblingOrder = false)
    {
        if (group.ParentGroupId is null)
        {
            _groups.Add(group);
            return;
        }

        var descendants = GroupSubtree(group.ParentGroupId);
        var insertionIndex = _groups.FindLastIndex(g => descendants.Contains(g.GroupId)) + 1;
        if (naturalSiblingOrder)
        {
            var nextSibling = _groups.FindIndex(g => g.ParentGroupId == group.ParentGroupId &&
                NaturalNameComparer.Instance.Compare(g.Name, group.Name) > 0);
            if (nextSibling >= 0) insertionIndex = nextSibling;
        }
        _groups.Insert(insertionIndex, group);
    }

    public ClipGroup? CreateGroup(string name, string? parentGroupId = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if ((parentGroupId is not null && _groups.All(g => g.GroupId != parentGroupId)) ||
                _groups.Any(g => g.ParentGroupId == parentGroupId && g.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var group = new ClipGroup(NewGroupId(_groups), trimmed, parentGroupId);
            try
            {
                InsertGroup(group);
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
            var index = _groups.FindIndex(g => g.GroupId == groupId);
            if (index < 0) return false;
            var original = _groups[index];
            if (trimmed.Length == 0 || _groups.Any(g => g.GroupId != groupId &&
                g.ParentGroupId == original.ParentGroupId && g.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
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
                _groups[index] = original;
                throw;
            }

            Changed?.Invoke();
            return true;
        }
    }

    /// <summary>Deletes a group subtree and ungroups its clips without deleting assets. Returns the moved count.</summary>
    public int DeleteGroup(string groupId)
    {
        lock (_gate)
        {
            if (_groups.All(g => g.GroupId != groupId)) return 0;
            var descendants = GroupSubtree(groupId);
            var originalGroups = _groups;
            var originalItems = _items;
            _groups = _groups.Where(g => !descendants.Contains(g.GroupId)).ToList();
            var moved = 0;
            _items = new List<ClipItem>(_items.Count);
            foreach (var item in originalItems)
            {
                if (item.GroupId is not null && descendants.Contains(item.GroupId))
                {
                    _items.Add(item with { GroupId = null });
                    moved++;
                }
                else _items.Add(item);
            }

            try { SaveIndex(); }
            catch
            {
                _groups = originalGroups;
                _items = originalItems;
                throw;
            }
            Changed?.Invoke();
            return moved;
        }
    }

    /// <summary>Deletes a group subtree and its managed copies in one index transaction. Original source files and independent derived clips remain untouched.</summary>
    public string[] DeleteGroupAndClips(string groupId)
    {
        lock (_gate)
        {
            if (_groups.All(g => g.GroupId != groupId)) return [];
            var descendants = GroupSubtree(groupId);
            var removed = _items.Where(i => i.GroupId is not null && descendants.Contains(i.GroupId)).ToArray();
            return CommitDeletion(removed, descendants);
        }
    }

    public bool Delete(string clipId)
    {
        lock (_gate)
        {
            var removed = _items.FirstOrDefault(i => i.ClipId == clipId);
            if (removed is null) return false;
            CommitDeletion([removed], deletedGroups: null);
            return true;
        }
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

    private string[] CommitDeletion(ClipItem[] removed, HashSet<string>? deletedGroups)
    {
        var deletedIds = removed.Select(i => i.ClipId).ToArray();
        var removedIds = deletedIds.ToHashSet(StringComparer.Ordinal);
        var originalItems = _items;
        var originalGroups = _groups;
        var remainingItems = _items.Where(i => !removedIds.Contains(i.ClipId)).ToList();
        var remainingGroups = deletedGroups is null ? _groups : _groups.Where(g => !deletedGroups.Contains(g.GroupId)).ToList();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var retainedAssets = remainingItems.Select(AssetAbsolutePath).ToHashSet(pathComparer);
        var stagedPaths = new HashSet<string>(pathComparer);
        var staged = new List<StagedAsset>(removed.Length);
        var stagingDirectory = Path.Combine(_root, $".delete-{Guid.NewGuid():N}");
        try
        {
            try
            {
                foreach (var item in removed)
                {
                    var assetPath = AssetAbsolutePath(item);
                    if (retainedAssets.Contains(assetPath) || !stagedPaths.Add(assetPath)) continue;

                    FileStream reservation;
                    try
                    {
                        // Delete sharing permits our moves/deletes, but blocks incompatible playback and new readers/writers.
                        reservation = new FileStream(assetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                    }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }

                    try
                    {
                        Directory.CreateDirectory(stagingDirectory);
                        var stagedPath = Path.Combine(stagingDirectory, $"{staged.Count}{Path.GetExtension(assetPath)}");
                        File.Move(assetPath, stagedPath);
                        staged.Add(new StagedAsset(assetPath, stagedPath, reservation));
                    }
                    catch
                    {
                        reservation.Dispose();
                        throw;
                    }
                }

                _items = remainingItems;
                _groups = remainingGroups;
                SaveIndex();
            }
            catch (Exception failure)
            {
                _items = originalItems;
                _groups = originalGroups;
                List<Exception>? rollbackFailures = null;
                for (var i = staged.Count - 1; i >= 0; i--)
                {
                    try { File.Move(staged[i].StagedPath, staged[i].OriginalPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        (rollbackFailures ??= []).Add(ex);
                    }
                }
                if (rollbackFailures is not null)
                {
                    rollbackFailures.Insert(0, failure);
                    throw new AggregateException($"asset rollback failed; recoverable copies remain in '{stagingDirectory}'", rollbackFailures);
                }
                throw;
            }

            foreach (var asset in staged) File.Delete(asset.StagedPath);
        }
        finally
        {
            foreach (var asset in staged) asset.Reservation.Dispose();
            TryDeleteEmptyDirectory(stagingDirectory);
        }

        Changed?.Invoke();
        return deletedIds;
    }

    private readonly record struct StagedAsset(string OriginalPath, string StagedPath, FileStream Reservation);

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
                Origin = i.Origin,
                Peaks = i.WaveformPeaks.Length > 0 ? i.WaveformPeaks : null,
            })],
            Groups = [.. _groups.Select(g => new ClipGroupDto { GroupId = g.GroupId, Name = g.Name, ParentGroupId = g.ParentGroupId })],
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
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        if (output.Length != input.Length) throw new IOException("asset copy verify failed");
        output.Flush(flushToDisk: true);
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
            // Only uncommitted copies use best-effort cleanup; persisted deletions reserve and stage their assets.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ValidateOrigin(ClipOrigin origin, string? clipId = null)
    {
        if (origin.Kind != "trim") return "unsupported origin kind";
        if (origin.ParentClipId is not null && (string.IsNullOrWhiteSpace(origin.ParentClipId) || origin.ParentClipId == clipId))
            return "invalid origin parentClipId";
        if (origin.StartMs < 0 || origin.EndMs <= origin.StartMs || (long)origin.EndMs - origin.StartMs > 60000)
            return "origin selection must be within 0–60 seconds";
        try
        {
            if (string.IsNullOrWhiteSpace(origin.OriginalPath) || !Path.IsPathFullyQualified(origin.OriginalPath))
                return "origin originalPath must be fully qualified";
            _ = Path.GetFullPath(origin.OriginalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "origin originalPath is invalid";
        }
        return null;
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

        public ClipOrigin? Origin { get; set; }

        public float[]? Peaks { get; set; }
    }

    private sealed class ClipGroupDto
    {
        public string GroupId { get; set; } = "";

        public string Name { get; set; } = "";

        public string? ParentGroupId { get; set; }
    }
}
