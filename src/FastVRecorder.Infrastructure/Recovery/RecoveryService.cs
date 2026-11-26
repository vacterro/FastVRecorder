using System.Text.Json;
using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.History;
using FastVRecorder.Core.Recovery;
using FastVRecorder.Core.Settings;
using NAudio.Wave;

namespace FastVRecorder.Infrastructure.Recovery;

public sealed class RecoveredItem
{
    public required string Path { get; init; }
    public required string OriginalPart { get; init; }
    public required TimeSpan Duration { get; init; }
    public required long SizeBytes { get; init; }
    public required string Format { get; init; }
}

public sealed class RecoveryFailure
{
    public required string Path { get; init; }
    public required string Error { get; init; }
}

public sealed class RecoveryReport
{
    public List<RecoveredItem> Recovered { get; } = new();
    public List<RecoveryFailure> Failed { get; } = new();
    public int Scanned { get; set; }
}

/// <summary>
/// Finds .wav.part files left by a crash and rebuilds a verified, separately named WAV from the
/// physical audio bytes. The partial file and its sidecar are never deleted.
/// </summary>
public sealed class RecoveryService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly PortableRoot _paths;
    private readonly RecordingJournal _journal;
    private readonly HistoryStore _history;
    private readonly ILog _log;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string, bool>? _isActivePart;

    public RecoveryService(
        PortableRoot paths,
        RecordingJournal journal,
        HistoryStore history,
        ILog log,
        Func<AppSettings> settings,
        Func<string, bool>? isActivePart = null)
    {
        _paths = paths;
        _journal = journal;
        _history = history;
        _log = log;
        _settings = settings;
        _isActivePart = isActivePart;
    }

    public RecoveryReport RecoverAll(CancellationToken cancellationToken = default)
    {
        var report = new RecoveryReport();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _journal.Snapshot())
        {
            if (File.Exists(entry.PartPath))
            {
                candidates.Add(entry.PartPath);
            }
            else
            {
                _journal.Remove(entry.PartPath);
            }
        }

        foreach (var folder in EnumerateFolders())
        {
            ScanFolder(folder, candidates);
        }

        foreach (var part in candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (_isActivePart?.Invoke(part) == true)
            {
                // A recording that started before the scan finished owns this part.
                continue;
            }

            report.Scanned++;
            try
            {
                var recovered = RecoverOne(part, report);
                if (recovered is not null) report.Recovered.Add(recovered);
                else _journal.Remove(part);
            }
            catch (Exception ex)
            {
                _log.Error("Recovery failed for " + part, ex);
                report.Failed.Add(new RecoveryFailure { Path = part, Error = ex.Message });
            }
        }

        if (report.Recovered.Count > 0)
        {
            _log.Info($"Recovered {report.Recovered.Count} partial recording(s).");
        }

        return report;
    }

    private IEnumerable<string> EnumerateFolders()
    {
        var folders = new List<string>();
        string configured;
        try
        {
            configured = _paths.ResolveRecordingFolder(_settings().Files.RecordingFolder);
        }
        catch
        {
            configured = _paths.DefaultRecordingsDir;
        }

        folders.Add(configured);
        if (!string.Equals(configured, _paths.DefaultRecordingsDir, StringComparison.OrdinalIgnoreCase))
        {
            folders.Add(_paths.DefaultRecordingsDir);
        }

        return folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void ScanFolder(string root, HashSet<string> candidates)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (folder, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.wav.part", SearchOption.TopDirectoryOnly))
                {
                    candidates.Add(file);
                }

                if (depth < 4)
                {
                    foreach (var sub in Directory.EnumerateDirectories(folder))
                    {
                        // Skip the portable config/themes/media trees and hidden/system folders.
                        var name = Path.GetFileName(sub);
                        if (name is "config" or "themes" or "media" or "logs") continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Cannot scan {folder} for partial recordings: {ex.Message}");
            }
        }
    }

    private RecoveredItem? RecoverOne(string partPath, RecoveryReport report)
    {
        var basePath = partPath.EndsWith(FileNamer.PartSuffix, StringComparison.OrdinalIgnoreCase)
            ? partPath[..^FileNamer.PartSuffix.Length]
            : partPath;
        var metaPath = basePath + FileNamer.MetaSuffix;
        var markerPath = basePath + ".recovered";
        var unrecoverableMarker = basePath + ".unrecoverable.json";

        var metadata = TryReadMetadata(metaPath);
        if (File.Exists(markerPath))
        {
            _log.Debug("Partial already recovered: " + partPath);
            return null;
        }

        if (metadata?.RecoveredAtUtc is not null && metadata.RecoveredPath is not null && File.Exists(metadata.RecoveredPath))
        {
            return null;
        }

        if (File.Exists(unrecoverableMarker))
        {
            return null;
        }

        if (!WavHeader.TryParse(partPath, out var info, out var headerError) || info is null)
        {
            Fail(partPath, "Cannot read the stored WAV header: " + headerError, unrecoverableMarker, report);
            return null;
        }

        var format = info.ToTargetFormat();
        if (format is null)
        {
            Fail(partPath, $"Unsupported stored format (tag {info.FormatTag}, {info.BitsPerSample}-bit).", unrecoverableMarker, report);
            return null;
        }

        var dataBytes = info.PhysicalDataBytes;
        if (dataBytes < format.BlockAlign)
        {
            Fail(partPath, "No complete audio frames are stored.", unrecoverableMarker, report);
            return null;
        }

        var directory = Path.GetDirectoryName(partPath)!;
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var outputPath = Path.Combine(directory, stem + "_RECOVERED.wav");
        if (File.Exists(outputPath))
        {
            outputPath = FileNamer.NextFreePath(outputPath);
        }

        long written;
        using (var writer = WavStreamWriter.CreateNew(outputPath, format, dataBytes + format.BlockAlign))
        {
            using var source = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            source.Position = info.DataOffset;
            var buffer = new byte[(int)Math.Min(int.MaxValue, (long)format.BlockAlign * 65536)];
            var remaining = dataBytes;
            while (remaining >= format.BlockAlign)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                want -= want % format.BlockAlign;
                if (want <= 0) break;
                var read = source.Read(buffer, 0, want);
                if (read <= 0) break;
                read -= read % format.BlockAlign;
                if (read <= 0) break;
                writer.Write(buffer.AsSpan(0, read));
                remaining -= read;
            }

            written = writer.DataBytes;
            writer.Complete();
        }

        if (written == 0)
        {
            TryDelete(outputPath);
            Fail(partPath, "Copying the stored audio produced no frames.", unrecoverableMarker, report);
            return null;
        }

        if (!WavVerifier.Verify(outputPath, format, written, out var verifyError))
        {
            TryDelete(outputPath);
            Fail(partPath, "Recovered WAV failed verification: " + verifyError, unrecoverableMarker, report);
            return null;
        }

        if (!CanReadAudio(outputPath, out var playError))
        {
            TryDelete(outputPath);
            Fail(partPath, "Recovered WAV cannot be read back: " + playError, unrecoverableMarker, report);
            return null;
        }

        var duration = TimeSpan.FromSeconds((double)written / format.AverageBytesPerSecond);
        var size = new FileInfo(outputPath).Length;
        var created = metadata?.StartedAtUtc.ToLocalTime() ?? File.GetCreationTime(partPath);

        if (_history.Snapshot().All(e => !string.Equals(e.Path, outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            _history.Add(new HistoryEntry
            {
                Path = outputPath,
                CreatedLocal = created,
                DurationSeconds = duration.TotalSeconds,
                SizeBytes = size,
                Format = "WAV",
                Recovered = true,
                Note = "Recovered from " + Path.GetFileName(partPath) +
                       (written < dataBytes ? $" ({dataBytes - written} bytes of the last frame were dropped)." : "."),
            });
        }

        if (metadata is not null)
        {
            metadata.RecoveredAtUtc = DateTime.UtcNow;
            metadata.RecoveredPath = outputPath;
            metadata.RecoveryError = null;
            TryWriteJson(metaPath, metadata, overwrite: true);
        }

        TryWriteJson(markerPath, new
        {
            recoveredPath = outputPath,
            recoveredAtUtc = DateTime.UtcNow,
            sourcePart = partPath,
            format = format.Describe(),
            dataBytes = written,
        });

        _journal.Remove(partPath);
        _log.Info($"Recovered partial recording {partPath} -> {outputPath} ({duration:hh\\:mm\\:ss}).");

        // The reserved final file (if any) is an empty placeholder created by the reservation; the
        // partial audio is kept as-is for diagnosis.
        if (metadata?.IntendedOutputPath is { } intended && File.Exists(intended) && new FileInfo(intended).Length == 0)
        {
            TryDelete(intended);
        }

        return new RecoveredItem
        {
            Path = outputPath,
            OriginalPart = partPath,
            Duration = duration,
            SizeBytes = size,
            Format = "WAV",
        };
    }

    private void Fail(string partPath, string error, string markerPath, RecoveryReport report)
    {
        _log.Warn($"Cannot recover {partPath}: {error}");
        report.Failed.Add(new RecoveryFailure { Path = partPath, Error = error });
        TryWriteJson(markerPath, new
        {
            error,
            failedAtUtc = DateTime.UtcNow,
            note = "The damaged .wav.part was kept for diagnosis.",
        });
    }

    private static bool CanReadAudio(string path, out string? error)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var buffer = new byte[Math.Min(16384, Math.Max(4, reader.WaveFormat.AverageBytesPerSecond / 4))];
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                error = "The WAV contains no readable frames.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static RecoveryMetadata? TryReadMetadata(string path)
    {
        var text = AtomicFile.TryReadAllText(path);
        if (text is null) return null;
        try
        {
            return JsonSerializer.Deserialize<RecoveryMetadata>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryWriteJson(string path, object value, bool overwrite = false)
    {
        try
        {
            if (!overwrite && File.Exists(path)) return;
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Markers are advisory.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
