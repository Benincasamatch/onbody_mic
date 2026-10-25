namespace OnbodyMic.Persistence.Tests;

using OnbodyMic.Domain;
using OnbodyMic.Persistence;
using Xunit;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onbodymic-tests-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void New_write_lands_content_without_backup()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteText(path, "one");
        Assert.Equal("one", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Overwrite_replaces_content_and_keeps_previous_as_backup()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteText(path, "one");
        AtomicFile.WriteText(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Repeated_saves_leave_exactly_one_backup()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteText(path, "1");
        AtomicFile.WriteText(path, "2");
        AtomicFile.WriteText(path, "3");

        Assert.Equal("3", File.ReadAllText(path));
        Assert.Equal("2", File.ReadAllText(path + ".bak"));
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // target + backup, no temp litter
    }
}

public sealed class GlobalSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onbodymic-tests-" + Guid.NewGuid().ToString("N"));

    public GlobalSettingsStoreTests() => Directory.CreateDirectory(_dir);

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

    private string PathFor(string name) => System.IO.Path.Combine(_dir, name);

    [Fact]
    public void Missing_file_loads_defaults_with_no_bindings()
    {
        var settings = new GlobalSettingsStore(PathFor("settings.json")).Load();
        Assert.Empty(settings.Bindings);
        Assert.Empty(settings.Issues);
        Assert.Equal("name", settings.Data.Ui.ClipSort);
    }

    [Fact]
    public void Bindings_round_trip_through_save_and_load()
    {
        var path = PathFor("settings.json");
        var store = new GlobalSettingsStore(path);
        var data = new GlobalSettingsData
        {
            GlobalHotkeys = { ["emergencyMute"] = "Ctrl+Alt+F10", ["toggleMicSend"] = "Ctrl+Alt+M" },
            ClipHotkeys = { ["clip-airhorn"] = "Ctrl+Alt+1" },
        };
        store.Save(data);

        var loaded = store.Load();
        Assert.Equal(3, loaded.Bindings.Count);
        Assert.Contains(loaded.Bindings, b => b.Action == HotkeyAction.EmergencyMute && b.Gesture.ToString() == "Ctrl+Alt+F10");
        Assert.Contains(loaded.Bindings, b => b.Action == HotkeyAction.PlayClip && b.TargetId == "clip-airhorn");
        Assert.Empty(loaded.Issues);
    }


    [Fact]
    public void Preset_hotkeys_round_trip_and_conflict_with_clip_bindings()
    {
        var path = PathFor("settings.json");
        var store = new GlobalSettingsStore(path);
        var data = new GlobalSettingsData
        {
            PresetHotkeys = { ["preset-a"] = "Ctrl+Alt+P", ["preset-x"] = "Ctrl+Alt+O" },
            ClipHotkeys = { ["clip-airhorn"] = "Ctrl+Alt+P", ["clip-b"] = "Ctrl+Alt+2" },
        };
        store.Save(data);

        var loaded = store.Load();

        // clip bindings validate first; the conflicting preset entry stays on disk but is not activated
        Assert.Contains(loaded.Bindings, b => b.Action == HotkeyAction.PlayClip && b.TargetId == "clip-airhorn");
        Assert.Contains(loaded.Bindings, b => b.Action == HotkeyAction.PlayClip && b.TargetId == "clip-b");
        Assert.Contains(loaded.Bindings, b => b.Action == HotkeyAction.SelectPreset && b.TargetId == "preset-x");
        Assert.DoesNotContain(loaded.Bindings, b => b.Action == HotkeyAction.SelectPreset && b.TargetId == "preset-a");
        Assert.Contains(loaded.Issues, i => i.Section == "presetHotkeys" && i.Key == "preset-a");
    }

    [Fact]
    public void Corrupt_json_throws_and_leaves_the_file_intact()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, "{ not json !!!");
        var store = new GlobalSettingsStore(path);

        Assert.Throws<SettingsLoadException>(store.Load);
        Assert.Equal("{ not json !!!", File.ReadAllText(path)); // docs/06 §7: failure never overwrites
    }

    [Fact]
    public void Unknown_schema_version_throws()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, """{"schemaVersion":99}""");
        Assert.Throws<SettingsLoadException>(() => new GlobalSettingsStore(path).Load());
    }

    [Fact]
    public void Unknown_action_is_kept_on_disk_but_not_activated()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"globalHotkeys":{"playPreview":"Ctrl+Alt+P","emergencyMute":"Ctrl+Alt+F10"}}""");

        var settings = new GlobalSettingsStore(path).Load();

        Assert.Single(settings.Bindings); // only emergencyMute activates
        Assert.Contains(settings.Issues, i => i.Section == "globalHotkeys" && i.Key == "playPreview");
    }

    [Fact]
    public void Unparseable_gesture_reports_an_issue()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"globalHotkeys":{"stopClip":"Ctrl+NotAKey"}}""");

        var settings = new GlobalSettingsStore(path).Load();

        Assert.Empty(settings.Bindings);
        Assert.Contains(settings.Issues, i => i.Key == "stopClip" && i.Reason.Contains("unparseable"));
    }

    [Fact]
    public void Duplicate_gesture_activates_only_the_first()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(
            path,
            """{"schemaVersion":1,"globalHotkeys":{"stopClip":"Ctrl+Alt+S"},"clipHotkeys":{"clip-a":"Ctrl+Alt+S","clip-b":"Ctrl+Alt+B"}}""");

        var settings = new GlobalSettingsStore(path).Load();

        Assert.Equal(2, settings.Bindings.Count); // stopClip + clip-b
        Assert.DoesNotContain(settings.Bindings, b => b.TargetId == "clip-a");
        Assert.Contains(settings.Issues, i => i.Key == "clip-a" && i.Reason.Contains("duplicate"));
    }

    [Fact]
    public void Empty_gesture_entries_mean_unbound()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"globalHotkeys":{"stopClip":null},"clipHotkeys":{"clip-a":""}}""");

        var settings = new GlobalSettingsStore(path).Load();

        Assert.Empty(settings.Bindings);
        Assert.Empty(settings.Issues);
    }

    [Fact]
    public void Invalid_clip_sort_falls_back_with_an_issue()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"ui":{"clipSort":"chaos"}}""");

        var settings = new GlobalSettingsStore(path).Load();

        Assert.Equal("name", settings.Data.Ui.ClipSort);
        Assert.Contains(settings.Issues, i => i.Key == "clipSort");
    }
}
