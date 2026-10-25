namespace OnbodyMic.Platform.Windows;

using System.Diagnostics;

/// <summary>
/// Resolves a preset's saved executable path to live process instances (B02:
/// identity by path, never by name). Ambiguity is surfaced, never guessed.
/// </summary>
public static class WindowsAppCatalog
{
    public sealed record AppInstance(uint ProcessId, string ExecutablePath);

    /// <summary>All reachable processes whose main module path matches exactly.</summary>
    public static IReadOnlyList<AppInstance> FindByExecutablePath(string executablePath)
    {
        var normalized = Path.GetFullPath(executablePath);
        var matches = new List<AppInstance>();
        foreach (var process in Process.GetProcesses())
        {
            using var _ = process;
            try
            {
                if (process.MainModule?.FileName is { } file
                    && string.Equals(Path.GetFullPath(file), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new AppInstance((uint)process.Id, file));
                }
            }
            catch
            {
                // system/protected processes deny module queries; they can't match anyway
            }
        }

        return matches;
    }
}
