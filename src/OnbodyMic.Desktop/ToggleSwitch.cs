namespace OnbodyMic.Desktop;

using System.ComponentModel;
 using System.Drawing;
 using System.Drawing.Drawing2D;

/// <summary>
/// Owner-drawn toggle switch (docs/09 §B1), based on the user's sample with
/// the gaps filled in: keyboard access (TabStop, Space toggles), a visible
/// focus rectangle, a 45%-opacity disabled look and accessibility properties,
/// so a switch is never mouse-only. Colors come from <see cref="Theme"/> —
/// on = accent blue, off = idle gray — and every metric is DPI-scaled.
/// </summary>
internal sealed class ToggleSwitch : Control
{
    private bool _on;
    private bool _hover;
    private bool _spaceHeld;

    /// <summary>Raised after <see cref="On"/> changed (user or code).</summary>
    public event EventHandler? Toggled;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Size = Theme.DS(44, 26);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
     public bool On
    {
        get => _on;
        set
        {
            if (_on == value)
            {
                return;
            }

            _on = value;
            Invalidate();
            Toggled?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Screen-reader text: the accessible name when set, else 开/关. The base
    /// property is annotated [AllowNull] on the setter, which C# cannot repeat
    /// on an accessor override, so the setter body runs with the annotation off.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override string Text
    {
        get => AccessibleName is { Length: > 0 } accessible ? accessible : (_on ? "开" : "关");
#nullable disable
        set => AccessibleName = value ?? string.Empty;
#nullable restore
    }



    protected override void OnClick(EventArgs e)
    {
        if (Enabled)
        {
            On = !On;
        }

        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && e.KeyCode == Keys.Space && !_spaceHeld)
        {
            _spaceHeld = true;
            On = !On;
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            _spaceHeld = false;
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var alpha = Enabled ? 1f : 0.45f;
        var track = Mix(_on ? Theme.Accent : Theme.StatusIdle, alpha);
        var knob = alpha < 1f ? Mix(Color.White, alpha) : Color.White;

        int h = Height - 3, w = Width - 1;
        using (var path = new GraphicsPath())
        {
            path.AddArc(1, 1, h, h, 90, 180);
            path.AddArc(w - h, 1, h, h, 270, 180);
            path.CloseFigure();
            using var brush = new SolidBrush(track);
            g.FillPath(brush, path);

            // hover halo: a 2px ring keeps the affordance without moving pixels
            if (_hover && Enabled)
            {
                using var halo = new Pen(Mix(track, 0.25f));
                g.DrawPath(halo, path);
            }
        }

        int d = h - 6;
        var x = _on ? w - d - 3 : 4;
        using (var knobBrush = new SolidBrush(knob))
        {
            g.FillEllipse(knobBrush, x, 4, d, d);
        }

        // focus cue for keyboard users (§14): rectangle matches the control bounds
        if (Focused && Enabled)
        {
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var focusPen = new Pen(Theme.Accent) { DashPattern = [2f, 2f] };
            g.DrawRectangle(focusPen, bounds);
        }
    }

    /// <summary>Blends a color toward the window background by the given opacity.</summary>
    private static Color Mix(Color color, float alpha) =>
        Color.FromArgb((int)(color.R * alpha + Theme.WindowBack.R * (1 - alpha)),
            (int)(color.G * alpha + Theme.WindowBack.G * (1 - alpha)),
            (int)(color.B * alpha + Theme.WindowBack.B * (1 - alpha)));
}
