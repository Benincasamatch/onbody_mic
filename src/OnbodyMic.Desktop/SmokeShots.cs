namespace OnbodyMic.Desktop;

 using System.Drawing;
 using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// GUI smoke evidence (docs/09 verification): PrintWindow capture of the real
/// main window, top and sound-library half, when --selftest runs. Files go to
/// build/shots/ next to the repo; missing directory is not an error (selftest
/// must stay green on clean checkouts).
/// </summary>
internal static class SmokeShots
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    public static void Capture(Form form, string fileName)
    {
        try
        {
            var dir = Path.Combine(FindRepoRoot(), "build", "shots");
            if (!Directory.Exists(dir))
            {
                return;
            }

            // scroll viewport to the sound library first for the second shot
            if (fileName == "scrolled.png" && ScrollViewport(form) is { } content)
            {
                content.AutoScrollPosition = new Point(0, content.DisplayRectangle.Height);
                form.PerformLayout();
                Application.DoEvents();
            }

            var bounds = form.ClientRectangle;
            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            using var g = Graphics.FromImage(bmp);
            var hdc = g.GetHdc();
            _ = PrintWindow(form.Handle, hdc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
            bmp.Save(Path.Combine(dir, fileName), System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"selftest: shot build/shots/{fileName}");
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
