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

    private readonly ComboBox _micCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _renderCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _cableCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _monitorMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _nameBox = new() { Text = "开黑默认", Width = 200 };
    private readonly Button _okButton = new() { Text = "创建并应用预设" };
    private readonly Button _skipButton = new() { Text = "跳过（稍后再配置）", DialogResult = DialogResult.Cancel };

    public FirstRunWizard(AppShell shell)
    {
        _shell = shell;
        Text = "随声麦 · 首次引导";
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 430);
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "真实麦克风", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        Fill(_micCombo, capture: true, preferred: "麦克风");
        layout.Controls.Add(_micCombo);
        layout.Controls.Add(new Label { Text = "虚拟输出（线缆 Render，独占写入）", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        Fill(_renderCombo, capture: false, preferred: "Steam");
        layout.Controls.Add(_renderCombo);
        layout.Controls.Add(new Label { Text = "虚拟麦 Capture（实际端点监听用，可暂空）", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        Fill(_cableCombo, capture: true, preferred: "Steam", optional: "（暂不配置）");
        layout.Controls.Add(_cableCombo);
        layout.Controls.Add(new Label { Text = "监听输出（耳机）", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        Fill(_monitorCombo, capture: false, preferred: null);
        layout.Controls.Add(_monitorCombo);
        layout.Controls.Add(new Label { Text = "监听偏好（开启状态不会被恢复）", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        _monitorMode.Items.AddRange(["仅音效", "实际虚拟麦"]);
        _monitorMode.SelectedIndex = 0;
        layout.Controls.Add(_monitorMode);
        layout.Controls.Add(new Label { Text = "预设名称", AutoSize = true, Margin = new Padding(4, 10, 4, 0) });
        layout.Controls.Add(_nameBox);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            Text = "提示：在 KOOK 中手动把输入设备选为该虚拟麦的录音端点；不要把虚拟线设为系统默认播放设备。\n"
                + "创建后所有发送与监听保持关闭，由你在主窗口或热键逐路开启。",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
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
        var endpoints = WindowsAudioDevices.List(activeOnly: true)
            .Where(d => d.DataFlow == (capture ? "Capture" : "Render"))
            .ToList();
        var preferredIndex = -1;
        foreach (var endpoint in endpoints)
        {
            var isDefault = endpoint.Id == defaultId;
            combo.Items.Add(new EndpointRow(endpoint, isDefault));
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

    private sealed record EndpointRow(AudioEndpointInfo Endpoint, bool IsDefault)
    {
        public override string ToString() => IsDefault
            ? $"{Endpoint.FriendlyName}（当前系统默认）"
            : Endpoint.FriendlyName;
    }
}
