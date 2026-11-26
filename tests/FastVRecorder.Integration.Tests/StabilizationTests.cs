using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.History;
using FastVRecorder.Core.Recovery;
using FastVRecorder.Core.Settings;
using FastVRecorder.Infrastructure.Recovery;
using Xunit;

namespace FastVRecorder.Integration.Tests;

/// <summary>SAIHANDOFF-4 recovery ownership, durable indexing and receipt identity regressions.</summary>
public sealed class StabilizationTests : TempDirectoryTest
{
    private sealed record Harness(PortableRoot Paths, AppSettings Settings, HistoryStore History, RecordingJournal Journal, string Folder);

    private Harness CreateHarness()
    {
        var paths = new PortableRoot(Root);
        var settings = new AppSettings();
        settings.Files.RecordingFolder = "Recordings";
        var history = new HistoryStore(paths.HistoryPath);
        history.Load();
        var journal = new RecordingJournal(paths.JournalPath);
        var folder = paths.ResolveRecordingFolder(settings.Files.RecordingFolder);
        Directory.CreateDirectory(folder);
        return new Harness(paths, settings, history, journal, folder);
    }

    private static string CreatePartial(string folder, string stem, int dataBytes, bool writeMetadata = true)
    {
        var format = TargetFormat.Standard;
        var part = Path.Combine(folder, stem + ".wav.part");
        using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(WavHeader.Build(format, 0));
            var data = new byte[dataBytes];
            new Random(17).NextBytes(data);
            stream.Write(data);
        }

        if (writeMetadata)
        {
            var intended = Path.Combine(folder, stem + ".wav");
            File.WriteAllText(intended + FileNamer.MetaSuffix, JsonSerializer.Serialize(new RecoveryMetadata
            {
                RecordingId = "stabilization",
                BaseName = stem,
                IntendedOutputPath = intended,
                PartPath = part,
                Format = format.Encoding.ToString(),
                SampleRate = format.SampleRate,
                Channels = format.Channels,
                BitsPerSample = format.BitsPerSample,
                BlockAlign = format.BlockAlign,
                ByteRate = format.AverageBytesPerSecond,
                StartedAtUtc = DateTime.UtcNow,
            }));
        }

        return part;
    }

    private static string WriteRecoveredCopy(string part, string outputPath, int dataBytes)
    {
        using var source = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var writer = WavStreamWriter.CreateNew(outputPath, TargetFormat.Standard, dataBytes + 2);
        source.Position = WavHeader.HeaderLength(TargetFormat.Standard);
        var data = new byte[dataBytes];
        source.ReadExactly(data);
        writer.Write(data);
        writer.Complete();
        return outputPath;
    }

    [Fact]
    public void HeldHistoryFileKeepsJournalAndIndexesOnRetry()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "history-locked", 4800);
        harness.Journal.Add(new JournalEntry { PartPath = part, StartedUtc = DateTime.UtcNow });

        Directory.CreateDirectory(harness.Paths.ConfigDir);
        File.WriteAllText(harness.Paths.HistoryPath, "[]");
        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);

        using (new FileStream(harness.Paths.HistoryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var first = service.RecoverAll();

            // The audio is published regardless of the index failure; the transaction stays open.
            var item = Assert.Single(first.Recovered);
            Assert.True(File.Exists(item.Path));
            Assert.Contains(first.Failed, failure => failure.Error.Contains("History", StringComparison.Ordinal));
            Assert.Single(harness.Journal.Snapshot());
            Assert.Contains(harness.History.Snapshot(), entry => entry.Path == item.Path);
        }

        var second = service.RecoverAll();
        Assert.Empty(second.Recovered);
        Assert.Empty(second.Failed);
        Assert.Empty(harness.Journal.Snapshot());
        Assert.Single(Directory.GetFiles(harness.Folder, "*_RECOVERED*.wav"));

        var onDisk = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(harness.Paths.HistoryPath))!;
        Assert.Contains(onDisk, entry => entry.Path.EndsWith("_RECOVERED.wav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NumberedRecoveredOutputFromInterruptedPublicationIsAdopted()
    {
        var harness = CreateHarness();
        const string stem = "interrupted";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes);
        var numbered = Path.Combine(harness.Folder, stem + "_RECOVERED_001.wav");
        WriteRecoveredCopy(part, numbered, dataBytes);

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        var item = Assert.Single(report.Recovered);
        Assert.Equal(numbered, item.Path);
        Assert.Single(Directory.GetFiles(harness.Folder, "*_RECOVERED*.wav"));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void MalformedUnrecoverableMarkerDoesNotHideRecoverableAudio()
    {
        var harness = CreateHarness();
        const string stem = "malformed-marker";
        var part = CreatePartial(harness.Folder, stem, 4800);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        File.WriteAllText(basePath + ".unrecoverable.json", "this is not json");

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.True(File.Exists(part));
        Assert.False(File.Exists(basePath + ".unrecoverable.json"));
    }

    [Fact]
    public void IdentityLessUnrecoverableMarkerIsReevaluatedOnce()
    {
        var harness = CreateHarness();
        const string stem = "legacy-marker";
        var part = CreatePartial(harness.Folder, stem, 4800);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        File.WriteAllText(basePath + ".unrecoverable.json",
            JsonSerializer.Serialize(new { error = "legacy", note = "no identity" }));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.False(File.Exists(basePath + ".unrecoverable.json"));
    }

    [Fact]
    public void RepairedHeaderAtTheSameLengthIsReevaluated()
    {
        var harness = CreateHarness();
        const string stem = "repaired";
        const int length = 5000;
        var part = Path.Combine(harness.Folder, stem + ".wav.part");
        File.WriteAllBytes(part, new byte[length]);
        var originalWrite = File.GetLastWriteTimeUtc(part);

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var first = service.RecoverAll();
        Assert.Empty(first.Recovered);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        Assert.True(File.Exists(basePath + ".unrecoverable.json"));

        // Repair the header in place without changing file size or timestamp.
        var dataBytes = length - WavHeader.HeaderLength(TargetFormat.Standard);
        using (var stream = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Write(WavHeader.Build(TargetFormat.Standard, dataBytes));
        }

        File.SetLastWriteTimeUtc(part, originalWrite);

        var second = service.RecoverAll();
        Assert.Single(second.Recovered);
        Assert.False(File.Exists(basePath + ".unrecoverable.json"));
    }

    [Fact]
    public void ExplicitRetryReevaluatesAMatchingUnrecoverableReceipt()
    {
        var harness = CreateHarness();
        const string stem = "forced-retry";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        var fingerprint = Fingerprint(part);
        File.WriteAllText(basePath + ".unrecoverable.json", JsonSerializer.Serialize(new
        {
            error = "misclassified",
            sourceLength = fingerprint.Length,
            sourceLastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
            sourceSampleSha256 = fingerprint.Sample,
            sourceHeaderSha256 = fingerprint.Header,
        }));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);

        // The receipt still matches: the ordinary scan intentionally does no repeated work.
        var ordinary = service.RecoverAll();
        Assert.Empty(ordinary.Recovered);

        harness.Journal.Add(new JournalEntry { PartPath = part, StartedUtc = DateTime.UtcNow });
        var forced = service.RecoverAll(retryUnrecoverable: true);
        Assert.Single(forced.Recovered);
        Assert.False(File.Exists(basePath + ".unrecoverable.json"));
    }

    [Fact]
    public void MatchingReceiptAvoidsRepeatedPermanentWork()
    {
        var harness = CreateHarness();
        var part = Path.Combine(harness.Folder, "permanent.wav.part");
        File.WriteAllBytes(part, new byte[5000]);
        var marker = part[..^FileNamer.PartSuffix.Length] + ".unrecoverable.json";

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        Assert.Empty(service.RecoverAll().Recovered);
        var receiptBefore = File.ReadAllBytes(marker);

        var second = service.RecoverAll();
        Assert.Empty(second.Recovered);
        Assert.Empty(second.Failed);
        Assert.Equal(receiptBefore, File.ReadAllBytes(marker));
    }

    [Fact]
    public void JournalEntryWithoutPartSuffixIsIgnored()
    {
        var harness = CreateHarness();
        var unrelated = Path.Combine(harness.Folder, "notes.txt");
        File.WriteAllText(unrelated, "not a recording");
        harness.Journal.Add(new JournalEntry { PartPath = unrelated, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Empty(report.Failed);
        Assert.False(File.Exists(unrelated + ".unrecoverable.json"));
        Assert.False(File.Exists(unrelated + ".recovered"));
        Assert.Single(harness.Journal.Snapshot());
    }

    [Fact]
    public void RelativeJournalAliasIsRemovedByTheRecoveryPath()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "alias", 4800);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, part);
        harness.Journal.Add(new JournalEntry { PartPath = relative, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.Empty(harness.Journal.Snapshot());
    }

    [Fact]
    public void JournalAcceptsCaseVariedPropertyNames()
    {
        var harness = CreateHarness();
        Directory.CreateDirectory(harness.Paths.ConfigDir);
        var part = Path.Combine(harness.Folder, "cased.wav.part");
        File.WriteAllText(harness.Paths.JournalPath,
            JsonSerializer.Serialize(new[] { new { partPath = part, format = "WAV" } }));

        var journal = new RecordingJournal(harness.Paths.JournalPath);
        Assert.Single(journal.Snapshot());
        Assert.Equal(part, journal.Snapshot()[0].PartPath);
    }

    [Fact]
    public void AnotherCoordinatorHoldingTheFolderLeaseBlocksRecovery()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "leased", 4800);
        var lockPath = Path.Combine(harness.Folder, ".fastvrecorder-recovery.lock");
        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);

        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = service.RecoverAll();
            Assert.Empty(blocked.Recovered);
            Assert.Contains(blocked.Failed, failure => failure.Error.Contains("exclusive folder ownership", StringComparison.Ordinal));
            Assert.Empty(Directory.GetFiles(harness.Folder, "*_RECOVERED.wav"));
        }

        var retry = service.RecoverAll();
        Assert.Single(retry.Recovered);
    }

    [Fact]
    public void MatchingReceiptDoesNotHideIndexingOfAnAlreadyRecoveredOutput()
    {
        var harness = CreateHarness();
        const string stem = "receipt-and-marker";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        var output = Path.Combine(harness.Folder, stem + "_RECOVERED.wav");
        WriteRecoveredCopy(part, output, dataBytes);
        var pcmHash = PcmSha256(output, dataBytes);
        File.WriteAllText(basePath + ".recovered", JsonSerializer.Serialize(new
        {
            recoveredPath = output,
            recoveredAtUtc = DateTime.UtcNow,
            sourcePart = part,
            sourceLength = new FileInfo(part).Length,
            dataBytes,
            dataSha256 = pcmHash,
        }));

        var fingerprint = Fingerprint(part);
        File.WriteAllText(basePath + ".unrecoverable.json", JsonSerializer.Serialize(new
        {
            error = "stale misclassification",
            sourceLength = fingerprint.Length,
            sourceLastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
            sourceSampleSha256 = fingerprint.Sample,
            sourceHeaderSha256 = fingerprint.Header,
        }));
        harness.Journal.Add(new JournalEntry { PartPath = part, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Empty(report.Failed);
        Assert.Single(Directory.GetFiles(harness.Folder, "*_RECOVERED*.wav"));
        Assert.Empty(harness.Journal.Snapshot());
        Assert.Contains(harness.History.Snapshot(), entry => entry.Path == output && entry.Recovered);
        Assert.False(File.Exists(basePath + ".unrecoverable.json"));
    }

    [Fact]
    public void SuccessfulRecoveryRemovesAStaleReceiptEvenWhenIndexingFails()
    {
        var harness = CreateHarness();
        const string stem = "receipt-cleanup";
        var part = CreatePartial(harness.Folder, stem, 4800);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        var fingerprint = Fingerprint(part);
        File.WriteAllText(basePath + ".unrecoverable.json", JsonSerializer.Serialize(new
        {
            error = "stale",
            sourceLength = fingerprint.Length,
            sourceLastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
            sourceSampleSha256 = fingerprint.Sample,
            sourceHeaderSha256 = fingerprint.Header,
        }));

        Directory.CreateDirectory(harness.Paths.ConfigDir);
        File.WriteAllText(harness.Paths.HistoryPath, "[]");
        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        using (new FileStream(harness.Paths.HistoryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = service.RecoverAll(retryUnrecoverable: true);
            Assert.Single(report.Recovered);
            Assert.Contains(report.Failed, failure => failure.Error.Contains("History", StringComparison.Ordinal));
            Assert.False(File.Exists(basePath + ".unrecoverable.json"));
        }
    }

    private static string PcmSha256(string path, int dataBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = WavHeader.HeaderLength(TargetFormat.Standard);
        var data = new byte[dataBytes];
        stream.ReadExactly(data);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static (long Length, long LastWriteUtcTicks, string Sample, string Header) Fingerprint(string path)
    {
        const int chunk = 1 << 20;
        const int headerBytes = 64;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hash.AppendData(lengthBytes);

        var firstCount = (int)Math.Min(chunk, length);
        var buffer = new byte[Math.Max(chunk, firstCount)];
        stream.Position = 0;
        stream.ReadExactly(buffer, 0, firstCount);
        hash.AppendData(buffer, 0, firstCount);

        using var header = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        header.AppendData(buffer, 0, (int)Math.Min(headerBytes, length));

        var lastStart = Math.Max(firstCount, length - chunk);
        var lastCount = (int)(length - lastStart);
        if (lastCount > 0)
        {
            stream.Position = lastStart;
            stream.ReadExactly(buffer, 0, lastCount);
            hash.AppendData(buffer, 0, lastCount);
        }

        return (length,
            File.GetLastWriteTimeUtc(path).Ticks,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(header.GetHashAndReset()).ToLowerInvariant());
    }
}
