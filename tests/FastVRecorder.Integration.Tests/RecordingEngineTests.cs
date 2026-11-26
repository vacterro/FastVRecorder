using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.History;
using FastVRecorder.Core.Recovery;
using FastVRecorder.Core.Settings;
using FastVRecorder.Infrastructure.Audio;
using FastVRecorder.Infrastructure.Recording;
using Xunit;

namespace FastVRecorder.Integration.Tests;

public sealed class RecordingEngineTests : TempDirectoryTest
{
    private static readonly SourceFormat Source = new(48000, 1, 16, IsFloat: false);

    private sealed record Harness(
        PortableRoot Paths,
        AppSettings Settings,
        RecordingEngine Engine,
        HistoryStore History,
        RecordingJournal Journal,
        string Folder);

    private Harness CreateHarness(FakeCaptureFactory? factory = null, string folder = "Recordings")
    {
        var paths = new PortableRoot(Root);
        var settings = new AppSettings();
        settings.Recording.Format = OutputFormat.Wav;
        settings.Recording.Preset = QualityPreset.Custom;
        settings.Recording.SampleRate = 48000;
        settings.Recording.Channels = 1;
        settings.Recording.Encoding = SampleEncoding.Pcm16;
        settings.Files.RecordingFolder = folder;
        settings.Advanced.RecoveryFlushSeconds = 0.5;
        settings.Advanced.LowDiskSpaceMb = 10;

        var history = new HistoryStore(paths.HistoryPath);
        history.Load();
        var journal = new RecordingJournal(paths.JournalPath);
        var namer = new FileNamer(new InMemoryCounterStore());
        var engine = new RecordingEngine(
            paths,
            factory ?? new FakeCaptureFactory(Source),
            namer,
            history,
            journal,
            NullLog.Instance,
            () => settings);

        return new Harness(paths, settings, engine, history, journal, paths.ResolveRecordingFolder(settings.Files.RecordingFolder));
    }

    [Fact]
    public async Task StartStop_SavesVerifiedWaveAndHistory()
    {
        var harness = CreateHarness();
        var completed = new TaskCompletionSource<RecordingCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.Completed += (_, e) => completed.TrySetResult(e);

        using var engine = harness.Engine;
        Assert.True(await engine.StartAsync());
        Assert.Equal(RecordingState.Recording, engine.State);

        await WaitUntil(() => engine.Elapsed >= TimeSpan.FromMilliseconds(150));
        await engine.StopAsync();
        await engine.StopAsync();

        Assert.Equal(RecordingState.Idle, engine.State);
        var eventArgs = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var files = Directory.GetFiles(harness.Folder, "*.wav");
        Assert.Single(files);
        Assert.Equal(files[0], eventArgs.FilePath);
        var dataBytes = new FileInfo(files[0]).Length - WavHeader.HeaderLength(TargetFormat.Standard);
        Assert.True(dataBytes > 0);
        Assert.True(WavVerifier.Verify(files[0], TargetFormat.Standard, dataBytes, out var error), error);

        Assert.Empty(Directory.GetFiles(harness.Folder, "*.part"));
        Assert.Empty(Directory.GetFiles(harness.Folder, "*.recovery.json"));

        var history = harness.History.Snapshot();
        Assert.Single(history);
        Assert.Equal("WAV", history[0].Format);
        Assert.True(history[0].DurationSeconds > 0.1);
        Assert.Equal(files[0], history[0].Path);
        Assert.Empty(harness.Journal.Snapshot());
    }

    [Fact]
    public async Task EnginePreservesTheInputSignalInsteadOfWritingSilence()
    {
        var harness = CreateHarness();
        using var engine = harness.Engine;
        Assert.True(await engine.StartAsync());
        await WaitUntil(() => engine.Elapsed >= TimeSpan.FromMilliseconds(300));
        await engine.StopAsync();

        var file = Directory.GetFiles(harness.Folder, "*.wav").Single();
        var bytes = File.ReadAllBytes(file);
        var peak = 0;
        for (var i = 44; i + 1 < bytes.Length; i += 2)
        {
            var sample = (short)(bytes[i] | (bytes[i + 1] << 8));
            var magnitude = Math.Abs((int)sample);
            if (magnitude > peak) peak = magnitude;
        }

        Assert.True(peak > 1000, $"recorded peak was {peak}; the engine lost the signal");
    }

    [Fact]
    public async Task ConcurrentStarts_ProduceOnlyOneRecording()
    {
        var harness = CreateHarness();
        using var engine = harness.Engine;

        var results = await Task.WhenAll(engine.StartAsync(), engine.StartAsync());

        Assert.Single(results, r => r);
        Assert.Equal(RecordingState.Recording, engine.State);
        await engine.StopAsync();
        Assert.Single(Directory.GetFiles(harness.Folder, "*.wav"));
    }

    [Fact]
    public async Task ConcurrentStops_AreSafeAndFinalizeOnce()
    {
        var harness = CreateHarness();
        using var engine = harness.Engine;
        Assert.True(await engine.StartAsync());
        await WaitUntil(() => engine.Elapsed >= TimeSpan.FromMilliseconds(80));

        await Task.WhenAll(engine.StopAsync(), engine.StopAsync());

        Assert.Equal(RecordingState.Idle, engine.State);
        Assert.Single(Directory.GetFiles(harness.Folder, "*.wav"));
        Assert.Empty(Directory.GetFiles(harness.Folder, "*.part"));
    }

    [Fact]
    public async Task PauseFreezesElapsedAndResumeContinues()
    {
        var harness = CreateHarness();
        using var engine = harness.Engine;
        Assert.True(await engine.StartAsync());
        await WaitUntil(() => engine.Elapsed >= TimeSpan.FromMilliseconds(150));

        Assert.True(await engine.TogglePauseAsync());
        Assert.Equal(RecordingState.Paused, engine.State);
        await Task.Delay(120); // let queued audio drain
        var pausedAt = engine.Elapsed;
        await Task.Delay(220);
        Assert.True(engine.Elapsed - pausedAt < TimeSpan.FromMilliseconds(60),
            $"elapsed grew while paused: {engine.Elapsed - pausedAt}");

        Assert.True(await engine.TogglePauseAsync());
        Assert.Equal(RecordingState.Recording, engine.State);
        await WaitUntil(() => engine.Elapsed > pausedAt + TimeSpan.FromMilliseconds(60));
        await engine.StopAsync();

        Assert.Single(Directory.GetFiles(harness.Folder, "*.wav"));
    }

    [Fact]
    public async Task MissingMicrophone_FailsCleanlyWithoutLeavingParts()
    {
        var harness = CreateHarness(new FakeCaptureFactory(Source, fail: true));
        using var engine = harness.Engine;
        var errorRaised = false;
        engine.Error += (_, _) => errorRaised = true;

        Assert.False(await engine.StartAsync());

        Assert.True(errorRaised);
        Assert.Equal(RecordingState.Idle, engine.State);
        Assert.False(string.IsNullOrWhiteSpace(engine.LastError));
        Assert.Empty(Directory.GetFiles(Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnavailableFolder_FailsWithClearError()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "Recordings"), "not a folder");
        var harness = CreateHarness(folder: "Recordings");
        using var engine = harness.Engine;

        Assert.False(await engine.StartAsync());
        Assert.Contains("folder", engine.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RecordingState.Idle, engine.State);
    }

    [Fact]
    public async Task ActivePart_IsRecognizedWhileRecordingAndReleasedAfterStop()
    {
        var harness = CreateHarness();
        using var engine = harness.Engine;
        Assert.True(await engine.StartAsync());
        await WaitUntil(() => Directory.GetFiles(harness.Folder, "*.wav.part").Length == 1);

        var part = Directory.GetFiles(harness.Folder, "*.wav.part").Single();
        Assert.True(engine.IsPartActive(part));

        await engine.StopAsync();
        Assert.False(engine.IsPartActive(part));
        Assert.Empty(Directory.GetFiles(harness.Folder, "*.wav.part"));
    }

    [Fact]
    public async Task RecoveryState_BlocksRecordingUntilTheScanEnds()
    {
        var harness = CreateHarness();
        using var engine = harness.Engine;

        engine.BeginRecoveryScan();
        Assert.Equal(RecordingState.Recovering, engine.State);
        Assert.False(await engine.StartAsync());
        Assert.Equal(RecordingState.Recovering, engine.State);

        engine.EndRecoveryScan();
        Assert.Equal(RecordingState.Idle, engine.State);
        Assert.True(await engine.StartAsync());
        await engine.StopAsync();
    }

    [Fact]
    public async Task Mp3_WhenMediaFoundationIsAvailable_ProducesMp3AndKeepsHistory()
    {
        if (!Mp3Encoder.IsAvailable) return;

        var harness = CreateHarness();
        harness.Settings.Recording.Format = OutputFormat.Mp3;
        harness.Settings.Recording.Mp3BitrateKbps = 96;
        using var engine = harness.Engine;

        Assert.True(await engine.StartAsync());
        await WaitUntil(() => engine.Elapsed >= TimeSpan.FromMilliseconds(250));
        await engine.StopAsync();

        var mp3 = Directory.GetFiles(harness.Folder, "*.mp3");
        Assert.Single(mp3);
        Assert.True(new FileInfo(mp3[0]).Length > 256);
        Assert.Empty(Directory.GetFiles(harness.Folder, "*.wav"));
        Assert.Equal("MP3", harness.History.Snapshot().Single().Format);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMilliseconds)
            {
                throw new TimeoutException("Condition was not reached in time.");
            }

            await Task.Delay(20);
        }
    }
}
