namespace OnbodyMic.Desktop;

using System.Runtime.InteropServices;
using OnbodyMic.Cli;

/// <summary>
/// Desktop entry point. Normal mode runs the tray application;
/// <c>--selftest</c> builds the whole shell (settings/library/session/preset
/// manager, main window, tray) without a message loop, verifies a status
/// snapshot and tears down — the automatable P3 smoke surface.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = ArgValue(args, "--root");
        RootOverride = root;
        if (args.Contains("--selftest"))
        {
            return SelfTest(root);
        }

        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            LogEverywhere($"[crash-handler] 未观察的任务异常（已记录，不中止）: {e.Exception?.GetType().Name}");
        };
        System.Windows.Forms.Application.ThreadException += (_, e) => ReportCrash("UI thread", e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ReportCrash("AppDomain", ex, fatal: true);
            }
        };

        using var single = new Mutex(true, @"Local\OnbodyMic.Desktop.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("随声麦已在运行（查看托盘图标）", "随声麦", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var logs = new List<string>();
        using var shell = new AppShell(root, message =>
        {
            logs.Add(message);
            Console.WriteLine(message);
            if (LogPath.Length == 0 && Diagnostics.TryEnableFileLog(LogDirectory(), out var opened))
            {
                LogPath = opened;
                LogEverywhere($"[startup] 随声麦 v{VersionText()}，日志: {LogPath}（仅本地记录，不上传）");
            }

            Diagnostics.WriteLog(LogPath, message);
            if (logs.Count > 200)
            {
                logs.RemoveRange(0, 100); // bounded in-memory log
            }
        });

        var restored = shell.TryRestoreStartup();
        var context = new TrayContext(shell, showFirstRun: !restored);
        System.Windows.Forms.Application.Run(context);
        GC.KeepAlive(single);
        return 0;
    }

    private static string VersionText() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string? RootOverride { get; set; }

    private static string LogDirectory() =>
        Path.Combine(Soundboard.ResolveRoot(RootOverride), "logs");

    private static string LogPath { get; set; } = string.Empty;

    private static void LogEverywhere(string message)
    {
        Console.WriteLine(message);
        Diagnostics.WriteLog(LogPath, message);
    }

    private static void ReportCrash(string source, Exception exception, bool fatal)
    {
        var path = Diagnostics.WriteCrashReport(LogDirectory(), source, exception);
        LogEverywhere($"[crash-handler] {(fatal ? "致命" : "UI")}异常已记录: {exception.GetType().Name}（{path}）");
        if (!fatal)
        {
            MessageBox.Show(
                $"程序遇到一个错误，已恢复运行。\n详情已写入：{path}\n\n{exception.GetType().Name}: {exception.Message}",
                "随声麦", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static int SelfTest(string? root)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            using var shell = new AppShell(root, message => Console.WriteLine(message));
            var restored = shell.TryRestoreStartup();
            var status = shell.Session.Status();
            var presets = shell.Presets.List();

            using var main = new MainForm(shell, () => { });
            main.RefreshStatus();
            using var tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = false };
            using var wizard = new FirstRunWizard(shell);
            using var settings = new DeviceSettingsForm(shell);
            using var hotkeys = new HotkeyEditorForm(shell, null);
            using var picker = new AppPickerForm(shell);

            Console.WriteLine($"selftest: restored={restored} phase={status.Phase} presets={presets.Count}");
            Console.WriteLine($"selftest: switches={status.Switches} devices={(status.Devices is null ? "none" : "configured")}");

            var failures = LayoutAudit.Run(main);
            foreach (var line in LayoutAudit.Report(main))
            {
                Console.WriteLine($"selftest: {line}");
            }

            Console.WriteLine(failures.Count == 0
                ? "selftest: layout OK"
                : $"selftest: layout FAIL ({failures.Count} 项)");
            foreach (var failure in failures)
            {
                Console.WriteLine($"selftest: layout-problem {failure}");
            }

            Console.WriteLine(failures.Count == 0 ? "selftest: OK" : "selftest: FAIL");
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"selftest: FAIL {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
