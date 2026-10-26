namespace OnbodyMic.Desktop;

using System.Drawing;

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

    // §5.2 typography, in DIP (13 DIP ≈ 9.75pt at 96 DPI)
    private const int BodySize = 13;
    private const int NoteSize = 12;
    private const int CardTitleSize = 15;
    private const int SectionTitleSize = 17;
    private const int AppTitleSize = 20;

    public static int Dpi => _dpi;

    public static Font Body => Build(BodySize, FontStyle.Regular);
    public static Font BodyBold => Build(BodySize, FontStyle.Bold);
    public static Font NoteFont => Build(NoteSize, FontStyle.Regular);
    public static Font CardTitle => Build(CardTitleSize, FontStyle.Bold);
    public static Font SectionTitleFont => Build(SectionTitleSize, FontStyle.Bold);
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

    /// <summary>Draws the 1px card border (attach to the card's Paint event).</summary>
    public static void CardBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Control card)
        {
            return;
        }

        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, card.Width - 1), Math.Max(0, card.Height - 1));
    }

    /// <summary>Primary button (§13.1): accent fill, white text.</summary>
    public static Button PrimaryButton(string text) => ButtonBase(text, Accent, Color.White, Accent, AccentHover);

    /// <summary>Secondary button: neutral surface with a visible border.</summary>
    public static Button SecondaryButton(string text) => ButtonBase(text, CardBack, TextMain, Border, SecondaryBack);

    /// <summary>Danger button (§6.3): only for emergency/destructive actions.</summary>
    public static Button DangerButton(string text) => ButtonBase(text, Error, Color.White, Error, Hex("#991B1B"));

    private static Button ButtonBase(string text, Color back, Color fore, Color border, Color hover) => new()
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

    /// <summary>Pill-shaped status chip (§7.3): text plus semantic color, never color alone.</summary>
    public static Label StatusChip(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color,
        BackColor = SecondaryBack,
        Font = Body,
        Padding = DP(8, 3, 8, 3),
        Margin = DP(0, 2, GapSmall, 2),
    };

    /// <summary>Section header (§5.2).</summary>
    public static Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = TextMain,
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
