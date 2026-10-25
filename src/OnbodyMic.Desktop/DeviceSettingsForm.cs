namespace OnbodyMic.Desktop;

using OnbodyMic.Domain;
using OnbodyMic.Platform.Windows;

/// <summary>
/// Device &amp; monitor settings (docs/01 §6 设置页): reopens the first-run
/// choices any time. Saving writes the endpoints and monitor preference into
/// the active preset and re-applies it through the safe switch transaction —
/// sending and monitoring turn off, re-enabling stays explicit.
/// </summary>
internal sealed class DeviceSettingsForm : Form
{
    private readonly AppShell _shell;
    private readonly ComboBox _micCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly ComboBox _renderCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly ComboBox _cableCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly ComboBox _monitorMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Button _saveButton = new() { Text = "保存并重新应用（将全部关闭）", Dock = DockStyle.Bottom };
    private readonly Button _cancelButton = new() { Text = "取消", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom };

    public DeviceSettingsForm(AppShell shell)
    {
        _shell = shell;
        Text = "随声麦 · 设备与监听设置";
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(580, 400);
        ShowInTaskbar = false;

        var current = _shell.Session.Status().Devices;
        var active = WindowsAudioDevices.List(activeOnly: true);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, AutoSize = true };
        layout.Controls.Add(Row("真实麦克风", _micCombo, current?.MicDeviceId, active, capture: true));
        layout.Controls.Add(Row("虚拟输出（线缆 Render，独占写入）", _renderCombo, current?.VirtualRenderDeviceId, active, capture: false));
        layout.Controls.Add(Row("虚拟麦 Capture（实际端点监听的读取源）", _cableCombo, current?.VirtualCaptureDeviceId, active, capture: true, allowEmpty: true));
        layout.Controls.Add(Row("监听输出（耳机——反听从这出声）", _monitorCombo, current?.MonitorDeviceId, active, capture: false));

        var modeRow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        modeRow.Controls.Add(new Label { Text = "监听偏好（开启状态不会被恢复）", AutoSize = true, Padding = new Padding(0, 4, 12, 0) });
        _monitorMode.Items.AddRange(["仅音效", "实际虚拟麦"]);
        _monitorMode.SelectedIndex = _shell.Session.Status().MonitorMode == MonitorMode.FullEndpoint ? 1 : 0;
        modeRow.Controls.Add(_monitorMode);
        layout.Controls.Add(modeRow);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Text = "保存会写回当前活动预设并重新应用：所有发送与监听先关闭，再由你手动开启。\n"
                + "提示：KOOK 需手动选择虚拟麦（Capture 对应的录音端点）；不要把虚拟线设为系统默认播放。",
        };

        Controls.Add(layout);
        Controls.Add(note);
        Controls.Add(new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Controls = { _saveButton, _cancelButton } });

        _saveButton.Click += (_, _) => Save();
    }

    private static Control Row(string label, ComboBox combo, string? currentId, IReadOnlyList<AudioEndpointInfo> devices, bool capture, bool allowEmpty = false)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        flow.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
        var endpoints = devices.Where(d => d.DataFlow == (capture ? "Capture" : "Render")).ToList();
        var selected = 0;
        for (var i = 0; i < endpoints.Count; i++)
        {
            combo.Items.Add(new EndpointRow(endpoints[i]));
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
        }

        combo.SelectedIndex = selected;
        flow.Controls.Add(combo);
        return flow;
    }

    private void Save()
    {
        if (_micCombo.SelectedItem is not EndpointRow mic
            || _renderCombo.SelectedItem is not EndpointRow render
            || _monitorCombo.SelectedItem is not EndpointRow monitor)
        {
            MessageBox.Show(this, "请选择麦克风、虚拟输出与监听输出", "随声麦");
            return;
        }

        var presetId = _shell.Presets.ActivePresetId;
        if (presetId is null)
        {
            MessageBox.Show(this, "无活动预设；先在主窗口创建预设", "随声麦");
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
            MessageBox.Show(this, $"保存失败: {result.Rejection}", "随声麦");
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    internal sealed record EndpointRow(AudioEndpointInfo Endpoint)
    {
        public override string ToString() => Endpoint.FriendlyName;
    }
}
