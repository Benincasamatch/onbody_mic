using System.Globalization;
using OnbodyMic.Application;
using OnbodyMic.Domain;
using Xunit;

namespace OnbodyMic.Persistence.Tests;

using OnbodyMic.Domain;
using Xunit;

public sealed class JsonPresetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onbodymic-preset-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private JsonPresetStore Store => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PresetConfig ValidConfig(string id = "preset-test1", string name = "开黑默认") => new(
        id,
        name,
        new PresetDeviceConfig("mic-1", "render-1", "cable-1", "monitor-1", new AppSelection("C:/Apps/player.exe", "Player")),
        1.5f,
        0.8f,
        1.2f,
        new MonitorPreferences(MonitorMode.FullEndpoint, 0.5f),
        ["clip-a", "clip-b"],
        LibraryFavoritesOnly: true,
        LibraryGroupId: "group-game");

    [Fact]
    public void Save_then_load_roundtrips_every_field()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));

        var loaded = store.Load("preset-test1");

        Assert.Equal(PresetLoadStatus.Loaded, loaded.Status);
        var config = loaded.Config!;
        Assert.Equal("preset-test1", config.PresetId);
        Assert.Equal("开黑默认", config.Name);
        Assert.Equal("mic-1", config.Devices.MicDeviceId);
        Assert.Equal("render-1", config.Devices.VirtualRenderDeviceId);
        Assert.Equal("cable-1", config.Devices.VirtualCaptureDeviceId);
        Assert.Equal("monitor-1", config.Devices.MonitorDeviceId);
        Assert.Equal("C:/Apps/player.exe", config.Devices.Application!.ExecutablePath);
        Assert.Equal(1.5f, config.MicGain);
        Assert.Equal(0.8f, config.AppGain);
        Assert.Equal(1.2f, config.ClipGain);
        Assert.Equal(MonitorMode.FullEndpoint, config.Monitor.Mode);
        Assert.Equal(0.5f, config.Monitor.Gain);
        Assert.Equal(["clip-a", "clip-b"], config.ClipLayout);
        Assert.True(config.LibraryFavoritesOnly);
        Assert.Equal("group-game", config.LibraryGroupId);
    }

    [Fact]
    public void Missing_preset_reports_not_found()
    {
        Assert.Equal(PresetLoadStatus.NotFound, Store.Load("preset-absent").Status);
    }

    [Fact]
    public void Corrupt_json_is_rejected_and_file_stays_untouched()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));
        var path = Path.Combine(_root, "preset-test1.json");
        File.WriteAllText(path, "{ not json");

        var loaded = store.Load("preset-test1");

        Assert.Equal(PresetLoadStatus.Corrupt, loaded.Status);
        Assert.Contains("json invalid", loaded.Reason);
        Assert.Equal("{ not json", File.ReadAllText(path)); // never modified on a failed load
    }

    [Fact]
    public void Unknown_schema_version_is_rejected()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "preset-future.json"), """{"schemaVersion": 99}""");

        var loaded = Store.Load("preset-future");

        Assert.Equal(PresetLoadStatus.Corrupt, loaded.Status);
        Assert.Contains("schemaVersion", loaded.Reason);
    }

    [Fact]
    public void Preset_id_mismatch_between_file_and_name_is_rejected()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));
        var path = Path.Combine(_root, "preset-test1.json");
        File.Move(path, Path.Combine(_root, "preset-other.json"));

        Assert.Equal(PresetLoadStatus.Corrupt, Store.Load("preset-other").Status);
    }

    [Theory]
    [InlineData(-0.1f, "gain-out-of-range")]
    [InlineData(4.1f, "gain-out-of-range")]
    public void Out_of_range_gains_are_refused_at_save_and_load(float gain, string reason)
    {
        var store = Store;
        var config = ValidConfig() with { MicGain = gain };

        var saved = store.Save(config);
        Assert.False(saved.Accepted);
        Assert.Equal(reason, saved.Rejection);

        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "preset-bad.json"),
            "{\"schemaVersion\":1,\"presetId\":\"preset-bad\",\"name\":\"x\",\"microphone\":{\"deviceStableId\":\"m\",\"gain\":" + gain.ToString(CultureInfo.InvariantCulture) + "},\"virtualOutput\":{\"deviceStableId\":\"r\"},\"monitor\":{\"deviceStableId\":\"h\"}}");
        Assert.Equal(PresetLoadStatus.Corrupt, Store.Load("preset-bad").Status);
    }

    [Fact]
    public void Save_rejects_invalid_preset_ids_and_missing_device_ids()
    {
        var store = Store;
        Assert.Equal("invalid preset id '../escape'", store.Save(ValidConfig("../escape")).Rejection);
        Assert.Equal("name-required", store.Save(ValidConfig() with { Name = " " }).Rejection);

        var noMic = ValidConfig() with { Devices = new PresetDeviceConfig("", "r", null, "h", null) };
        Assert.Equal("device-id-required", store.Save(noMic).Rejection);
    }

    [Fact]
    public void NaN_gain_cannot_be_serialized_into_a_preset()
    {
        var store = Store;
        var config = ValidConfig() with { AppGain = float.NaN };

        Assert.False(store.Save(config).Accepted); // structural guard fires first

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "preset-nan.json"),
            """{"schemaVersion":1,"presetId":"preset-nan","name":"x","microphone":{"deviceStableId":"m"},"virtualOutput":{"deviceStableId":"r"},"monitor":{"deviceStableId":"h"},"appGain":NaN}""");
        Assert.Equal(PresetLoadStatus.Corrupt, Store.Load("preset-nan").Status); // STJ rejects the NaN literal
    }

    [Fact]
    public void List_marks_corrupt_files_instead_of_hiding_them()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "preset-broken.json"), "garbage");

        var list = store.List();

        Assert.Contains(list, p => p.PresetId == "preset-test1" && !p.Corrupt && p.Name == "开黑默认");
        Assert.Contains(list, p => p.PresetId == "preset-broken" && p.Corrupt);
    }

    [Fact]
    public void Rename_updates_only_the_name()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));

        Accept(store.Rename("preset-test1", "训练场"));

        var loaded = store.Load("preset-test1");
        Assert.Equal("训练场", loaded.Config!.Name);
        Assert.Equal("mic-1", loaded.Config.Devices.MicDeviceId);
    }

    [Fact]
    public void Delete_removes_the_file_and_its_sides()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));
        Accept(store.Save(ValidConfig() with { Name = "v2" })); // creates a .bak

        Accept(store.Delete("preset-test1"));

        Assert.False(store.Exists("preset-test1"));
        Assert.False(File.Exists(Path.Combine(_root, "preset-test1.bak")));
        Assert.Equal("preset-not-found", store.Delete("preset-test1").Rejection);
    }

    [Fact]
    public void Restore_backup_recovers_a_broken_main_file()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));
        Accept(store.Save(ValidConfig() with { Name = "second" }));
        File.WriteAllText(Path.Combine(_root, "preset-test1.json"), "corrupted");

        Accept(store.RestoreBackup("preset-test1"));

        var loaded = store.Load("preset-test1");
        Assert.Equal(PresetLoadStatus.Loaded, loaded.Status);
        Assert.Equal("开黑默认", loaded.Config!.Name); // the backup predates the second save
    }

    [Fact]
    public void Restore_backup_validates_the_backup_first()
    {
        var store = Store;
        Accept(store.Save(ValidConfig()));
        File.WriteAllText(Path.Combine(_root, "preset-test1.json.bak"), "also broken");
        File.WriteAllText(Path.Combine(_root, "preset-test1.json"), "corrupted");

        var result = store.RestoreBackup("preset-test1");

        Assert.False(result.Accepted);
        Assert.Equal("backup-invalid", result.Rejection);
        Assert.Equal("corrupted", File.ReadAllText(Path.Combine(_root, "preset-test1.json"))); // nothing changed
    }

    [Fact]
    public void Restore_backup_without_backup_is_refused()
    {
        Assert.Equal("no-backup", Store.RestoreBackup("preset-absent").Rejection);
    }

    [Fact]
    public void New_ids_are_valid_and_unique()
    {
        var store = Store;
        var ids = Enumerable.Range(0, 32).Select(_ => store.NewId()).ToHashSet();

        Assert.All(ids, id => Assert.True(PresetConfigRules.IsValidId(id)));
        Assert.Equal(32, ids.Count);
    }

    private static void Accept(CommandResult result) => Assert.True(result.Accepted, result.Rejection);
}
