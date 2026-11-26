using System.Text;

namespace FastVRecorder.Core.Files;

/// <summary>Crash-safe whole-file replacement: write temp, flush to disk, atomic replace with backup.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, bool keepBackup = true)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(content);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        try
        {
            if (File.Exists(path))
            {
                if (keepBackup)
                {
                    File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
