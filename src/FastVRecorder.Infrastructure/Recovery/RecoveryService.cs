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

    public RecoveryReport RecoverAll(CancellationToken cancellationToken = default, bool retryUnrecoverable = false)
    {
        // One coordinator per process: the startup scan and the user Retry Recovery must never
        // recover the same partial twice (which would publish duplicate, independently named outputs).
        lock (_runLock)
        {
            var report = new RecoveryReport();
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _journal.Snapshot())
            {
                var part = entry.PartPath;
                if (string.IsNullOrWhiteSpace(part) ||
                    !part.EndsWith(FileNamer.PartSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // The journal is app-owned, but never let a damaged/edited entry turn an
                    // arbitrary file into a recovery source (and a sibling output).
                    _log.Warn("Ignoring a journal entry that does not name a .wav.part file: " + part);
                    continue;
                }

                string full;
                try { full = Path.GetFullPath(part); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    _log.Warn("Ignoring a journal entry with an invalid path: " + part);
                    continue;
                }

                if (File.Exists(full))
                {
                    candidates.Add(full);
                }
                else
                {
                    // The volume may simply be disconnected; keep the journal so reconnecting retries.
                    _log.Warn("Journal part is currently unavailable: " + full);
                }
            }

            foreach (var folder in EnumerateFolders())
            {
                ScanFolder(folder, candidates);
            }

            // Group by the directory that recovery would publish into. A cross-session exclusive
            // lease on that directory serializes every coordinator sharing the folder, including
            // a second portable installation and another Windows session.
            var work = candidates
                .Where(p => _isActivePart?.Invoke(p) != true)
                .Select(p => (Part: p, Directory: TryGetDirectory(p)))
                .Where(x => x.Directory is not null)
                .OrderBy(x => x.Part, StringComparer.OrdinalIgnoreCase)
                .GroupBy(x => x.Directory!, StringComparer.OrdinalIgnoreCase);

            foreach (var folder in work)
            {
                if (cancellationToken.IsCancellationRequested) break;
                using var lease = TryAcquireFolderLease(folder.Key, out var leaseError);
                if (lease is null)
                {
                    var message = "Recovery for " + folder.Key + " was skipped: exclusive folder ownership could not be acquired. " + leaseError;
                    _log.Error(message);
                    foreach (var item in folder)
                    {
                        report.Failed.Add(new RecoveryFailure { Path = item.Part, Error = message });
                    }

                    continue;
                }

                foreach (var item in folder)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var part = item.Part;
                    if (_isActivePart?.Invoke(part) == true)
                    {
                        // A recording that started before the scan finished owns this part.
                        continue;
                    }

                    if (IsReparsePoint(part))
                    {
                        _log.Warn("Refusing to recover a partial file that is itself a reparse point: " + part);
                        report.Failed.Add(new RecoveryFailure
                        {
                            Path = part,
                            Error = "The partial file is a link/reparse point; recovery will not follow it.",
                        });
                        continue;
                    }

                    var ownership = ProbePartOwnership(part);
                    if (ownership != PartOwnership.Free)
                    {
                        // OS-enforced, so a paused or buffered writer that has not grown the file
                        // recently is still recognized. Never snapshot a held file.
                        _log.Warn(ownership == PartOwnership.Held
                            ? "Skipping a partial recording held open by a live writer: " + part
                            : "Skipping a partial recording whose ownership cannot be verified: " + part);
                        if (ownership == PartOwnership.Unknown)
                        {
                            // A live writer is expected and silent; an unverifiable owner is a
                            // real inability to protect the file and must be visible.
                            report.Failed.Add(new RecoveryFailure
                            {
                                Path = part,
                                Error = "Recovery was skipped because the partial file's ownership could not be verified; it will be retried.",
                            });
                        }

                        continue;
                    }

                    report.Scanned++;
                    try
                    {
                        var recovered = RecoverOne(part, report, retryUnrecoverable);
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
            }

            if (report.Recovered.Count > 0)
            {
                _log.Info($"Recovered {report.Recovered.Count} partial recording(s).");
            }

            return report;
        }
    }

    private static string? TryGetDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// An OS-enforced lease on the folder that will receive recovered output. A named mutex was
    /// not sufficient: creating a Global\ object can be denied by session privileges, and silently
    /// scanning without it is exactly the duplicate-publication race this must prevent.
    /// </summary>
    private static FolderLease? TryAcquireFolderLease(string directory, out string error)
    {
        error = string.Empty;
        try
        {
            var path = Path.Combine(directory, ".fastvrecorder-recovery.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new FolderLease(stream, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    private sealed class FolderLease : IDisposable
    {
        private FileStream? _stream;
        private readonly string _path;

        public FolderLease(FileStream stream, string path)
        {
            _stream = stream;
            _path = path;
        }

        public void Dispose()
        {
            var stream = _stream;
            _stream = null;
            stream?.Dispose();
            try { File.Delete(_path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private enum PartOwnership
    {
        Free,
        Held,
        Unknown,
    }

    /// <summary>
    /// Tests for a live writer with Windows sharing rules instead of a length/time heuristic.
    /// The recorder keeps every part open with FileShare.Read while it writes, so a FileShare.None
    /// open fails for a live writer even when it is paused, buffered or stalled on slow storage.
    /// </summary>
    private static PartOwnership ProbePartOwnership(string partPath)
    {
        try
        {
            using var probe = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return PartOwnership.Free;
        }
        catch (FileNotFoundException)
        {
            return PartOwnership.Free;
        }
        catch (DirectoryNotFoundException)
        {
            return PartOwnership.Free;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return PartOwnership.Held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PartOwnership.Unknown;
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

    private RecoveredItem? RecoverOne(string partPath, RecoveryReport report, bool retryUnrecoverable)
    {
        if (!File.Exists(partPath))
        {
            // The owner finalized and renamed it between the scan and this attempt.
            _log.Debug("Partial file disappeared before recovery: " + partPath);
            return null;
        }

        var basePath = partPath.EndsWith(FileNamer.PartSuffix, StringComparison.OrdinalIgnoreCase)
            ? partPath[..^FileNamer.PartSuffix.Length]
            : partPath;
        var metaPath = basePath + FileNamer.MetaSuffix;
        var markerPath = basePath + ".recovered";
        var unrecoverableMarker = basePath + ".unrecoverable.json";

        var metadata = TryReadMetadata(metaPath);
        var directory = Path.GetDirectoryName(partPath)!;
        var stem = Path.GetFileNameWithoutExtension(basePath);

        // Receipts are consulted before any parsing: a structurally damaged file must not be
        // re-read and re-classified on every launch, and an explicit Retry must bypass the receipt.
        // A verified recovery claim (canonical existing marker/metadata output) keeps the file in
        // the normal flow so a stale receipt can never hide the need to index a recovered output.
        if (!retryUnrecoverable && File.Exists(unrecoverableMarker) &&
            !HasVerifiedRecoveryClaim(markerPath, metadata, directory, stem) &&
            UnrecoverableMarkerStillApplies(unrecoverableMarker, partPath))
        {
            _journal.Remove(partPath);
            return null;
        }

        if (retryUnrecoverable && File.Exists(unrecoverableMarker))
        {
            _log.Info("Explicit retry re-evaluates the unrecoverable receipt: " + partPath);
        }

        WavInfo? info;
        try
        {
            if (!WavHeader.TryParse(partPath, out info, out var headerError) || info is null)
            {
                Fail(partPath, "Cannot read the stored WAV header: " + headerError, unrecoverableMarker, report, permanent: true);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partially readable volume is transient; never classify the source as corrupt.
            Fail(partPath, "The partial file is currently unreadable: " + ex.Message, unrecoverableMarker, report, permanent: false);
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

        var outputPath = Path.Combine(directory, stem + "_RECOVERED.wav");
        var partLength = TryGetFileLength(partPath);
        var created = metadata?.StartedAtUtc.ToLocalTime() ?? File.GetCreationTime(partPath);

        if (File.Exists(markerPath))
        {
            var marker = TryReadMarker(markerPath);
            if (marker is not null &&
                IsTrustedOutput(marker.RecoveredPath, partPath, directory, stem, format, dataBytes,
                    partLength, marker.SourcePart, marker.SourceLength, marker.DataSha256))
            {
                _log.Debug("Partial already recovered: " + partPath);
                if (EnsureIndexed(marker.RecoveredPath!, partPath, created, dataBytes, format, report))
                {
                    _journal.Remove(partPath);
                }

                // The source produced a verified recovered output; any earlier failure receipt
                // is stale even when indexing still has to be retried.
                TryDelete(unrecoverableMarker);
                return null;
            }

            _log.Warn("Recovery marker is missing, unrelated or unverified; rebuilding the recovered WAV: " + partPath);
        }

        if (metadata?.RecoveredAtUtc is not null && metadata.RecoveredPath is not null &&
            IsTrustedOutput(metadata.RecoveredPath, partPath, directory, stem, format, dataBytes,
                partLength, metadata.PartPath, metadata.SourceLength, metadata.SourceDataSha256))
        {
            if (EnsureIndexed(metadata.RecoveredPath, partPath, created, dataBytes, format, report))
            {
                _journal.Remove(partPath);
            }

            TryDelete(unrecoverableMarker);
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

        // Adopt any canonical sibling left by an interrupted publication, including a
        // collision-numbered `_RECOVERED_001.wav`; comparing PCM identity, never the name alone.
        var adoptedPath = TryAdoptExistingOutput(directory, stem, format, dataBytes, expectedPcmSha256);
        string? outputPcmSha256 = null;
        long written;
        var createdOutput = false;
        if (adoptedPath is not null)
        {
            outputPath = adoptedPath;
            outputPcmSha256 = expectedPcmSha256;
            written = dataBytes;
            _log.Info("Adopting existing verified recovery output: " + outputPath);
        }
        else
        {
            if (File.Exists(outputPath) || IsReparsePoint(outputPath))
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
                    // The source header was readable but produced no frames; treat it as
                    // structural, because retrying the same bytes cannot succeed.
                    Fail(partPath, "Copying the stored audio produced no frames.", unrecoverableMarker, report, permanent: true);
                    return null;
                }

                if (!WavVerifier.Verify(temporary, format, written, out var verifyError))
                {
                    // The source parsed and hashed; a bad temporary is a destination-side problem.
                    Fail(partPath, "Recovered WAV failed verification: " + verifyError, unrecoverableMarker, report, permanent: false);
                    return null;
                }

                if (!CanReadAudio(temporary, out var playError))
                {
                    Fail(partPath, "Recovered WAV cannot be read back: " + playError, unrecoverableMarker, report, permanent: false);
                    return null;
                }

                var temporaryPcmSha256 = TryComputePcmSha256(temporary, WavHeader.DataOffsetOf(temporary), written);
                if (!string.Equals(temporaryPcmSha256, expectedPcmSha256, StringComparison.Ordinal))
                {
                    Fail(partPath, "Recovered WAV does not carry the original audio payload.", unrecoverableMarker, report, permanent: false);
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
                createdOutput = true;
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
            Fail(partPath, "Recovered WAV failed final verification: " + resultVerifyError, unrecoverableMarker, report, permanent: false);
            if (createdOutput) TryDelete(outputPath);
            return null;
        }

        if (!CanReadAudio(outputPath, out var resultReadError))
        {
            Fail(partPath, "Recovered WAV cannot be read back: " + resultReadError, unrecoverableMarker, report, permanent: false);
            if (createdOutput) TryDelete(outputPath);
            return null;
        }

        if (outputPcmSha256 is null ||
            !string.Equals(outputPcmSha256, expectedPcmSha256, StringComparison.Ordinal))
        {
            Fail(partPath, "Recovered WAV identity changed before publication.", unrecoverableMarker, report, permanent: false);
            if (createdOutput) TryDelete(outputPath);
            return null;
        }

        var duration = TimeSpan.FromSeconds((double)written / format.AverageBytesPerSecond);
        var size = new FileInfo(outputPath).Length;

        // Audio publication is complete at this point. Indexing and receipts decide only whether
        // the transaction can be closed, never whether the recovered file is kept.
        var indexed = EnsureIndexed(outputPath, partPath, created, written, format, report);
        // Publication is verified; the failure receipt (if any) no longer describes this part.
        TryDelete(unrecoverableMarker);

        var metaWritten = false;
        if (metadata is not null)
        {
            metadata.RecoveredAtUtc = DateTime.UtcNow;
            metadata.RecoveredPath = outputPath;
            metadata.RecoveryError = null;
            metadata.SourceLength = partLength;
            metadata.SourceDataSha256 = outputPcmSha256;
            metaWritten = TryWriteJson(metaPath, metadata, overwrite: true);
        }

        var markerWritten = TryWriteJson(markerPath, new
        {
            recoveredPath = outputPath,
            recoveredAtUtc = DateTime.UtcNow,
            sourcePart = partPath,
            format = format.Describe(),
            dataBytes = written,
            sourceLength = partLength,
            dataSha256 = outputPcmSha256,
        }, overwrite: true);

        if (indexed && (markerWritten || metaWritten))
        {
            _journal.Remove(partPath);
        }
        else
        {
            var reason = !indexed
                ? "the History index could not be saved"
                : "neither recovery receipt could be written";
            _log.Error($"Recovered audio is verified at {outputPath}, but {reason}; the journal was kept for retry.");
            report.Failed.Add(new RecoveryFailure
            {
                Path = partPath,
                Error = $"Recovered audio is saved, but {reason}. It will be retried on the next launch.",
            });
        }

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
    /// Adds the verified recovered file to History when it is missing. Returns true when the index
    /// already contains it or the save succeeded; false means a durable retry is still required.
    /// </summary>
    private bool EnsureIndexed(
        string outputPath,
        string partPath,
        DateTime created,
        long dataBytes,
        TargetFormat format,
        RecoveryReport report)
    {
        if (_history.Snapshot().Any(e => string.Equals(e.Path, outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            // The entry may exist only in memory after a failed save; retry the write before
            // closing the recovery transaction.
            return _history.LastSaveError is null || _history.TrySave();
        }

        var entry = new HistoryEntry
        {
            Path = outputPath,
            CreatedLocal = created,
            DurationSeconds = (double)dataBytes / format.AverageBytesPerSecond,
            SizeBytes = TryGetFileLength(outputPath) ?? 0,
            Format = "WAV",
            Recovered = true,
            Note = "Recovered from " + Path.GetFileName(partPath) + ".",
        };

        if (_history.TryAdd(entry))
        {
            return true;
        }

        var error = _history.LastSaveError ?? "the index write failed";
        _log.Error("Recovered audio was published but History could not be saved: " + outputPath + " - " + error);
        report.Failed.Add(new RecoveryFailure
        {
            Path = partPath,
            Error = "Recovered audio is saved, but History could not be updated (" + error + "). Indexing will be retried on the next launch.",
        });
        return false;
    }

    /// <summary>
    /// Finds a canonical `_RECOVERED*.wav` sibling whose PCM payload is exactly this part's
    /// payload. This is what makes a crash between publishing and committing the marker harmless.
    /// </summary>
    private string? TryAdoptExistingOutput(
        string directory,
        string stem,
        TargetFormat format,
        long dataBytes,
        string expectedPcmSha256)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, stem + "_RECOVERED*.wav", SearchOption.TopDirectoryOnly).Take(100).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var candidate in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsCanonicalRecoveredOutput(candidate, directory, stem)) continue;
            if (IsReparsePoint(candidate)) continue;
            if (!WavVerifier.Verify(candidate, format, dataBytes, out _)) continue;
            if (!CanReadAudio(candidate, out _)) continue;

            var actual = TryComputePcmSha256(candidate, WavHeader.DataOffsetOf(candidate), dataBytes);
            if (string.Equals(actual, expectedPcmSha256, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the part carries a verified claim of a recovered output: the marker parses and
    /// names a canonical existing sibling, or metadata names one. A merely present/stale marker or
    /// metadata field is not a claim and must not defeat a matching unrecoverable receipt.
    /// </summary>
    private static bool HasVerifiedRecoveryClaim(
        string markerPath,
        RecoveryMetadata? metadata,
        string directory,
        string stem)
    {
        if (File.Exists(markerPath))
        {
            var marker = TryReadMarker(markerPath);
            if (IsExistingCanonicalOutput(marker?.RecoveredPath, directory, stem)) return true;
        }

        return IsExistingCanonicalOutput(metadata?.RecoveredPath, directory, stem);
    }

    private static bool IsExistingCanonicalOutput(string? claimedPath, string directory, string stem)
    {
        if (string.IsNullOrWhiteSpace(claimedPath)) return false;
        if (!IsCanonicalRecoveredOutput(claimedPath, directory, stem)) return false;
        if (IsReparsePoint(claimedPath)) return false;
        return File.Exists(claimedPath);
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

    /// <summary>
    /// The receipt suppresses repeated work only while it still describes the same bytes. A
    /// malformed or identity-less marker is re-evaluated once, then rewritten with identity.
    /// </summary>
    private bool UnrecoverableMarkerStillApplies(string markerPath, string partPath)
    {
        var prior = TryReadUnrecoverable(markerPath);
        if (prior is null)
        {
            _log.Warn("A malformed unrecoverable receipt is advisory; re-evaluating the partial file: " + markerPath);
            return false;
        }

        if (prior.SourceLength is null || string.IsNullOrWhiteSpace(prior.SourceSampleSha256))
        {
            _log.Warn("An unrecoverable receipt without source identity is stale; re-evaluating: " + markerPath);
            return false;
        }

        var current = TryComputeSourceFingerprint(partPath);
        if (current is null)
        {
            // The source is momentarily unreadable (disconnected/busy storage); let the normal
            // recovery path report a transient failure instead of hiding it behind the receipt.
            return false;
        }

        if (prior.SourceLength.Value != current.Length ||
            !string.Equals(prior.SourceSampleSha256, current.SampleSha256, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(prior.SourceHeaderSha256) &&
             !string.Equals(prior.SourceHeaderSha256, current.HeaderSha256, StringComparison.OrdinalIgnoreCase)) ||
            (prior.SourceLastWriteUtcTicks is not null && prior.SourceLastWriteUtcTicks.Value != current.LastWriteUtcTicks))
        {
            _log.Warn("The unrecoverable receipt no longer matches the partial file; retrying recovery: " + markerPath);
            return false;
        }

        return true;
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

    private sealed record SourceFingerprint(long Length, long LastWriteUtcTicks, string SampleSha256, string HeaderSha256);

    /// <summary>
    /// A bounded identity for a partial file: length, a hash of the first and last 1 MiB, and a
    /// hash of the header bytes. Enough to notice a repaired header or rewritten audio without
    /// hashing multi-gigabyte files on every launch.
    /// </summary>
    private static SourceFingerprint? TryComputeSourceFingerprint(string path)
    {
        const int chunk = 1 << 20;
        const int headerBytes = 64;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var length = stream.Length;
            if (length <= 0) return null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var lengthBytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
            hash.AppendData(lengthBytes);

            var firstCount = (int)Math.Min(chunk, length);
            var buffer = new byte[Math.Max(chunk, firstCount)];
            stream.Position = 0;
            ReadExactly(stream, buffer, firstCount);
            hash.AppendData(buffer, 0, firstCount);

            var headerCount = (int)Math.Min(headerBytes, length);
            using var headerHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            headerHash.AppendData(buffer, 0, headerCount);

            var lastStart = Math.Max(firstCount, length - chunk);
            var lastCount = (int)(length - lastStart);
            if (lastCount > 0)
            {
                stream.Position = lastStart;
                ReadExactly(stream, buffer, lastCount);
                hash.AppendData(buffer, 0, lastCount);
            }

            return new SourceFingerprint(
                length,
                File.GetLastWriteTimeUtc(path).Ticks,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToHexString(headerHash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var done = 0;
        while (done < count)
        {
            var read = stream.Read(buffer, done, count - done);
            if (read <= 0) throw new EndOfStreamException();
            done += read;
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

        var fingerprint = TryComputeSourceFingerprint(partPath);
        if (fingerprint is null)
        {
            // Without a bounded identity the receipt would hide audio forever; keep the part
            // retryable instead of writing an unidentified permanent marker.
            _log.Warn("The partial file's identity could not be read; no permanent receipt was written for " + partPath);
            return;
        }

        TryWriteJson(markerPath, new
        {
            error,
            failedAtUtc = DateTime.UtcNow,
            note = "The damaged .wav.part was kept for diagnosis.",
            sourceLength = fingerprint.Length,
            sourceLastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
            sourceSampleSha256 = fingerprint.SampleSha256,
            sourceHeaderSha256 = fingerprint.HeaderSha256,
            fingerprintVersion = 1,
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

    private sealed record LegacyUnrecoverable(
        long? SourceLength,
        string? SourceSampleSha256,
        string? SourceHeaderSha256,
        long? SourceLastWriteUtcTicks);

    private static LegacyUnrecoverable? TryReadUnrecoverable(string path)
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
            return new LegacyUnrecoverable(
                ReadLong("sourceLength"),
                ReadString("sourceSampleSha256"),
                ReadString("sourceHeaderSha256"),
                ReadLong("sourceLastWriteUtcTicks"));
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

    private static bool TryWriteJson(string path, object value, bool overwrite = false)
    {
        try
        {
            if (overwrite && File.Exists(path) && IsReparsePoint(path))
            {
                // Never write through a planted marker symlink.
                return false;
            }

            if (!overwrite && File.Exists(path)) return true;
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Markers are advisory, but recovery still needs to know whether the receipt landed.
            return false;
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
