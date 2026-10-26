namespace OnbodyMic.Desktop;

using OnbodyMic.Audio;
using OnbodyMic.Domain;

/// <summary>
/// Turns engine rejection codes into what the user can act on (§13.3: what
/// happened, what it affects, what to do next). Codes come from
/// CommandResult.Rejection / PresetApplyResult.Rejection and are the only
/// error vocabulary the session exposes; anything unknown is shown verbatim so
/// a new code is never silently swallowed.
/// </summary>
internal static class Rejections
{
    public static string Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "未知原因（未返回错误码）";
        }

        var (head, tail) = Split(code);
        return head switch
        {
            "emergency-latched" => "紧急静音已锁存，所有发送与监听被强制关闭",
            "extras-disabled" => "「附加音频」发送未开启，应用捕获无法启用",
            "extras-disabled-or-emergency" => "需要先开启「附加音频」发送，且不能处于紧急静音",
            "output-lost" => "虚拟输出已失效，发送被全部关闭",
            "no-app-bound" => "尚未绑定目标应用",
            "app-send-active" => "应用正在发送，需先关闭应用捕获再更换绑定",
            "no-clip" => "当前没有正在播放的音效",
            "not-latched" => "当前并未处于紧急静音状态",
            "full-endpoint-unavailable" => "实际虚拟麦监听不可用：虚拟麦 Capture 端点未配置",
            "no-device-config" => "尚未配置设备（先完成设备设置）",
            "invalid-gain" => "增益超出允许范围（0.00–4.00）",
            "name-required" => "名称不能为空",
            "invalid-preset" or "invalid-preset-id" => "预设标识无效",
            "preset-not-found" => "预设不存在（可能已被删除）",
            "preset-id-required" => "缺少预设标识",
            "clip-id-required" => "缺少音效标识",
            "gesture-required" => "尚未捕获快捷键",
            "target-not-supported" => "该动作不支持指定目标",
            "backup-invalid" => "备份文件同样损坏，未做任何修改",
            "no-backup" => "没有可用备份",
            "hotkey-backend-unavailable" => "热键后端未启动（重启程序可恢复）",
            "clip-unavailable" => $"音效素材缺失，无法播放{Detail(tail)}",
            "decode-failed" => $"音效解码失败{Detail(tail)}",
            "unknown-clip" => $"音效不在库中{Detail(tail)}",
            "unknown-preset" => $"预设不存在{Detail(tail)}",
            "hotkey-conflict" => $"快捷键已被其他动作占用{Detail(tail)}",
            "register-failed" => $"系统拒绝注册该快捷键（可能被其他程序占用）{Detail(tail)}",
            "monitor-start-failed" => $"监听启动失败{Detail(tail)}",
            "attach-failed" or "output-attach-failed" => "虚拟输出接入失败（独占设备可能被占用）",
            "preset-corrupt" => $"预设文件损坏，已安全关闭全部路径{Detail(tail)}",
            "device-id-required" => "预设缺少设备标识",
            "gain-out-of-range" => "预设中的增益超出范围",
            _ => code,
        };
    }

    public static string NextStep(string? code)
    {
        var head = Split(code).Head;
        return head switch
        {
            "emergency-latched" => "处理：点击「解除静音（保持全关）」，再逐路开启需要的路径。",
            "extras-disabled" => "处理：在「附加音频」卡片勾选「发送到队友」，再启用应用捕获。",
            "extras-disabled-or-emergency" => "处理：先开启附加音频发送；若已锁存，先解除静音。",
            "output-lost" => "处理：打开「设备设置…」确认虚拟输出端点存在，然后保存重新应用。",
            "no-app-bound" => "处理：在「附加音频」卡片点「绑定运行中的应用…」。",
            "app-send-active" => "处理：先取消「捕获指定应用」，再重新绑定。",
            "no-clip" => "无需处理。",
            "full-endpoint-unavailable" => "处理：在「设备设置…」中选择虚拟麦 Capture 端点。",
            "invalid-gain" => "处理：把增益调到 0.00–4.00 之间。",
            "clip-unavailable" => "处理：原文件已移动或删除；重新导入该音效，或在「更多」中删除此条目。",
            "decode-failed" => "处理：确认文件为 wav/mp3 且未损坏，然后重新导入。",
            "hotkey-conflict" => "处理：换一个组合键，或先清除占用它的绑定。",
            "register-failed" => "处理：该组合键可能被系统或其他程序独占，换一个组合键。",
            "monitor-start-failed" => "处理：确认监听设备存在且未被独占，然后在「设备设置…」重新选择。",
            "attach-failed" or "output-attach-failed" => "处理：关闭占用虚拟输出的程序，或在「设备设置…」换一个端点后重新应用。",
            "preset-corrupt" => "处理：从备份恢复该预设，或新建一个预设。",
            "hotkey-backend-unavailable" => "处理：重启随声麦；若仍失败，查看 logs/onbody.log。",
            _ => "如问题持续，请查看 logs/onbody.log 并提供其中的错误行。",
        };
    }

    private static (string Head, string Tail) Split(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (string.Empty, string.Empty);
        }

        var index = code.IndexOf(':');
        return index < 0 ? (code, string.Empty) : (code[..index], code[(index + 1)..].Trim());
    }

    private static string Detail(string tail) => string.IsNullOrWhiteSpace(tail) ? string.Empty : $"（{tail}）";

    /// <summary>Human label for a hotkey action, shared by the digest and the editor.</summary>
    public static string ActionLabel(HotkeyAction action, Func<string, string>? clipName = null, Func<string, string>? presetName = null) => action switch
    {
        HotkeyAction.PlayClip => $"播放音效",
        HotkeyAction.StopClip => "停止音效",
        HotkeyAction.ToggleExtrasSend => "切换附加音频",
        HotkeyAction.ToggleMicSend => "切换真麦发送",
        HotkeyAction.EmergencyMute => "全部静音",
        HotkeyAction.ToggleMonitor => "切换监听",
        HotkeyAction.SelectPreset => "切换预设",
        _ => action.ToString(),
    };

    public static string BindingLabel(HotkeyBinding binding, Func<string, string>? clipName = null, Func<string, string>? presetName = null) => binding.Action switch
    {
        HotkeyAction.PlayClip => $"播放音效 {clipName?.Invoke(binding.TargetId ?? string.Empty) ?? binding.TargetId}",
        HotkeyAction.SelectPreset => $"切换到预设 {presetName?.Invoke(binding.TargetId ?? string.Empty) ?? binding.TargetId}",
        _ => ActionLabel(binding.Action),
    };
}
