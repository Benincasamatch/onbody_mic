namespace OnbodyMic.Desktop;

using System.Diagnostics;
using OnbodyMic.Application;
using OnbodyMic.Platform.Windows;

/// <summary>
/// Target-application picker (§8.3): lists running processes that expose a
/// main module path, so binding stays identity-by-path (B02) instead of
/// guessing by window title. Enumeration runs off the UI thread.
/// </summary>
internal sealed class AppPickerForm : Form
{
    private readonly ListBox _list = new();
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly Label _status;
    private List<WindowsAppCatalog.AppInstance> _instances = [];

    public AppPickerForm(AppShell shell)
    {
        Theme.InitializeFor(this);
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Body;
        Text = "绑定目标应用";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBack;
        ClientSize = Theme.DS(520, 420);
        MinimumSize = Theme.DS(420, 320);

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.D(Theme.ControlHeight),
            Text = "正在枚举运行中的程序…",
            ForeColor = Theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = Theme.DP(Theme.GapMedium, 0, 0, 0),
        };

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = Theme.Body,
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _list.DoubleClick += (_, _) => Accept();

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.D(Theme.ControlHeight + Theme.GapSmall),
            Text = "只列出有可执行文件路径的进程；系统/受保护进程不会出现。",
            ForeColor = Theme.TextSecondary,
            Padding = Theme.DP(Theme.GapMedium, 0, 0, 0),
        };

        _ok = Theme.PrimaryButton("绑定");
        _ok.Click += (_, _) => Accept();
        _cancel = Theme.SecondaryButton("取消");
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Margin = Theme.DP(Theme.GapSmall, 0, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = Theme.DP(Theme.GapMedium, Theme.GapSmall, Theme.GapMedium, Theme.GapMedium),
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(hint);
        Controls.Add(buttons);
        CancelButton = _cancel;
        AcceptButton = _ok;

        Load += async (_, _) => await LoadAsync();
    }

    /// <summary>Set when the user confirms; the caller performs the actual bind.</summary>
    public WindowsAppCatalog.AppInstance? Selected { get; private set; }

    private async Task LoadAsync()
    {
        _ok.Enabled = false;
        var list = await Task.Run(EnumerateProcesses);
        _instances = list;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var instance in list)
            {
                _list.Items.Add(new Row(instance));
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _status.Text = list.Count == 0
            ? "未找到可绑定的程序：先启动目标应用，再重新打开此窗口。"
            : $"共 {list.Count} 个可绑定进程；选择后点「绑定」。";
        _ok.Enabled = list.Count > 0;
    }

    private static List<WindowsAppCatalog.AppInstance> EnumerateProcesses()
    {
        var result = new List<WindowsAppCatalog.AppInstance>();
        foreach (var process in Process.GetProcesses())
        {
            using var _ = process;
            try
            {
                if (process.MainModule?.FileName is not { Length: > 0 } path)
                {
                    continue;
                }

                result.Add(new WindowsAppCatalog.AppInstance((uint)process.Id, path));
            }
            catch
            {
                // protected/system processes deny module queries
            }
        }

        return result
            .OrderBy(i => Path.GetFileName(i.ExecutablePath), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.ProcessId)
            .ToList();
    }

    private void Accept()
    {
        if (_list.SelectedItem is not Row row)
        {
            return;
        }

        Selected = row.Instance;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record Row(WindowsAppCatalog.AppInstance Instance)
    {
        public override string ToString() =>
            $"{Path.GetFileName(Instance.ExecutablePath)}　(pid {Instance.ProcessId})　{Instance.ExecutablePath}";
    }
}
