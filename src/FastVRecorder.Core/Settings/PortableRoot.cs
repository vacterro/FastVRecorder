namespace FastVRecorder.Core.Settings;

/// <summary>
/// Resolves all application paths relative to the folder that contains the executable
/// (never the process working directory), so the whole folder can be moved.
/// </summary>
public sealed class PortableRoot
{
    public PortableRoot(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public static PortableRoot FromExecutable()
    {
        var exe = Environment.ProcessPath;
        var dir = !string.IsNullOrEmpty(exe) ? Path.GetDirectoryName(exe)! : AppContext.BaseDirectory;
        return new PortableRoot(dir);
    }

    public string Root { get; }
    public string ConfigDir => Path.Combine(Root, "config");
    public string SettingsPath => Path.Combine(ConfigDir, "settings.json");
    public string HistoryPath => Path.Combine(ConfigDir, "history.json");
    public string CountersPath => Path.Combine(ConfigDir, "counters.json");
    public string JournalPath => Path.Combine(ConfigDir, "active-recordings.json");
    public string LogPath => Path.Combine(ConfigDir, "logs", "fastvrecorder.log");
    public string MediaDir => Path.Combine(Root, "media");
    public string ThemesDir => Path.Combine(Root, "themes");
    public string LanguagesDir => Path.Combine(Root, "languages");
    public string DefaultRecordingsDir => Path.Combine(Root, "Recordings");

    /// <summary>Absolute recording folder for a stored setting (relative values are portable).</summary>
    public string ResolveRecordingFolder(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return DefaultRecordingsDir;
        var s = stored.Trim();
        return Path.IsPathRooted(s) ? Path.GetFullPath(s) : Path.GetFullPath(Path.Combine(Root, s));
    }

    /// <summary>Stores folders inside the portable root as relative paths, others as absolute.</summary>
    public string ToStoredFolder(string absolute)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute));
        if (string.Equals(full, DefaultRecordingsDir, StringComparison.OrdinalIgnoreCase)) return "";
        var rootWithSep = Root + Path.DirectorySeparatorChar;
        if (full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(Root, full);
        return full;
    }

    /// <summary>Checks whether the configuration folder is writable (read-only media etc.).</summary>
    public bool CanWriteConfig(out string? error)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var probe = Path.Combine(ConfigDir, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The portable folder {Root} is read-only ({ex.Message}). Settings and history cannot be saved; move FastVRecorder to a writable folder.";
            return false;
        }
    }
}
