namespace OnbodyMic.Media.Tests;

using OnbodyMic.Audio;
using OnbodyMic.Media;
using Xunit;

public sealed class StubDecoder : IClipDecoder
{
    public Func<string, float[]> Generator { get; set; } = _ => new float[4410]; // 100 ms @ 44.1 kHz

    public int Calls { get; private set; }

    public DecodedClip Decode(string path)
    {
        Calls++;
        return new(Generator(path));
    }
}

public sealed class ClipLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onbodymic-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StubDecoder _decoder = new();

    public ClipLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SourceFile(string name, string? contents = null) => Write(name, contents ?? $"{name}-{Guid.NewGuid():N}");

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private ClipLibrary OpenLib(ClipLibraryLimits? limits = null) => ClipLibrary.Open(Path.Combine(_dir, "lib"), _decoder, limits);

    [Fact]
    public void Import_validates_copies_asset_and_persists_stable_id()
    {
        var source = SourceFile("beep.wav");
        using var library = OpenLib();

        var result = library.Import(source);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.StartsWith("clip-", result.Item.ClipId);
        Assert.Equal("beep", result.Item.DisplayName);
        Assert.Equal("wav", result.Item.Format);
        Assert.Equal(100, result.Item.DurationMs); // from the decoded sample count
        var asset = Path.Combine(library.Root, "assets", $"{result.Item.ClipId}.wav");
        Assert.True(File.Exists(asset));
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(asset));

        // original untouched (T15), and the index survives a reopen with the same id
        Assert.True(File.Exists(source));
        using var reopened = OpenLib();
        var item = reopened.Find(result.Item.ClipId);
        Assert.NotNull(item);
        Assert.Equal(result.Item.ManagedAssetPath, item!.ManagedAssetPath);
        Assert.True(File.Exists(Path.Combine(reopened.Root, item.ManagedAssetPath)));
    }

    [Fact]
    public void Import_failures_are_explicit_and_leave_nothing_behind()
    {
        using var library = OpenLib();

        Assert.Throws<ClipImportException>(() => library.Import(Path.Combine(_dir, "missing.wav")));
        Assert.Throws<ClipImportException>(() => library.Import(SourceFile("song.flac")));

        var corrupt = SourceFile("corrupt.wav");
        _decoder.Generator = _ => throw new InvalidDataException("not audio");
        Assert.Throws<ClipImportException>(() => library.Import(corrupt));

        Assert.Empty(library.ItemsSnapshot());
        Assert.Empty(Directory.GetFiles(Path.Combine(library.Root, "assets")));
    }

    [Fact]
    public void Oversize_file_is_rejected_before_decoding()
    {
        using var library = OpenLib(new ClipLibraryLimits(MaxFileBytes: 8, MaxDurationSeconds: 60));
        var big = Write("big.wav", new string('x', 64));

        Assert.Throws<ClipImportException>(() => library.Import(big));
        Assert.Equal(0, _decoder.Calls);
    }

    [Fact]
    public void Overlong_clip_is_rejected()
    {
        using var library = OpenLib(new ClipLibraryLimits(MaxFileBytes: 32 * 1024 * 1024, MaxDurationSeconds: 1));
        _decoder.Generator = _ => new float[44100 * 2];
        var longClip = SourceFile("long.wav");

        Assert.Throws<ClipImportException>(() => library.Import(longClip));
    }

    [Fact]
    public void Duplicate_content_is_detected_and_can_be_forced()
    {
        var source = SourceFile("same.wav");
        using var library = OpenLib();

        var first = library.Import(source);
        var second = library.Import(source);

        Assert.Equal(ImportOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.Item.ClipId, second.ExistingDuplicate!.ClipId);
        Assert.Single(library.ItemsSnapshot());

        var forced = library.Import(source, displayName: "second config", allowDuplicate: true);
        Assert.Equal(ImportOutcome.Imported, forced.Outcome);
        Assert.NotEqual(first.Item.ClipId, forced.Item.ClipId);
        Assert.Equal(2, library.ItemsSnapshot().Count);
    }

    [Fact]
    public void Search_sort_favorites_and_rename_cover_the_management_matrix()
    {
        using var library = OpenLib();
        var alpha = library.Import(SourceFile("zeta.wav"), displayName: "Zeta").Item;
        var beta = library.Import(SourceFile("alpha.wav"), displayName: "Alpha").Item;

        Assert.Equal("Alpha", library.Search(sort: ClipSortOrder.Name)[0].DisplayName);
        Assert.Equal(beta.ClipId, library.Search(sort: ClipSortOrder.ImportTime)[0].ClipId);

        Assert.Equal("Alpha", library.Search("alp").Single().DisplayName);

        Assert.True(library.SetFavorite(alpha.ClipId, true));
        Assert.Equal(alpha.ClipId, library.Search(favoritesOnly: true).Single().ClipId);

        Assert.False(library.Rename(alpha.ClipId, "  "));
        Assert.True(library.Rename(alpha.ClipId, "Airhorn"));
        Assert.Equal("Airhorn", library.Find(alpha.ClipId)!.DisplayName);
        Assert.Empty(library.Search("Zeta"));

        using var reopened = OpenLib(); // favorites and renames persist (T34 metadata half)
        Assert.True(reopened.Find(alpha.ClipId)!.Favorite);
        Assert.Equal("Airhorn", reopened.Find(alpha.ClipId)!.DisplayName);
    }

    [Fact]
    public void Groups_are_single_level_and_deleting_a_group_only_ungroups()
    {
        using var library = OpenLib();
        var a = library.Import(SourceFile("a.wav")).Item;
        var b = library.Import(SourceFile("b.wav")).Item;

        var group = library.CreateGroup("游戏")!;
        Assert.NotNull(library.CreateGroup("其他"));
        Assert.Null(library.CreateGroup("游戏")); // duplicate name rejected

        Assert.True(library.SetGroup(a.ClipId, group.GroupId));
        Assert.False(library.SetGroup(b.ClipId, "group-nonsense"));

        Assert.Equal(a.ClipId, library.Search(groupId: group.GroupId).Single().ClipId);

        Assert.True(library.RenameGroup(group.GroupId, "开黑"));
        Assert.Equal(2, library.GroupsSnapshot().Count);

        Assert.Equal(1, library.DeleteGroup(group.GroupId));
        Assert.Null(library.Find(a.ClipId)!.GroupId); // ungrouped, not deleted
        Assert.Equal(2, library.ItemsSnapshot().Count);
        Assert.Single(library.GroupsSnapshot());
    }

    [Fact]
    public void Delete_removes_managed_copy_but_never_the_original()
    {
        var source = SourceFile("kill.wav");
        using var library = OpenLib();
        var item = library.Import(source).Item;

        Assert.True(library.Delete(item.ClipId));

        Assert.Null(library.Find(item.ClipId));
        Assert.False(File.Exists(Path.Combine(library.Root, item.ManagedAssetPath)));
        Assert.True(File.Exists(source)); // the user's original survives (T15)
        using var reopened = OpenLib();
        Assert.Null(reopened.Find(item.ClipId));
    }

    [Fact]
    public void Missing_asset_reports_unavailable()
    {
        using var library = OpenLib();
        var item = library.Import(SourceFile("gone.wav")).Item;
        File.Delete(Path.Combine(library.Root, item.ManagedAssetPath));

        Assert.False(library.IsAvailable(item.ClipId));
        Assert.True(library.IsAvailable("clip-does-not-exist") is false);
    }

    [Fact]
    public void Corrupt_index_throws_and_is_never_overwritten()
    {
        var libRoot = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(libRoot);
        var index = Path.Combine(libRoot, "index.json");
        File.WriteAllText(index, "{ broken");

        Assert.Throws<ClipLibraryLoadException>(() => OpenLib());
        Assert.Equal("{ broken", File.ReadAllText(index)); // refuse to operate, keep the file
    }

    [Fact]
    public void Path_escaping_asset_entries_are_rejected_on_load()
    {
        var libRoot = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(libRoot);
        File.WriteAllText(
            Path.Combine(libRoot, "index.json"),
            """{"schemaVersion":1,"items":[{"clipId":"clip-x","displayName":"X","managedAssetPath":"../../escape.wav","durationMs":10,"format":"wav","favorite":false,"gain":1.0,"importUnixMs":100}],"groups":[]}""");

        Assert.Throws<ClipLibraryLoadException>(() => OpenLib());
    }

    [Fact]
    public void Gain_is_validated_and_persisted()
    {
        using var library = OpenLib();
        var item = library.Import(SourceFile("g.wav")).Item;

        Assert.False(library.SetGain(item.ClipId, float.NaN));
        Assert.False(library.SetGain(item.ClipId, 5f));
        Assert.True(library.SetGain(item.ClipId, 0.5f));

        using var reopened = OpenLib();
        Assert.Equal(0.5f, reopened.Find(item.ClipId)!.Gain);
    }

    [Fact]
    public void Prefill_orders_favorites_first_then_newest()
    {
        using var library = OpenLib();
        var a = library.Import(SourceFile("a1.wav")).Item;
        Thread.Sleep(15);
        var b = library.Import(SourceFile("b1.wav")).Item;
        Thread.Sleep(15);
        var c = library.Import(SourceFile("c1.wav")).Item;
        library.SetFavorite(c.ClipId, true); // oldest but favorite -> first

        var order = library.PrefillCandidates().Select(i => i.ClipId).ToArray();
        Assert.Equal([c.ClipId, b.ClipId, a.ClipId], order);
    }
}

public sealed class ClipCacheTests
{
    private const int N = 4410;

    [Fact]
    public void Lru_eviction_uses_recency_not_insertion_order()
    {
        var cache = new ClipCache(2 * N);
        cache.Store("a", new float[N]);
        cache.Store("b", new float[N]);
        Assert.True(cache.TryGet("a", out _)); // refresh a: b is now the LRU
        cache.Store("c", new float[N]); // evicts b

        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(1, cache.Stats().Evictions);
    }

    [Fact]
    public void Oversize_clip_is_declined_without_evicting()
    {
        var cache = new ClipCache(2 * N);
        cache.Store("a", new float[N]);

        cache.Store("huge", new float[5 * N]);

        Assert.True(cache.TryGet("a", out _)); // existing entries untouched
        Assert.Equal(1, cache.Stats().DeclinedOversize);
        Assert.Equal(0, cache.Stats().Evictions);
    }

    [Fact]
    public void Remove_drops_the_entry()
    {
        var cache = new ClipCache(10 * N);
        cache.Store("a", new float[N]);
        cache.Remove("a");
        Assert.False(cache.TryGet("a", out _));
        cache.Remove("a"); // idempotent
    }

    [Fact]
    public void Store_updates_an_existing_entry_in_place()
    {
        var cache = new ClipCache(10 * N);
        cache.Store("a", new float[N]);
        var replacement = new float[2 * N];
        cache.Store("a", replacement);

        Assert.True(cache.TryGet("a", out var samples));
        Assert.Same(replacement, samples);
        Assert.Equal(1, cache.Stats().Count);
        Assert.Equal(2 * N, cache.Stats().TotalFrames);
    }

    [Fact]
    public void Stats_track_hits_and_misses()
    {
        var cache = new ClipCache(10 * N);
        cache.TryGet("a", out _);
        cache.Store("a", new float[N]);
        cache.TryGet("a", out _);

        var stats = cache.Stats();
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
    }

    [Fact]
    public void Prefill_keeps_priority_entries_when_the_budget_runs_out()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onbodymic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var decoder = new StubDecoder(); // 100 ms per clip
            using var library = ClipLibrary.Open(Path.Combine(dir, "lib"), decoder);
            var favorite = library.Import(TempSource(dir, "f.wav")).Item;
            Thread.Sleep(15);
            var older = library.Import(TempSource(dir, "o1.wav")).Item;
            Thread.Sleep(15);
            var newer = library.Import(TempSource(dir, "o2.wav")).Item;
            library.SetFavorite(favorite.ClipId, true);

            var cache = new ClipCache(2 * N); // exactly two clips fit
            var cached = cache.Prefill(library, decoder);

            // priority order is favorite, then newest: the third is declined,
            // never squeezed in by evicting an already-cached entry
            Assert.Equal(2, cached);
            Assert.True(cache.TryGet(favorite.ClipId, out _));
            Assert.True(cache.TryGet(newer.ClipId, out _));
            Assert.False(cache.TryGet(older.ClipId, out _));
            Assert.Equal(0, cache.Stats().Evictions);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string TempSource(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, $"{name}-{Guid.NewGuid():N}");
        return path;
    }
}
