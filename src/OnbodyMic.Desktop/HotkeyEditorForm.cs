namespace OnbodyMic.Desktop;

using OnbodyMic.Domain;

/// <summary>
/// Hotkey editor (§10.2/§10.3): the detail surface the main window only
/// summarizes. It binds through <see cref="OnbodyMic.Cli.Soundboard.BindHotkey"/>
/// — the same dispatcher the CLI panel and startup restore use — so there is
/// exactly one place that registers, validates and persists a gesture.
///
/// While recording, the dispatcher is suspended so pressing an already-bound
/// combination cannot fire its action mid-capture (§10.3). Conflicts are
/// reported before anything is registered: local duplicates are rejected in
/// the UI, OS-level rejection comes back from the backend as
/// <c>hotkey-conflict:</c> / <c>register-failed:</c>.
/// </summary>
internal sealed class HotkeyEditorForm : Form
{
    private readonly AppShell _shell;
    private readonly ComboBox _actionCombo = Theme.DropDown();
    private ComboBox _targetCombo = Theme.DropDown();
    private readonly TextBox _gestureBox = new() { ReadOnly = true, Font = Theme.Body };
    private readonly Button _recordButton;
    private readonly Button _bindButton;
    private readonly Button _unbindButton;
    private readonly Button _closeButton;
    private readonly ListView _boundList = new();
    private readonly Label _status = new();
    private Label _targetLabel = new();

    private TableLayoutPanel _editor = null!;
    private int _targetRowIndex;
    private HotkeyAction _action = HotkeyAction.ToggleMicSend;
    private HotkeyModifiers _pressed;
    private HotkeyGesture _captured;
    private bool _recording;
    private List<Choice> _targets = [];

    public HotkeyEditorForm(AppShell shell, string? focusClipId)
    {
        _shell = shell;
        Theme.InitializeFor(this);
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Body;
        Text = "全局热键设置";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = Theme.WindowBack;
        ClientSize = Theme.DS(680, 560);
        MinimumSize = Theme.DS(600, 480);

        _recordButton = Theme.SecondaryButton("录制…");
        _bindButton = Theme.PrimaryButton("绑定");
        _unbindButton = Theme.SecondaryButton("清除选中绑定");
        _closeButton = Theme.SecondaryButton("关闭");
        _closeButton.DialogResult = DialogResult.Cancel;

        BuildLayout(focusClipId);
        WireEvents();
        OnActionChanged();
        LoadBindings();
    }

    private void BuildLayout(string? focusClipId)
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

        var editor = Theme.Card(
            new ColumnStyle(SizeType.Absolute, Theme.D(Theme.LabelColumnWidth)),
            new ColumnStyle(SizeType.Percent, 100f));
        editor.Dock = DockStyle.Top;
        editor.Paint += Theme.CardBorder;
        editor.Margin = new Padding(0, 0, 0, Theme.D(Theme.GapMedium));
        _editor = editor;

        var title = new Label
        {
            Text = "绑定新的快捷键",
            Font = Theme.BodyBold,
            ForeColor = Theme.TextMain,
            AutoSize = true,
            Margin = Theme.DP(0, 0, 0, Theme.GapSmall),
        };

        _actionCombo.Width = Theme.D(200);
        _actionCombo.Items.AddRange(["切换真麦发送", "切换附加音频", "切换监听", "停止音效", "全部静音", "播放音效…", "切换预设…"]);
        _actionCombo.SelectedIndex = 0;
        _actionCombo.SelectedIndexChanged += (_, _) => OnActionChanged();

        _targetLabel = new Label
        {
            Text = "目标",
            ForeColor = Theme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _targetCombo.Width = Theme.D(300);
        _targetCombo.Visible = false;
        _targetCombo.Margin = Theme.DP(0, 0, 0, Theme.GapSmall);

        _gestureBox.Width = Theme.D(240);
        _gestureBox.PlaceholderText = "尚未录制";
        _gestureBox.TabStop = false;

        var gestureRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        _gestureBox.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _recordButton.Margin = Theme.DP(0, 0, 0, 0);
        gestureRow.Controls.AddRange([_gestureBox, _recordButton]);

        AddEditorRow(editor, title, null, row: 0);
        editor.SetColumnSpan(title, 2);
        AddEditorRow(editor, FieldLabel("动作"), _actionCombo, row: 1);
        AddEditorRow(editor, _targetLabel, _targetCombo, row: 2);
        _targetRowIndex = 2;
        AddEditorRow(editor, FieldLabel("快捷键"), gestureRow, row: 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Theme.DP(0, Theme.GapSmall, 0, 0),
        };
        _bindButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _unbindButton.Margin = Theme.DP(0, 0, 0, 0);
        actions.Controls.AddRange([_bindButton, _unbindButton]);
        AddEditorRow(editor, null, actions, row: 4);

        _status.Text = "";
        _status.AutoSize = true;
        _status.MaximumSize = new Size(Theme.D(560), 0);
        _status.ForeColor = Theme.TextSecondary;
        _status.Margin = Theme.DP(0, Theme.GapSmall, 0, 0);
        AddEditorRow(editor, null, _status, row: 5);

        var listCard = Theme.Card();
        listCard.Dock = DockStyle.Fill;
        listCard.Padding = Theme.DP(1);
        listCard.Paint += Theme.CardBorder;
        _boundList.Dock = DockStyle.Fill;
        _boundList.View = View.Details;
        _boundList.FullRowSelect = true;
        _boundList.MultiSelect = false;
        _boundList.HideSelection = false;
        _boundList.BorderStyle = BorderStyle.None;
        _boundList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _boundList.Font = Theme.Body;
        _boundList.Columns.Add("快捷键", Theme.D(150));
        _boundList.Columns.Add("动作", Theme.D(220));
        _boundList.Columns.Add("目标", Theme.D(180));
        listCard.Controls.Add(_boundList);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = Theme.DP(0, Theme.GapSmall, 0, 0),
        };
        footer.Controls.Add(_closeButton);

        root.Controls.Add(editor, 0, 0);
        root.Controls.Add(listCard, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        CancelButton = _closeButton;

        if (focusClipId is not null)
        {
            var clip = _shell.Soundboard.Library?.Find(focusClipId);
            if (clip is not null)
            {
                _actionCombo.SelectedIndex = 5;
                OnActionChanged();
                SelectTarget(clip.ClipId);
            }
        }
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        ForeColor = Theme.TextSecondary,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static void AddEditorRow(TableLayoutPanel panel, Control? left, Control? right, int row)
    {
        panel.RowCount = Math.Max(panel.RowCount, row + 1);
        while (panel.RowStyles.Count <= row)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        if (left is null && right is not null)
        {
            panel.Controls.Add(right, 0, row);
            panel.SetColumnSpan(right, 2);
            return;
        }

        if (left is not null)
        {
            panel.Controls.Add(left, 0, row);
        }

        if (right is not null)
        {
            panel.Controls.Add(right, 1, row);
        }
    }

    private void WireEvents()
    {
        _recordButton.Click += (_, _) => ToggleRecording();
        _bindButton.Click += (_, _) => BindCaptured();
        _unbindButton.Click += (_, _) => UnbindSelected();
        _boundList.SelectedIndexChanged += (_, _) => _unbindButton.Enabled = _boundList.SelectedItems.Count > 0;
        _boundList.DoubleClick += (_, _) => LoadSelectedIntoEditor();
        FormClosed += (_, _) => StopRecording();
        KeyPreview = true;
        KeyDown += OnCaptureKeyDown;
        KeyUp += OnCaptureKeyUp;
    }

    private void OnActionChanged()
    {
        _action = _actionCombo.SelectedIndex switch
        {
            0 => HotkeyAction.ToggleMicSend,
            1 => HotkeyAction.ToggleExtrasSend,
            2 => HotkeyAction.ToggleMonitor,
            3 => HotkeyAction.StopClip,
            4 => HotkeyAction.EmergencyMute,
            5 => HotkeyAction.PlayClip,
            _ => HotkeyAction.SelectPreset,
        };

        var needsTarget = _action is HotkeyAction.PlayClip or HotkeyAction.SelectPreset;
        _targetLabel.Visible = needsTarget;
        _targetCombo.Visible = needsTarget;

        // Collapse the row instead of leaving an empty gap when no target applies.
        if (needsTarget)
        {
            _editor.RowStyles[_targetRowIndex] = new RowStyle(SizeType.AutoSize);
            LoadTargets();
        }
        else
        {
            _editor.RowStyles[_targetRowIndex] = new RowStyle(SizeType.Absolute, 0);
        }
    }

    private void LoadTargets()
    {
        _targetCombo.Items.Clear();
        _targets = _action == HotkeyAction.PlayClip
            ? (_shell.Soundboard.Library?.ItemsSnapshot() ?? [])
                .Select(i => new Choice(i.ClipId, i.DisplayName))
                .ToList()
            : _shell.Presets.List().Select(p => new Choice(p.PresetId, p.Name)).ToList();
        foreach (var choice in _targets)
        {
            _targetCombo.Items.Add(choice);
        }

        _targetCombo.SelectedIndex = _targets.Count > 0 ? 0 : -1;
    }

    private void SelectTarget(string targetId)
    {
        for (var i = 0; i < _targetCombo.Items.Count; i++)
        {
            if (_targetCombo.Items[i] is Choice choice && choice.Id == targetId)
            {
                _targetCombo.SelectedIndex = i;
                return;
            }
        }
    }

    private void ToggleRecording()
    {
        if (_recording)
        {
            StopRecording();
            return;
        }

        _recording = true;
        _captured = default;
        _pressed = HotkeyModifiers.None;
        _gestureBox.Text = "请按下组合键…（Esc 取消）";
        _recordButton.Text = "取消录制";
        _bindButton.Enabled = false;
        SetStatus("录制中：按下包含 Ctrl/Alt/Shift/Win 的组合键。", Theme.Warning);
        _shell.Soundboard.SuspendHotkeys();
    }

    private void StopRecording()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        _recordButton.Text = "录制…";
        _shell.Soundboard.ResumeHotkeys();
        if (_captured.IsUnbound)
        {
            _gestureBox.Text = "";
            _bindButton.Enabled = false;
        }
    }

    /// <summary>
    /// Modifiers are tracked from our own key messages instead of the global
    /// keyboard state: capture then reflects exactly what the user pressed in
    /// this dialog, and a released modifier can never leak into the gesture.
    /// </summary>
    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.KeyCode == Keys.Escape)
        {
            StopRecording();
            SetStatus("已取消录制，原绑定未改变。", Theme.TextSecondary);
            return;
        }

        if (TryModifier(e.KeyCode, out var modifier))
        {
            _pressed |= modifier;
            _gestureBox.Text = Describe(_pressed) + "…";
            return;
        }

        var gesture = new HotkeyGesture(_pressed, (int)e.KeyCode);
        if (gesture.Modifiers == HotkeyModifiers.None)
        {
            SetStatus("无修饰键的快捷键容易被游戏占用，请加 Ctrl/Alt/Shift/Win 之一。", Theme.Warning);
            return;
        }

        _captured = gesture;
        _gestureBox.Text = gesture.ToString();
        StopRecording();
        _bindButton.Enabled = true;

        var conflict = FindConflict(gesture);
        if (conflict is not null)
        {
            SetStatus($"冲突：{gesture} 已绑定到「{Rejections.BindingLabel(conflict, ClipName, PresetName)}」；直接绑定会失败，请先清除或换一个组合键。", Theme.Warning);
            return;
        }

        SetStatus($"已捕获 {gesture}；点「绑定」生效。", Theme.TextSecondary);
    }

    private void OnCaptureKeyUp(object? sender, KeyEventArgs e)
    {
        if (TryModifier(e.KeyCode, out var modifier))
        {
            _pressed &= ~modifier;
            if (_recording && _captured.IsUnbound)
            {
                _gestureBox.Text = Describe(_pressed) + "…";
            }
        }
    }

    private static bool TryModifier(Keys key, out HotkeyModifiers modifier)
    {
        switch (key)
        {
            case Keys.ControlKey or Keys.LControlKey or Keys.RControlKey:
                modifier = HotkeyModifiers.Control;
                return true;
            case Keys.Menu or Keys.LMenu or Keys.RMenu:
                modifier = HotkeyModifiers.Alt;
                return true;
            case Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey:
                modifier = HotkeyModifiers.Shift;
                return true;
            case Keys.LWin or Keys.RWin:
                modifier = HotkeyModifiers.Win;
                return true;
            default:
                modifier = HotkeyModifiers.None;
                return false;
        }
    }

    private static string Describe(HotkeyModifiers modifiers)
    {
        var text = string.Empty;
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            text += "Ctrl+";
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            text += "Alt+";
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            text += "Shift+";
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            text += "Win+";
        }

        return text;
    }

    private void BindCaptured()
    {
        if (_captured.IsUnbound)
        {
            SetStatus("先录制一个组合键。", Theme.Warning);
            return;
        }

        var targetId = _action is HotkeyAction.PlayClip or HotkeyAction.SelectPreset
            ? (_targetCombo.SelectedItem as Choice)?.Id
            : null;
        if (_action is HotkeyAction.PlayClip or HotkeyAction.SelectPreset && targetId is null)
        {
            SetStatus("先选择目标音效或预设。", Theme.Warning);
            return;
        }

        var binding = new HotkeyBinding(_action, targetId, _captured);
        var result = _shell.Soundboard.BindHotkey(binding);
        if (!result.Accepted)
        {
            SetStatus($"{Rejections.Describe(result.Rejection)}　{Rejections.NextStep(result.Rejection)}", Theme.Error);
            return;
        }

        SetStatus($"已绑定 {binding.Gesture} = {Rejections.BindingLabel(binding, ClipName, PresetName)}", Theme.Success);
        _captured = default;
        _gestureBox.Text = "";
        _bindButton.Enabled = false;
        LoadBindings();
    }

    private void UnbindSelected()
    {
        if (_boundList.SelectedItems.Count == 0 || _boundList.SelectedItems[0].Tag is not HotkeyBinding binding)
        {
            return;
        }

        var result = _shell.Soundboard.UnbindHotkey(binding.Action, binding.TargetId);
        if (!result.Accepted)
        {
            SetStatus($"{Rejections.Describe(result.Rejection)}　{Rejections.NextStep(result.Rejection)}", Theme.Error);
            return;
        }

        SetStatus($"已解除 {binding.Gesture}（{Rejections.BindingLabel(binding, ClipName, PresetName)}）", Theme.TextSecondary);
        LoadBindings();
    }

    private void LoadSelectedIntoEditor()
    {
        if (_boundList.SelectedItems.Count == 0 || _boundList.SelectedItems[0].Tag is not HotkeyBinding binding)
        {
            return;
        }

        _actionCombo.SelectedIndex = binding.Action switch
        {
            HotkeyAction.ToggleMicSend => 0,
            HotkeyAction.ToggleExtrasSend => 1,
            HotkeyAction.ToggleMonitor => 2,
            HotkeyAction.StopClip => 3,
            HotkeyAction.EmergencyMute => 4,
            HotkeyAction.PlayClip => 5,
            _ => 6,
        };

        OnActionChanged();
        if (binding.TargetId is not null)
        {
            SelectTarget(binding.TargetId);
        }

        _captured = binding.Gesture;
        _gestureBox.Text = binding.Gesture.ToString();
        _bindButton.Enabled = true;
        SetStatus("已载入该绑定；重新录制后点「绑定」可替换。", Theme.TextSecondary);
    }

    private void LoadBindings()
    {
        _boundList.BeginUpdate();
        try
        {
            _boundList.Items.Clear();
            foreach (var binding in _shell.Soundboard.ActiveHotkeys.OrderBy(b => b.Gesture.ToString(), StringComparer.CurrentCulture))
            {
                var row = new ListViewItem(binding.Gesture.ToString()) { Tag = binding };
                row.SubItems.Add(Rejections.ActionLabel(binding.Action));
                row.SubItems.Add(binding.TargetId is null
                    ? ""
                    : binding.Action == HotkeyAction.PlayClip ? ClipName(binding.TargetId) : PresetName(binding.TargetId));
                _boundList.Items.Add(row);
            }
        }
        finally
        {
            _boundList.EndUpdate();
        }

        _unbindButton.Enabled = _boundList.Items.Count > 0 && _boundList.SelectedItems.Count > 0;
        if (!_shell.Soundboard.HotkeyBackendAvailable)
        {
            SetStatus("热键后端不可用：本次运行无法注册快捷键（查看 logs/onbody.log）。", Theme.Error);
        }
        else if (_boundList.Items.Count == 0)
        {
            SetStatus("当前没有任何绑定。", Theme.TextSecondary);
        }
    }

    private HotkeyBinding? FindConflict(HotkeyGesture gesture) =>
        _shell.Soundboard.ActiveHotkeys.FirstOrDefault(b =>
            b.Gesture == gesture && !b.Matches(_action, CurrentTargetId()));

    private string? CurrentTargetId() =>
        _action is HotkeyAction.PlayClip or HotkeyAction.SelectPreset
            ? (_targetCombo.SelectedItem as Choice)?.Id
            : null;

    private string ClipName(string clipId) => _shell.Soundboard.Library?.Find(clipId)?.DisplayName ?? $"（已删除 {clipId}）";

    private string PresetName(string presetId) => _shell.Presets.List().FirstOrDefault(p => p.PresetId == presetId).Name ?? presetId;

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private sealed record Choice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
    }
}
