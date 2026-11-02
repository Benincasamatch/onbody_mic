namespace OnbodyMic.Media.Tests;

using System.Text.Json.Nodes;
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
        var path = Path.GetFullPath(Path.Combine(_dir, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private string ExportedWav(string name, short sample = 1638)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        const int samples = 4410;
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * sizeof(short));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(44100);
        writer.Write(44100 * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * sizeof(short));
        for (var i = 0; i < samples; i++) writer.Write(sample);
        return path;
    }

    private ClipLibrary OpenLib(ClipLibraryLimits? limits = null) => ClipLibrary.Open(Path.Combine(_dir, "lib"), _decoder, limits);

    [Fact]
    public void Verbatim_windows_root_keeps_portable_managed_assets_available()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = SourceFile("verbatim.wav", "controlled asset bytes");
        var root = Path.Combine(_dir, "verbatim-lib");
        using var library = ClipLibrary.Open(@"\\?\" + root, _decoder);
        var imported = library.Import(source).Item;

        Assert.True(library.IsAvailable(imported.ClipId));
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(library.AssetAbsolutePath(imported)));
        using var reopened = ClipLibrary.Open(@"\\?\" + root, _decoder);
        Assert.True(reopened.IsAvailable(imported.ClipId));
    }

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
    public void Root_groups_reject_duplicate_names_and_deleting_only_ungroups()
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
    public void Direct_file_plans_keep_selection_order_without_creating_groups_or_decoding()
    {
        using var library = OpenLib();
        var ten = SourceFile("10.WAV");
        var two = SourceFile("2.mp3");
        var plan = library.PrepareFolderImport([ten, two]);
        Assert.Equal(new[] { ten, two }, plan.Files.Select(file => file.Path));
        Assert.All(plan.Files, file => Assert.Null(file.GroupId));
        Assert.Empty(plan.RootGroupIds);
        Assert.Empty(library.GroupsSnapshot());
        Assert.Empty(library.ItemsSnapshot());
        Assert.Equal(0, _decoder.Calls);
        Assert.False(File.Exists(Path.Combine(library.Root, "index.json")));
    }

    [Fact]
    public void Folder_plan_preserves_mixed_input_natural_depth_first_order_and_hierarchy_after_reopen()
    {
        var root = Path.Combine(_dir, "合集10");
        Directory.CreateDirectory(Path.Combine(root, "分组10"));
        Directory.CreateDirectory(Path.Combine(root, "分组2", "空目录"));
        var first = SourceFile("先导.wav");
        var last = SourceFile("末尾.MP3");
        var expected = new[]
        {
            first,
            Write("合集10/音效2.wav", "two"),
            Write("合集10/音效02.wav", "padded"),
            Write("合集10/音效10.MP3", "ten"),
            Write("合集10/音效99999999999999999999.wav", "large"),
            Write("合集10/音效100000000000000000000.wav", "larger"),
            last,
        };
        var child2 = Write("合集10/分组2/子音2.wav", "child two");
        var child10 = Write("合集10/分组10/子音10.mp3", "child ten");
        Write("合集10/说明.txt", "not audio");
        using var library = OpenLib();
        var changes = 0;
        library.Changed += () => changes++;

        var plan = library.PrepareFolderImport([first, root, last]);

        Assert.Equal(new[] { first, child2, child10 }.Concat(expected.Skip(1)), plan.Files.Select(f => f.Path));
        Assert.Null(plan.Files[0].GroupId);
        Assert.Null(plan.Files[^1].GroupId);
        Assert.Equal(0, _decoder.Calls);
        Assert.Empty(library.ItemsSnapshot());
        Assert.Equal(1, changes);
        var groups = library.GroupsSnapshot();
        Assert.Equal(new[] { "合集10", "分组2", "空目录", "分组10" }, groups.Select(g => g.Name));
        var rootGroup = groups[0];
        Assert.Equal(new[] { rootGroup.GroupId }, plan.RootGroupIds);
        Assert.Equal(rootGroup.GroupId, groups[1].ParentGroupId);
        Assert.Equal(groups[1].GroupId, groups[2].ParentGroupId);
        Assert.Equal(rootGroup.GroupId, groups[3].ParentGroupId);
        Assert.Equal(groups[1].GroupId, plan.Files[1].GroupId);
        Assert.Equal(groups[3].GroupId, plan.Files[2].GroupId);
        foreach (var file in plan.Files) library.Import(file.Path, groupId: file.GroupId);

        using var reopened = OpenLib();
        Assert.Equal(groups, reopened.GroupsSnapshot());
        Assert.Equal(plan.Files.Select(f => f.Path), reopened.Search(sort: ClipSortOrder.LibraryOrder).Select(c => c.OriginalImportPath));
        Assert.Equal(plan.Files.Length - 2, reopened.Search(groupId: rootGroup.GroupId).Count);
        var repeated = reopened.PrepareFolderImport([first, root, last]);
        Assert.Equal(plan.Files, repeated.Files);
        Assert.Equal(plan.RootGroupIds, repeated.RootGroupIds);
        Assert.Equal(groups, reopened.GroupsSnapshot());
    }

    [Fact]
    public void Folder_roots_keep_input_order_and_child_names_are_unique_only_among_siblings()
    {
        var root10 = Path.Combine(_dir, "root10");
        var root2 = Path.Combine(_dir, "root2");
        Directory.CreateDirectory(Path.Combine(root10, "same"));
        Directory.CreateDirectory(Path.Combine(root2, "same"));
        using var library = OpenLib();

        var plan = library.PrepareFolderImport([root10, root2]);
        var groups = library.GroupsSnapshot();
        Assert.Equal(new[] { "root10", "same", "root2", "same" }, groups.Select(g => g.Name));
        Assert.Equal(new[] { groups[0].GroupId, groups[2].GroupId }, plan.RootGroupIds);
        Assert.Empty(plan.Files);
        Assert.Null(library.CreateGroup("same", groups[0].GroupId));
        Assert.Null(library.CreateGroup("missing", "group-does-not-exist"));
        var child = library.CreateGroup("another", groups[0].GroupId)!;
        Assert.Equal(child.GroupId, library.GroupsSnapshot()[2].GroupId);
        Assert.False(library.RenameGroup(child.GroupId, "same"));
        Assert.True(library.RenameGroup(child.GroupId, "renamed"));
        using var reopened = OpenLib();
        Assert.Equal(library.GroupsSnapshot(), reopened.GroupsSnapshot());
    }

    [Fact]
    public void Folder_duplicate_choice_groups_only_new_or_forced_copies()
    {
        var root = Path.Combine(_dir, "duplicate-folder");
        Directory.CreateDirectory(root);
        var source = Write("duplicate-folder/same.wav", "identical");
        using var library = OpenLib();
        var originalGroup = library.CreateGroup("Original")!;
        var original = library.Import(source, groupId: originalGroup.GroupId).Item;
        library.SetFavorite(original.ClipId, true);
        library.SetGain(original.ClipId, 0.5f);
        library.Rename(original.ClipId, "Keep me");
        original = library.Find(original.ClipId)!;
        var file = library.PrepareFolderImport([root]).Files.Single();

        var duplicate = library.Import(file.Path, groupId: file.GroupId);
        Assert.Equal(ImportOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(original, duplicate.Item);
        var forced = library.Import(file.Path, allowDuplicate: true, groupId: file.GroupId);
        Assert.Equal(ImportOutcome.Imported, forced.Outcome);
        Assert.Equal(file.GroupId, forced.Item.GroupId);
        Assert.Equal(original, library.Find(original.ClipId));
        using var reopened = OpenLib();
        Assert.Equal(original, reopened.Find(original.ClipId));
        Assert.Equal(forced.Item, reopened.Find(forced.Item.ClipId));
        Assert.Equal(new[] { original.ClipId, forced.Item.ClipId }, reopened.Search(sort: ClipSortOrder.LibraryOrder).Select(c => c.ClipId));
    }

    [Fact]
    public void Parent_delete_removes_all_descendant_groups_but_never_assets()
    {
        using var library = OpenLib();
        var parent = library.CreateGroup("parent")!;
        var sibling = library.CreateGroup("sibling")!;
        var child = library.CreateGroup("child", parent.GroupId)!;
        var grandchild = library.CreateGroup("grandchild", child.GroupId)!;
        var a = library.Import(SourceFile("parent.wav"), groupId: parent.GroupId).Item;
        var b = library.Import(SourceFile("child.wav"), groupId: child.GroupId).Item;
        var c = library.Import(SourceFile("grandchild.wav"), groupId: grandchild.GroupId).Item;
        var d = library.Import(SourceFile("sibling.wav"), groupId: sibling.GroupId).Item;
        Assert.Equal(new[] { a.ClipId, b.ClipId, c.ClipId }, library.Search(sort: ClipSortOrder.LibraryOrder, groupId: parent.GroupId).Select(i => i.ClipId));

        Assert.Equal(3, library.DeleteGroup(parent.GroupId));

        Assert.Equal(sibling, library.GroupsSnapshot().Single());
        Assert.Equal(sibling.GroupId, library.Find(d.ClipId)!.GroupId);
        using var reopened = OpenLib();
        foreach (var item in new[] { a, b, c })
        {
            Assert.Null(reopened.Find(item.ClipId)!.GroupId);
            Assert.True(File.Exists(reopened.AssetAbsolutePath(item)));
            Assert.True(File.Exists(item.OriginalImportPath));
        }
        Assert.Equal(4, reopened.ItemsSnapshot().Count);
        Assert.Equal(0, reopened.DeleteGroup(parent.GroupId));
    }

    [Fact]
    public void Legacy_flat_groups_load_as_roots()
    {
        var libRoot = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(libRoot);
        Write("lib/index.json", """{"schemaVersion":1,"items":[],"groups":[{"groupId":"group-old","name":"旧分组"}]}""");

        using var library = OpenLib();
        Assert.Equal(new ClipGroup("group-old", "旧分组"), library.GroupsSnapshot().Single());
        var child = library.CreateGroup("child", "group-old")!;
        using var reopened = OpenLib();
        Assert.Equal("group-old", reopened.GroupsSnapshot().Single(g => g.GroupId == child.GroupId).ParentGroupId);
    }

    [Theory]
    [InlineData("""[{"groupId":"a","name":"A","parentGroupId":"missing"}]""")]
    [InlineData("""[{"groupId":"a","name":"A","parentGroupId":"a"}]""")]
    [InlineData("""[{"groupId":"a","name":"A","parentGroupId":"b"},{"groupId":"b","name":"B","parentGroupId":"a"}]""")]
    public void Invalid_parent_hierarchies_refuse_to_load_without_overwriting(string groups)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        var json = "{\"schemaVersion\":1,\"items\":[],\"groups\":" + groups + "}";
        var index = Write("lib/index.json", json);
        Assert.Throws<ClipLibraryLoadException>(() => OpenLib());
        Assert.Equal(json, File.ReadAllText(index));
    }

    [Fact]
    public void Folder_enumeration_failure_does_not_persist_partial_groups()
    {
        using var library = OpenLib();
        var root = Path.Combine(_dir, "valid");
        Directory.CreateDirectory(root);
        var index = Path.Combine(library.Root, "index.json");
        library.CreateGroup("existing");
        var before = File.ReadAllText(index);
        Assert.ThrowsAny<IOException>(() => library.PrepareFolderImport([root, Path.Combine(_dir, "missing")]));
        Assert.Single(library.GroupsSnapshot());
        Assert.Equal(before, File.ReadAllText(index));
    }

    [Fact]
    public void Failed_group_mutations_and_import_preserve_memory_disk_assets_and_library_order()
    {
        using var library = OpenLib();
        var root = library.CreateGroup("Root")!;
        var child = library.CreateGroup("Child", root.GroupId)!;
        var first = library.Import(SourceFile("first.wav"), groupId: child.GroupId).Item;
        library.Import(SourceFile("second.wav"));
        var originalGroups = library.GroupsSnapshot();
        var originalItems = library.ItemsSnapshot();
        var originalAssets = Directory.GetFiles(Path.Combine(library.Root, "assets"));
        var index = Path.Combine(library.Root, "index.json");
        var originalIndex = File.ReadAllText(index);
        Directory.CreateDirectory(index + ".tmp");
        var folder = Path.Combine(_dir, "new-root");
        Directory.CreateDirectory(folder);
        var newSource = SourceFile("third.wav");
        var selection = ExportedWav("selection.wav");
        var origin = new ClipOrigin("trim", first.ClipId, first.OriginalImportPath!, 0, 100);
        var changes = 0;
        library.Changed += () => changes++;

        Action[] mutations =
        [
            () => library.PrepareFolderImport([folder]),
            () => library.CreateGroup("new-child", root.GroupId),
            () => library.RenameGroup(root.GroupId, "renamed"),
            () => library.DeleteGroup(root.GroupId),
            () => library.DeleteGroupAndClips(root.GroupId),
            () => library.Import(newSource, groupId: child.GroupId),
            () => library.ImportSelection(selection, origin, "selection", groupId: child.GroupId),
            () => library.Delete(first.ClipId),
        ];
        foreach (var mutate in mutations)
        {
            var failure = Record.Exception(mutate);
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Equal(originalGroups, library.GroupsSnapshot());
            Assert.Equal(originalItems, library.Search(sort: ClipSortOrder.LibraryOrder));
            Assert.Equal(originalIndex, File.ReadAllText(index));
            Assert.Equal(originalAssets.Order(), Directory.GetFiles(Path.Combine(library.Root, "assets")).Order());
            Assert.Empty(Directory.GetDirectories(library.Root, ".delete-*"));
            Assert.Equal(0, changes);
        }
        Assert.True(File.Exists(selection));
        Assert.True(File.Exists(first.OriginalImportPath));
        Assert.Throws<ClipImportException>(() => library.Import(newSource, groupId: "group-missing"));
        Directory.Delete(index + ".tmp");
        using var reopened = OpenLib();
        Assert.Equal(originalGroups, reopened.GroupsSnapshot());
        Assert.Equal(originalItems, reopened.Search(sort: ClipSortOrder.LibraryOrder));
    }

    [Fact]
    public void Folder_plan_skips_reparse_directories_including_cycles_and_root_links()
    {
        var root = Path.Combine(_dir, "normal");
        var external = Path.Combine(_dir, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var source = Write("normal/real.wav", "real");
        Write("external/hidden.wav", "outside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "loop"), root);
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), external);
            Directory.CreateSymbolicLink(Path.Combine(_dir, "root-link"), external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Windows may require Developer Mode or symbolic-link privileges.
        }
        using var library = OpenLib();
        var plan = library.PrepareFolderImport([root, Path.Combine(_dir, "root-link")]);
        Assert.Equal(source, plan.Files.Single().Path);
        Assert.Equal("normal", library.GroupsSnapshot().Single().Name);
        Assert.Single(plan.RootGroupIds);
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

    [Fact]
    public void Trimmed_selection_reopens_with_origin_and_never_persists_the_temporary_export_path()
    {
        using var library = OpenLib();
        var source = SourceFile("original.mp3");
        var sourceBytes = File.ReadAllBytes(source);
        _decoder.Generator = _ => new float[44100];
        var parent = library.Import(source).Item;
        _decoder.Generator = _ => new float[4410];
        var group = library.CreateGroup("Crops")!;
        var export = ExportedWav("temporary/exported-selection.wav");
        var exportedBytes = File.ReadAllBytes(export);
        var origin = new ClipOrigin("trim", parent.ClipId, source, 250, 350);
        ClipItem? notifiedItem = null;
        library.Changed += () =>
        {
            var item = library.ItemsSnapshot().Last();
            notifiedItem = item;
            Assert.Equal(exportedBytes, File.ReadAllBytes(library.AssetAbsolutePath(item)));
        };

        var result = library.ImportSelection(export, origin, "  Selected sound  ", groupId: group.GroupId);
        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal(result.Item, notifiedItem);
        Assert.Equal("Selected sound", result.Item.DisplayName);
        Assert.Equal(origin, result.Item.Origin);
        Assert.Equal(source, result.Item.OriginalImportPath);
        Assert.Equal("wav", result.Item.Format);
        Assert.Equal(100, result.Item.DurationMs);
        Assert.True(library.SetFavorite(result.Item.ClipId, true));
        Assert.True(library.SetGain(result.Item.ClipId, 0.75f));
        var persistedItem = library.Find(result.Item.ClipId)!;
        var index = File.ReadAllText(Path.Combine(library.Root, "index.json"));
        Assert.Equal(2, JsonNode.Parse(index)!["schemaVersion"]!.GetValue<int>());
        Assert.DoesNotContain(Path.GetFileName(export), index);
        Assert.Equal(exportedBytes, File.ReadAllBytes(library.AssetAbsolutePath(persistedItem)));
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        File.Delete(export);

        using var reopened = OpenLib();
        Assert.Equal(persistedItem, reopened.Find(persistedItem.ClipId));
        Assert.Null(reopened.Find(parent.ClipId)!.Origin);
        Assert.True(reopened.IsAvailable(persistedItem.ClipId));
        Assert.Equal(exportedBytes, File.ReadAllBytes(reopened.AssetAbsolutePath(persistedItem)));
    }

    [Fact]
    public void Legacy_schema_one_keeps_metadata_and_order_then_migrates_only_after_a_successful_mutation()
    {
        using var library = OpenLib();
        var group = library.CreateGroup("Legacy")!;
        var first = library.Import(SourceFile("legacy-first.wav"), groupId: group.GroupId).Item;
        var second = library.Import(SourceFile("legacy-second.mp3")).Item;
        library.SetFavorite(first.ClipId, true);
        library.SetGain(first.ClipId, 0.5f);
        var before = library.ItemsSnapshot();
        var groups = library.GroupsSnapshot();
        var indexPath = Path.Combine(library.Root, "index.json");
        var document = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
        document["schemaVersion"] = 1;
        var legacyJson = document.ToJsonString();
        File.WriteAllText(indexPath, legacyJson);

        using var legacy = OpenLib();
        Assert.Equal(before, legacy.ItemsSnapshot());
        Assert.Equal(groups, legacy.GroupsSnapshot());
        Assert.All(legacy.ItemsSnapshot(), item => Assert.Null(item.Origin));
        Assert.Equal(legacyJson, File.ReadAllText(indexPath));
        Directory.CreateDirectory(indexPath + ".tmp");
        var failure = Record.Exception(() => legacy.Rename(second.ClipId, "Migrated"));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(before, legacy.ItemsSnapshot());
        Assert.Equal(legacyJson, File.ReadAllText(indexPath));
        Directory.Delete(indexPath + ".tmp");

        Assert.True(legacy.Rename(second.ClipId, "Migrated"));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(indexPath))!["schemaVersion"]!.GetValue<int>());
        using var reopened = OpenLib();
        Assert.Equal(legacy.ItemsSnapshot(), reopened.ItemsSnapshot());
        Assert.Equal(groups, reopened.GroupsSnapshot());
        Assert.Equal(new[] { first.ClipId, second.ClipId }, reopened.ItemsSnapshot().Select(item => item.ClipId));
        Assert.True(reopened.Find(first.ClipId)!.Favorite);
        Assert.Equal(0.5f, reopened.Find(first.ClipId)!.Gain);
        Assert.All(reopened.ItemsSnapshot(), item => Assert.Null(item.Origin));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(99)]
    public void Unknown_index_schemas_are_refused_without_overwriting(int version)
    {
        var json = "{\"schemaVersion\":" + version + ",\"items\":[],\"groups\":[]}";
        var index = Write("lib/index.json", json);
        Assert.Throws<ClipLibraryLoadException>(() => OpenLib());
        Assert.Equal(json, File.ReadAllText(index));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("relativePath")]
    [InlineData("emptyPath")]
    [InlineData("emptyParent")]
    [InlineData("selfParent")]
    [InlineData("negativeStart")]
    [InlineData("emptyRange")]
    [InlineData("tooLong")]
    [InlineData("format")]
    [InlineData("temporaryOriginalImportPath")]
    [InlineData("schemaOneOrigin")]
    public void Invalid_persisted_origins_refuse_to_load_without_overwriting(string invalidField)
    {
        using var library = OpenLib();
        var source = SourceFile("origin-source.mp3");
        var export = ExportedWav("origin-export.wav");
        var item = library.ImportSelection(export, new ClipOrigin("trim", null, source, 0, 100), "Crop").Item;
        var indexPath = Path.Combine(library.Root, "index.json");
        var document = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
        var dto = document["items"]![0]!.AsObject();
        var origin = dto["origin"]!.AsObject();
        switch (invalidField)
        {
            case "kind": origin["kind"] = "copy"; break;
            case "relativePath": origin["originalPath"] = "relative.wav"; break;
            case "emptyPath": origin["originalPath"] = " "; break;
            case "emptyParent": origin["parentClipId"] = " "; break;
            case "selfParent": origin["parentClipId"] = item.ClipId; break;
            case "negativeStart": origin["startMs"] = -1; break;
            case "emptyRange": origin["endMs"] = 0; break;
            case "tooLong": origin["endMs"] = 60001; break;
            case "format": dto["format"] = "mp3"; break;
            case "temporaryOriginalImportPath": dto["originalImportPath"] = export; break;
            case "schemaOneOrigin": document["schemaVersion"] = 1; break;
            default: throw new InvalidOperationException(invalidField);
        }
        var json = document.ToJsonString();
        File.WriteAllText(indexPath, json);

        Assert.Throws<ClipLibraryLoadException>(() => OpenLib());
        Assert.Equal(json, File.ReadAllText(indexPath));
        Assert.True(File.Exists(library.AssetAbsolutePath(item)));
    }

    [Theory]
    [InlineData("copy", null, 0, 100, false)]
    [InlineData("trim", " ", 0, 100, false)]
    [InlineData("trim", null, -1, 100, false)]
    [InlineData("trim", null, 100, 100, false)]
    [InlineData("trim", null, 100, 0, false)]
    [InlineData("trim", null, 0, 60001, false)]
    [InlineData("trim", null, 0, int.MaxValue, false)]
    [InlineData("trim", null, 0, 100, true)]
    public void Invalid_selection_origins_are_rejected_before_copying_or_decoding(string kind, string? parentId, int startMs, int endMs, bool relativePath)
    {
        using var library = OpenLib();
        var export = ExportedWav("invalid-origin-export.wav");
        var originalPath = relativePath ? "relative.wav" : Path.Combine(_dir, "original.mp3");
        var origin = new ClipOrigin(kind, parentId, originalPath, startMs, endMs);

        Assert.Throws<ClipImportException>(() => library.ImportSelection(export, origin, "Crop"));
        Assert.Equal(0, _decoder.Calls);
        Assert.Empty(library.ItemsSnapshot());
        Assert.Empty(Directory.GetFiles(Path.Combine(library.Root, "assets")));
        Assert.False(File.Exists(Path.Combine(library.Root, "index.json")));
        Assert.True(File.Exists(export));
    }

    [Fact]
    public void Selection_import_requires_wav_and_a_name_and_applies_final_size_and_duration_limits()
    {
        var export = ExportedWav("bounded-export.wav");
        var source = SourceFile("bounded-source.mp3");
        var origin = new ClipOrigin("trim", null, source, 0, 100);
        using var library = OpenLib();
        Assert.Throws<ClipImportException>(() => library.ImportSelection(source, origin, "Crop"));
        Assert.Throws<ClipImportException>(() => library.ImportSelection(export, origin, " "));
        Assert.Throws<ClipImportException>(() => library.ImportSelection(export, null!, "Crop"));
        Assert.Empty(library.ItemsSnapshot());
        Assert.Empty(Directory.GetFiles(Path.Combine(library.Root, "assets")));

        using var sizeLimited = OpenLib(new ClipLibraryLimits(8, 60));
        Assert.Throws<ClipImportException>(() => sizeLimited.ImportSelection(export, origin, "Crop"));
        Assert.Equal(0, _decoder.Calls);
        _decoder.Generator = _ => new float[44100 * 2];
        using var durationLimited = OpenLib(new ClipLibraryLimits(32 * 1024 * 1024, 1));
        Assert.Throws<ClipImportException>(() => durationLimited.ImportSelection(export, origin, "Crop"));
        Assert.Empty(durationLimited.ItemsSnapshot());
        Assert.Empty(Directory.GetFiles(Path.Combine(durationLimited.Root, "assets")));
        Assert.True(File.Exists(export));
    }

    [Fact]
    public void Duplicate_crops_preserve_the_existing_origin_and_forced_copies_link_the_requested_parent()
    {
        using var library = OpenLib();
        var sourceA = SourceFile("parent-a.mp3");
        var sourceB = SourceFile("parent-b.mp3");
        var parentA = library.Import(sourceA).Item;
        var parentB = library.Import(sourceB).Item;
        var groupA = library.CreateGroup("Existing")!;
        var groupB = library.CreateGroup("Requested")!;
        var exportA = ExportedWav("export-a.wav");
        var exportB = ExportedWav("export-b.wav");
        var originA = new ClipOrigin("trim", parentA.ClipId, sourceA, 0, 100);
        var originB = new ClipOrigin("trim", parentB.ClipId, sourceB, 0, 100);
        var first = library.ImportSelection(exportA, originA, "First", groupId: groupA.GroupId).Item;
        library.SetFavorite(first.ClipId, true);
        library.SetGain(first.ClipId, 0.5f);
        library.Rename(first.ClipId, "Keep this crop");
        first = library.Find(first.ClipId)!;
        var indexPath = Path.Combine(library.Root, "index.json");
        var before = File.ReadAllText(indexPath);
        var changes = 0;
        library.Changed += () => changes++;

        var duplicate = library.ImportSelection(exportB, originB, "Second", groupId: groupB.GroupId);
        Assert.Equal(ImportOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(first, duplicate.Item);
        Assert.Equal(first, duplicate.ExistingDuplicate);
        Assert.Equal(before, File.ReadAllText(indexPath));
        Assert.Equal(0, changes);
        var forced = library.ImportSelection(exportB, originB, "Second", allowDuplicate: true, groupId: groupB.GroupId);
        Assert.Equal(ImportOutcome.Imported, forced.Outcome);
        Assert.NotEqual(first.ClipId, forced.Item.ClipId);
        Assert.NotEqual(first.ManagedAssetPath, forced.Item.ManagedAssetPath);
        Assert.Equal(originB, forced.Item.Origin);
        Assert.Equal(groupB.GroupId, forced.Item.GroupId);
        Assert.Equal(first, library.Find(first.ClipId));
        Assert.Equal(1, changes);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(library.Root, "assets")).Length);

        using var reopened = OpenLib();
        Assert.Equal(first, reopened.Find(first.ClipId));
        Assert.Equal(forced.Item, reopened.Find(forced.Item.ClipId));
        Assert.Equal(new[] { parentA.ClipId, parentB.ClipId, first.ClipId, forced.Item.ClipId }, reopened.ItemsSnapshot().Select(item => item.ClipId));
    }

    [Fact]
    public void Full_selection_duplicate_preserves_normal_parent_metadata_until_explicitly_forced()
    {
        using var library = OpenLib();
        var source = ExportedWav("normal-parent.wav");
        var export = ExportedWav("full-selection.wav");
        var parent = library.Import(source, displayName: "Original").Item;
        library.SetFavorite(parent.ClipId, true);
        library.SetGain(parent.ClipId, 0.25f);
        parent = library.Find(parent.ClipId)!;
        var group = library.CreateGroup("New copies")!;
        var origin = new ClipOrigin("trim", parent.ClipId, source, 0, 100);

        var duplicate = library.ImportSelection(export, origin, "Crop", groupId: group.GroupId);
        Assert.Equal(ImportOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(parent, duplicate.Item);
        Assert.Null(duplicate.Item.Origin);
        Assert.Single(library.ItemsSnapshot());
        var forced = library.ImportSelection(export, origin, "Crop", allowDuplicate: true, groupId: group.GroupId).Item;
        Assert.Equal(origin, forced.Origin);
        Assert.Equal(group.GroupId, forced.GroupId);
        Assert.False(forced.Favorite);
        Assert.Equal(1f, forced.Gain);
        Assert.Equal(parent, library.Find(parent.ClipId));
        Assert.True(library.Delete(parent.ClipId));
        Assert.True(library.IsAvailable(forced.ClipId));
        Assert.True(File.Exists(source));
        using var reopened = OpenLib();
        Assert.Equal(forced, reopened.Find(forced.ClipId));
    }

    [Fact]
    public void Deleting_parent_never_damages_an_independent_crop_or_its_dangling_provenance()
    {
        using var library = OpenLib();
        var source = ExportedWav("deletable-parent.wav");
        var originalBytes = File.ReadAllBytes(source);
        var export = ExportedWav("independent-crop.wav", sample: 500);
        var croppedBytes = File.ReadAllBytes(export);
        var parent = library.Import(source).Item;
        var origin = new ClipOrigin("trim", parent.ClipId, source, 0, 100);
        var crop = library.ImportSelection(export, origin, "Independent crop").Item;
        File.Delete(export);

        Assert.True(library.Delete(parent.ClipId));
        Assert.False(File.Exists(library.AssetAbsolutePath(parent)));
        Assert.Equal(originalBytes, File.ReadAllBytes(source));
        Assert.Equal(croppedBytes, File.ReadAllBytes(library.AssetAbsolutePath(crop)));
        Assert.True(library.IsAvailable(crop.ClipId));
        File.Delete(source);

        using var reopened = OpenLib();
        Assert.Null(reopened.Find(parent.ClipId));
        Assert.Equal(crop, reopened.Find(crop.ClipId));
        Assert.Equal(origin, reopened.Find(crop.ClipId)!.Origin);
        Assert.True(reopened.IsAvailable(crop.ClipId));
        Assert.Equal(croppedBytes, File.ReadAllBytes(reopened.AssetAbsolutePath(crop)));
    }

    [Fact]
    public void Subtree_delete_removes_only_contained_copies_and_preserves_outside_order_metadata_and_crops()
    {
        using var library = OpenLib();
        var outsideBefore = library.CreateGroup("Before")!;
        var root = library.CreateGroup("Delete")!;
        var child = library.CreateGroup("Child", root.GroupId)!;
        var grandchild = library.CreateGroup("Grandchild", child.GroupId)!;
        var outsideAfter = library.CreateGroup("After")!;
        var outsideA = library.Import(SourceFile("outside-a.wav"), groupId: outsideBefore.GroupId).Item;
        var rootClip = library.Import(SourceFile("root-clip.mp3"), groupId: root.GroupId).Item;
        var export = ExportedWav("outside-derived.wav");
        var cropOrigin = new ClipOrigin("trim", rootClip.ClipId, rootClip.OriginalImportPath!, 0, 100);
        var outsideCrop = library.ImportSelection(export, cropOrigin, "Outside crop", groupId: outsideBefore.GroupId).Item;
        var childClip = library.Import(SourceFile("child-clip.wav"), groupId: child.GroupId).Item;
        var outsideB = library.Import(SourceFile("outside-b.wav"), groupId: outsideAfter.GroupId).Item;
        var grandchildClip = library.Import(SourceFile("grandchild-clip.wav"), groupId: grandchild.GroupId).Item;
        library.SetFavorite(outsideA.ClipId, true);
        library.SetGain(outsideB.ClipId, 0.5f);
        var removed = new[] { rootClip, childClip, grandchildClip };
        var retained = library.ItemsSnapshot().Where(item => !removed.Any(deleted => deleted.ClipId == item.ClipId)).ToArray();
        var retainedGroups = library.GroupsSnapshot().Where(group => group.GroupId != root.GroupId && group.GroupId != child.GroupId && group.GroupId != grandchild.GroupId).ToArray();
        var originalFiles = library.ItemsSnapshot().Select(item => item.OriginalImportPath!).Distinct().ToDictionary(path => path, File.ReadAllBytes);
        var changes = 0;
        library.Changed += () => changes++;

        Assert.Equal(removed.Select(item => item.ClipId), library.DeleteGroupAndClips(root.GroupId));
        Assert.Equal(1, changes);
        Assert.Equal(retained, library.ItemsSnapshot());
        Assert.Equal(retainedGroups, library.GroupsSnapshot());
        foreach (var item in removed)
        {
            Assert.Null(library.Find(item.ClipId));
            Assert.False(File.Exists(library.AssetAbsolutePath(item)));
        }
        foreach (var item in retained) Assert.True(library.IsAvailable(item.ClipId));
        foreach (var original in originalFiles) Assert.Equal(original.Value, File.ReadAllBytes(original.Key));
        Assert.Equal(File.ReadAllBytes(export), File.ReadAllBytes(library.AssetAbsolutePath(outsideCrop)));
        Assert.Equal(retained.Length, Directory.GetFiles(Path.Combine(library.Root, "assets")).Length);
        Assert.Empty(Directory.GetDirectories(library.Root, ".delete-*"));
        Assert.Empty(library.DeleteGroupAndClips(root.GroupId));
        Assert.Equal(1, changes);

        using var reopened = OpenLib();
        Assert.Equal(retained, reopened.ItemsSnapshot());
        Assert.Equal(retainedGroups, reopened.GroupsSnapshot());
        Assert.Equal(cropOrigin, reopened.Find(outsideCrop.ClipId)!.Origin);
        Assert.True(reopened.Find(outsideA.ClipId)!.Favorite);
        Assert.Equal(0.5f, reopened.Find(outsideB.ClipId)!.Gain);
    }

    [Fact]
    public void Empty_subtree_delete_still_commits_group_removal_and_missing_groups_do_nothing()
    {
        using var library = OpenLib();
        var before = library.CreateGroup("Before")!;
        var root = library.CreateGroup("Empty")!;
        library.CreateGroup("Empty child", root.GroupId);
        var after = library.CreateGroup("After")!;
        var changes = 0;
        library.Changed += () => changes++;

        Assert.Empty(library.DeleteGroupAndClips(root.GroupId));
        Assert.Equal(new[] { before, after }, library.GroupsSnapshot());
        Assert.Equal(1, changes);
        Assert.Empty(library.DeleteGroupAndClips("group-missing"));
        Assert.Equal(1, changes);
        using var reopened = OpenLib();
        Assert.Equal(new[] { before, after }, reopened.GroupsSnapshot());
        Assert.Empty(reopened.ItemsSnapshot());
    }

    [Fact]
    public void Missing_managed_assets_do_not_block_subtree_deletion_or_affect_sources()
    {
        using var library = OpenLib();
        var group = library.CreateGroup("Unavailable")!;
        var item = library.Import(SourceFile("missing-managed.wav"), groupId: group.GroupId).Item;
        File.Delete(library.AssetAbsolutePath(item));

        Assert.Equal(new[] { item.ClipId }, library.DeleteGroupAndClips(group.GroupId));
        Assert.True(File.Exists(item.OriginalImportPath));
        Assert.Empty(library.ItemsSnapshot());
        Assert.Empty(library.GroupsSnapshot());
        Assert.Empty(Directory.GetDirectories(library.Root, ".delete-*"));
        using var reopened = OpenLib();
        Assert.Empty(reopened.ItemsSnapshot());
        Assert.Empty(reopened.GroupsSnapshot());
    }

    [Fact]
    public void Locked_later_subtree_asset_restores_earlier_staged_copies_and_the_entire_index()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var library = OpenLib();
        var root = library.CreateGroup("Locked subtree")!;
        var child = library.CreateGroup("Child", root.GroupId)!;
        var first = library.Import(SourceFile("stage-first.wav"), groupId: root.GroupId).Item;
        var outside = library.Import(SourceFile("keep-outside.wav")).Item;
        var locked = library.Import(SourceFile("locked-last.wav"), groupId: child.GroupId).Item;
        var beforeItems = library.ItemsSnapshot();
        var beforeGroups = library.GroupsSnapshot();
        var indexPath = Path.Combine(library.Root, "index.json");
        var beforeIndex = File.ReadAllText(indexPath);
        var bytes = beforeItems.ToDictionary(item => item.ClipId, item => File.ReadAllBytes(library.AssetAbsolutePath(item)));
        var changes = 0;
        library.Changed += () => changes++;
        using (var reservation = new FileStream(library.AssetAbsolutePath(locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => library.DeleteGroupAndClips(root.GroupId));
            Assert.Equal(beforeItems, library.ItemsSnapshot());
            Assert.Equal(beforeGroups, library.GroupsSnapshot());
            Assert.Equal(beforeIndex, File.ReadAllText(indexPath));
            Assert.Equal(bytes[first.ClipId], File.ReadAllBytes(library.AssetAbsolutePath(first)));
            Assert.True(File.Exists(library.AssetAbsolutePath(locked)));
            Assert.Empty(Directory.GetDirectories(library.Root, ".delete-*"));
            Assert.Equal(0, changes);
        }

        using (var reopened = OpenLib())
        {
            Assert.Equal(beforeItems, reopened.ItemsSnapshot());
            Assert.Equal(beforeGroups, reopened.GroupsSnapshot());
            foreach (var item in beforeItems) Assert.Equal(bytes[item.ClipId], File.ReadAllBytes(reopened.AssetAbsolutePath(item)));
        }
        Assert.Equal(new[] { first.ClipId, locked.ClipId }, library.DeleteGroupAndClips(root.GroupId));
        Assert.Equal(outside, library.ItemsSnapshot().Single());
        Assert.True(File.Exists(first.OriginalImportPath));
        Assert.True(File.Exists(locked.OriginalImportPath));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Locked_single_clip_refuses_deletion_without_changing_memory_or_disk()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var library = OpenLib();
        var item = library.Import(SourceFile("locked-single.wav")).Item;
        var indexPath = Path.Combine(library.Root, "index.json");
        var beforeIndex = File.ReadAllText(indexPath);
        var changes = 0;
        library.Changed += () => changes++;
        using (var reservation = new FileStream(library.AssetAbsolutePath(item), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => library.Delete(item.ClipId));
            Assert.Equal(item, library.Find(item.ClipId));
            Assert.Equal(beforeIndex, File.ReadAllText(indexPath));
            Assert.True(File.Exists(library.AssetAbsolutePath(item)));
            Assert.Empty(Directory.GetDirectories(library.Root, ".delete-*"));
            Assert.Equal(0, changes);
        }
        using var reopened = OpenLib();
        Assert.Equal(item, reopened.Find(item.ClipId));
        Assert.True(reopened.IsAvailable(item.ClipId));
        Assert.True(File.Exists(item.OriginalImportPath));
    }

    [Fact]
    public void Locked_selection_export_fails_before_decoding_or_publishing_assets()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var library = OpenLib();
        var export = ExportedWav("locked-export.wav");
        var origin = new ClipOrigin("trim", null, Path.Combine(_dir, "original.mp3"), 0, 100);
        using var reservation = new FileStream(export, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => library.ImportSelection(export, origin, "Crop"));
        Assert.Equal(0, _decoder.Calls);
        Assert.Empty(library.ItemsSnapshot());
        Assert.Empty(Directory.GetFiles(Path.Combine(library.Root, "assets")));
        Assert.False(File.Exists(Path.Combine(library.Root, "index.json")));
        Assert.True(File.Exists(export));
    }

    [Fact]
    public void Readonly_export_remains_untouched_while_its_independent_managed_copy_is_deletable()
    {
        var export = ExportedWav("readonly-export.wav");
        var originalAttributes = File.GetAttributes(export);
        File.SetAttributes(export, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            using var library = OpenLib();
            var origin = new ClipOrigin("trim", null, Path.Combine(_dir, "deleted-original.mp3"), 0, 100);
            var bytes = File.ReadAllBytes(export);
            var item = library.ImportSelection(export, origin, "Readonly source crop").Item;
            var managed = library.AssetAbsolutePath(item);
            Assert.False((File.GetAttributes(managed) & FileAttributes.ReadOnly) != 0);
            Assert.True(library.Delete(item.ClipId));
            Assert.False(File.Exists(managed));
            Assert.Equal(bytes, File.ReadAllBytes(export));
            Assert.Equal(originalAttributes | FileAttributes.ReadOnly, File.GetAttributes(export));
            using var reopened = OpenLib();
            Assert.Empty(reopened.ItemsSnapshot());
        }
        finally
        {
            File.SetAttributes(export, originalAttributes);
        }
    }

    [Fact]
    public void Folder_planning_retains_oversized_and_overlong_supported_sources_for_the_crop_flow()
    {
        using var library = OpenLib(new ClipLibraryLimits(8, 1));
        var root = Path.Combine(_dir, "crop-before-import");
        var wav = Write("crop-before-import/long.wav", new string('x', 64));
        var mp3 = Write("crop-before-import/long.mp3", new string('y', 64));
        _decoder.Generator = _ => new float[44100 * 2];

        var plan = library.PrepareFolderImport([root]);
        Assert.Equal(new[] { mp3, wav }, plan.Files.Select(file => file.Path));
        Assert.All(plan.Files, file => Assert.Equal(plan.RootGroupIds.Single(), file.GroupId));
        Assert.Equal(0, _decoder.Calls);
        Assert.Empty(library.ItemsSnapshot());
        Assert.Throws<ClipImportException>(() => library.Import(wav, groupId: plan.Files[1].GroupId));
        Assert.True(File.Exists(wav));
        Assert.True(File.Exists(mp3));
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
