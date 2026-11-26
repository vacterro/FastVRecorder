using System.Security.Cryptography;
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

public sealed class RecoveryHardeningTests : TempDirectoryTest
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

    private static string CreatePartial(string folder, string stem, int dataBytes, out string metaPath)
    {
        var format = TargetFormat.Standard;
        var part = Path.Combine(folder, stem + ".wav.part");
        using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(WavHeader.Build(format, 0));
            var data = new byte[dataBytes];
            new Random(9).NextBytes(data);
            stream.Write(data);
        }

        var intended = Path.Combine(folder, stem + ".wav");
        metaPath = intended + FileNamer.MetaSuffix;
        File.WriteAllText(metaPath, JsonSerializer.Serialize(new RecoveryMetadata
        {
            RecordingId = "hardening",
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
        return part;
    }

    [Fact]
    public void StaleRecoveredMarkerWithoutOutput_RebuildsThePart()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "stale-marker", 4800, out _);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        var output = Path.Combine(harness.Folder, "stale-marker_RECOVERED.wav");
        File.WriteAllText(basePath + ".recovered",
            JsonSerializer.Serialize(new { recoveredPath = output, recoveredAtUtc = DateTime.UtcNow }));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(part));
        Assert.Contains(harness.History.Snapshot(), entry => entry.Recovered);
    }

    [Fact]
    public void RecoveryNeverDeletesAZeroByteFileReferencedByMetadata()
    {
        var harness = CreateHarness();
        var sentinel = Path.Combine(Root, "unrelated-zero-byte-sentinel.bin");
        File.WriteAllBytes(sentinel, Array.Empty<byte>());

        var part = CreatePartial(harness.Folder, "sentinel-test", 4800, out var metaPath);
        var metadata = JsonSerializer.Deserialize<RecoveryMetadata>(File.ReadAllText(metaPath))!;
        metadata.IntendedOutputPath = sentinel;
        File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.True(File.Exists(sentinel));
        Assert.Empty(File.ReadAllBytes(sentinel));
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void VerifiedRecoveredOutputIsAdoptedInsteadOfDuplicated()
    {
        var harness = CreateHarness();
        const string stem = "crash-between-output-and-history";
        var part = CreatePartial(harness.Folder, stem, 4800, out _);
        var adopted = Path.Combine(harness.Folder, stem + "_RECOVERED.wav");
        using (var source = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var writer = WavStreamWriter.CreateNew(adopted, TargetFormat.Standard, 4800 + 2))
        {
            source.Position = WavHeader.HeaderLength(TargetFormat.Standard);
            var data = new byte[4800];
            source.ReadExactly(data);
            writer.Write(data);
            writer.Complete();
        }

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var first = service.RecoverAll();
        var second = service.RecoverAll();

        Assert.Single(first.Recovered);
        Assert.Empty(second.Recovered);
        Assert.Equal(adopted, first.Recovered[0].Path);
        Assert.Empty(Directory.GetFiles(harness.Folder, "*_RECOVERED_001.wav"));
        Assert.Single(harness.History.Snapshot());
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void LockedPartialIsTransientKeepsJournalAndRetries()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "locked", 4800, out var metaPath);
        harness.Journal.Add(new JournalEntry { PartPath = part, MetaPath = metaPath, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        using (new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedReport = service.RecoverAll();
            Assert.Empty(lockedReport.Recovered);
            Assert.NotEmpty(lockedReport.Failed);
            Assert.Single(harness.Journal.Snapshot());
            Assert.False(File.Exists(Path.Combine(harness.Folder, "locked.wav.unrecoverable.json")));
        }

        var retry = service.RecoverAll();
        Assert.Single(retry.Recovered);
        Assert.Empty(harness.Journal.Snapshot());
    }

    [Fact]
    public void MissingJournalPartIsRetainedForAReconnectedVolume()
    {
        var harness = CreateHarness();
        var missing = Path.Combine(Root, "disconnected-volume", "lost.wav.part");
        harness.Journal.Add(new JournalEntry { PartPath = missing, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Single(harness.Journal.Snapshot());
    }

    [Fact]
    public void CopyWholeFramesLosesNothingAcrossShortNonAlignedReads()
    {
        var format = TargetFormat.Standard;
        const int frames = 1000;
        var data = new byte[format.BlockAlign * frames];
        new Random(7).NextBytes(data);
        using var source = new ShortReadStream(data, maxPerRead: 7);
        var path = Path.Combine(Root, "short-read.wav");
        long written;
        using (var writer = WavStreamWriter.CreateNew(path, format, data.Length + format.BlockAlign))
        {
            written = RecoveryService.CopyWholeFrames(source, writer, data.Length);
            writer.Complete();
        }

        Assert.Equal(data.Length, written);
        var actual = new byte[data.Length];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
        file.Position = WavHeader.HeaderLength(format);
        file.ReadExactly(actual);
        Assert.Equal(data, actual);
    }

    private static string ReadPcmHash(string path, int dataBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = WavHeader.HeaderLength(TargetFormat.Standard);
        var data = new byte[dataBytes];
        stream.ReadExactly(data);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static string PartPcmHash(string part, int dataBytes) => ReadPcmHash(part, dataBytes);

    [Fact]
    public void MarkerReferencingUnrelatedSameLengthWav_IsRebuiltFromTheOriginalAudio()
    {
        var harness = CreateHarness();
        const string stem = "unrelated-marker";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes, out _);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        var claimed = Path.Combine(harness.Folder, stem + "_RECOVERED.wav");
        var different = new byte[dataBytes];
        new Random(11).NextBytes(different);
        using (var writer = WavStreamWriter.CreateNew(claimed, TargetFormat.Standard, dataBytes + 2))
        {
            writer.Write(different);
            writer.Complete();
        }

        File.WriteAllText(basePath + ".recovered", JsonSerializer.Serialize(new
        {
            recoveredPath = claimed,
            recoveredAtUtc = DateTime.UtcNow,
            sourcePart = part,
            sourceLength = new FileInfo(part).Length,
            dataBytes,
            dataSha256 = Convert.ToHexString(SHA256.HashData(different)).ToLowerInvariant(),
        }));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.NotEqual(claimed, report.Recovered[0].Path);
        Assert.Equal(PartPcmHash(part, dataBytes), ReadPcmHash(report.Recovered[0].Path, dataBytes));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(different)).ToLowerInvariant(),
            ReadPcmHash(claimed, dataBytes));

        // The verified rebuild must repair the advisory marker: a stale marker that kept naming
        // the rejected file would make every later launch publish another duplicate.
        var rewrittenMarker = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(basePath + ".recovered"));
        Assert.Equal(report.Recovered[0].Path, rewrittenMarker.GetProperty("recoveredPath").GetString());

        var outputCount = Directory.GetFiles(harness.Folder, "*_RECOVERED*.wav").Length;
        for (var run = 0; run < 2; run++)
        {
            var second = service.RecoverAll();
            Assert.Empty(second.Recovered);
            Assert.Equal(outputCount, Directory.GetFiles(harness.Folder, "*_RECOVERED*.wav").Length);
            Assert.Single(harness.History.Snapshot());
        }
    }

    [Fact]
    public void MarkerReferencingAnExternalPath_IsIgnoredAndThePartIsRebuilt()
    {
        var harness = CreateHarness();
        const string stem = "external-marker";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes, out _);
        var basePath = part[..^FileNamer.PartSuffix.Length];
        var external = Path.Combine(Root, "elsewhere_RECOVERED.wav");
        using (var source = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var writer = WavStreamWriter.CreateNew(external, TargetFormat.Standard, dataBytes + 2))
        {
            source.Position = WavHeader.HeaderLength(TargetFormat.Standard);
            var data = new byte[dataBytes];
            source.ReadExactly(data);
            writer.Write(data);
            writer.Complete();
        }

        File.WriteAllText(basePath + ".recovered", JsonSerializer.Serialize(new
        {
            recoveredPath = external,
            recoveredAtUtc = DateTime.UtcNow,
            dataBytes,
            dataSha256 = PartPcmHash(part, dataBytes),
        }));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.NotEqual(external, report.Recovered[0].Path);
        Assert.StartsWith(harness.Folder, report.Recovered[0].Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PartPcmHash(part, dataBytes), ReadPcmHash(report.Recovered[0].Path, dataBytes));
    }

    [Fact]
    public void MarkerWithMatchingPayloadIdentity_SuppressesDuplicateRecovery()
    {
        var harness = CreateHarness();
        const string stem = "identity-marker";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes, out var metaPath);
        harness.Journal.Add(new JournalEntry { PartPath = part, MetaPath = metaPath, StartedUtc = DateTime.UtcNow });
        var basePath = part[..^FileNamer.PartSuffix.Length];
        var output = Path.Combine(harness.Folder, stem + "_RECOVERED.wav");
        using (var source = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var writer = WavStreamWriter.CreateNew(output, TargetFormat.Standard, dataBytes + 2))
        {
            source.Position = WavHeader.HeaderLength(TargetFormat.Standard);
            var data = new byte[dataBytes];
            source.ReadExactly(data);
            writer.Write(data);
            writer.Complete();
        }

        File.WriteAllText(basePath + ".recovered", JsonSerializer.Serialize(new
        {
            recoveredPath = output,
            recoveredAtUtc = DateTime.UtcNow,
            sourcePart = part,
            sourceLength = new FileInfo(part).Length,
            dataBytes,
            dataSha256 = PartPcmHash(part, dataBytes),
        }));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Empty(Directory.GetFiles(harness.Folder, "*_RECOVERED_001.wav"));
        Assert.Empty(harness.Journal.Snapshot());
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void MetadataReferencingAnUnrelatedWav_IsAdvisoryOnly()
    {
        var harness = CreateHarness();
        const string stem = "metadata-advisory";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes, out var metaPath);
        var unrelated = Path.Combine(harness.Folder, "unrelated.wav");
        var different = new byte[dataBytes];
        new Random(12).NextBytes(different);
        using (var writer = WavStreamWriter.CreateNew(unrelated, TargetFormat.Standard, dataBytes + 2))
        {
            writer.Write(different);
            writer.Complete();
        }

        var metadata = JsonSerializer.Deserialize<RecoveryMetadata>(File.ReadAllText(metaPath))!;
        metadata.RecoveredAtUtc = DateTime.UtcNow;
        metadata.RecoveredPath = unrelated;
        metadata.SourceLength = new FileInfo(part).Length;
        metadata.SourceDataSha256 = Convert.ToHexString(SHA256.HashData(different)).ToLowerInvariant();
        File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata));

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.NotEqual(unrelated, report.Recovered[0].Path);
        Assert.Equal(PartPcmHash(part, dataBytes), ReadPcmHash(report.Recovered[0].Path, dataBytes));
    }

    [Fact]
    public void ScanDoesNotFollowDirectoryJunctions()
    {
        var harness = CreateHarness();
        var external = Path.Combine(Root, "external-volume");
        Directory.CreateDirectory(external);
        var externalPart = Path.Combine(external, "linked.wav.part");
        using (var stream = new FileStream(externalPart, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(WavHeader.Build(TargetFormat.Standard, 0));
            stream.Write(new byte[4800]);
        }

        var junction = Path.Combine(harness.Folder, "linked-folder");
        try
        {
            Directory.CreateSymbolicLink(junction, external);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // symlink privilege unavailable on this host
        }

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Empty(Directory.GetFiles(external, "*_RECOVERED.wav"));
        Assert.Empty(Directory.GetFiles(harness.Folder, "*_RECOVERED.wav"));
        Assert.True(File.Exists(externalPart));
    }

    [Fact]
    public void ConcurrentRecoverAll_ProducesExactlyOneRecoveredOutput()
    {
        var harness = CreateHarness();
        const string stem = "concurrent";
        const int dataBytes = 4800;
        var part = CreatePartial(harness.Folder, stem, dataBytes, out var metaPath);
        harness.Journal.Add(new JournalEntry { PartPath = part, MetaPath = metaPath, StartedUtc = DateTime.UtcNow });
        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);

        var reports = new RecoveryReport[2];
        var threads = new[]
        {
            new Thread(() => reports[0] = service.RecoverAll()),
            new Thread(() => reports[1] = service.RecoverAll()),
        };
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Single(Directory.GetFiles(harness.Folder, "*_RECOVERED.wav"));
        Assert.Equal(1, reports.Sum(r => r.Recovered.Count));
        Assert.True(File.Exists(part));
    }

    private sealed class ShortReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxPerRead;
        private int _position;

        public ShortReadStream(byte[] data, int maxPerRead)
        {
            _data = data;
            _maxPerRead = maxPerRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(count, _maxPerRead), _data.Length - _position);
            if (take <= 0) return 0;
            Array.Copy(_data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
