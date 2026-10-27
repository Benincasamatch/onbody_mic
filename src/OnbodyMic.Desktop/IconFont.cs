namespace OnbodyMic.Desktop;

using System.Drawing;

/// <summary>
/// Icon glyph label (docs/09 §E2): renders one codepoint from Segoe Fluent
/// Icons, falling back to Segoe MDL2 Assets on older Windows. When neither
/// icon font is installed the glyph renders as nothing — the layout must
/// never depend on it (§5.4: icons accompany text, never replace it).
/// </summary>
internal static class IconFont
{
    private static readonly Lazy<FontFamily?> FamilyLazy = new(Detect);

    public static readonly char Mic = '\uE720';
    public static readonly char AppWindow = '\uE744';
    public static readonly char Headphones = '\uE7F5';
    public static readonly char Speaker = '\uE767';
    public static readonly char Star = '\uE735';
    public static readonly char StarFill = '\uE735';
    public static readonly char Play = '\uE768';
    public static readonly char Stop = '\uE71A';
    public static readonly char Settings = '\uE713';
    public static readonly char Search = '\uE721';
    public static readonly char Add = '\uE710';
    public static readonly char Volume = '\uE767';

    public static FontFamily? Family => FamilyLazy.Value;

    public static Font Font(int dipSize = 14) =>
        Family is { } family
            ? new Font(family, Theme.D(dipSize), FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily,
                Theme.D(dipSize), FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>True when the icon font exists; callers skip the glyph entirely otherwise.</summary>
    public static bool Available => Family is not null;

    private static FontFamily? Detect()
    {
        try
        {
            using var fluent = new FontFamily("Segoe Fluent Icons");
            return fluent;
        }
        catch (Exception)
        {
            try
            {
                using var mdl2 = new FontFamily("Segoe MDL2 Assets");
                return mdl2;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
