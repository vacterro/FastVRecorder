using Microsoft.Win32;

namespace FastVRecorder.Infrastructure.Startup;

/// <summary>
/// "Start with Windows" through a Startup-folder shortcut (original icon, no admin rights), with
/// the HKCU Run key as a fallback when WScript.Shell is unavailable.
/// </summary>
public sealed class StartupService
{
    private const string ShortcutName = "FastVRecorder.lnk";
    private const string RunValueName = "FastVRecorder";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _executablePath;
    private readonly string _workingDirectory;

    public StartupService(string executablePath, string workingDirectory)
    {
        _executablePath = executablePath;
        _workingDirectory = workingDirectory;
    }

    private string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public bool IsEnabled
    {
        get
        {
            if (File.Exists(ShortcutPath)) return true;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        if (!enabled)
        {
            try
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                error ??= ex.Message;
            }

            return error is null;
        }

        if (TryCreateShortcut(out var shortcutError))
        {
            return true;
        }

        if (TrySetRunKey(out var runError))
        {
            return true;
        }

        error = shortcutError ?? runError ?? "Could not enable start with Windows.";
        return false;
    }

    private bool TryCreateShortcut(out string? error)
    {
        error = null;
        try
        {
            var path = ShortcutPath;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) throw new IOException("Startup folder is unavailable.");
            Directory.CreateDirectory(directory);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) throw new InvalidOperationException("WScript.Shell is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = _executablePath;
            shortcut.WorkingDirectory = _workingDirectory;
            shortcut.IconLocation = _executablePath + ",0";
            shortcut.Description = "FastVRecorder - press Alt+0 to record";
            shortcut.Arguments = string.Empty;
            shortcut.WindowStyle = 1;
            shortcut.Save();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TrySetRunKey(out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new IOException("Cannot open the Run registry key.");
            key.SetValue(RunValueName, $"\"{_executablePath}\"");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
