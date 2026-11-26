using FastVRecorder.Core.Files;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class PathSafetyTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("a/b", "a_b")]
    [InlineData("a:b", "a_b")]
    [InlineData("  name.  ", "name")]
    [InlineData("", "Recording")]
    [InlineData(null, "Recording")]
    public void SanitizeComponent_ProducesSafeSingleComponent(string? input, string expected)
    {
        Assert.Equal(expected, PathSafety.SanitizeComponent(input));
    }

    [Fact]
    public void IsSafeComponent_RejectsSeparators()
    {
        Assert.False(PathSafety.IsSafeComponent("a/b"));
        Assert.False(PathSafety.IsSafeComponent("a\\b"));
        Assert.True(PathSafety.IsSafeComponent("safe-name_1.wav"));
    }

    [Fact]
    public void IsUnder_DetectsContainment()
    {
        Assert.True(PathSafety.IsUnder(@"C:\root", @"C:\root\a\b"));
        Assert.False(PathSafety.IsUnder(@"C:\root", @"C:\rooted\a"));
        Assert.False(PathSafety.IsUnder(@"C:\root", @"C:\other"));
    }
}
