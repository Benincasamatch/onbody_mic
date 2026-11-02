namespace OnbodyMic.Desktop;

using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// GUI smoke evidence (docs/09 verification): WinForms bitmap rendering of the
/// main window, top and sound-library half, when --selftest runs. Files go to
/// the explicitly selected screenshot directory or the isolated selftest root.
/// </summary>
internal static class SmokeShots
{
    public static string? OutputDirectory { get; set; }
    public static void Capture(Form form, string fileName)
    {
        try
        {
            var dir = OutputDirectory;
            if (dir is null) return;
            Directory.CreateDirectory(dir);

            // scroll viewport to the sound library first for the second shot
            if (fileName == "scrolled.png" && ScrollViewport(form) is { } content)
            {
                content.AutoScrollPosition = new Point(0, content.DisplayRectangle.Height);
                form.PerformLayout();
                Application.DoEvents();
            }

            form.PerformLayout();
            Application.DoEvents();
            form.Refresh();
            var bounds = new Rectangle(Point.Empty, form.Size);
            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            form.DrawToBitmap(bmp, bounds);
            bmp.Save(Path.Combine(dir, fileName), System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"selftest: shot {Path.Combine(dir, fileName)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"selftest: 截图失败（不影响审计）: {ex.GetType().Name}");
        }
    }

    private static Panel? ScrollViewport(Form form) =>
        typeof(MainForm)
            .GetField("_content", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
            .GetValue(form) as Panel;

}
