namespace OnbodyMic.Desktop;

using System.Runtime.InteropServices;

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
        var root = ArgValue(args, "--root");
        if (args.Contains("--selftest"))
        {
            return SelfTest(root);
        }

        ApplicationConfiguration.Initialize();
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
            if (logs.Count > 200)
            {
                logs.RemoveRange(0, 100); // bounded in-memory log; nothing persisted by default
            }
        });

        var restored = shell.TryRestoreStartup();
        var context = new TrayContext(shell, showFirstRun: !restored);
        System.Windows.Forms.Application.Run(context);
        GC.KeepAlive(single);
        return 0;
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

            Console.WriteLine($"selftest: restored={restored} phase={status.Phase} presets={presets.Count}");
            Console.WriteLine($"selftest: switches={status.Switches} devices={(status.Devices is null ? "none" : "configured")}");
            Console.WriteLine("selftest: OK");
            return 0;
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
