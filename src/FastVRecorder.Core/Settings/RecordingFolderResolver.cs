namespace FastVRecorder.Core.Settings;

public sealed record RecordingFolderResolution(
    string StoredPath,
    string AbsolutePath,
    bool IsValid,
    string? Error);

/// <summary>
/// Turns a manually typed recording-folder address into the stored portable form.
/// Accepts absolute paths, paths relative to the application root, and %ENVIRONMENT% variables.
/// </summary>
public static class RecordingFolderResolver
{
    public static RecordingFolderResolution Resolve(PortableRoot paths, string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new RecordingFolderResolution(string.Empty, paths.DefaultRecordingsDir, true, null);
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(text);
            var absolute = Path.GetFullPath(expanded, paths.Root);
            var stored = paths.ToStoredFolder(absolute);
            return new RecordingFolderResolution(stored, absolute, true, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return new RecordingFolderResolution(string.Empty, string.Empty, false, ex.Message);
        }
    }
}
