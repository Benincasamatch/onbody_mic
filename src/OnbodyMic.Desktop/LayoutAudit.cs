namespace OnbodyMic.Desktop;

/// <summary>
/// Structural layout audit (docs/随声麦_UI重设计方案.md §20.3): instead of
/// eyeballing screenshots, build the real window at several DPI settings and
/// window sizes and assert three properties that the original defects broke —
/// no control outside the window, no horizontal overflow inside a scrolling
/// viewport, and no button whose label does not fit.
///
/// Used by <c>--selftest</c>; deterministic, needs no message loop, and each
/// simulated DPI disposes the form it built.
/// </summary>
internal static class LayoutAudit
{
    private static readonly int[] Dpis = [96, 120, 144, 192];

    private static readonly (int WidthDip, int HeightDip, string Label)[] Sizes =
    [
        (1000, 780, "默认"),
        (860, 640, "中等"),
        (760, 560, "最小"),
    ];

    public static IReadOnlyList<string> Run(MainForm template)
    {
        var failures = new List<string>(SelfCheck());
        var shell = ShellOf(template);
        failures.AddRange(RunDialogs(shell));
        foreach (var dpi in Dpis)
        {
            foreach (var (widthDip, heightDip, label) in Sizes)
            {
                Theme.UseDpi(dpi);
                using var form = new MainForm(shell, () => { })
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000, -32000),
                    ShowInTaskbar = false,
                };

                // child Visible stays false until the form is shown, so a
                // hidden form would make every structural check pass silently
                form.Show();
                form.Size = new Size(Theme.D(widthDip), Theme.D(heightDip));
                form.PerformLayout();

                var prefix = $"{dpi / 96.0:0.##}x {label}({widthDip}×{heightDip})";
                foreach (var problem in Problems(form))
                {
                    failures.Add($"{prefix}: {problem}");
                }

                foreach (var problem in Expectations(form))
                {
                    failures.Add($"{prefix}: {problem}");
                }

                form.Hide();
            }
        }

        Theme.UseDpi(96);
        return failures;
    }

    /// <summary>
    /// The dialogs are audited too: the first-run wizard shipped clipped combo
    /// text and a cut-off primary button on a clean machine, which a main-window
    /// -only audit cannot see.
    /// </summary>
    private static IReadOnlyList<string> RunDialogs(AppShell shell)
    {
        var failures = new List<string>();
        foreach (var dpi in Dpis)
        {
            Theme.UseDpi(dpi);
            var prefix = $"{dpi / 96.0:0.##}x";

            using (var wizard = new FirstRunWizard(shell) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false })
            {
                wizard.Show();
                wizard.PerformLayout();
                foreach (var problem in Problems(wizard))
                {
                    failures.Add($"{prefix} 首次引导: {problem}");
                }

                wizard.Hide();
            }

            using (var settings = new DeviceSettingsForm(shell) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false })
            {
                settings.Show();
                settings.PerformLayout();
                foreach (var problem in Problems(settings))
                {
                    failures.Add($"{prefix} 设备设置: {problem}");
                }

                settings.Hide();
            }

            using (var hotkeys = new HotkeyEditorForm(shell, null) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false })
            {
                hotkeys.Show();
                hotkeys.PerformLayout();
                foreach (var problem in Problems(hotkeys))
                {
                    failures.Add($"{prefix} 热键设置: {problem}");
                }

                hotkeys.Hide();
            }
        }

        Theme.UseDpi(96);
        return failures;
    }

    public static IReadOnlyList<string> Report(MainForm template)
    {
        return
        [
            $"layout: 审计 DPI {string.Join("/", Dpis.Select(d => $"{d}({d / 96.0:0.##}x)"))} × 尺寸 "
            + string.Join("/", Sizes.Select(s => $"{s.WidthDip}×{s.HeightDip}"))
            + "；对话框：首次引导/设备设置/热键设置",
        ];
    }

    /// <summary>
    /// Behaviour the layout must show at a given width, independent of pixel
    /// geometry: the emergency action is always reachable, the route grid is
    /// two columns only when there is room, and the preset selector survives.
    /// </summary>
    private static IReadOnlyList<string> Expectations(MainForm form)
    {
        var problems = new List<string>();
        var fields = typeof(MainForm).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        object? Field(string name) => fields.FirstOrDefault(f => f.Name == name)?.GetValue(form);

        if (Field("_emergencyButton") is not Button emergency || !emergency.Visible)
        {
            problems.Add("紧急按钮不可见");
        }
        else if (emergency.Text.Length == 0)
        {
            problems.Add("紧急按钮无文案");
        }

        if (Field("_routeGrid") is not TableLayoutPanel grid)
        {
            problems.Add("找不到路由卡片网格");
            return problems;
        }

        // the rule uses the client area, not the outer window size
        var expectedColumns = form.ClientSize.Width - (2 * Theme.D(Theme.WindowMargin)) >= Theme.D(TwoColumnMinDip) ? 2 : 1;
        var usedColumns = grid.Controls.Cast<Control>().Select(c => grid.GetColumn(c)).DefaultIfEmpty(0).Max() + 1;
        if (usedColumns != expectedColumns)
        {
            problems.Add($"路由卡片列数 {usedColumns}，期望 {expectedColumns}（客户区宽 {form.ClientSize.Width}）");
        }

        if (Field("_presetCombo") is not ComboBox preset || !preset.Visible)
        {
            problems.Add("预设选择器不可见");
        }

        return problems;
    }

    private const int TwoColumnMinDip = 720;

    /// <summary>
    /// Proves the detector itself can fail: a deliberately broken layout must
    /// be reported. Without this, "layout OK" could simply mean "audit blind".
    /// </summary>
    private static IReadOnlyList<string> SelfCheck()
    {
        var problems = new List<string>();
        using var form = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            ClientSize = new Size(300, 200),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        var overflow = new Button
        {
            Text = "很长的按钮文案",
            AutoSize = false,
            Size = new Size(20, 20),
            Location = new Point(280, 10),
        };
        var clipped = new Button
        {
            Text = "超出右缘",
            AutoSize = true,
            Location = new Point(260, 60),
        };
        form.Controls.Add(overflow);
        form.Controls.Add(clipped);

        // A form must be shown: child Visible is false while the form is hidden,
        // which would make every check below silently pass.
        form.Show();
        form.PerformLayout();

        var detected = Problems(form);
        if (detected.Count == 0)
        {
            problems.Add("审计自检失败：人为制造的裁切布局未被检出（审计失效）");
        }

        form.Hide();
        return problems;
    }

    /// <summary>All structural problems of one realised form; empty means the layout holds.</summary>
    public static IReadOnlyList<string> Problems(Form form)
    {
        var problems = new List<string>();

        foreach (var control in Descendants(form))
        {
            if (!control.Visible || control.Parent is null)
            {
                continue;
            }

            if (control.Width <= 0 || control.Height <= 0)
            {
                continue;
            }

            var parentClient = control.Parent.ClientSize;
            var scrollable = HasScrollableAncestor(control, form);

            // 1) horizontal containment is strict: a control wider than its
            //    parent is clipped, and no ancestor can scroll sideways here
            if (control.Right > parentClient.Width + 1)
            {
                problems.Add(
                    $"{Describe(control)} 右缘 {control.Right} 超出父容器 {Describe(control.Parent)} 客户区宽 {parentClient.Width}");
            }

            // 2) vertical overflow is legal only inside a scrolling viewport
            //    (that is what AutoScroll is for); elsewhere it is a clip
            if (!scrollable && control.Bottom > parentClient.Height + 1)
            {
                problems.Add(
                    $"{Describe(control)} 下缘 {control.Bottom} 超出父容器 {Describe(control.Parent)} 客户区高 {parentClient.Height}");
            }

            // 3) a fixed-size button must be able to render its label
            if (control is Button { AutoSize: false, Text.Length: > 0 } button)
            {
                var needed = TextRenderer.MeasureText(button.Text, button.Font).Width;
                if (needed > button.ClientSize.Width)
                {
                    problems.Add($"按钮「{button.Text}」文本宽 {needed} > 可用宽 {button.ClientSize.Width}");
                }
            }
        }

        // 4) the emergency action must stay fully visible at every size (§6.1)
        var emergency = Descendants(form).OfType<Button>().FirstOrDefault(b => b.Text.StartsWith("全部静音", StringComparison.Ordinal));
        if (emergency is not null && emergency.Visible)
        {
            var bounds = AbsoluteBounds(emergency, form);
            if (bounds.Right > form.ClientSize.Width + 1 || bounds.Left < -1)
            {
                problems.Add($"紧急按钮 {bounds} 超出窗体客户区宽 {form.ClientSize.Width}");
            }
        }

        return problems;
    }

    /// <summary>True when any ancestor between the control and the form scrolls vertically.</summary>
    private static bool HasScrollableAncestor(Control control, Form form)
    {
        for (var parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
        {
            if (parent is ScrollableControl { AutoScroll: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Bounds of a control in the form's client coordinates.</summary>
    private static Rectangle AbsoluteBounds(Control control, Form form)
    {
        var location = control.Location;
        var parent = control.Parent;
        while (parent is not null && parent != form)
        {
            location.Offset(parent.Location);
            if (parent is ScrollableControl { AutoScroll: true } scrollable)
            {
                location.Offset(-scrollable.AutoScrollPosition.X, -scrollable.AutoScrollPosition.Y);
            }

            parent = parent.Parent;
        }

        return new Rectangle(location, control.Size);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static string Describe(Control control) => control switch
    {
        Button b => $"按钮「{b.Text}」",
        Label l => $"标签「{Truncate(l.Text)}」",
        CheckBox c => $"复选框「{c.Text}」",
        ComboBox => "下拉框",
        NumericUpDown => "数值框",
        RouteCard => "路由卡片",
        TableLayoutPanel => "布局面板(Table)",
        FlowLayoutPanel => "布局面板(Flow)",
        Panel => "面板",
        ListView => "列表",
        StatusStrip => "状态栏",
        _ => control.GetType().Name,
    };

    private static string Truncate(string text) => text.Length <= 16 ? text : text[..16] + "…";

    private static AppShell ShellOf(MainForm form) =>
        (AppShell)typeof(MainForm)
            .GetField("_shell", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(form)!;
}
