namespace OnbodyMic.Desktop;

using OnbodyMic.Domain;

/// <summary>
/// Tray lifecycle (docs/01 §6): the window hides to tray on close (audio
/// keeps running — explained once via balloon); only the tray's 退出 tears
/// everything down. Menu switch state comes from the same session snapshot
/// the main window polls.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppShell _shell;
    private readonly MainForm _main;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private readonly ToolStripMenuItem _openItem = new("打开主窗口");
    private readonly ToolStripMenuItem _micItem = new("真麦发送");
    private readonly ToolStripMenuItem _extrasItem = new("附加音频（应用+音效）");
    private readonly ToolStripMenuItem _monitorItem = new("本地监听");
    private readonly ToolStripMenuItem _muteItem = new("紧急全静音");
    private readonly ToolStripMenuItem _exitItem = new("退出");

    public TrayContext(AppShell shell, bool showFirstRun)
    {
        _shell = shell;
        _main = new MainForm(shell, () => { });
        _main.Tray = this;


        var menu = new ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_micItem);
        menu.Items.Add(_extrasItem);
        menu.Items.Add(_monitorItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_muteItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "随声麦 Onbody Mic",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _main.ShowFromTray();

        _openItem.Click += (_, _) => _main.ShowFromTray();
        _micItem.Click += (_, _) => Toggle(() => _shell.Session.EnableMic(!_shell.Session.Status().Switches.MicSendEnabled), "真麦发送");
        _extrasItem.Click += (_, _) => Toggle(() => _shell.Session.EnableExtras(!_shell.Session.Status().Switches.ExtrasSendEnabled), "附加音频");
        _monitorItem.Click += (_, _) => Toggle(() => _shell.Session.SetMonitor(
            !_shell.Session.Status().Switches.MonitorEnabled,
            _shell.Session.Status().MonitorMode), "监听");
        _muteItem.Click += (_, _) => Toggle(
            () => _shell.Session.Status().EmergencyLatched
                ? _shell.Session.UnlockEmergency()
                : _shell.Session.EmergencyMute(),
            "紧急静音");
        _exitItem.Click += (_, _) => ExitApplication();

        _timer.Tick += (_, _) => RefreshTray();
        _timer.Start();
        RefreshTray();

        if (showFirstRun)
        {
            using var wizard = new FirstRunWizard(_shell);
            if (wizard.ShowDialog(_main) == DialogResult.OK)
            {
                _main.ShowFromTray();
            }
        }
        else
        {
            _main.ShowFromTray();
        }
    }

    /// <summary>One-time explanation that closing the window does not stop sending (P3 gate).</summary>
    public void NotifyHiddenToTray(IWin32Window owner)
    {
        var settings = _shell.Soundboard.Settings.Data;
        if (settings.Ui.CloseToTrayAcknowledged)
        {
            return;
        }

        settings.Ui.CloseToTrayAcknowledged = true;
        _shell.Soundboard.SaveSettingsPublic();
        _tray.BalloonTipTitle = "随声麦仍在运行";
        _tray.BalloonTipText = "关闭窗口不会停止发送；停止一切请用托盘菜单“退出”（退出将停止本程序全部音频与热键）。";
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(5000);
    }

    private void Toggle(Func<CommandResult> command, string what)
    {
        var result = command();
        if (!result.Accepted)
        {
            _tray.BalloonTipTitle = what;
            _tray.BalloonTipText = $"被拒绝: {result.Rejection}";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
            _tray.ShowBalloonTip(3000);
        }

        RefreshTray();
        _main.RefreshStatus();
    }

    private void RefreshTray()
    {
        var status = _shell.Session.Status();
        _micItem.Checked = status.Switches.MicSendEnabled;
        _extrasItem.Checked = status.Switches.ExtrasSendEnabled;
        _monitorItem.Checked = status.Switches.MonitorEnabled;
        var phase = status.EmergencyLatched
            ? "紧急静音"
            : status.Switches.AnySendEnabled ? "发送中" : "全关";
        _tray.Text = $"随声麦 — {phase}{(status.Switches.MonitorEnabled ? "+监听" : "")}";
    }

    private void ExitApplication()
    {
        if (!_main.ConfirmDiscardOrSave("退出"))
        {
            return; // cancel keeps the session exactly as it was — no silent authorization changes
        }

        _timer.Stop();
        _tray.Visible = false;
        _main.Tray = null;
        _main.Close();
        _tray.Dispose();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _shell.Dispose();
        base.ExitThreadCore();
    }
}
