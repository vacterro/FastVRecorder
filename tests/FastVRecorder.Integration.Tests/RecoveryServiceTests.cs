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

public sealed class RecoveryServiceTests : TempDirectoryTest
{
    private sealed record Harness(
        PortableRoot Paths,
        AppSettings Settings,
        HistoryStore History,
        RecordingJournal Journal,
        string Folder);

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

    private static string CreatePartial(string folder, string stem, TargetFormat format, int dataBytes, bool writeMetadata, out string metaPath, out string intendedPath)
    {
        var part = Path.Combine(folder, stem + ".wav.part");
        using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(WavHeader.Build(format, 0));
            var data = new byte[dataBytes];
            new Random(1234).NextBytes(data);
            stream.Write(data);
        }

        intendedPath = Path.Combine(folder, stem + ".wav");
        metaPath = intendedPath + FileNamer.MetaSuffix;
        if (writeMetadata)
        {
            var metadata = new RecoveryMetadata
            {
                RecordingId = "test",
                BaseName = stem,
                IntendedOutputPath = intendedPath,
                PartPath = part,
                Format = format.Encoding.ToString(),
                SampleRate = format.SampleRate,
                Channels = format.Channels,
                BitsPerSample = format.BitsPerSample,
                BlockAlign = format.BlockAlign,
                ByteRate = format.AverageBytesPerSecond,
                StartedAtUtc = DateTime.UtcNow,
            };
            File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        return part;
    }

    [Fact]
    public void RecoversPlayableWaveKeepsPartialAndIndexesHistory()
    {
        var harness = CreateHarness();
        var format = TargetFormat.Standard;
        const string stem = "2026-01-02_03-04-05";
        var part = CreatePartial(harness.Folder, stem, format, 9600, writeMetadata: true, out var metaPath, out var intendedPath);
        File.WriteAllBytes(intendedPath, Array.Empty<byte>()); // reservation placeholder
        harness.Journal.Add(new JournalEntry { PartPath = part, MetaPath = metaPath, FinalPath = intendedPath, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        var item = Assert.Single(report.Recovered);
        Assert.Contains("_RECOVERED", item.Path);
        Assert.True(File.Exists(item.Path));
        Assert.True(WavVerifier.Verify(item.Path, format, 9600, out var error), error);
        Assert.Equal(TimeSpan.FromSeconds(0.1), item.Duration);
        Assert.True(File.Exists(part));
        Assert.False(File.Exists(intendedPath));
        Assert.True(File.Exists(Path.ChangeExtension(part, null) + ".recovered"));
        Assert.Empty(harness.Journal.Snapshot());

        var history = Assert.Single(harness.History.Snapshot());
        Assert.True(history.Recovered);
        Assert.Equal(item.Path, history.Path);
        Assert.NotNull(history.Note);
    }

    [Fact]
    public void SecondScan_DoesNotCreateDuplicateRecoveredFiles()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "2026-01-02_03-04-05", TargetFormat.Standard, 4800, false, out _, out _);
        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);

        Assert.Single(service.RecoverAll().Recovered);
        var before = Directory.GetFiles(harness.Folder, "*_RECOVERED.wav").Length;
        var second = service.RecoverAll();

        Assert.Empty(second.Recovered);
        Assert.Equal(before, Directory.GetFiles(harness.Folder, "*_RECOVERED.wav").Length);
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void RecoversFromHeaderAlone_WhenMetadataIsMissing()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "no-meta", TargetFormat.Standard, 4800, false, out _, out _);

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void JournalFindsPartialOutsideTheConfiguredFolder()
    {
        var harness = CreateHarness();
        var other = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(other);
        var part = CreatePartial(other, "external", TargetFormat.Standard, 4800, false, out _, out _);
        harness.Journal.Add(new JournalEntry { PartPath = part, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Single(report.Recovered);
        Assert.True(File.Exists(part));
    }

    [Fact]
    public void ActiveRecordingParts_AreNeverRecoveredOrRemovedFromTheJournal()
    {
        var harness = CreateHarness();
        var part = CreatePartial(harness.Folder, "live", TargetFormat.Standard, 4800, false, out _, out _);
        harness.Journal.Add(new JournalEntry { PartPath = part, StartedUtc = DateTime.UtcNow });

        var service = new RecoveryService(
            harness.Paths,
            harness.Journal,
            harness.History,
            NullLog.Instance,
            () => harness.Settings,
            _ => true);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Empty(report.Failed);
        Assert.True(File.Exists(part));
        Assert.Single(harness.Journal.Snapshot());
        Assert.Empty(Directory.GetFiles(harness.Folder, "*_RECOVERED.wav"));
    }

    [Fact]
    public void UnreadablePartial_IsReportedAndKeptWithMarker()
    {
        var harness = CreateHarness();
        var part = Path.Combine(harness.Folder, "broken.wav.part");
        File.WriteAllText(part, "this is not a wave file");

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Single(report.Failed);
        Assert.True(File.Exists(part));
        Assert.True(File.Exists(Path.Combine(harness.Folder, "broken.wav.unrecoverable.json")) || File.Exists(Path.Combine(harness.Folder, "broken.wav.unrecoverable.json")));
    }

    [Fact]
    public void PartialWithNoFrames_IsReportedNotDeleted()
    {
        var harness = CreateHarness();
        var part = Path.Combine(harness.Folder, "empty.wav.part");
        using (var stream = new FileStream(part, FileMode.CreateNew))
        {
            stream.Write(WavHeader.Build(TargetFormat.Standard, 0));
        }

        var service = new RecoveryService(harness.Paths, harness.Journal, harness.History, NullLog.Instance, () => harness.Settings);
        var report = service.RecoverAll();

        Assert.Empty(report.Recovered);
        Assert.Single(report.Failed);
        Assert.True(File.Exists(part));
    }
}
