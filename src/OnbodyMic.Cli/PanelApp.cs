namespace OnbodyMic.Cli;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using OnbodyMic.Platform.Windows;

/// <summary>
/// P1 minimal control panel: an interactive REPL driving the real audio
/// session end to end (capture -> gate -> mixer -> exclusive virtual output,
/// plus both monitor modes), printing desensitized metrics. Visual polish is
/// deliberately out of scope (docs/03 P1 task 6).
/// </summary>
internal static class PanelApp
{
    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var devices = WindowsAudioDevices.List(activeOnly: true);

        var micId = ResolveEndpoint(options, "mic-id", "mic", devices, capture: true, "麦克风（真实输入）");
        var renderId = ResolveEndpoint(options, "render-id", "render", devices, capture: false, "虚拟输出（线缆 Render，独占写入）");
        var cableId = ResolveEndpoint(options, "capture-id", "capture", devices, capture: true, "虚拟麦（线缆 Capture，FullEndpoint 监听用）");
        var monitorId = ResolveEndpoint(options, "monitor-id", "monitor", devices, capture: false, "监听输出（耳机）");
        if (micId is null || renderId is null || cableId is null || monitorId is null)
        {
            return 1;
        }

        if (options.TryGetValue("app-pid", out var pidText) && uint.TryParse(pidText, out var pid))
        {
            // used below after the session exists
            _ = pid;
        }
        else
        {
            pid = 0;
        }

        using var session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = new NAudioClipDecoder(),
            MicAdapterFactory = channel => new WindowsMicSource(micId, channel),
            AppAdapterFactory = (channel, binding) => new WindowsAppSource(binding, channel),
            OutputFactory = () => new WasapiExclusiveRenderPump(renderId),
            MonitorOutputFactory = () => new WindowsMonitorOutput(monitorId),
            MonitorCaptureFactory = _ => new WindowsMonitorCapture(cableId),
        });
        session.Noticed += notice => Console.WriteLine($"[notice] {notice.Kind}: {notice.Message}");

        var attach = session.AttachOutput();
        Console.WriteLine(attach.Accepted
            ? "输出已接入（发送/监听全部关闭，符合启动安全语义）"
            : $"输出接入失败: {attach.Rejection}");
        if (!attach.Accepted)
        {
            return 1;
        }

        if (pid != 0)
        {
            var bound = session.BindApplication(new AppBinding(pid, $"pid {pid}"));
            Console.WriteLine(bound.Accepted ? $"已绑定应用 pid={pid}" : $"绑定失败: {bound.Rejection}");
        }

        Console.WriteLine("命令: help status metrics | mic on|off | extras on|off | app on|off | app bind <pid>");
        Console.WriteLine("      play <file> | preview <file> | stop | monitor clip|full|off | gain mic|app|clip|monitor <v>");
        Console.WriteLine("      mute | unmute | auto on|off | quit");
        using var metrics = new MetricsPrinter(session);
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var command = parts[0].ToLowerInvariant();
            if (command is "quit" or "exit")
            {
                break;
            }

            if (!Handle(session, command, parts, metrics))
            {
                Console.WriteLine("未知命令，输入 help 查看帮助");
            }
        }

        return 0;
    }

    private static bool Handle(AudioSession session, string command, string[] parts, MetricsPrinter metrics)
    {
        switch (command)
        {
            case "help":
                Console.WriteLine("mic|extras|app on|off, bind app <pid>, play|preview <file>, stop,");
                Console.WriteLine("monitor clip|full|off, gain mic|app|clip|monitor <v>, mute, unmute,");
                Console.WriteLine("status, metrics, auto on|off, quit");
                return true;
            case "status":
                PrintStatus(session);
                return true;
            case "metrics":
                PrintMetrics(session);
                return true;
            case "auto":
                metrics.Enabled = parts.Length > 1 && parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                return true;
            case "mic":
            case "extras":
            case "app":
                if (parts.Length != 2 || !TryOnOff(parts[1], out var on))
                {
                    Console.WriteLine("用法: mic on|off");
                    return true;
                }

                var result = command switch
                {
                    "mic" => session.EnableMic(on),
                    "extras" => session.EnableExtras(on),
                    _ => session.EnableApp(on),
                };
                Console.WriteLine(result.Accepted ? "ok" : $"拒绝: {result.Rejection}");
                return true;
            case "bind":
                if (parts.Length == 3 && parts[1] == "app" && uint.TryParse(parts[2], out var pid))
                {
                    var bound = session.BindApplication(new AppBinding(pid, $"pid {pid}"));
                    Console.WriteLine(bound.Accepted ? "ok" : $"拒绝: {bound.Rejection}");
                    return true;
                }

                Console.WriteLine("用法: bind app <pid>");
                return true;
            case "play":
            case "preview":
                if (parts.Length != 2)
                {
                    Console.WriteLine($"用法: {command} <file>");
                    return true;
                }

                var play = command == "play" ? session.PlayClip(parts[1]) : session.PreviewClip(parts[1]);
                Console.WriteLine(play.Accepted ? "ok" : $"拒绝: {play.Rejection}");
                return true;
            case "stop":
                var stop = session.StopClip();
                Console.WriteLine(stop.Accepted ? "ok" : $"拒绝: {stop.Rejection}");
                return true;
            case "monitor":
                if (parts.Length != 2)
                {
                    Console.WriteLine("用法: monitor clip|full|off");
                    return true;
                }

                var monitorResult = parts[1] switch
                {
                    "clip" => session.SetMonitor(true, MonitorMode.ClipOnly),
                    "full" => session.SetMonitor(true, MonitorMode.FullEndpoint),
                    "off" => session.SetMonitor(false, MonitorMode.ClipOnly),
                    _ => CommandResult.Reject("用法: monitor clip|full|off"),
                };
                Console.WriteLine(monitorResult.Accepted ? "ok" : $"拒绝: {monitorResult.Rejection}");
                return true;
            case "gain":
                if (parts.Length != 3 || !float.TryParse(parts[2], out var value))
                {
                    Console.WriteLine("用法: gain mic|app|clip|monitor <v>");
                    return true;
                }

                var status = session.Status();
                var gains = parts[1] switch
                {
                    "mic" => status.Gains with { Mic = value },
                    "app" => status.Gains with { App = value },
                    "clip" => status.Gains with { Clip = value },
                    "monitor" => status.Gains with { Monitor = value },
                    _ => status.Gains,
                };
                var gainResult = session.SetGains(gains);
                Console.WriteLine(gainResult.Accepted ? "ok" : $"拒绝: {gainResult.Rejection}");
                return true;
            case "mute":
                Console.WriteLine(session.EmergencyMute().Accepted ? "紧急静音已锁存" : "失败");
                return true;
            case "unmute":
                Console.WriteLine(session.UnlockEmergency().Accepted ? "已解除（全部仍关闭）" : "失败");
                return true;
            default:
                return false;
        }
    }

    private static void PrintStatus(AudioSession session)
    {
        var status = session.Status();
        Console.WriteLine($"phase={status.Phase} emergency={status.EmergencyLatched} outputLost={status.OutputLost}");
        Console.WriteLine($"mic={status.Switches.MicSendEnabled} extras={status.Switches.ExtrasSendEnabled} "
            + $"app={status.Switches.AppSendEnabled} monitor={status.Switches.MonitorEnabled}({status.MonitorMode})");
        Console.WriteLine($"gain mic={status.Gains.Mic:F2} app={status.Gains.App:F2} clip={status.Gains.Clip:F2} monitor={status.Gains.Monitor:F2}");
        Console.WriteLine($"appBinding={(status.AppBinding is { } binding ? binding.Description : "未绑定")}");
        Console.WriteLine($"clip={(status.Clip is { } active ? $"{active.ClipId} [{active.Mode}]" : "无")}");
    }

    private static void PrintMetrics(AudioSession session)
    {
        var m = session.Metrics();
        Console.WriteLine(
            $"mic q={m.Mic.QueuedFrames}({m.MicRatio:F4}) drop={m.Mic.DroppedFrames} deny={m.Mic.DeniedFrames} | " +
            $"app q={m.App.QueuedFrames}({m.AppRatio:F4}) drop={m.App.DroppedFrames} deny={m.App.DeniedFrames} | " +
            $"mix peak={m.MixerPeak:F3} clip={m.MixerClippedSamples} under={m.MixerUnderrunFrames} denied={m.MixerDeniedPulls} hard={m.MixerHardSteps} | " +
            $"mon q={m.Monitor.QueuedFrames} drop={m.Monitor.FullEndpointDroppedFrames}");
    }

    private static bool TryOnOff(string text, out bool value)
    {
        if (text.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (text.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static string? ResolveEndpoint(
        Dictionary<string, string> options,
        string idKey,
        string indexKey,
        IReadOnlyList<AudioEndpointInfo> devices,
        bool capture,
        string role)
    {
        if (options.TryGetValue(idKey, out var id))
        {
            return devices.Any(d => d.Id == id) ? id : null;
        }

        var flow = capture ? "Capture" : "Render";
        var candidates = devices.Where(d => d.DataFlow == flow).ToList();
        if (options.TryGetValue(indexKey, out var indexText) && int.TryParse(indexText, out var index)
            && index >= 0 && index < candidates.Count)
        {
            return candidates[index].Id;
        }

        Console.WriteLine($"== 选择{role} ==");
        for (var i = 0; i < candidates.Count; i++)
        {
            Console.WriteLine($"  {i}: {candidates[i].FriendlyName} [{candidates[i].State}]");
        }

        Console.Write("输入编号: ");
        var input = Console.ReadLine();
        if (!int.TryParse(input, out var chosen) || chosen < 0 || chosen >= candidates.Count)
        {
            Console.WriteLine("无效编号");
            return null;
        }

        return candidates[chosen].Id;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i][2..]] = args[i + 1];
            }
        }

        return result;
    }

    private sealed class MetricsPrinter : IDisposable
    {
        private readonly Timer _timer;

        public MetricsPrinter(AudioSession session)
        {
            _timer = new Timer(_ =>
            {
                if (!Enabled)
                {
                    return;
                }

                Console.Write("  metrics ");
                PrintMetrics(session);
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public bool Enabled { get; set; }

        public void Dispose() => _timer.Dispose();
    }
}
