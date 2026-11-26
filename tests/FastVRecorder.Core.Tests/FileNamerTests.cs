using FastVRecorder.Core.Files;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class FileNamerTests : TempDirectoryTest
{
    private static readonly DateTime FixedNow = new(2026, 11, 26, 5, 36, 0);

    private FileNamer CreateNamer(ICounterStore? counters = null) =>
        new(counters ?? new InMemoryCounterStore(), () => FixedNow);

    [Fact]
    public void TimestampNaming_MatchesObsStyleAndSuffixesCollisions()
    {
        var namer = CreateNamer();
        var options = new NamingOptions(NamingPreset.DateTime, string.Empty, false);

        using var first = namer.Reserve(Root, options);
        using var second = namer.Reserve(Root, options);

        Assert.Equal("2026-11-26_05-36-00", first.BaseName);
        Assert.Equal("2026-11-26_05-36-00_001", second.BaseName);
        Assert.True(File.Exists(first.PartPath));
        Assert.True(File.Exists(second.PartPath));
        Assert.NotEqual(first.FinalPath, second.FinalPath);
    }

    [Fact]
    public void CounterNaming_UsesPersistentCounter()
    {
        var counters = new InMemoryCounterStore();
        var namer = CreateNamer(counters);
        var options = new NamingOptions(NamingPreset.Counter, "Recording", false);

        using var first = namer.Reserve(Root, options);
        using var second = namer.Reserve(Root, options);

        Assert.Equal("Recording_0001", first.BaseName);
        Assert.Equal("Recording_0002", second.BaseName);
    }

    [Fact]
    public void NonIncrementalCounter_FallsBackToCollisionSuffix()
    {
        var namer = CreateNamer();
        var options = new NamingOptions(NamingPreset.Counter, "Recording", false, IncrementalNumbering: false);

        using var first = namer.Reserve(Root, options);
        using var second = namer.Reserve(Root, options);

        Assert.Equal("Recording_0001", first.BaseName);
        Assert.Equal("Recording_0001_001", second.BaseName);
    }

    [Fact]
    public void Prefix_IsSanitizedAgainstTraversalAndInvalidCharacters()
    {
        var namer = CreateNamer();
        var options = new NamingOptions(NamingPreset.DateTime, "..\\..\\evil:name", false);

        using var target = namer.Reserve(Root, options);

        Assert.DoesNotContain('/', target.BaseName);
        Assert.DoesNotContain('\\', target.BaseName);
        Assert.DoesNotContain(':', target.BaseName);
        Assert.Equal(target.BaseName + ".wav", Path.GetFileName(target.FinalPath));
        Assert.StartsWith(target.Directory, target.FinalPath);
    }

    [Fact]
    public void DateFolders_CreateYearMonthDayPath()
    {
        var namer = CreateNamer();
        var options = new NamingOptions(NamingPreset.DateTime, string.Empty, DateFolders: true);

        using var target = namer.Reserve(Root, options);

        var expected = Path.Combine(Root, "2026", "11", "26");
        Assert.Equal(expected, target.Directory);
        Assert.True(Directory.Exists(expected));
        Assert.Matches(@"^\d{2}-\d{2}-\d{2}$", target.BaseName);
    }

    [Fact]
    public void Reserve_NeverOverwritesAnExistingFinalFile()
    {
        var existing = Path.Combine(Root, "2026-11-26_05-36-00.wav");
        File.WriteAllText(existing, "user data");
        var namer = CreateNamer();
        var options = new NamingOptions(NamingPreset.DateTime, string.Empty, false);

        using var target = namer.Reserve(Root, options);

        Assert.Equal("2026-11-26_05-36-00_001", target.BaseName);
        Assert.Equal("user data", File.ReadAllText(existing));
    }

    [Fact]
    public void Preview_DoesNotAllocateCounter()
    {
        var counters = new InMemoryCounterStore();
        var namer = CreateNamer(counters);
        var options = new NamingOptions(NamingPreset.Counter, "Recording", false);

        var preview = namer.Preview(options);
        Assert.Equal("Recording_0001.wav", preview);
        Assert.Equal(1, counters.Peek("global"));
    }
}
