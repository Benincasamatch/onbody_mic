namespace OnbodyMic.Desktop;

using OnbodyMic.Domain;
using OnbodyMic.Platform.Windows;

/// <summary>
/// Device settings (§11): one place for the four endpoints plus the monitor
/// preference. Saving writes into the active preset and re-applies it through
/// the safe switch transaction, so everything turns off and re-enabling stays
/// explicit.
///
/// Endpoint enumeration happens off the UI thread and can be re-run with
/// 「刷新设备」 without closing the dialog; a device that vanished stays listed
/// as 「(缺失)」 instead of silently falling back to another endpoint (B05).
/// </summary>
internal sealed class DeviceSettingsForm : Form
{
    private readonly AppShell _shell;
    private readonly ComboBox _micCombo = Theme.DropDown();
    private readonly ComboBox _renderCombo = Theme.DropDown();
    private readonly ComboBox _cableCombo = Theme.DropDown();
    private readonly ComboBox _monitorCombo = Theme.DropDown();
    private readonly ComboBox _monitorMode = Theme.DropDown();
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _refreshButton;
    private readonly Button _enableButton;
    private readonly Label _status;
    private IReadOnlyList<AudioEndpointInfo> _devices = [];
    private bool _busy;

    public DeviceSettingsForm(AppShell shell)
    {
        _shell = shell;
        Theme.InitializeFor(this);
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Body;
        Text = "随声麦 · 设备设置";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBack;
        ClientSize = Theme.DS(700, 460);
        MinimumSize = Theme.DS(600, 400);

        _saveButton = Theme.PrimaryButton("保存并重新应用");
        _saveButton.Click += (_, _) => Save();
        _cancelButton = Theme.SecondaryButton("取消");
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Margin = Theme.DP(Theme.GapSmall, 0, 0, 0);
        _refreshButton = Theme.SecondaryButton("刷新设备");
        _refreshButton.Click += async (_, _) => await LoadDevicesAsync();
        _refreshButton.Margin = Theme.DP(Theme.GapSmall, 0, 0, 0);

        // A disabled endpoint cannot be used, and on a clean machine the virtual
        // capture endpoint starts disabled — without this the user can only fix it
        // from the command line (user report: 没有虚拟麦克风输出).
        _enableButton = Theme.SecondaryButton("启用选中端点");
        _enableButton.Click += async (_, _) => await EnableSelectedAsync();
        _enableButton.Margin = Theme.DP(Theme.GapSmall, 0, 0, 0);

        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(Theme.D(600), 0),
            ForeColor = Theme.TextSecondary,
            Margin = Theme.DP(0, Theme.GapSmall, 0, 0),
        };

        BuildLayout();
        Load += async (_, _) => await LoadDevicesAsync();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = Theme.DP(Theme.WindowMargin),
            BackColor = Theme.WindowBack,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // The label column sizes to its longest label: a fixed width clipped
        // "独占写入" and "读取源" at 125% DPI.
        var card = Theme.Card(
            new ColumnStyle(SizeType.AutoSize),
            new ColumnStyle(SizeType.Percent, 100f));
        card.Dock = DockStyle.Fill;
        card.Paint += Theme.CardBorder;
        card.Margin = new Padding(0, 0, 0, Theme.D(Theme.GapMedium));

        AddRow(card, "真实麦克风", _micCombo, row: 0);
        AddRow(card, "虚拟输出（线缆 Render，独占写入）", _renderCombo, row: 1);
        AddRow(card, "虚拟麦 Capture（实际端点监听用，可留空）", _cableCombo, row: 2);
        AddRow(card, "监听输出（耳机——反听从这出声）", _monitorCombo, row: 3);

        _monitorMode.Width = Theme.D(200);
        _monitorMode.Items.AddRange(["仅音效", "实际虚拟麦"]);
        _monitorMode.SelectedIndex = _shell.Session.Status().MonitorMode == MonitorMode.FullEndpoint ? 1 : 0;
        AddRow(card, "监听偏好", _monitorMode, row: 4);

        var note = new Label
        {
            Text = "保存会写回当前活动预设并重新应用：所有发送与监听先关闭，再由你手动开启。\n"
                + "提示：KOOK 需手动选择虚拟麦（Capture 对应的录音端点）；不要把虚拟线设为系统默认播放。",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Font = Theme.NoteFont,
            Margin = Theme.DP(0, Theme.GapSmall, 0, 0),
        };
        AddSpan(card, note, row: 5);
        AddSpan(card, _status, row: 6);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0),
        };
        buttons.Controls.AddRange([_saveButton, _cancelButton, _refreshButton, _enableButton]);

        root.Controls.Add(card, 0, 0);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        CancelButton = _cancelButton;
        AcceptButton = _saveButton;
    }

    private static void AddRow(TableLayoutPanel card, string label, ComboBox combo, int row)
    {
        card.RowCount = Math.Max(card.RowCount, row + 1);
        while (card.RowStyles.Count <= row)
        {
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var text = new Label
        {
            Text = label,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Theme.DP(0, Theme.D(8), Theme.GapMedium, Theme.GapSmall),
        };
        combo.Dock = DockStyle.Fill;
        combo.Margin = Theme.DP(0, 0, 0, Theme.GapSmall);
        card.Controls.Add(text, 0, row);
        card.Controls.Add(combo, 1, row);
    }

    private static void AddSpan(TableLayoutPanel card, Control control, int row)
    {
        card.RowCount = Math.Max(card.RowCount, row + 1);
        while (card.RowStyles.Count <= row)
        {
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        card.Controls.Add(control, 0, row);
        card.SetColumnSpan(control, 2);
    }

    private async Task LoadDevicesAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _refreshButton.Enabled = false;
        _saveButton.Enabled = false;
        SetStatus("正在扫描音频设备…", Theme.TextSecondary);
        try
        {
            var current = _shell.Session.Status().Devices;

            // include disabled endpoints: on a clean machine the virtual capture
            // endpoint is disabled and must still be selectable (and enableable)
            var devices = await Task.Run(() => WindowsAudioDevices.List(activeOnly: false));
            _devices = devices;
            Fill(_micCombo, capture: true, current?.MicDeviceId, allowEmpty: false);
            Fill(_renderCombo, capture: false, current?.VirtualRenderDeviceId, allowEmpty: false);
            Fill(_cableCombo, capture: true, current?.VirtualCaptureDeviceId, allowEmpty: true);
            Fill(_monitorCombo, capture: false, current?.MonitorDeviceId, allowEmpty: false);

            var missing = new[] { current?.MicDeviceId, current?.VirtualRenderDeviceId, current?.MonitorDeviceId }
                .Count(id => id is not null && devices.All(d => d.Id != id));
            var disabled = devices.Count(d => d.State != "Active" && d.State != "NotPresent");
            var active = devices.Count(d => d.State == "Active");
            var text = devices.Count == 0
                ? "未检测到任何音频端点：确认声卡/线缆驱动正常后点「刷新设备」。"
                : missing > 0
                    ? $"已发现 {devices.Count} 个端点（{active} 个可用），但预设中有 {missing} 个端点已不存在。"
                    : disabled > 0
                        ? $"已发现 {devices.Count} 个端点，其中 {disabled} 个被禁用（标为「已禁用」，选中后点「启用选中端点」）。"
                        : $"已发现 {devices.Count} 个端点，全部可用。";
            SetStatus(text, missing > 0 || disabled > 0 ? Theme.Warning : Theme.TextSecondary);
        }
        catch (Exception ex)
        {
            SetStatus($"设备扫描失败：{ex.Message}", Theme.Error);
        }
        finally
        {
            _busy = false;
            _refreshButton.Enabled = true;
            _saveButton.Enabled = true;
        }
    }

    /// <summary>
    /// Enables whichever disabled endpoint the user picked in any of the four
    /// lists, then rescans. The endpoint is enabled through IPolicyConfig (the
    /// same call 声音设置 uses); the result is reported, never assumed.
    /// </summary>
    private async Task EnableSelectedAsync()
    {
        // Enable every disabled endpoint the dialog currently knows about. The
        // button used to require a selection in a combo box, so a user who only
        // opened the dialog got a status line and nothing happened — the virtual
        // microphone stayed dead.
        var targets = _devices
            .Where(d => d.State != "Active" && d.State != "NotPresent")
            .GroupBy(d => d.Id)
            .Select(g => g.First())
            .ToList();

        if (targets.Count == 0)
        {
            SetStatus("没有需要启用的端点（列表中的端点都可用）。", Theme.TextSecondary);
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        _enableButton.Enabled = false;
        _saveButton.Enabled = false;
        try
        {
            var enabled = new List<string>();
            var failed = new List<string>();
            foreach (var target in targets)
            {
                SetStatus($"正在启用「{target.FriendlyName}」…", Theme.TextSecondary);
                var ok = await Task.Run(() =>
                {
                    try
                    {
                        WindowsEndpointControl.SetEnabled(target.Id, enabled: true);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _shell.Log($"[devices] 启用端点失败 {target.Id}: {ex.GetType().Name}: {ex.Message}");
                        return false;
                    }
                });

                if (ok)
                {
                    enabled.Add(target.FriendlyName);
                }
                else
                {
                    failed.Add(target.FriendlyName);
                }
            }

            await LoadDevicesAsync();
            _shell.Devices.RefreshAsync();

            var text = enabled.Count > 0
                ? $"已启用 {string.Join("、", enabled)}。"
                : "未能启用任何端点。";
            if (failed.Count > 0)
            {
                text += $"\n失败：{string.Join("、", failed)}（该端点可能属于其他用户或驱动不支持启用）。";
            }

            SetStatus(text, failed.Count == 0 ? Theme.Success : Theme.Error);
        }
        finally
        {
            _busy = false;
            _enableButton.Enabled = true;
            _saveButton.Enabled = true;
        }
    }

    private void Fill(ComboBox combo, bool capture, string? currentId, bool allowEmpty)
    {
        combo.BeginUpdate();
        try
        {
            combo.Items.Clear();
            var selected = 0;
            var endpoints = _devices.Where(d => d.DataFlow == (capture ? "Capture" : "Render")).ToList();
            for (var i = 0; i < endpoints.Count; i++)
            {
                combo.Items.Add(new EndpointRow(endpoints[i], Missing: false));
                if (currentId is not null && endpoints[i].Id == currentId)
                {
                    selected = i;
                }
            }

            if (allowEmpty)
            {
                combo.Items.Add("（不配置——实际端点监听将不可用）");
                if (currentId is null)
                {
                    selected = combo.Items.Count - 1;
                }
            }

            if (combo.Items.Count == 0)
            {
                combo.Items.Add("（无可用设备）");
                selected = 0;
            }
            else if (currentId is not null && endpoints.All(e => e.Id != currentId))
            {
                // a configured endpoint that vanished stays visible and selectable;
                // never silently substitute another device (B05)
                combo.Items.Add(new EndpointRow(
                    new AudioEndpointInfo(currentId, "（已缺失的端点）", capture ? "Capture" : "Render", "NotPresent"),
                    Missing: true));
                selected = combo.Items.Count - 1;
            }

            combo.SelectedIndex = selected;
        }
        finally
        {
            combo.EndUpdate();
        }
    }

    private void Save()
    {
        if (_micCombo.SelectedItem is not EndpointRow mic
            || _renderCombo.SelectedItem is not EndpointRow render
            || _monitorCombo.SelectedItem is not EndpointRow monitor)
        {
            SetStatus("请选择麦克风、虚拟输出与监听输出。", Theme.Error);
            return;
        }

        var presetId = _shell.Presets.ActivePresetId;
        if (presetId is null)
        {
            SetStatus("没有活动预设：先在主窗口「配置管理」中新建预设。", Theme.Error);
            return;
        }

        var cable = _cableCombo.SelectedItem as EndpointRow;
        var result = _shell.Presets.UpdateDeviceSettings(
            presetId,
            new PresetDeviceConfig(
                mic.Endpoint.Id,
                render.Endpoint.Id,
                cable?.Endpoint.Id,
                monitor.Endpoint.Id,
                _shell.Session.Status().Devices?.Application),
            new MonitorPreferences(
                _monitorMode.SelectedIndex == 1 ? MonitorMode.FullEndpoint : MonitorMode.ClipOnly,
                _shell.Session.Status().Gains.Monitor));
        if (!result.Accepted)
        {
            SetStatus($"{Rejections.Describe(result.Rejection)}　{Rejections.NextStep(result.Rejection)}", Theme.Error);
            return;
        }

        if (result.Issues.Count > 0 || result.OutputAttachRejection is not null)
        {
            var detail = string.Join("；", result.Issues.Select(i => $"{i.Route}:{i.Reason}"));
            if (result.OutputAttachRejection is not null)
            {
                detail += $"；输出接入失败: {result.OutputAttachRejection}";
            }

            MessageBox.Show(
                this,
                $"设备已保存，但存在问题：{detail}\n\n处理：确认端点存在且未被独占，然后再次保存。",
                "设备设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _shell.Devices.RefreshAsync();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private sealed record EndpointRow(AudioEndpointInfo Endpoint, bool Missing)
    {
        /// <summary>True when Windows reports the endpoint disabled/unplugged: selectable, but not usable until enabled.</summary>
        public bool Disabled => !Missing && Endpoint.State != "Active";

        public override string ToString() => Missing
            ? $"{Endpoint.FriendlyName}（未检测到）"
            : Disabled
                ? $"{Endpoint.FriendlyName}（已禁用 — 选中后点「启用选中端点」）"
                : Endpoint.FriendlyName;
    }
}
