using System.Text.Json;
using FastVRecorder.Core.Themes;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class ThemeTests
{
    private static string ThemeJson(string slug, string label = "Test") =>
        JsonSerializer.Serialize(new
        {
            slug,
            label,
            order = 5,
            tokens = ThemeDefinition.TokenNames.ToDictionary(n => n, _ => "#112233"),
        });

    [Fact]
    public void GoldenDefault_HasTheFullTwentyOneTokenContract()
    {
        Assert.Equal(21, ThemeDefinition.TokenNames.Length);
        Assert.Equal(21, ThemeDefinition.GoldenDefault.Tokens.Count);
        Assert.All(ThemeDefinition.TokenNames, name => Assert.True(ThemeDefinition.GoldenDefault.Tokens.ContainsKey(name)));
        Assert.Equal("#F0D060", ThemeDefinition.GoldenDefault.Tokens["link"]);
    }

    [Fact]
    public void ParsesValidTheme()
    {
        Assert.True(ThemeDefinition.TryParse(ThemeJson("mytheme", "My Theme"), "test", out var theme, out var error), error);
        Assert.NotNull(theme);
        Assert.Equal("mytheme", theme!.Slug);
        Assert.Equal("My Theme", theme.Label);
        Assert.Equal(5, theme.Order);
        Assert.Equal(21, theme.Tokens.Count);
    }

    [Fact]
    public void RejectsMissingTokenBadHexAndBadSlug()
    {
        var missing = JsonSerializer.Serialize(new
        {
            slug = "broken",
            label = "Broken",
            tokens = new Dictionary<string, string> { ["background"] = "#112233" },
        });
        Assert.False(ThemeDefinition.TryParse(missing, "test", out _, out _));

        var json = ThemeJson("badhex");
        json = json.Replace("#112233", "#ZZZZZZ");
        Assert.False(ThemeDefinition.TryParse(json, "test", out _, out _));

        Assert.False(ThemeDefinition.TryParse(ThemeJson("Has Spaces"), "test", out _, out _));
        Assert.False(ThemeDefinition.TryParse("not json", "test", out _, out _));
    }

    [Fact]
    public void Catalog_AlwaysExposesGoldenDefaultAndFallsBack()
    {
        var catalog = ThemeCatalog.Load(new[] { ("mine.json", ThemeJson("mine", "Mine")) }, portableDirectory: null);

        Assert.Contains(catalog.Themes, t => t.Slug == ThemeDefinition.DefaultSlug);
        Assert.Equal("mine", catalog.Resolve("MINE").Slug);
        Assert.Equal(ThemeDefinition.DefaultSlug, catalog.Resolve("missing").Slug);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void Catalog_ReportsBrokenThemesWithoutFailing()
    {
        var catalog = ThemeCatalog.Load(new[] { ("broken.json", "{ nope") }, null);

        Assert.Single(catalog.Themes);
        Assert.Single(catalog.Problems);
        Assert.Equal(ThemeDefinition.DefaultSlug, catalog.Resolve("broken").Slug);
    }

    [Fact]
    public void ParseHex_ReturnsRgb()
    {
        Assert.Equal(((byte)0x12, (byte)0x34, (byte)0x56), ThemeDefinition.ParseHex("#123456"));
    }
}
