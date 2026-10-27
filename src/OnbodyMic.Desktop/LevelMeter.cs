namespace OnbodyMic.Desktop;

using System.Drawing;
using System.Drawing.Drawing2D;

/// <summary>
/// Live level meter (docs/09 §A3): horizontal bar fed by the 500 ms polled
/// <c>MixerPeak</c>. The caller pushes raw samples via <see cref="Sample"/>;
/// the meter keeps a peak hold with per-frame decay so the bar moves smoothly
/// between polls instead of stepping. Green bar, amber above 80%, red at the
/// limiter ceiling — color is a cue only, never the sole signal, and the dB
/// text next to it carries the same information for color-blind users.
///
/// Peak decay runs on the UI timer that already polls status (500 ms); with a
/// decay factor of 0.72 per tick the bar falls roughly one "loud step" per
/// second, which matches the reference mock's feel without any second timer.
/// </summary>
internal sealed class LevelMeter : Control
{
    private float _peakHeld;
    private float _lastSample;
    private bool _active = true;
    private readonly string? _idleText;

    public LevelMeter(string? idleText = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _idleText = idleText;
        Size = Theme.DS(160, 8);
        Cursor = Cursors.Default;
    }

    /// <summary>
    /// Pushes a new peak sample (0..1+) and applies one decay tick to the held
    /// peak. Called by the status poll; also safe to call extra ticks while
    /// inactive so the bar eases back to zero.
    /// </summary>
    public void Sample(float peak, bool active)
    {
        _lastSample = peak;
        _active = active;
        if (peak > _peakHeld)
        {
            _peakHeld = peak;
        }
        else
        {
            _peakHeld *= 0.72f;
            if (_peakHeld < 0.01f)
            {
                _peakHeld = 0;
            }
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundRect(bounds, 4))
        using (var back = new SolidBrush(Theme.MeterBack))
        {
            g.FillPath(back, path);
        }

        if (!_active)
        {
            return; // idle route: gray track only, no fake movement
        }

        var level = Math.Clamp(_peakHeld, 0f, 1f);
        if (level <= 0.01f)
        {
            return;
        }

        var barWidth = (int)Math.Round(level * (Width - 3));
        var barBounds = new Rectangle(1, 1, Math.Max(2, barWidth), Height - 3);
        var fill = _peakHeld >= 0.99f ? Theme.MeterClip : _peakHeld >= 0.8f ? Theme.MeterHot : Theme.StatusSending;
        using (var path = Theme.RoundRect(barBounds, 4))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }
    }
}
