namespace OnbodyMic.Persistence;

/// <summary>
/// Durability-first file writes (docs/06 §7): write a temp file, flush to
/// disk, re-read and verify, then atomically replace the target while keeping
/// the previous version as a recoverable backup. A failed save never
/// truncates the old file.
/// </summary>
public static class AtomicFile
{
    public static void WriteText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

            if (File.ReadAllText(temp) != contents)
            {
                throw new IOException("temp verify mismatch");
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak");
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
