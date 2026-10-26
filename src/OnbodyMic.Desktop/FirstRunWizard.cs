namespace OnbodyMic.Desktop;

using OnbodyMic.Domain;
using OnbodyMic.Platform.Windows;

/// <summary>
/// First-run onboarding (docs/01 §4.1): pick the four endpoints, choose a
/// monitor preference, create the first preset. Applying stays safe — all
/// sends/monitoring remain off until explicitly enabled. KOOK setup is the
/// user's explicit action; this wizard only explains it.
/// </summary>
internal sealed class FirstRunWizard : Form
{
    private readonly AppShell _shell;

    private readonly ComboBox _micCombo = Theme.DropDown();
    private readonly ComboBox _renderCombo = Theme.DropDown();
    private readonly ComboBox _cableCombo = Theme.DropDown();
    private readonly ComboBox _monitorCombo = Theme.DropDown();
    private readonly ComboBox _monitorMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _nameBox = new() { Text = "开黑默认", Width = 200 };
    private readonly Button _okButton = new() { Text = "创建并应用预设" };
    private readonly Button _skipButton = new() { Text = "跳过（稍后再配置）", DialogResult = DialogResult.Cancel };

    public FirstRunWizard(AppShell shell)
    {
        _shell = shell;
        Text = "随声麦 · 首次引导";
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        // Resizable, and wide enough for real endpoint names: the fixed 560px
        // dialog clipped both the combo text and the bottom buttons.
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = Theme.DS(680, 480);
        MinimumSize = Theme.DS(600, 440);
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = Theme.DP(Theme.GapMedium),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // Explicit row/column placement: Controls.Add without a cell lets the
        // table pick cells in add order, which scrambled the dialog.
        var row = 0;
        void Field(string label, Control input)
        {
            layout.RowCount = row + 1;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = Theme.DP(0, 0, Theme.GapMedium, Theme.GapSmall),
            }, 0, row);
            input.Dock = DockStyle.Fill;
            input.Margin = Theme.DP(0, 0, 0, Theme.GapSmall);
            layout.Controls.Add(input, 1, row);
            row++;
        }

        Fill(_micCombo, capture: true, preferred: "麦克风");
        Field("真实麦克风", _micCombo);
        Fill(_renderCombo, capture: false, preferred: "Steam");
        Field("虚拟输出（线缆 Render，独占写入）", _renderCombo);
        Fill(_cableCombo, capture: true, preferred: "Steam", optional: "（暂不配置）");
        Field("虚拟麦 Capture（实际端点监听用，可暂空）", _cableCombo);
        Fill(_monitorCombo, capture: false, preferred: null);
        Field("监听输出（耳机）", _monitorCombo);
        _monitorMode.Items.AddRange(["仅音效", "实际虚拟麦"]);
        _monitorMode.SelectedIndex = 0;
        Field("监听偏好（开启状态不会被恢复）", _monitorMode);
        Field("预设名称", _nameBox);

        // Trailing 100% row absorbs the dialog's extra height. Without it the
        // last field's row is stretched and its label drifts away from the input.
        layout.RowCount = row + 1;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = Theme.D(56),
            ForeColor = Theme.TextSecondary,
            Text = "提示：在 KOOK 中手动把输入设备选为该虚拟麦的录音端点；不要把虚拟线设为系统默认播放设备。\n"
                + "创建后所有发送与监听保持关闭，由你在主窗口或热键逐路开启。",
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = Theme.DP(Theme.GapMedium, Theme.GapSmall, Theme.GapMedium, Theme.GapMedium),
        };
        _skipButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _skipButton.MinimumSize = new Size(Theme.D(150), Theme.D(Theme.ControlHeight));
        _okButton.MinimumSize = new Size(Theme.D(150), Theme.D(Theme.ControlHeight));
        buttons.Controls.Add(_okButton);
        buttons.Controls.Add(_skipButton);

        Controls.Add(layout);
        Controls.Add(note);
        Controls.Add(buttons);

        _okButton.Click += (_, _) => CreateAndApply();
    }

    private static void Fill(ComboBox combo, bool capture, string? preferred, string? optional = null)
    {
        combo.Items.Clear();
        var defaultId = WindowsAudioDevices.DefaultDeviceId(capture);

        // Include disabled endpoints: on a clean machine the Steam virtual capture
        // endpoint ships disabled, and an active-only list leaves the user with no
        // selectable virtual microphone (user report: 没有虚拟麦克风输出).
        var endpoints = WindowsAudioDevices.List(activeOnly: false)
            .Where(d => d.DataFlow == (capture ? "Capture" : "Render"))
            .ToList();
        var preferredIndex = -1;
        foreach (var endpoint in endpoints)
        {
            var isDefault = endpoint.Id == defaultId;
            var active = endpoint.State == "Active";
            combo.Items.Add(new EndpointRow(endpoint, isDefault, active));

            // a disabled endpoint must never be pre-selected: it would create a
            // preset that silently cannot work
            if (!active)
            {
                continue;
            }

            if (preferred is not null && endpoint.FriendlyName.Contains(preferred, StringComparison.OrdinalIgnoreCase)
                && preferredIndex < 0)
            {
                preferredIndex = combo.Items.Count - 1;
            }

            // without a preferred candidate, the system default is the sane initial pick
            if (preferred is null && isDefault)
            {
                preferredIndex = combo.Items.Count - 1;
            }
        }

        if (optional is not null)
        {
            combo.Items.Add(optional);
        }

        combo.SelectedIndex = preferredIndex >= 0 ? preferredIndex : combo.Items.Count > 0 ? 0 : -1;
    }

    private void CreateAndApply()
    {
        if (_micCombo.SelectedItem is not EndpointRow mic
            || _renderCombo.SelectedItem is not EndpointRow render
            || _monitorCombo.SelectedItem is not EndpointRow monitor)
        {
            MessageBox.Show(this, "请选择麦克风、虚拟输出与监听输出", "随声麦");
            return;
        }

        var cable = _cableCombo.SelectedItem as EndpointRow;
        var config = new PresetConfig(
            _shell.PresetStore.NewId(),
            string.IsNullOrWhiteSpace(_nameBox.Text) ? "开黑默认" : _nameBox.Text.Trim(),
            new PresetDeviceConfig(
                mic.Endpoint.Id,
                render.Endpoint.Id,
                cable?.Endpoint.Id,
                monitor.Endpoint.Id,
                AppSelection.None),
            1f,
            1f,
            1f,
            new MonitorPreferences(_monitorMode.SelectedIndex == 1 ? MonitorMode.FullEndpoint : MonitorMode.ClipOnly, 1f),
            [],
            false,
            null);

        var saved = _shell.PresetStore.Save(config);
        if (!saved.Accepted)
        {
            MessageBox.Show(this, $"预设保存失败: {saved.Rejection}", "随声麦");
            return;
        }

        var applied = _shell.Presets.Apply(config.PresetId);
        if (!applied.Accepted)
        {
            MessageBox.Show(this, $"预设应用失败: {applied.Rejection}", "随声麦");
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record EndpointRow(AudioEndpointInfo Endpoint, bool IsDefault, bool Active)
    {
        public bool Disabled => !Active;

        public override string ToString() => (IsDefault, Disabled) switch
        {
            (true, false) => $"{Endpoint.FriendlyName}（当前系统默认）",
            (false, true) => $"{Endpoint.FriendlyName}（已禁用 — 创建后到「设备设置…」启用）",
            _ => Endpoint.FriendlyName,
        };
    }
}
