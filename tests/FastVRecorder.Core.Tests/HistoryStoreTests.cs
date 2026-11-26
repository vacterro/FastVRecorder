using FastVRecorder.Core.History;
using FastVRecorder.Core.Recovery;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class HistoryStoreTests : TempDirectoryTest
{
    private string HistoryPath => Path.Combine(Root, "config", "history.json");

    [Fact]
    public void AddLoadRemoveAndUpdate_PersistAcrossInstances()
    {
        var store = new HistoryStore(HistoryPath);
        store.Load();
        store.Add(new HistoryEntry { Path = @"C:\a\one.wav", CreatedLocal = new DateTime(2026, 1, 1), DurationSeconds = 3, SizeBytes = 100 });
        store.Add(new HistoryEntry { Path = @"C:\a\two.wav", CreatedLocal = new DateTime(2026, 2, 1), DurationSeconds = 4, SizeBytes = 200, Pinned = true });

        var reopened = new HistoryStore(HistoryPath);
        reopened.Load();
        var snapshot = reopened.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot[0].Pinned);
        Assert.Equal(@"C:\a\two.wav", snapshot[0].Path);

        reopened.Update(@"C:\a\one.wav", e => e.Note = "updated");
        reopened.Remove(@"C:\a\two.wav");

        var third = new HistoryStore(HistoryPath);
        third.Load();
        var final = third.Snapshot();
        Assert.Single(final);
        Assert.Equal("updated", final[0].Note);
    }

    [Fact]
    public void Load_CorruptFileFallsBackToEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, "not json");

        var store = new HistoryStore(HistoryPath);
        store.Load();

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Add_SamePathReplacesEntry()
    {
        var store = new HistoryStore(HistoryPath);
        store.Load();
        store.Add(new HistoryEntry { Path = "x.wav", CreatedLocal = DateTime.Now, SizeBytes = 1 });
        store.Add(new HistoryEntry { Path = "x.wav", CreatedLocal = DateTime.Now, SizeBytes = 2 });

        Assert.Single(store.Snapshot());
        Assert.Equal(2, store.Snapshot()[0].SizeBytes);
    }

    [Fact]
    public void RefreshExistence_MarksMissingFiles()
    {
        var existing = Path.Combine(Root, "exists.wav");
        File.WriteAllBytes(existing, new byte[10]);
        var store = new HistoryStore(HistoryPath);
        store.Load();
        store.Add(new HistoryEntry { Path = existing, CreatedLocal = DateTime.Now });
        store.Add(new HistoryEntry { Path = Path.Combine(Root, "missing.wav"), CreatedLocal = DateTime.Now });

        store.RefreshExistence();

        Assert.False(store.Snapshot().Single(e => e.Path == existing).Missing);
        Assert.True(store.Snapshot().Single(e => e.Path.EndsWith("missing.wav")).Missing);
    }
}

public sealed class RecordingJournalTests : TempDirectoryTest
{
    [Fact]
    public void AddRemoveAndReload_Persist()
    {
        var path = Path.Combine(Root, "config", "active.json");
        var journal = new RecordingJournal(path);
        journal.Add(new JournalEntry { PartPath = @"C:\rec\a.wav.part", MetaPath = @"C:\rec\a.wav.recovery.json", StartedUtc = DateTime.UtcNow });

        var reopened = new RecordingJournal(path);
        Assert.Single(reopened.Snapshot());

        reopened.Remove(@"C:\rec\a.wav.part");
        var third = new RecordingJournal(path);
        Assert.Empty(third.Snapshot());
    }

    [Fact]
    public void CorruptJournal_LoadsEmpty()
    {
        var path = Path.Combine(Root, "config", "active.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "garbage");

        var journal = new RecordingJournal(path);
        Assert.Empty(journal.Snapshot());
    }
}
