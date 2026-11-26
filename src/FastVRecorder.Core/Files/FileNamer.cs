using System.Globalization;

namespace FastVRecorder.Core.Files;

public enum NamingPreset
{
    /// <summary>2026-11-26_05-36-00.wav (OBS-like). Default.</summary>
    DateTime,
    /// <summary>Recording_0001.wav (prefix + persistent counter).</summary>
    Counter,
    /// <summary>Voice_2026-11-26_0001.wav (prefix + date + daily counter).</summary>
    PrefixDateCounter,
}

public sealed record NamingOptions(NamingPreset Preset, string Prefix, bool DateFolders, bool IncrementalNumbering = true)
{
    public static readonly NamingOptions Default = new(NamingPreset.DateTime, "Recording", false);
}

/// <summary>A name reserved on disk by creating the .part file with CreateNew.</summary>
public sealed class ReservedTarget : IDisposable
{
    public required string Directory { get; init; }
    public required string BaseName { get; init; }
    public required string FinalPath { get; init; }
    public required string PartPath { get; init; }
    public required string MetaPath { get; init; }
    public FileStream? Stream { get; set; }

    /// <summary>Transfers ownership of the reserved empty stream to the caller.</summary>
    public FileStream TakeStream()
    {
        var s = Stream ?? throw new InvalidOperationException("Stream already taken.");
        Stream = null;
        return s;
    }

    public void Dispose()
    {
        Stream?.Dispose();
        Stream = null;
    }
}

/// <summary>Builds and atomically reserves unique, Windows-safe recording file names.</summary>
public sealed class FileNamer
{
    public const string PartSuffix = ".part";
    public const string MetaSuffix = ".recovery.json";
    private readonly ICounterStore _counters;
    private readonly Func<DateTime> _clock;

    public FileNamer(ICounterStore counters, Func<DateTime>? clock = null)
    {
        _counters = counters;
        _clock = clock ?? (() => DateTime.Now);
    }

    public static string SanitizePrefix(string? prefix) => PathSafety.SanitizeComponent(prefix, "Recording", 40);

    /// <summary>Relative path preview, e.g. "2026\11\26\05-36-00.wav". Does not allocate counters.</summary>
    public string Preview(NamingOptions options, string extension = ".wav")
    {
        var now = _clock();
        var (sub, stem) = Compose(options, now, peekOnly: true);
        var name = stem + extension;
        return sub.Length == 0 ? name : Path.Combine(sub, name);
    }

    /// <summary>
    /// Reserves a unique target under <paramref name="root"/>. The .part file is created with
    /// FileMode.CreateNew (atomic on NTFS/SMB), and neither the final name nor any alternative output
    /// extension may already exist. Never overwrites.
    /// </summary>
    public ReservedTarget Reserve(string root, NamingOptions options, IReadOnlyCollection<string>? alsoAvoidExtensions = null)
    {
        var now = _clock();
        var (sub, stem) = Compose(options, now, peekOnly: false);
        var dir = sub.Length == 0 ? root : Path.Combine(root, sub);
        System.IO.Directory.CreateDirectory(dir);

        for (var attempt = 0; attempt < 10000; attempt++)
        {
            string baseName;
            if (attempt == 0)
            {
                baseName = stem;
            }
            else if (options.Preset == NamingPreset.DateTime || !options.IncrementalNumbering)
            {
                baseName = $"{stem}_{attempt:000}";
            }
            else
            {
                // Counter presets: take the next number instead of adding a suffix.
                (_, baseName) = Compose(options, now, peekOnly: false);
            }

            var final = Path.Combine(dir, baseName + ".wav");
            if (Exists(final)) continue;
            if (alsoAvoidExtensions is not null && alsoAvoidExtensions.Any(ext => Exists(Path.Combine(dir, baseName + ext)))) continue;
            var part = final + PartSuffix;
            try
            {
                var fs = new FileStream(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                return new ReservedTarget
                {
                    Directory = dir,
                    BaseName = baseName,
                    FinalPath = final,
                    PartPath = part,
                    MetaPath = final + MetaSuffix,
                    Stream = fs,
                };
            }
            catch (IOException) when (Exists(part))
            {
                // Lost the race for this name; try the next.
            }
        }
        throw new IOException($"Could not reserve a unique file name in '{dir}'.");
    }

    /// <summary>Finds a free final path for a rename (used if the target appeared after reservation).</summary>
    public static string NextFreePath(string desiredPath)
    {
        if (!Exists(desiredPath)) return desiredPath;
        var dir = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);
        for (var i = 1; i < 10000; i++)
        {
            var p = Path.Combine(dir, $"{stem}_{i:000}{ext}");
            if (!Exists(p)) return p;
        }
        throw new IOException($"No free file name near '{desiredPath}'.");
    }

    private (string sub, string stem) Compose(NamingOptions options, DateTime now, bool peekOnly)
    {
        var prefix = SanitizePrefix(options.Prefix);
        var sub = options.DateFolders
            ? Path.Combine(now.ToString("yyyy", CultureInfo.InvariantCulture), now.ToString("MM", CultureInfo.InvariantCulture), now.ToString("dd", CultureInfo.InvariantCulture))
            : string.Empty;
        var time = options.DateFolders
            ? now.ToString("HH-mm-ss", CultureInfo.InvariantCulture)
            : now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string stem;
        switch (options.Preset)
        {
            case NamingPreset.Counter:
            {
                var n = peekOnly || !options.IncrementalNumbering ? _counters.Peek("global") : _counters.Next("global");
                stem = $"{prefix}_{n:0000}";
                break;
            }
            case NamingPreset.PrefixDateCounter:
            {
                var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var n = peekOnly || !options.IncrementalNumbering ? _counters.Peek("day:" + day) : _counters.Next("day:" + day);
                stem = $"{prefix}_{day}_{n:0000}";
                break;
            }
            default:
                stem = time;
                break;
        }
        return (sub, stem);
    }

    private static bool Exists(string path) => File.Exists(path) || System.IO.Directory.Exists(path);
}
