using System.Security.Cryptography;
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
///
/// Markers and metadata are ADVISORY: they may point at a surviving output, but that output is
/// only trusted after its PCM payload is proven identical to the partial recording's payload.
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
    private readonly object _runLock = new();

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
        // One coordinator: the startup scan and the user Retry Recovery must never recover the
        // same partial twice (which would publish duplicate, independently named outputs).
        lock (_runLock)
        {
            var report = new RecoveryReport();
            // Recovery is scoped by every folder the scan can touch: the portable root (its
            // journal/history) plus the configured and default recording folders. Sorted
            // acquisition keeps multiple processes from deadlocking on each other.
            var scopes = new List<string> { _paths.Root, _paths.DefaultRecordingsDir };
            try
            {
                scopes.Add(_paths.ResolveRecordingFolder(_settings().Files.RecordingFolder));
            }
            catch
            {
                // An unreadable configured folder is already handled by EnumerateFolders.
            }

            var heldMutexes = new List<Mutex>();
            foreach (var scope in scopes
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var mutex = TryCreateScopeMutex(scope);
                if (mutex is null) continue;
                var owned = false;
                try
                {
                    owned = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (!owned)
                {
                    // Another Windows session sharing this folder is recovering right now;
                    // scanning the same partials from both sides would duplicate outputs.
                    mutex.Dispose();
                    foreach (var held in heldMutexes)
                    {
                        try { held.ReleaseMutex(); } catch { /* shutdown race */ }
                        held.Dispose();
                    }

                    _log.Warn("Crash recovery is already running for " + scope + " in another Windows session; skipping this scan.");
                    return report;
                }

                heldMutexes.Add(mutex);
            }

            try
            {
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in _journal.Snapshot())
                {
                    if (File.Exists(entry.PartPath))
                    {
                        candidates.Add(entry.PartPath);
                    }
                    else
                    {
                        // The volume may simply be disconnected; keep the journal so reconnecting retries.
                        _log.Warn("Journal part is currently unavailable: " + entry.PartPath);
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

                    if (IsStillBeingWrittenByAnotherSession(part))
                    {
                        _log.Warn("Skipping a partial recording that is still growing: " + part);
                        continue;
                    }

                    report.Scanned++;
                    try
                    {
                        var recovered = RecoverOne(part, report);
                        if (recovered is not null)
                        {
                            report.Recovered.Add(recovered);
                        }
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
            finally
            {
                foreach (var held in heldMutexes)
                {
                    try { held.ReleaseMutex(); } catch { /* shutdown race */ }
                    held.Dispose();
                }
            }
        }
    }

    private static Mutex? TryCreateScopeMutex(string scope)
    {
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope.ToUpperInvariant())))[..16];
            return new Mutex(false, @"Global\FastVRecorder.Recovery." + digest);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
            or System.Threading.WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A non-empty part whose length grows right after the last write belongs to a live writer
    /// in another Windows session; its mutex is not ours to take, so never snapshot it.
    /// </summary>
    private static bool IsStillBeingWrittenByAnotherSession(string partPath)
    {
        try
        {
            var info = new FileInfo(partPath);
            if (!info.Exists) return false;
            if (DateTime.UtcNow - info.LastWriteTimeUtc >= TimeSpan.FromSeconds(10)) return false;
            var firstLength = info.Length;
            Thread.Sleep(200);
            return new FileInfo(partPath).Length != firstLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
                        // Never follow a junction/symlink out of the recording tree: a linked
                        // folder must not make recovery adopt or scan unrelated storage.
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(sub); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
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

        if (!WavHeader.TryParse(partPath, out var info, out var headerError) || info is null)
        {
            Fail(partPath, "Cannot read the stored WAV header: " + headerError, unrecoverableMarker, report, permanent: true);
            return null;
        }

        var format = info.ToTargetFormat();
        if (format is null)
        {
            Fail(partPath, $"Unsupported stored format (tag {info.FormatTag}, {info.BitsPerSample}-bit).", unrecoverableMarker, report, permanent: true);
            return null;
        }

        var dataBytes = info.PhysicalDataBytes;
        if (dataBytes < format.BlockAlign)
        {
            Fail(partPath, "No complete audio frames are stored.", unrecoverableMarker, report, permanent: true);
            return null;
        }

        var directory = Path.GetDirectoryName(partPath)!;
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var outputPath = Path.Combine(directory, stem + "_RECOVERED.wav");
        var partLength = TryGetFileLength(partPath);

        if (File.Exists(markerPath))
        {
            var marker = TryReadMarker(markerPath);
            if (marker is not null &&
                IsTrustedOutput(marker.RecoveredPath, partPath, directory, stem, format, dataBytes,
                    partLength, marker.SourcePart, marker.SourceLength, marker.DataSha256))
            {
                _log.Debug("Partial already recovered: " + partPath);
                _journal.Remove(partPath);
                return null;
            }

            _log.Warn("Recovery marker is missing, unrelated or unverified; rebuilding the recovered WAV: " + partPath);
        }

        if (metadata?.RecoveredAtUtc is not null && metadata.RecoveredPath is not null &&
            IsTrustedOutput(metadata.RecoveredPath, partPath, directory, stem, format, dataBytes,
                partLength, metadata.PartPath, metadata.SourceLength, metadata.SourceDataSha256))
        {
            _journal.Remove(partPath);
            return null;
        }

        if (File.Exists(unrecoverableMarker) && UnrecoverableMarkerStillApplies(unrecoverableMarker, partLength))
        {
            _journal.Remove(partPath);
            return null;
        }

        // The expected payload identity is read from the part once and then used to gate BOTH
        // adoption of a surviving output and the final publication of a rebuilt one.
        var expectedPcmSha256 = TryComputePcmSha256(partPath, info.DataOffset, dataBytes);
        if (expectedPcmSha256 is null)
        {
            Fail(partPath, "The partial file could not be read for identity verification.", unrecoverableMarker, report, permanent: false);
            return null;
        }

        string? outputPcmSha256 = null;
        var adopted = false;
        if (File.Exists(outputPath) &&
            !IsReparsePoint(outputPath) &&
            WavVerifier.Verify(outputPath, format, dataBytes, out _) &&
            CanReadAudio(outputPath, out _))
        {
            outputPcmSha256 = TryComputePcmSha256(outputPath, WavHeader.DataOffsetOf(outputPath), dataBytes);
            adopted = string.Equals(outputPcmSha256, expectedPcmSha256, StringComparison.Ordinal);
        }

        long written;
        if (adopted)
        {
            // A crash after publishing the recovered file but before history/marker commit:
            // adopt the identity-verified file instead of creating an uncontrolled duplicate.
            written = dataBytes;
            _log.Info("Adopting existing verified recovery output: " + outputPath);
        }
        else
        {
            if (File.Exists(outputPath))
            {
                outputPath = FileNamer.NextFreePath(outputPath);
            }

            var temporary = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var writer = WavStreamWriter.CreateNew(temporary, format, dataBytes + format.BlockAlign))
                {
                    using var source = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    source.Position = info.DataOffset;
                    written = CopyWholeFrames(source, writer, dataBytes);
                    writer.Complete();
                }

                if (written == 0)
                {
                    Fail(partPath, "Copying the stored audio produced no frames.", unrecoverableMarker, report, permanent: true);
                    return null;
                }

                if (!WavVerifier.Verify(temporary, format, written, out var verifyError))
                {
                    Fail(partPath, "Recovered WAV failed verification: " + verifyError, unrecoverableMarker, report, permanent: !IsTransientlyUnreadable(temporary));
                    return null;
                }

                if (!CanReadAudio(temporary, out var playError))
                {
                    Fail(partPath, "Recovered WAV cannot be read back: " + playError, unrecoverableMarker, report, permanent: !IsTransientlyUnreadable(temporary));
                    return null;
                }

                var temporaryPcmSha256 = TryComputePcmSha256(temporary, WavHeader.DataOffsetOf(temporary), written);
                if (!string.Equals(temporaryPcmSha256, expectedPcmSha256, StringComparison.Ordinal))
                {
                    Fail(partPath, "Recovered WAV does not carry the original audio payload.", unrecoverableMarker, report,
                        permanent: !IsTransientlyUnreadable(temporary));
                    return null;
                }

                // The verified bytes are published by an atomic rename in the same directory;
                // reuse the verified identity instead of reading the payload a third time.
                outputPcmSha256 = temporaryPcmSha256;

                var finalPath = outputPath;
                if (File.Exists(finalPath))
                {
                    finalPath = FileNamer.NextFreePath(finalPath);
                }

                File.Move(temporary, finalPath, overwrite: false);
                outputPath = finalPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked/removed/malformed volume is transient: keep the journal and retry later.
                _log.Warn($"Recovery I/O failed for {partPath}; the partial file and journal were kept: {ex.Message}");
                report.Failed.Add(new RecoveryFailure { Path = partPath, Error = "Transient recovery failure: " + ex.Message });
                return null;
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        if (!WavVerifier.Verify(outputPath, format, written, out var resultVerifyError))
        {
            Fail(partPath, "Recovered WAV failed final verification: " + resultVerifyError, unrecoverableMarker, report, permanent: !IsTransientlyUnreadable(outputPath));
            return null;
        }

        if (!CanReadAudio(outputPath, out var resultReadError))
        {
            Fail(partPath, "Recovered WAV cannot be read back: " + resultReadError, unrecoverableMarker, report, permanent: !IsTransientlyUnreadable(outputPath));
            return null;
        }

        if (outputPcmSha256 is null ||
            !string.Equals(outputPcmSha256, expectedPcmSha256, StringComparison.Ordinal))
        {
            Fail(partPath, "Recovered WAV identity changed before publication.", unrecoverableMarker, report,
                permanent: !IsTransientlyUnreadable(outputPath));
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
            metadata.SourceLength = partLength;
            metadata.SourceDataSha256 = outputPcmSha256;
            TryWriteJson(metaPath, metadata, overwrite: true);
        }

        TryWriteJson(markerPath, new
        {
            recoveredPath = outputPath,
            recoveredAtUtc = DateTime.UtcNow,
            sourcePart = partPath,
            format = format.Describe(),
            dataBytes = written,
            sourceLength = partLength,
            dataSha256 = outputPcmSha256,
        }, overwrite: true);

        _journal.Remove(partPath);
        _log.Info($"Recovered partial recording {partPath} -> {outputPath} ({duration}).");

        // Never delete a file merely because recovery metadata references it. The part itself is
        // preserved for diagnosis; only the newly created/adopted recovered output is published.

        return new RecoveredItem
        {
            Path = outputPath,
            OriginalPart = partPath,
            Duration = duration,
            SizeBytes = size,
            Format = "WAV",
        };
    }

    /// <summary>
    /// True only when a surviving output is canonical for this part AND its PCM payload is the
    /// part's payload. A marker naming an arbitrary path, another recording, or a same-length
    /// WAV with different samples is never trusted.
    /// </summary>
    private bool IsTrustedOutput(
        string? claimedPath,
        string partPath,
        string directory,
        string stem,
        TargetFormat format,
        long dataBytes,
        long? partLength,
        string? claimedSourcePart,
        long? claimedSourceLength,
        string? claimedDataSha256)
    {
        if (string.IsNullOrWhiteSpace(claimedPath)) return false;
        if (!IsCanonicalRecoveredOutput(claimedPath, directory, stem)) return false;
        if (!File.Exists(claimedPath)) return false;
        if (IsReparsePoint(claimedPath)) return false;
        if (!string.IsNullOrWhiteSpace(claimedSourcePart))
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(claimedSourcePart), Path.GetFullPath(partPath), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        if (claimedSourceLength is not null && partLength is not null && claimedSourceLength.Value != partLength.Value)
        {
            return false;
        }

        if (!WavVerifier.Verify(claimedPath, format, dataBytes, out _)) return false;
        if (!CanReadAudio(claimedPath, out _)) return false;

        var expected = TryComputePcmSha256(partPath, WavHeader.DataOffsetOf(partPath), dataBytes);
        var actual = TryComputePcmSha256(claimedPath, WavHeader.DataOffsetOf(claimedPath), dataBytes);
        if (expected is null || actual is null) return false;
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(claimedDataSha256))
        {
            return string.Equals(claimedDataSha256, actual, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool IsCanonicalRecoveredOutput(string claimedPath, string directory, string stem)
    {
        try
        {
            var full = Path.GetFullPath(claimedPath);
            var expectedDirectory = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(full), expectedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(Path.GetExtension(full), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Path.GetFileNameWithoutExtension(full)
                .StartsWith(stem + "_RECOVERED", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool UnrecoverableMarkerStillApplies(string markerPath, long? partLength)
    {
        var prior = TryReadUnrecoverable(markerPath);
        if (prior is null)
        {
            // A legacy/damaged marker: keep the historical behavior of not retrying forever.
            return true;
        }

        if (prior.SourceLength is null) return true;
        if (partLength is null) return true;
        if (prior.SourceLength.Value == partLength.Value) return true;
        _log.Warn("The unrecoverable marker no longer matches the partial file; retrying recovery: " + markerPath);
        return false;
    }

    private static long? TryGetFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a freshly written/checked file is momentarily locked or denied. Such a failure
    /// is transient and must not be recorded as a permanent unrecoverable classification.
    /// </summary>
    private static bool IsTransientlyUnreadable(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? TryComputePcmSha256(string path, long dataOffset, long dataBytes)
    {
        if (dataOffset < 0 || dataBytes <= 0) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (stream.Length - dataOffset < dataBytes) return null;
            stream.Position = dataOffset;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            var remaining = dataBytes;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) return null;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Copies whole frames while tolerating short, non-frame-aligned stream reads.</summary>
    internal static long CopyWholeFrames(Stream source, WavStreamWriter writer, long dataBytes)
    {
        var block = writer.Format.BlockAlign;
        var buffer = new byte[block * 65536];
        var carry = 0;
        var remaining = dataBytes;
        long written = 0;
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length - carry, remaining);
            if (want <= 0) break;
            var read = source.Read(buffer, carry, want);
            if (read <= 0) break;
            var total = carry + read;
            var whole = total - total % block;
            if (whole > 0)
            {
                writer.Write(buffer.AsSpan(0, whole));
                written += whole;
            }

            carry = total - whole;
            if (carry > 0 && whole > 0)
            {
                Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
            }

            remaining -= read;
        }

        return written;
    }

    private void Fail(string partPath, string error, string markerPath, RecoveryReport report, bool permanent)
    {
        _log.Warn($"Cannot recover {partPath}: {error}");
        report.Failed.Add(new RecoveryFailure { Path = partPath, Error = error });
        if (!permanent)
        {
            return;
        }

        TryWriteJson(markerPath, new
        {
            error,
            failedAtUtc = DateTime.UtcNow,
            note = "The damaged .wav.part was kept for diagnosis.",
            sourceLength = TryGetFileLength(partPath),
        }, overwrite: true);
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

    private sealed record RecoveredMarker(
        string? RecoveredPath,
        string? SourcePart,
        long? SourceLength,
        long? DataBytes,
        string? DataSha256);

    private static RecoveredMarker? TryReadMarker(string path)
    {
        var text = AtomicFile.TryReadAllText(path);
        if (text is null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            string? ReadString(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            long? ReadLong(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var number)
                    ? number
                    : null;
            return new RecoveredMarker(
                ReadString("recoveredPath"),
                ReadString("sourcePart"),
                ReadLong("sourceLength"),
                ReadLong("dataBytes"),
                ReadString("dataSha256"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record LegacyUnrecoverable(long? SourceLength);

    private static LegacyUnrecoverable? TryReadUnrecoverable(string path)
    {
        var text = AtomicFile.TryReadAllText(path);
        if (text is null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("sourceLength", out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var length))
            {
                return new LegacyUnrecoverable(length);
            }

            return new LegacyUnrecoverable(null);
        }
        catch (JsonException)
        {
            return null;
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
            if (overwrite && File.Exists(path) && IsReparsePoint(path))
            {
                // Never write through a planted marker symlink.
                return;
            }

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
