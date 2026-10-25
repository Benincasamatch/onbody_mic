namespace OnbodyMic.Desktop;

using System.Text;

/// <summary>
/// P4 diagnostics (docs/03 P4 task 3): bounded local file log and crash
/// reports. Privacy contract (docs/05 §7.3, docs/07 checklist): never audio
/// content, window titles or chat text; log lines come from the already
/// desensitized session notice stream; crash files hold exception type,
/// message and stack only. Everything stays under <c>logs/</c> in the app
/// root and is never uploaded; writes must never throw into audio/UI paths.
/// </summary>
internal static class Diagnostics
{
    private const long LogCapBytes = 256 * 1024;
    private const int CrashFilesKept = 5;
    private static readonly object Gate = new();

    /// <summary>Creates the file-logging sink; returns false when the directory is unusable (console-only fallback).</summary>
    public static bool TryEnableFileLog(string logDir, out string logPath)
    {
        try
        {
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir, "onbody.log");
            return true;
        }
        catch
        {
            logPath = string.Empty;
            return false;
        }
    }

    /// <summary>Appends one line to the bounded log; rotates to <c>onbody.old.log</c> past the cap.</summary>
    public static void WriteLog(string logPath, string message)
    {
        if (string.IsNullOrEmpty(logPath))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                RotateIfOversized(logPath);
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never break audio or UI paths; console keeps the line
        }
    }

    /// <summary>Writes a crash report; returns the file path or empty on failure. Keeps the newest five.</summary>
    public static string WriteCrashReport(string logDir, string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var version = ThisAssemblyVersion();
            var sb = new StringBuilder();
            sb.AppendLine($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"pid: {Environment.ProcessId}");
            sb.AppendLine($"version: {version}");
            sb.AppendLine($"source: {source}");
            AppendException(sb, exception, depth: 0);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            PruneCrashReports(logDir);
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RotateIfOversized(string logPath)
    {
        var info = new FileInfo(logPath);
        if (info.Exists && info.Length > LogCapBytes)
        {
            var old = Path.Combine(Path.GetDirectoryName(logPath)!, "onbody.old.log");
            File.Copy(logPath, old, overwrite: true);
            info.Delete();
        }
    }

    private static void PruneCrashReports(string logDir)
    {
        var files = new DirectoryInfo(logDir)
            .GetFiles("crash-*.txt")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
        for (var i = CrashFilesKept; i < files.Count; i++)
        {
            files[i].Delete();
        }
    }

    private static void AppendException(StringBuilder sb, Exception? exception, int depth)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var indent = new string(' ', depth * 2);
            sb.AppendLine($"{indent}type: {ex.GetType().FullName}");
            sb.AppendLine($"{indent}message: {ex.Message}");
            sb.AppendLine(ex.StackTrace is null ? $"{indent}stack: (none)" : $"{indent}stack:");
            if (ex.StackTrace is not null)
            {
                sb.AppendLine(ex.StackTrace.Indent(indent.Length));
            }

            depth++;
        }
    }

    private static string Indent(this string text, int spaces) =>
        string.Join(Environment.NewLine, text.Split(Environment.NewLine).Select(line => spaces > 0 ? new string(' ', spaces) + line : line));

    private static string ThisAssemblyVersion()
    {
        var asm = typeof(Diagnostics).Assembly;
        var info = asm.GetCustomAttributes(false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault();
        return info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "unknown";
    }
}
