namespace OnbodyMic.Desktop;

 using System.Drawing;
using System.Drawing.Drawing2D;

/// <summary>
/// Design tokens (docs/随声麦_UI重设计方案.md §5): one place for colors,
/// spacing, fonts, control metrics and DPI scaling, so no style constant
/// leaks into individual controls.
///
/// DPI: every metric and every font size goes through <see cref="D"/>, which
/// converts a DIP with the DPI captured by <see cref="InitializeFor"/>. Fonts
/// are created in <see cref="GraphicsUnit.Pixel"/> from DIP values, so a font
/// and the padding next to it always scale by the same factor — point-based
/// fonts scaled while pixel paddings did not, which is what clipped the top
/// bar and the gain boxes at 125%.
///
/// <see cref="UseDpi"/> exists for the layout audit (--ui-selftest): it makes
/// a simulated 150% build measurable on any monitor.
/// </summary>
internal static class Theme
{
    private static int _dpi = 96;
    private static readonly Dictionary<(int Dpi, int Size, FontStyle Style), Font> FontCache = [];

    // §5.1 colors (light theme; semantic names only)
    public static readonly Color WindowBack = Hex("#F7F8FA");
    public static readonly Color CardBack = Color.White;
    public static readonly Color SecondaryBack = Hex("#F1F3F5");
    public static readonly Color TextMain = Hex("#1F2937");
    public static readonly Color TextSecondary = Hex("#6B7280");
    public static readonly Color Border = Hex("#E5E7EB");
    public static readonly Color Accent = Hex("#2563EB");
    public static readonly Color AccentHover = Hex("#1D4ED8");
    public static readonly Color Success = Hex("#15803D");
    public static readonly Color Warning = Hex("#B45309");
    public static readonly Color Error = Hex("#B91C1C");
    public static readonly Color TextDisabled = Hex("#9CA3AF");

    // 2026-10-26 modernization pass (docs/09): air = "sends to teammates" only,
    // status hues match the big status bar truth table, chips carry their own
    // muted surfaces so red text stays reserved for faults.
    public static readonly Color Air = Hex("#B45309");
    public static readonly Color AirHover = Hex("#92400E");
    public static readonly Color StatusIdle = Hex("#7B8492");
    public static readonly Color StatusSending = Hex("#1E7D4F");
    public static readonly Color StatusWarn = Hex("#B45309");
    public static readonly Color StatusFault = Hex("#C62828");
    public static readonly Color ChipBack = Hex("#EEF0F3");
    public static readonly Color ChipBackGood = Hex("#E3F4EA");
    public static readonly Color ChipBackWarn = Hex("#FBF0E3");
    public static readonly Color ChipBackFault = Hex("#FBE9E9");
    public static readonly Color IconBack = Hex("#EEF2FC");
    public static readonly Color MeterBack = Hex("#E6E9EE");
    public static readonly Color MeterHot = Hex("#F59E0B");
    public static readonly Color MeterClip = Hex("#C62828");

    // corner radii (DIP): cards 10, buttons/chips 8
    public const int CardRadius = 10;
    public const int ButtonRadius = 8;

    // §4.2 spacing (4/8 rhythm, in DIP)
    public const int GapSmall = 8;
    public const int GapMedium = 12;
    public const int CardPadding = 10;
    public const int SectionSpacing = 16;
    public const int WindowMargin = 16;
    public const int ControlHeight = 30;
    public const int ButtonMinWidth = 84;
    public const int NumericWidth = 72;
    public const int LabelColumnWidth = 84;
    public const int DescriptionHeight = 18;
    public const int ListRowHeight = 22;
    public const int CardIconSize = 15;
    public const int ClipRowHeight = 52;

    // §5.2 typography, in DIP (13 DIP ≈ 9.75pt at 96 DPI)
    private const int BodySize = 13;
    private const int NoteSize = 12;
    private const int CardTitleSize = 15;
    private const int SectionTitleSize = 17;
    private const int AppTitleSize = 20;
    private const int HeadlineSize = 17;
    private const int SectionLabelSize = 13;

    public static int Dpi => _dpi;

    public static Font Body => Build(BodySize, FontStyle.Regular);
    public static Font BodyBold => Build(BodySize, FontStyle.Bold);
    public static Font NoteFont => Build(NoteSize, FontStyle.Regular);
    public static Font CardTitle => Build(CardTitleSize, FontStyle.Bold);
    public static Font SectionTitleFont => Build(SectionLabelSize, FontStyle.Bold);
    public static Font Headline => Build(HeadlineSize, FontStyle.Bold);
    public static Font AppTitle => Build(AppTitleSize, FontStyle.Bold);

    /// <summary>Fonts are immutable and cached per (DPI, size, style); a DPI change builds new ones.</summary>
    private static Font Build(int dipSize, FontStyle style)
    {
        var key = (_dpi, dipSize, style);
        if (FontCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var font = new Font(Family, D(dipSize), style, GraphicsUnit.Pixel);
        FontCache[key] = font;
        return font;
    }

    private static FontFamily Family =>
        SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily;

    /// <summary>Captures the DPI of the form being built; call first in every form constructor.</summary>
    public static void InitializeFor(Control control)
    {
        var dpi = control.DeviceDpi;
        UseDpi(dpi > 0 ? dpi : 96);
    }

    /// <summary>Overrides the DPI (layout audit only).</summary>
    internal static void UseDpi(int dpi)
    {
        _dpi = dpi <= 0 ? 96 : dpi;
    }

    /// <summary>DIP → device pixels.</summary>
    public static int D(int dip) => (int)Math.Round(dip * _dpi / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>Rounded-rect path in DIP radius; caller disposes.</summary>
    public static GraphicsPath RoundRect(Rectangle bounds, int radiusDip)
    {
        var r = Math.Max(1, Math.Min(D(radiusDip), Math.Min(bounds.Width, bounds.Height) / 2));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, 2 * r, 2 * r, 180, 90);
        path.AddArc(bounds.Right - 2 * r, bounds.Y, 2 * r, 2 * r, 270, 90);
        path.AddArc(bounds.Right - 2 * r, bounds.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Size DS(int widthDip, int heightDip) => new(D(widthDip), D(heightDip));

    public static Padding DP(int allDip) => new(D(allDip));

    public static Padding DP(int left, int top, int right, int bottom) =>
        new(D(left), D(top), D(right), D(bottom));

    /// <summary>Applies the body font recursively (dialogs that build plain controls).</summary>
    public static void ApplyBodyFont(Control root)
    {
        root.Font = Body;
        foreach (Control child in root.Controls)
        {
            ApplyBodyFont(child);
        }
    }

    /// <summary>
    /// Standard card container: white surface, 1px border, uniform padding.
    /// Columns are declared by the caller — a card that later sets
    /// <c>ColumnCount = 2</c> would otherwise keep this single 100% style and
    /// shift every value into the middle of the card.
    /// </summary>
    public static TableLayoutPanel Card() => new()
    {
        BackColor = CardBack,
        Padding = DP(CardPadding),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        Dock = DockStyle.Fill,
        Margin = new Padding(0),
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
    };

    /// <summary>Card whose columns are exactly the given styles (no inherited column).</summary>
    public static TableLayoutPanel Card(params ColumnStyle[] columns)
    {
        var card = Card();
        card.ColumnCount = columns.Length;
        card.ColumnStyles.Clear();
        foreach (var column in columns)
        {
            card.ColumnStyles.Add(column);
        }

        return card;
    }

    /// <summary>
    /// Rounded card chrome (docs/09 §B3): 10 DIP corners, 1px border. Attach
    /// to the card's Paint event; pair with <see cref="ClipToCard"/> on resize
    /// so child controls cannot paint over the curve.
    /// </summary>
    public static void CardChrome(object? sender, PaintEventArgs e)
    {
        if (sender is not Control card)
        {
            return;
        }

        var bounds = new Rectangle(0, 0, Math.Max(0, card.Width - 1), Math.Max(0, card.Height - 1));
        using var path = RoundRect(bounds, CardRadius);
        using var fill = new SolidBrush(card.BackColor);
        using var pen = new Pen(Border);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }

    /// <summary>Rounded clip region; call from the card's SizeChanged handler.</summary>
    public static void ClipToCard(Control card)
    {
        card.Region?.Dispose();
        using var path = RoundRect(new Rectangle(0, 0, card.Width, card.Height), CardRadius);
        card.Region = new Region(path);
    }

    /// <summary>Legacy alias of <see cref="CardChrome"/>; rounded look applies wherever cards attach it.</summary>
    public static void CardBorder(object? sender, PaintEventArgs e) => CardChrome(sender, e);

    /// <summary>
    /// Air button (docs/09 §C1): amber fill, reserved for actions that send
    /// audio to teammates. Distinct from primary so a window never shows two
    /// equally-weighted accent buttons.
    /// </summary>
    public static Button AirButton(string text) => ButtonBase(text, Air, Color.White, Air, AirHover);

    /// <summary>Primary button (§13.1): accent fill, white text.</summary>
    public static Button PrimaryButton(string text) => ButtonBase(text, Accent, Color.White, Accent, AccentHover);

    /// <summary>Secondary button: neutral surface with a visible border.</summary>
    public static Button SecondaryButton(string text) => ButtonBase(text, CardBack, TextMain, Border, SecondaryBack);

    /// <summary>Danger button (§6.3): only for emergency/destructive actions.</summary>
    public static Button DangerButton(string text) => ButtonBase(text, Error, Color.White, Error, Hex("#991B1B"));

    private static Button ButtonBase(string text, Color back, Color fore, Color border, Color hover)
    {
        var button = new Button
        {
            Text = text,
            BackColor = back,
            ForeColor = fore,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 1, BorderColor = border, MouseOverBackColor = hover },
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(D(ButtonMinWidth), D(ControlHeight)),
            Padding = DP(12, 4, 12, 4),
            Margin = new Padding(0),
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
        };

        // Rounded corners (docs/09 §B4) via clip region: keeps the native
        // Flat look (hover, disabled, accessibility) while carving 8 DIP
        // corners; re-clipped on every size change so AutoSize stays honest.
        void RoundClip(object? sender, EventArgs e)
        {
            if (sender is not Button b)
            {
                return;
            }

            b.Region?.Dispose();
            using var path = RoundRect(new Rectangle(0, 0, b.Width, b.Height), ButtonRadius);
            b.Region = new Region(path);
        }

        button.SizeChanged += RoundClip;
        return button;
    }

    /// <summary>
    /// Pill-shaped status chip (§7.3): text plus semantic color, never color
    /// alone. The surface echoes the text hue (docs/09 §A2) so "设备不可用"
    /// reads as fault-red at a glance while plain "关" stays neutral gray.
    /// </summary>
    public static Label StatusChip(string text, Color color, Color? back = null) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color,
        BackColor = back ?? ChipBack,
        Font = Body,
        Padding = DP(10, 3, 10, 3),
        Margin = DP(0, 2, GapSmall, 2),
    };

    /// <summary>Section header (§5.2).</summary>
    public static Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = TextSecondary,
        Font = SectionTitleFont,
        Margin = DP(0, 0, 0, GapSmall),
    };

    /// <summary>Muted helper text.</summary>
    public static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = TextSecondary,
        Font = NoteFont,
    };

    /// <summary>Single-line label that ellipsizes instead of clipping; full text goes to a tooltip.</summary>
    public static Label Ellipsis(string text = "") => new()
    {
        Text = text,
        AutoSize = false,
        AutoEllipsis = true,
        Height = D(DescriptionHeight),
        Dock = DockStyle.Fill,
        ForeColor = TextSecondary,
        Font = Body,
        Margin = new Padding(0),
        TextAlign = ContentAlignment.MiddleLeft,
    };

    public static NumericUpDown Numeric(decimal min, decimal max, decimal increment, int decimals) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = increment,
        DecimalPlaces = decimals,
        Width = D(NumericWidth),
        Font = Body,
        TextAlign = HorizontalAlignment.Right,
        Margin = new Padding(0),
    };

    public static ComboBox DropDown() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        Font = Body,
        Margin = new Padding(0),
    };

    private static Color Hex(string value) => ColorTranslator.FromHtml(value);
}
