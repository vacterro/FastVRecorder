using System.Text;

namespace FastVRecorder.Core.Files;

/// <summary>Windows-safe file name handling.</summary>
public static class PathSafety
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    private static readonly char[] Invalid =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

    public const int MaxComponentLength = 120;

    /// <summary>
    /// Produces a single safe path component: no separators, no traversal, no control or
    /// reserved characters, no reserved device names, no trailing dots/spaces.
    /// Returns <paramref name="fallback"/> when nothing usable remains.
    /// </summary>
    public static string SanitizeComponent(string? value, string fallback = "Recording", int maxLength = MaxComponentLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsControl(ch) || Array.IndexOf(Invalid, ch) >= 0) sb.Append('_');
            else sb.Append(ch);
        }
        var s = sb.ToString().Trim().TrimEnd('.', ' ').TrimStart(' ');
        while (s.StartsWith("..", StringComparison.Ordinal)) s = s[1..];
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd('.', ' ');
        if (s.Length == 0 || s.All(c => c == '.' || c == '_')) return fallback;
        var stem = s.Split('.')[0];
        if (Reserved.Contains(stem.TrimEnd(' '))) s = "_" + s;
        return s;
    }

    public static bool IsSafeComponent(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SanitizeComponent(value, fallback: "\0") == value;

    /// <summary>True when <paramref name="candidate"/> resolves inside <paramref name="root"/>.</summary>
    public static bool IsUnder(string root, string candidate)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
