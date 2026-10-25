namespace OnbodyMic.Desktop;

using System.ComponentModel;
using System.Diagnostics;
using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using OnbodyMic.Media;

/// <summary>
/// Main window (docs/01 §6): preset bar, send switches, soundboard and a
/// status strip. Every control routes through the same session commands as
/// the hotkeys and the tray; the 500 ms poll keeps window, tray and hotkey
/// state identical (P3 gate).
/// </summary>
internal sealed class MainForm : Form
{
    private const float GainStep = 0.05f;

    private readonly AppShell _shell;
    private readonly Action _onDirty;

    private readonly ComboBox _presetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Button _applyButton = new() { Text = "应用预设" };
    private readonly Button _newButton = new() { Text = "新建" };
    private readonly Button _duplicateButton = new() { Text = "复制" };
    private readonly Button _renameButton = new() { Text = "重命名" };
    private readonly Button _deleteButton = new() { Text = "删除" };
    private readonly Button _saveButton = new() { Text = "保存" };
    private readonly Label _dirtyLabel = new() { Text = "", ForeColor = Color.Firebrick, AutoSize = true };
    private readonly Label _phaseLabel = new() { Text = "...", Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold), AutoSize = true };
    private readonly Button _emergencyButton = new() { Text = "紧急全静音", BackColor = Color.Firebrick, ForeColor = Color.White };

    private readonly CheckBox _micCheck = new() { Text = "真麦发送", AutoSize = true };
    private readonly CheckBox _extrasCheck = new() { Text = "附加音频（应用+音效）", AutoSize = true };
    private readonly CheckBox _appCheck = new() { Text = "应用发送", AutoSize = true };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly TrackBar _monitorVolume = new() { Minimum = 0, Maximum = 400, TickFrequency = 50, Width = 140 };
    private readonly Label _monitorVolumeLabel = new() { AutoSize = true };
    private readonly NumericUpDown _micGain = MakeGain();
    private readonly NumericUpDown _appGain = MakeGain();
    private readonly NumericUpDown _clipGain = MakeGain();

    private readonly ListBox _clipsList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _playButton = new() { Text = "向队友播放" };
    private readonly Button _previewButton = new() { Text = "本地试听（不发送）" };
    private readonly Button _stopButton = new() { Text = "停止音效" };
    private readonly Button _importButton = new() { Text = "导入..." };
    private readonly Button _favoriteButton = new() { Text = "收藏/取消" };
    private readonly Button _deleteClipButton = new() { Text = "删除音效" };

    private readonly ListBox _hotkeyList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private bool _refreshing;

    public MainForm(AppShell shell, Action onDirty)
    {
        _shell = shell;
        _onDirty = onDirty;
        Text = "随声麦 Onbody Mic";
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        RefreshPresets();
        RefreshClips();
        RefreshHotkeys();
        RefreshMonitorCombo();

        _applyButton.Click += (_, _) => ApplySelectedPreset();
        _newButton.Click += (_, _) => CreatePreset();
        _duplicateButton.Click += (_, _) => DuplicatePreset();
        _renameButton.Click += (_, _) => RenamePreset();
        _deleteButton.Click += (_, _) => DeletePreset();
        _saveButton.Click += (_, _) => SavePreset();
        _emergencyButton.Click += (_, _) => ToggleEmergency();

        _micCheck.CheckedChanged += (_, _) => Toggle("mic", _micCheck.Checked);
        _extrasCheck.CheckedChanged += (_, _) => Toggle("extras", _extrasCheck.Checked);
        _appCheck.CheckedChanged += (_, _) => Toggle("app", _appCheck.Checked);
        _monitorCombo.SelectedIndexChanged += (_, _) => ApplyMonitorSelection();
        _monitorVolume.ValueChanged += (_, _) => SetMonitorGain();
        WireGain(_micGain, g => _shell.Session.SetGains(CurrentGains() with { Mic = g }));
        WireGain(_appGain, g => _shell.Session.SetGains(CurrentGains() with { App = g }));
        WireGain(_clipGain, g => _shell.Session.SetGains(CurrentGains() with { Clip = g }));

        _playButton.Click += (_, _) => PlaySelected(send: true);
        _previewButton.Click += (_, _) => PlaySelected(send: false);
        _stopButton.Click += (_, _) => Run(_shell.Session.StopClip(), "停止");
        _importButton.Click += (_, _) => ImportClip();
        _favoriteButton.Click += (_, _) => ToggleFavorite();
        _deleteClipButton.Click += (_, _) => DeleteClip();

        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    public bool Dirty { get; private set; }

    /// <summary>Offers 保存/放弃/取消 when preset edits exist; false means cancel.</summary>
    public bool ConfirmDiscardOrSave(string action)
    {
        if (!Dirty)
        {
            return true;
        }

        switch (MessageBox.Show(this, $"当前预设有未保存修改，{action}前如何处理？", "未保存的修改",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question))
        {
            case DialogResult.Yes:
                SavePreset();
                return true;
            case DialogResult.No:
                return true;
            default:
                return false;
        }
    }

    public void RefreshStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var status = _shell.Session.Status();
            _phaseLabel.Text = DescribePhase(status);
            _phaseLabel.ForeColor = status.EmergencyLatched
                ? Color.Firebrick
                : status.Switches.AnySendEnabled ? Color.Green : SystemColors.ControlText;
            _emergencyButton.Text = status.EmergencyLatched ? "解除紧急静音（仍全关）" : "紧急全静音";

            _micCheck.Checked = status.Switches.MicSendEnabled;
            _extrasCheck.Checked = status.Switches.ExtrasSendEnabled;
            _appCheck.Checked = status.Switches.AppSendEnabled;
            _monitorCombo.SelectedIndex = MonitorIndex(status);
            _monitorVolume.Value = ClampTrack((int)(status.Gains.Monitor * 100));

            _micGain.Value = (decimal)status.Gains.Mic;
            _appGain.Value = (decimal)status.Gains.App;
            _clipGain.Value = (decimal)status.Gains.Clip;

            var clipState = status.Clip is { } clip
                ? $"{(clip.Mode == ClipPlaybackMode.Send ? "发送中" : "试听中")}: {clip.ClipId}"
                : "无音效播放";
            _statusLabel.Text =
                $"{status.Phase} | 真麦:{On(status.Switches.MicSendEnabled)} 附加:{On(status.Switches.ExtrasSendEnabled)} "
                + $"应用:{On(status.Switches.AppSendEnabled)} 监听:{On(status.Switches.MonitorEnabled)}({status.MonitorMode}) | {clipState}";
            Text = $"随声麦 — {ActivePresetName()}{(Dirty ? " *" : "")}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.WindowsShutDown)
        {
            return;
        }

        // close-to-tray baseline (docs/01 §6): closing the window never stops audio
        e.Cancel = true;
        Hide();
        Tray?.NotifyHiddenToTray(this);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal TrayContext? Tray { get; set; }

    internal void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
        top.Controls.AddRange([_presetCombo, _applyButton, _newButton, _duplicateButton, _renameButton, _deleteButton, _saveButton, _dirtyLabel]);

        var phase = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        phase.Controls.AddRange([_phaseLabel, _emergencyButton]);

        var switches = new GroupBox { Text = "发送与监听", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var switchFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        switchFlow.Controls.AddRange([
            _micCheck, _extrasCheck, _appCheck,
            Labeled("监听:", _monitorCombo), Labeled("监听音量:", _monitorVolume), _monitorVolumeLabel,
            Labeled("真麦增益:", _micGain), Labeled("应用增益:", _appGain), Labeled("音效增益:", _clipGain),
        ]);
        switches.Controls.Add(switchFlow);

        var hotkeys = new GroupBox { Text = "全局热键（在命令行面板中编辑: onbody panel）", Dock = DockStyle.Bottom, Height = 110 };
        hotkeys.Controls.Add(_hotkeyList);

        var clipButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        clipButtons.Controls.AddRange([_playButton, _previewButton, _stopButton, _importButton, _favoriteButton, _deleteClipButton]);
        var clips = new GroupBox { Text = "音效库（导入/收藏/搜索由面板与列表共享同一全局库）", Dock = DockStyle.Fill };
        clips.Controls.Add(_clipsList);
        var clipsHost = new Panel { Dock = DockStyle.Fill };
        clipsHost.Controls.Add(clips);
        clipsHost.Controls.Add(clipButtons);
        clipButtons.Dock = DockStyle.Bottom;

        _status.Items.Add(_statusLabel);

        Controls.Add(clipsHost);
        Controls.Add(switches);
        Controls.Add(phase);
        Controls.Add(top);
        Controls.Add(hotkeys);
        Controls.Add(_status);
    }

    private static Control Labeled(string text, Control inner)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 6, 12, 0) };
        panel.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
        panel.Controls.Add(inner);
        return panel;
    }

    private static NumericUpDown MakeGain() => new()
    {
        Minimum = 0m,
        Maximum = 4m,
        Increment = (decimal)GainStep,
        DecimalPlaces = 2,
        Width = 60,
    };

    private void WireGain(NumericUpDown control, Action<float> apply)
    {
        control.ValueChanged += (_, _) =>
        {
            if (!_refreshing)
            {
                apply((float)control.Value);
                MarkDirty();
            }
        };
    }

    private RouteGains CurrentGains() => _shell.Session.Status().Gains;

    private void MarkDirty()
    {
        Dirty = true;
        _dirtyLabel.Text = "有未保存修改";
    }

    private void ClearDirty()
    {
        Dirty = false;
        _dirtyLabel.Text = "";
    }

    private void Run(CommandResult result, string what)
    {
        if (!result.Accepted)
        {
            MessageBox.Show(this, $"{what}被拒绝: {result.Rejection}", "随声麦", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        RefreshStatus();
    }

    private void Toggle(string route, bool on)
    {
        if (_refreshing)
        {
            return;
        }

        switch (route)
        {
            case "mic":
                Run(_shell.Session.EnableMic(on), "真麦发送");
                break;
            case "extras":
                Run(_shell.Session.EnableExtras(on), "附加音频");
                break;
            default:
                Run(_shell.Session.EnableApp(on), "应用发送");
                break;
        }
    }

    private void ToggleEmergency()
    {
        var status = _shell.Session.Status();
        Run(status.EmergencyLatched ? _shell.Session.UnlockEmergency() : _shell.Session.EmergencyMute(), "紧急静音");
    }

    private static int MonitorIndex(SessionStatus status) => status.Switches.MonitorEnabled
        ? status.MonitorMode == MonitorMode.FullEndpoint ? 2 : 1
        : 0;

    private void RefreshMonitorCombo()
    {
        _monitorCombo.Items.AddRange(["关闭", "仅音效", "实际虚拟麦"]);
        _monitorCombo.SelectedIndex = 0;
    }

    private void ApplyMonitorSelection()
    {
        if (_refreshing)
        {
            return;
        }

        var current = _shell.Session.Status();
        var result = _monitorCombo.SelectedIndex switch
        {
            1 => _shell.Session.SetMonitor(true, MonitorMode.ClipOnly),
            2 => _shell.Session.SetMonitor(true, MonitorMode.FullEndpoint),
            _ => _shell.Session.SetMonitor(false, current.MonitorMode),
        };
        Run(result, "监听");
    }

    private void SetMonitorGain()
    {
        if (_refreshing)
        {
            return;
        }

        var gain = _monitorVolume.Value / 100f;
        _monitorVolumeLabel.Text = $"{gain:0.00}";
        _ = _shell.Session.SetGains(CurrentGains() with { Monitor = gain });
        MarkDirty();
    }

    private void PlaySelected(bool send)
    {
        if (_clipsList.SelectedItem is not ClipRow { Item: var item })
        {
            return;
        }

        Run(send ? _shell.Session.PlayClip(item.ClipId) : _shell.Session.PreviewClip(item.ClipId),
            send ? "向队友播放" : "本地试听");
    }

    private void ImportClip()
    {
        using var dialog = new OpenFileDialog { Filter = "音频文件 (*.wav;*.mp3)|*.wav;*.mp3|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (_shell.Soundboard.Library is null)
        {
            MessageBox.Show(this, "音效库不可用（索引损坏）", "随声麦");
            return;
        }

        try
        {
            var imported = _shell.Soundboard.Library.Import(dialog.FileName);
            _shell.Log($"[clip] 已导入 {imported.Item.ClipId}（{imported.Item.DisplayName}）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "随声麦");
            return;
        }

        RefreshClips();
    }

    private void ToggleFavorite()
    {
        if (_clipsList.SelectedItem is ClipRow { Item: var item } && _shell.Soundboard.Library?.SetFavorite(item.ClipId, !item.Favorite) == true)
        {
            RefreshClips();
        }
    }

    private void DeleteClip()
    {
        if (_clipsList.SelectedItem is not ClipRow { Item: var item })
        {
            return;
        }

        var referencing = _shell.Presets.PresetsReferencingClip(item.ClipId);
        var referenceNote = referencing.Count > 0
            ? $"\n以下预设的布局引用了它: {string.Join(", ", referencing)}"
            : "";
        if (MessageBox.Show(this, $"删除音效 {item.DisplayName}？（仅删除库内副本，原文件不受影响）{referenceNote}",
                "删除音效", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _shell.Soundboard.DeleteClip(item.ClipId);
        RefreshClips();
    }

    private void RefreshClips()
    {
        _clipsList.BeginUpdate();
        _clipsList.Items.Clear();
        if (_shell.Soundboard.Library is { } library)
        {
            foreach (var item in library.Search())
            {
                _clipsList.Items.Add(new ClipRow(item));
            }
        }

        _clipsList.EndUpdate();
    }

    private sealed record ClipRow(ClipItem Item)
    {
        public override string ToString() =>
            $"{(Item.Favorite ? "★" : " ")} {Item.DisplayName}  {Item.DurationMs}ms{(Item.GroupId is null ? "" : $"  [{Item.GroupId}]")}";
    }

    private void RefreshHotkeys()
    {
        _hotkeyList.BeginUpdate();
        _hotkeyList.Items.Clear();
        foreach (var binding in _shell.Soundboard.ActiveHotkeys)
        {
            _hotkeyList.Items.Add($"{binding.Gesture}  →  {binding.Describe()}");
        }

        if (_hotkeyList.Items.Count == 0)
        {
            _hotkeyList.Items.Add("（无激活绑定；热键初始均为空，需主动绑定）");
        }

        _hotkeyList.EndUpdate();
    }

    private void RefreshPresets()
    {
        _presetCombo.BeginUpdate();
        _presetCombo.Items.Clear();
        var active = _shell.Presets.ActivePresetId;
        var select = 0;
        foreach (var preset in _shell.Presets.List())
        {
            _presetCombo.Items.Add(preset.Corrupt ? $"{preset.Name}（损坏）" : preset.Name);
            if (preset.PresetId == active)
            {
                select = _presetCombo.Items.Count - 1;
            }
        }

        _presetCombo.EndUpdate();
        _presetCombo.SelectedIndex = _presetCombo.Items.Count > 0 ? select : -1;
    }

    private string ActivePresetName()
    {
        var active = _shell.Presets.ActivePresetId;
        var match = _shell.Presets.List().FirstOrDefault(p => p.PresetId == active);
        return match.PresetId == active ? match.Name : "（未选择预设）";
    }

    private PresetSummary? SelectedPreset()
    {
        var index = _presetCombo.SelectedIndex;
        if (index < 0)
        {
            return null;
        }

        var list = _shell.Presets.List();
        return index < list.Count ? list[index] : null;
    }

    private void ApplySelectedPreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            MessageBox.Show(this, "选择一个有效预设", "随声麦");
            return;
        }

        if (!ConfirmDiscardOrSave("切换预设"))
        {
            return;
        }

        var result = _shell.Presets.Apply(preset.PresetId);
        if (!result.Accepted)
        {
            MessageBox.Show(this, $"应用失败: {result.Rejection}", "随声麦");
        }

        ClearDirty();
        RefreshPresets();
        RefreshStatus();
    }

    private void CreatePreset()
    {
        var name = Prompt("新预设名称", "新建预设");
        if (name is null)
        {
            return;
        }

        var layout = _shell.Soundboard.Library?.ItemsSnapshot().Select(i => i.ClipId).ToList() ?? [];
        var created = _shell.Presets.Create(name, layout, favoritesOnly: false, groupId: null);
        if (!created.Result.Accepted)
        {
            MessageBox.Show(this, $"创建失败: {created.Result.Rejection}", "随声麦");
            return;
        }

        ClearDirty();
        RefreshPresets();
    }

    private void DuplicatePreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            return;
        }

        var name = Prompt($"复制 {preset.Name} 为新预设名称", "复制预设");
        if (name is null)
        {
            return;
        }

        Run(_shell.Presets.Duplicate(preset.PresetId, name), "复制");
        RefreshPresets();
    }

    private void RenamePreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            return;
        }

        var name = Prompt($"将 {preset.Name} 重命名为", "重命名预设", preset.Name);
        if (name is null)
        {
            return;
        }

        Run(_shell.Presets.Rename(preset.PresetId, name), "重命名");
        RefreshPresets();
    }

    private void DeletePreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            return;
        }

        var active = _shell.Presets.ActivePresetId == preset.PresetId;
        var message = active
            ? $"预设 {preset.Name} 当前活动：删除将安全关闭全部发送/监听，且不自动启用其他预设。确认删除？"
            : $"删除预设 {preset.Name}？";
        if (MessageBox.Show(this, message, "删除预设", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var deleted = _shell.Presets.Delete(preset.PresetId);
        if (deleted.Accepted)
        {
            _shell.Soundboard.RemovePresetHotkey(preset.PresetId);
        }

        Run(deleted, "删除");
        RefreshPresets();
        RefreshStatus();
    }

    private void SavePreset()
    {
        var active = _shell.Presets.ActivePresetId;
        if (active is null)
        {
            MessageBox.Show(this, "无活动预设可保存；先新建", "随声麦");
            return;
        }

        var layout = _shell.Soundboard.Library?.ItemsSnapshot().Select(i => i.ClipId).ToList() ?? [];
        var result = _shell.Presets.Save(active, layout, favoritesOnly: false, groupId: null);
        if (result.Accepted)
        {
            ClearDirty();
        }
        else
        {
            MessageBox.Show(this, $"保存失败（旧文件保留）: {result.Rejection}", "随声麦");
        }
    }

    private string? Prompt(string text, string title, string initial = "")
    {
        var input = new TextBox { Text = initial, Dock = DockStyle.Top };
        var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 90),
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom };
        form.Controls.Add(input);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }

    private static string On(bool value) => value ? "开" : "关";

    private static string DescribePhase(SessionStatus status) => status.EmergencyLatched
        ? "紧急静音已锁存"
        : status.OutputLost
            ? "虚拟输出已失效（发送全关）"
            : status.Switches.AnySendEnabled
                ? "正在发送"
                : status.DevicesValidated
                    ? "就绪（全部关闭）"
                    : "待配置";

    private static int ClampTrack(int value) => Math.Clamp(value, 0, 400);
}
