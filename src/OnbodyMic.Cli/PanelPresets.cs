namespace OnbodyMic.Cli;

using OnbodyMic.Application;
using OnbodyMic.Domain;
using OnbodyMic.Persistence;
using OnbodyMic.Platform.Windows;

/// <summary>
/// P3 preset surface for the panel: CRUD, transactional apply, startup
/// restore and path-based app re-binding. Mirrors what the desktop shell
/// does, through the same PresetManager and session commands.
/// </summary>
internal sealed class PanelPresets
{
    private readonly AudioSession _session;
    private readonly Soundboard _soundboard;
    private readonly IReadOnlyList<AudioEndpointInfo> _devices;

    public PanelPresets(AudioSession session, Soundboard soundboard, IReadOnlyList<AudioEndpointInfo> devices)
    {
        _session = session;
        _soundboard = soundboard;
        _devices = devices;

        Store = new JsonPresetStore(Path.Combine(soundboard.Root, "presets"));
        Manager = new PresetManager(session, Store, soundboard.CreateLastPresetSink());
        Manager.Noticed += message => Console.WriteLine($"[preset] {message}");
        soundboard.SetPresetClipReferenceLookup(Manager.PresetsReferencingClip);
    }

    public PresetManager Manager { get; }

    public JsonPresetStore Store { get; }

    /// <summary>Startup path (T01/T24): restore the last preset config-only; a missing id falls back to guidance.</summary>
    public void RestoreStartupOrGuide()
    {
        var result = Manager.RestoreStartup();
        if (!result.Accepted)
        {
            if (result.Rejection == "no-last-preset")
            {
                Console.WriteLine("[preset] 无已保存预设；当前命令行设备配置即工作配置，用 `preset new <名称>` 固化为预设");
            }
            else
            {
                Console.WriteLine($"[preset] 启动恢复失败（保持安全关闭）: {result.Rejection}");
            }
        }
    }

    public bool Handle(string command, string[] parts)
    {
        switch (command)
        {
            case "presets":
                ListPresets();
                return true;
            case "preset":
                HandlePreset(parts);
                return true;
            case "bind":
                HandleBind(parts);
                return true;
            default:
                return false;
        }
    }

    private void ListPresets()
    {
        var list = Manager.List();
        if (list.Count == 0)
        {
            Console.WriteLine("（无预设；preset new <名称> 创建）");
            return;
        }

        var active = Manager.ActivePresetId;
        foreach (var preset in list)
        {
            var marker = preset.PresetId == active ? "*" : " ";
            var state = preset.Corrupt ? " [损坏——preset restore 可从备份恢复]" : "";
            Console.WriteLine($"{marker} {preset.PresetId}  {preset.Name}{state}");
        }
    }

    private void HandlePreset(string[] parts)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("用法: preset new <名称> | save [id] | copy <id> <名称> | rename <id> <名称> | del <id> | apply <id> | restore <id>");
            return;
        }

        switch (parts[1])
        {
            case "new":
                if (parts.Length < 3)
                {
                    Console.WriteLine("用法: preset new <名称>");
                    return;
                }

                var created = Manager.Create(JoinRest(parts, 2), CurrentLayout(), favoritesOnly: false, groupId: null);
                Console.WriteLine(created.Result.Accepted
                    ? $"已创建并保存 {created.Config!.PresetId}（当前设备/增益/监听偏好已固化；布局含 {created.Config.ClipLayout.Count} 条音效引用）"
                    : $"创建失败: {created.Result.Rejection}");
                return;
            case "save":
            {
                var id = parts.Length >= 3 ? parts[2] : Manager.ActivePresetId;
                if (id is null)
                {
                    Console.WriteLine("无活动预设；preset save <id> 或先 preset new");
                    return;
                }

                var result = Manager.Save(id, CurrentLayout(), favoritesOnly: false, groupId: null);
                Console.WriteLine(result.Accepted ? "ok" : $"保存失败: {result.Rejection}");
                return;
            }
            case "copy":
                if (parts.Length < 4)
                {
                    Console.WriteLine("用法: preset copy <id> <新名称>");
                    return;
                }

                Console.WriteLine(Result(Manager.Duplicate(parts[2], JoinRest(parts, 3))));
                return;
            case "rename":
                if (parts.Length < 4)
                {
                    Console.WriteLine("用法: preset rename <id> <新名称>");
                    return;
                }

                Console.WriteLine(Result(Manager.Rename(parts[2], JoinRest(parts, 3))));
                return;
            case "del":
            {
                if (parts.Length != 3)
                {
                    Console.WriteLine("用法: preset del <id>");
                    return;
                }

                var id = parts[2];
                if (Manager.ActivePresetId == id)
                {
                    Console.WriteLine("该预设当前活动：删除将安全关闭全部发送/监听，且不自动启用其他预设。确认请用 preset del <id> --force");
                    if (!parts.Contains("--force") && parts.Length == 3)
                    {
                        return;
                    }
                }

                var deleted = Manager.Delete(id);
                if (deleted.Accepted)
                {
                    _soundboard.RemovePresetHotkey(id);
                }

                Console.WriteLine(Result(deleted));
                return;
            }
            case "apply":
            {
                if (parts.Length != 3)
                {
                    Console.WriteLine("用法: preset apply <id>");
                    return;
                }

                var applied = Manager.Apply(parts[2]);
                if (!applied.Accepted)
                {
                    Console.WriteLine($"应用失败: {applied.Rejection}");
                    return;
                }

                var status = _session.Status();
                Console.WriteLine(
                    $"已应用（发送/监听全部关闭: mic={status.Switches.MicSendEnabled} extras={status.Switches.ExtrasSendEnabled} app={status.Switches.AppSendEnabled} monitor={status.Switches.MonitorEnabled}，监听偏好={status.MonitorMode}）");
                return;
            }
            case "restore":
                if (parts.Length != 3)
                {
                    Console.WriteLine("用法: preset restore <id>（从备份恢复）");
                    return;
                }

                Console.WriteLine(Result(Manager.RestoreBackup(parts[2])));
                return;
            default:
                Console.WriteLine($"未知子命令: {parts[1]}");
                return;
        }
    }

    private void HandleBind(string[] parts)
    {
        // bind app <pid|auto>
        if (parts.Length == 3 && parts[1] == "app")
        {
            if (parts[2] == "auto")
            {
                BindAppAuto();
                return;
            }

            if (uint.TryParse(parts[2], out var pid))
            {
                var bound = _session.BindApplication(new AppBinding(pid, $"pid {pid}"));
                Console.WriteLine(bound.Accepted ? "ok" : $"拒绝: {bound.Rejection}");
                return;
            }
        }

        Console.WriteLine("用法: bind app <pid|auto>");
    }

    /// <summary>
    /// Re-binds the preset's saved application by exact executable path (T08):
    /// one live instance binds, zero reports missing, several report ambiguity.
    /// </summary>
    private void BindAppAuto()
    {
        var selection = _session.Status().Devices?.Application;
        if (selection is not { IsBound: true } bound)
        {
            Console.WriteLine("当前配置未指定应用（预设里 application.selection.executablePath 为空）");
            return;
        }

        var matches = WindowsAppCatalog.FindByExecutablePath(bound.ExecutablePath!);
        if (matches.Count == 0)
        {
            Console.WriteLine($"未找到运行中的实例: {bound.DisplayName ?? bound.ExecutablePath}（先启动应用再绑定）");
            return;
        }

        if (matches.Count > 1)
        {
            Console.WriteLine($"歧义：{matches.Count} 个实例匹配该路径（pid {string.Join(", ", matches.Select(m => m.ProcessId))}），请用 bind app <pid> 手动选择");
            return;
        }

        var instance = matches[0];
        var result = _session.BindApplication(new AppBinding(instance.ProcessId, bound.DisplayName ?? bound.ExecutablePath!));
        Console.WriteLine(result.Accepted
            ? $"已按路径绑定 pid={instance.ProcessId}（{bound.DisplayName ?? bound.ExecutablePath}）"
            : $"拒绝: {result.Rejection}");
    }

    private IReadOnlyList<string> CurrentLayout() =>
        _soundboard.Library?.ItemsSnapshot().Select(item => item.ClipId).ToList() ?? [];

    private static string Result(CommandResult result) => result.Accepted ? "ok" : $"失败: {result.Rejection}";

    private static string JoinRest(string[] parts, int from) => string.Join(' ', parts[from..]);
}
