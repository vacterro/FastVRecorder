using FastVRecorder.Core.History;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class HistoryHardeningTests : TempDirectoryTest
{
    private string HistoryPath => Path.Combine(Root, "config", "history.json");

    [Fact]
    public void LatestExisting_SkipsMissingNewerEntryAndUsesFileTime()
    {
        var store = new HistoryStore(HistoryPath);
        store.Load();
        var older = Path.Combine(Root, "older.wav");
        var newerMissing = Path.Combine(Root, "missing.wav");
        File.WriteAllBytes(older, new byte[10]);
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        store.Add(new HistoryEntry { Path = newerMissing, CreatedLocal = new DateTime(2026, 12, 31), DurationSeconds = 5 });
        store.Add(new HistoryEntry { Path = older, CreatedLocal = new DateTime(2026, 1, 1), DurationSeconds = 1 });

        var latest = store.LatestExisting();

        Assert.NotNull(latest);
        Assert.Equal(older, latest!.Path);
    }

    [Fact]
    public void ConcurrentAdds_SaveCompleteSnapshotAndDoNotLoseEntries()
    {
        var store = new HistoryStore(HistoryPath);
        store.Load();

        Parallel.For(0, 100, i =>
        {
            store.Add(new HistoryEntry
            {
                Path = Path.Combine(Root, $"rec-{i}.wav"),
                CreatedLocal = new DateTime(2026, 1, 1).AddSeconds(i),
                DurationSeconds = i,
            });
        });

        var reopened = new HistoryStore(HistoryPath);
        reopened.Load();
        Assert.Equal(100, reopened.Snapshot().Count);
        Assert.Equal(100, store.Snapshot().Count);
        Assert.Null(store.LastSaveError);
    }
}
