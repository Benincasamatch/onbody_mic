namespace OnbodyMic.Cli;

using OnbodyMic.Application;
using OnbodyMic.Domain;
using OnbodyMic.Media;
using OnbodyMic.Persistence;
using OnbodyMic.Platform.Windows;

/// <summary>
/// P2 soundboard wiring for the panel: managed library + prefill cache +
/// settings-backed global hotkeys (restored at startup, saved on every
/// change). Owns the hotkey backend's lifetime so unregistration happens
/// before the session goes away.
/// </summary>
internal sealed class Soundboard : IDisposable
{
    private readonly GlobalSettingsStore _settingsStore;
    private readonly NAudioClipDecoder _decoder = new();
    private readonly Action<string> _print;
    private readonly string _root;

    private GlobalSettings _settings;
    private WindowsHotkeyService? _hotkeys;
    private HotkeyDispatcher? _dispatcher;
    private Task? _prefill;
    private Func<string, IReadOnlyList<string>>? _presetClipReferences;
    private bool _disposed;

    private Soundboard(GlobalSettingsStore settingsStore, GlobalSettings settings, ClipLibrary? library, ClipCache cache, Action<string> print, string root)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        Library = library;
        Cache = cache;
        _print = print;
        _root = root;
    }

    public ClipLibrary? Library { get; }

    public ClipCache Cache { get; }

    public string Root => _root;

    public GlobalSettings Settings => _settings;

    public void SaveSettingsPublic() => SaveSettings();

    /// <summary>Shared root resolution: data root for settings, library, cache and diagnostics.</summary>
    public static string ResolveRoot(string? rootOverride) => rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnbodyMic");

    /// <summary>Creates settings + library + cache (phase 1, before the session exists).</summary>
    public static Soundboard Create(string? rootOverride, Action<string> print)
    {
        var root = ResolveRoot(rootOverride);
        var settingsStore = new GlobalSettingsStore(Path.Combine(root, "settings.json"));
        GlobalSettings settings;
        try
        {
            settings = settingsStore.Load();
        }
        catch (SettingsLoadException ex)
        {
            // the corrupt file stays untouched; this run starts with no
            // bindings (all audio paths remain off — safe default)
            print($"[settings] 配置加载失败，按空白配置继续（文件未改动）: {ex.Message}");
            settings = GlobalSettings.Default();
        }

        foreach (var issue in settings.Issues)
        {
            print($"[settings] 绑定未激活 {issue.Section}/{issue.Key}: {issue.Reason}");
        }

        var libraryRoot = settings.Data.LibraryRoot ?? Path.Combine(root, "library");
        ClipLibrary? library = null;
        try
        {
            library = ClipLibrary.Open(libraryRoot, new NAudioClipDecoder());
        }
        catch (ClipLibraryLoadException ex)
        {
            print($"[library] 音效库索引损坏，本次禁用音效库（文件未改动）: {ex.Message}");
        }

        return new Soundboard(settingsStore, settings, library, new ClipCache(ClipCache.DefaultCapacityFrames), print, root);
    }

    /// <summary>Wires the hotkey backend and restores persisted bindings (phase 2, after the session exists).</summary>
    public void Attach(
        AudioSession session,
        Func<string, bool>? presetExists = null,
        Func<string, PresetApplyResult>? presetApplier = null)
    {
        _hotkeys = new WindowsHotkeyService();
        _dispatcher = new HotkeyDispatcher(session, _hotkeys, Resolve, presetExists, presetApplier);
        _dispatcher.Noticed += notice => _print($"[hotkey] {notice.Message}");

        foreach (var binding in _settings.Bindings)
        {
            var result = _dispatcher.Bind(binding);
            _print(result.Accepted
                ? $"[hotkey] 已恢复 {binding.Describe()} = {binding.Gesture}"
                : $"[hotkey] 失效绑定（未激活）{binding.Describe()} = {binding.Gesture}: {result.Rejection}");
        }

        StartPrefill();
    }

    /// <summary>Lets preset delete warn about clip references held by stored presets (docs/01 §5.3).</summary>
    public void SetPresetClipReferenceLookup(Func<string, IReadOnlyList<string>> lookup) => _presetClipReferences = lookup;

    public ILastPresetSink CreateLastPresetSink() => new SettingsLastPresetSink(this);

    /// <summary>Deletes a library clip: removes its hotkey binding and cache entry; only the managed copy dies (docs/01 §5.3).</summary>
    public bool DeleteClip(string clipId)
    {
        if (Library is null || Library.Find(clipId) is null)
        {
            return false;
        }

        if (_settings.Data.ClipHotkeys.TryGetValue(clipId, out var gesture) && !string.IsNullOrWhiteSpace(gesture))
        {
            RemoveClipHotkey(clipId);
        }

        var deleted = Library.Delete(clipId);
        Cache.Remove(clipId);
        return deleted;
    }

    public IReadOnlyList<HotkeyBinding> ActiveHotkeys => _dispatcher?.Active ?? [];

    public void RemovePresetHotkey(string presetId)
    {
        if (_settings.Data.PresetHotkeys.Remove(presetId))
        {
            SaveSettings();
        }

        _dispatcher?.Unbind(HotkeyAction.SelectPreset, presetId);
    }

    public ClipResolution Resolve(string clipId)
    {
        var item = Library?.Find(clipId);
        if (item is null)
        {
            return ClipResolution.Unknown;
        }

        var path = Library!.AssetAbsolutePath(item);
        return new ClipResolution(true, File.Exists(path), path, item.Gain);
    }

    public void StartPrefill()
    {
        if (Library is null)
        {
            return;
        }

        _prefill = Task.Run(() =>
        {
            try
            {
                var cached = Cache.Prefill(Library, _decoder);
                _print($"[cache] 预加载完成: {cached} 条（{(long)Cache.Stats().TotalFrames * 1000 / 44100}ms / 上限 {(long)Cache.Stats().CapacityFrames * 1000 / 44100}ms）");
            }
            catch (Exception ex)
            {
                _print($"[cache] 预加载失败: {ex.Message}");
            }
        });
    }

    public bool Handle(string command, string[] parts)
    {
        switch (command)
        {
            case "import":
                HandleImport(parts);
                return true;
            case "clips":
            case "search":
                HandleList(parts);
                return true;
            case "fav":
            case "unfav":
                HandleFavorite(command == "fav", parts);
                return true;
            case "rename":
                if (parts.Length < 3)
                {
                    Console.WriteLine("用法: rename <clipId> <新名称>");
                    return true;
                }

                Console.WriteLine(Library is not null && Library.Rename(parts[1], JoinRest(parts, 2))
                    ? "ok" : "失败（未知音效或名称为空）");
                return true;
            case "clipgain":
                HandleGain(parts);
                return true;
            case "group":
                HandleGroup(parts);
                return true;
            case "del":
                HandleDelete(parts);
                return true;
            case "hotkeys":
                ListHotkeys();
                return true;
            case "hotkey":
                HandleHotkey(parts);
                return true;
            case "cache":
                HandleCache(parts);
                return true;
            default:
                return false;
        }
    }

    private void HandleImport(string[] parts)
    {
        if (Library is null)
        {
            Console.WriteLine("音效库不可用（索引损坏）");
            return;
        }

        if (parts.Length < 2)
        {
            Console.WriteLine("用法: import <file> [--name 名称] [--dup]");
            return;
        }

        string? name = null;
        var allowDuplicate = false;
        for (var i = 2; i < parts.Length - 1; i++)
        {
            if (parts[i] == "--name")
            {
                name = parts[i + 1];
            }

            if (parts[i] == "--dup")
            {
                allowDuplicate = true;
            }
        }

        allowDuplicate |= parts[^1] == "--dup";
        try
        {
            var result = Library.Import(parts[1], name, allowDuplicate);
            if (result.Outcome == ImportOutcome.Duplicate)
            {
                Console.WriteLine($"重复导入: 已有条目 {result.ExistingDuplicate!.ClipId}（{result.ExistingDuplicate.DisplayName}）。加 --dup 创建独立条目");
                return;
            }

            Console.WriteLine($"已导入 {result.Item.ClipId}（{result.Item.DisplayName}, {result.Item.DurationMs}ms）→ {result.Item.ManagedAssetPath}");
        }
        catch (ClipImportException ex)
        {
            Console.WriteLine($"导入失败: {ex.Message}");
        }
    }

    private void HandleList(string[] parts)
    {
        if (Library is null)
        {
            Console.WriteLine("音效库不可用（索引损坏）");
            return;
        }

        var query = parts.Skip(1).FirstOrDefault(p => !p.StartsWith("--"));
        var favoritesOnly = parts.Contains("--fav");
        var sort = ClipSortOrder.Name;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "--sort" && parts[i + 1] == "time")
            {
                sort = ClipSortOrder.ImportTime;
            }
        }

        string? groupName = Value(parts, "--group");
        string? groupId = null;
        if (groupName is not null)
        {
            var group = Library.GroupsSnapshot().FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                Console.WriteLine($"分组不存在: {groupName}（可用: {string.Join(", ", Library.GroupsSnapshot().Select(g => g.Name))}）");
                return;
            }

            groupId = group.GroupId;
        }

        var items = Library.Search(query, sort, favoritesOnly, groupId);
        if (items.Count == 0)
        {
            Console.WriteLine("（无匹配条目）");
            return;
        }

        foreach (var item in items)
        {
            var hotkey = GestureForClip(item.ClipId);
            var state = Library.IsAvailable(item.ClipId) ? "" : " [素材缺失]";
            Console.WriteLine(
                $"{item.ClipId}  {item.DisplayName}  {(item.Favorite ? "★" : "-")}{(item.GroupId is null ? "" : $" [{GroupName(item.GroupId)}]")}  {item.DurationMs}ms  gain {item.Gain:0.00}{(hotkey is null ? "" : $"  {hotkey}")}{state}");
        }
    }

    private void HandleFavorite(bool favorite, string[] parts)
    {
        if (parts.Length != 2)
        {
            Console.WriteLine($"用法: {(favorite ? "fav" : "unfav")} <clipId>");
            return;
        }

        Console.WriteLine(Library is not null && Library.SetFavorite(parts[1], favorite) ? "ok" : "失败（未知音效）");
    }

    private void HandleGain(string[] parts)
    {
        if (parts.Length != 3 || !float.TryParse(parts[2], out var gain))
        {
            Console.WriteLine("用法: clipgain <clipId> <0..4>");
            return;
        }

        Console.WriteLine(Library is not null && Library.SetGain(parts[1], gain) ? "ok" : "失败（未知音效或增益越界）");
    }

    private void HandleGroup(string[] parts)
    {
        if (Library is null)
        {
            Console.WriteLine("音效库不可用（索引损坏）");
            return;
        }

        if (parts.Length >= 3 && parts[1] == "add")
        {
            var group = Library.CreateGroup(JoinRest(parts, 2));
            Console.WriteLine(group is not null ? $"已创建 {group.GroupId}（{group.Name}）" : "失败（名称为空或重复）");
            return;
        }

        if (parts.Length >= 4 && parts[1] == "rename")
        {
            var old = FindGroup(parts[2]);
            Console.WriteLine(old is not null && Library.RenameGroup(old.GroupId, JoinRest(parts, 3)) ? "ok" : "失败");
            return;
        }

        if (parts.Length >= 3 && parts[1] == "del")
        {
            var group = FindGroup(JoinRest(parts, 2));
            Console.WriteLine(group is not null ? $"已删除分组，{Library.DeleteGroup(group.GroupId)} 条移入未分组" : "分组不存在");
            return;
        }

        if (parts.Length == 4 && parts[1] == "set")
        {
            var groupName = parts[3] == "-" ? null : parts[3];
            string? groupId = null;
            if (groupName is not null)
            {
                var group = FindGroup(groupName);
                if (group is null)
                {
                    Console.WriteLine($"分组不存在（先 group add {groupName}）");
                    return;
                }

                groupId = group.GroupId;
            }

            Console.WriteLine(Library.SetGroup(parts[2], groupId) ? "ok" : "失败（未知音效）");
            return;
        }

        Console.WriteLine("用法: group add|rename|del|set <名称|clipId ...>");
    }

    private void HandleDelete(string[] parts)
    {
        if (Library is null)
        {
            Console.WriteLine("音效库不可用（索引损坏）");
            return;
        }

        if (parts.Length != 2 && !(parts.Length == 3 && parts[2] == "--force"))
        {
            Console.WriteLine("用法: del <clipId> [--force]");
            return;
        }

        var clipId = parts[1];
        if (Library.Find(clipId) is null)
        {
            Console.WriteLine("未知音效");
            return;
        }

        var referencing = _presetClipReferences?.Invoke(clipId) ?? [];
        if (referencing.Count > 0 && !parts.Contains("--force"))
        {
            Console.WriteLine($"以下预设的布局引用了该音效: {string.Join(", ", referencing)}；仍要删除请用 del {clipId} --force（预设引用将悬空标红）");
            return;
        }

        if (_settings.Data.ClipHotkeys.TryGetValue(clipId, out var gesture) && !string.IsNullOrWhiteSpace(gesture))
        {
            Console.WriteLine($"该音效绑定了热键 {gesture}，删除时一并解除");
            RemoveClipHotkey(clipId);
        }

        Console.WriteLine(Library.Delete(clipId) ? "已删除（仅受管副本；原文件不受影响）" : "失败");
        Cache.Remove(clipId);
    }

    private void HandleHotkey(string[] parts)
    {
        if (_dispatcher is null)
        {
            Console.WriteLine("热键后端未启动");
            return;
        }

        if (parts.Length >= 2 && parts[1] == "unbind")
        {
            HandleHotkeyUnbind(parts);
            return;
        }

        // hotkey bind <action> <gesture> | hotkey bind clip <clipId> <gesture> | hotkey bind preset <presetId> <gesture>
        string? targetId = null;
        string actionName;
        string gestureText;
        if (parts.Length == 5 && parts[2] == "clip")
        {
            targetId = parts[3];
            actionName = "clip";
            gestureText = parts[4];
        }
        else if (parts.Length == 5 && parts[2] == "preset")
        {
            targetId = parts[3];
            actionName = "preset";
            gestureText = parts[4];
        }
        else if (parts.Length == 4)
        {
            actionName = parts[2];
            gestureText = parts[3];
        }
        else
        {
            Console.WriteLine("用法: hotkey bind stop|extras|mic|mute|monitor <手势> | hotkey bind clip <clipId> <手势>");
            Console.WriteLine("      hotkey bind preset <presetId> <手势>；手势如 Ctrl+Alt+F7；解除: hotkey unbind 同上");
            return;
        }

        if (!HotkeyGesture.TryParse(gestureText, out var gesture) || gesture.IsUnbound)
        {
            Console.WriteLine($"无法解析手势: {gestureText}");
            return;
        }

        var action = actionName switch
        {
            "stop" => HotkeyAction.StopClip,
            "extras" => HotkeyAction.ToggleExtrasSend,
            "mic" => HotkeyAction.ToggleMicSend,
            "mute" => HotkeyAction.EmergencyMute,
            "monitor" => HotkeyAction.ToggleMonitor,
            "clip" => HotkeyAction.PlayClip,
            "preset" => HotkeyAction.SelectPreset,
            _ => (HotkeyAction?)null,
        };
        if (action is null)
        {
            Console.WriteLine($"未知动作: {actionName}");
            return;
        }

        var binding = new HotkeyBinding(action.Value, targetId, gesture);
        var result = _dispatcher.Bind(binding);
        Console.WriteLine(result.Accepted ? $"已绑定 {binding.Describe()} = {gesture}" : $"绑定失败: {result.Rejection}");
        if (!result.Accepted)
        {
            return;
        }

        if ((gesture.Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            Console.WriteLine("提示: 无修饰键的热键容易被游戏占用，建议加 Ctrl/Alt/Win");
        }

        PersistBinding(binding);
    }

    private void HandleHotkeyUnbind(string[] parts)
    {
        // hotkey unbind <action> | hotkey unbind clip <clipId> | hotkey unbind preset <presetId>
        string? targetId = null;
        string actionName;
        if (parts.Length == 4 && parts[2] == "clip")
        {
            targetId = parts[3];
            actionName = "clip";
        }
        else if (parts.Length == 4 && parts[2] == "preset")
        {
            targetId = parts[3];
            actionName = "preset";
        }
        else if (parts.Length == 3)
        {
            actionName = parts[2];
        }
        else
        {
            Console.WriteLine("用法: hotkey unbind stop|extras|mic|mute|monitor|clip <clipId>|preset <presetId>");
            return;
        }

        var action = actionName switch
        {
            "stop" => HotkeyAction.StopClip,
            "extras" => HotkeyAction.ToggleExtrasSend,
            "mic" => HotkeyAction.ToggleMicSend,
            "mute" => HotkeyAction.EmergencyMute,
            "monitor" => HotkeyAction.ToggleMonitor,
            "clip" => HotkeyAction.PlayClip,
            "preset" => HotkeyAction.SelectPreset,
            _ => (HotkeyAction?)null,
        };
        if (action is null || _dispatcher is null)
        {
            Console.WriteLine($"未知动作: {actionName}");
            return;
        }

        Console.WriteLine(_dispatcher.Unbind(action.Value, targetId).Accepted ? "已解除" : "失败");
        if (targetId is not null && action == HotkeyAction.PlayClip)
        {
            RemoveClipHotkey(targetId);
        }
        else if (targetId is not null && action == HotkeyAction.SelectPreset)
        {
            RemovePresetHotkey(targetId);
        }
        else
        {
            var storageName = StorageNameFor(action.Value);
            if (storageName is not null && _settings.Data.GlobalHotkeys.Remove(storageName))
            {
                SaveSettings();
            }
        }
    }

    private void ListHotkeys()
    {
        if (_dispatcher is null)
        {
            Console.WriteLine("热键后端未启动");
            return;
        }

        var active = _dispatcher.Active;
        if (active.Count == 0)
        {
            Console.WriteLine("（无激活绑定；热键初始均为空，需主动绑定）");
        }

        foreach (var binding in active)
        {
            Console.WriteLine($"{binding.Gesture,-16} {binding.Describe()}");
        }

        // stored but inactive bindings are the "标红" view
        var activeSet = active.Select(HotkeyKey).ToHashSet();
        foreach (var (section, key, reason) in _settings.Issues.Where(i => i.Section.Contains("otkey")))
        {
            Console.WriteLine($"[失效] {section}/{key}: {reason}");
        }

        foreach (var stored in StoredBindings())
        {
            if (!activeSet.Contains(HotkeyKey(stored)))
            {
                Console.WriteLine($"[失效] {stored.Describe()} = {stored.Gesture}（未激活，重新绑定可修复）");
            }
        }
    }

    private void HandleCache(string[] parts)
    {
        if (parts.Length > 1 && parts[1] == "prefill")
        {
            StartPrefill();
            Console.WriteLine("预加载已启动");
            return;
        }

        var stats = Cache.Stats();
        Console.WriteLine(
            $"条目 {stats.Count}，占用 {(long)stats.TotalFrames * 1000 / 44100}ms / 上限 {(long)stats.CapacityFrames * 1000 / 44100}ms，命中 {stats.Hits}，未中 {stats.Misses}，淘汰 {stats.Evictions}，超限拒收 {stats.DeclinedOversize}");
    }

    private void PersistBinding(HotkeyBinding binding)
    {
        if (binding.Action == HotkeyAction.PlayClip && binding.TargetId is not null)
        {
            _settings.Data.ClipHotkeys[binding.TargetId] = binding.Gesture.ToString();
        }
        else if (binding.Action == HotkeyAction.SelectPreset && binding.TargetId is not null)
        {
            _settings.Data.PresetHotkeys[binding.TargetId] = binding.Gesture.ToString();
        }
        else
        {
            var storageName = StorageNameFor(binding.Action);
            if (storageName is null)
            {
                return;
            }

            _settings.Data.GlobalHotkeys[storageName] = binding.Gesture.ToString();
        }

        SaveSettings();
    }

    private void RemoveClipHotkey(string clipId)
    {
        if (_settings.Data.ClipHotkeys.Remove(clipId))
        {
            SaveSettings();
        }

        _dispatcher?.Unbind(HotkeyAction.PlayClip, clipId);
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings.Data);
        }
        catch (Exception ex)
        {
            // runtime truth stays active; the file is stale and the user knows
            Console.WriteLine($"设置保存失败（绑定仍生效，重启后不恢复）: {ex.Message}");
        }
    }

    private IEnumerable<HotkeyBinding> StoredBindings() => _settings.Bindings;

    private static string? StorageNameFor(HotkeyAction action) => HotkeyActionNames.ToName(action);

    private static string HotkeyKey(HotkeyBinding binding) => $"{binding.Action}|{binding.TargetId}";

    private string? GestureForClip(string clipId) =>
        _settings.Data.ClipHotkeys.TryGetValue(clipId, out var gesture) ? gesture : null;

    private string? GroupName(string groupId) => Library?.GroupsSnapshot().FirstOrDefault(g => g.GroupId == groupId)?.Name ?? groupId;

    private ClipGroup? FindGroup(string name) => Library?.GroupsSnapshot().FirstOrDefault(
        g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] parts, string key)
    {
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == key)
            {
                return parts[i + 1];
            }
        }

        return null;
    }

    private static string JoinRest(string[] parts, int from) => string.Join(' ', parts[from..]);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _prefill?.Wait(5000);
        }
        catch (AggregateException)
        {
        }

        _dispatcher?.Dispose();
        _hotkeys?.Dispose();
        (Library as IDisposable)?.Dispose();
    }
}

/// <summary>lastPresetId persistence over settings.json; only successful applies write it (PresetManager contract).</summary>
internal sealed class SettingsLastPresetSink(Soundboard soundboard) : ILastPresetSink
{
    public string? LastPresetId => soundboard.Settings.Data.LastPresetId;

    public void SetLastPreset(string? presetId)
    {
        soundboard.Settings.Data.LastPresetId = presetId;
        soundboard.SaveSettingsPublic();
    }
}
