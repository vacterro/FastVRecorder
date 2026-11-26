using FastVRecorder.Core.Localization;
using FastVRecorder.Core.Settings;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class LanguageTests
{
    [Fact]
    public void CatalogContainsThirtyThreeUniqueLanguages()
    {
        var languages = LanguageCatalog.All;
        Assert.Equal(33, languages.Count);
        Assert.Equal(33, languages.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(languages, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Code));
            Assert.False(string.IsNullOrWhiteSpace(l.NativeName));
            Assert.False(string.IsNullOrWhiteSpace(l.EnglishName));
        });
        Assert.True(LanguageCatalog.Find("ar")!.RightToLeft);
        Assert.False(LanguageCatalog.Find("en")!.RightToLeft);
        Assert.Equal("简体中文", LanguageCatalog.Find("zh-Hans")!.NativeName);
    }

    [Theory]
    [InlineData("auto", "ru-RU", "ru")]
    [InlineData("auto", "zh-CN", "zh-Hans")]
    [InlineData("auto", "nb-NO", "no")]
    [InlineData("auto", "pt-BR", "pt")]
    [InlineData("de", "ru-RU", "de")]
    [InlineData("fr-CA", "en-US", "fr")]
    [InlineData("nonsense", "en-US", "en")]
    [InlineData("auto", "xx-XX", "en")]
    public void ResolveMapsSettingsAndOsCultures(string setting, string system, string expected)
    {
        Assert.Equal(expected, LanguageCatalog.Resolve(setting, system).Code);
    }

    [Fact]
    public void SettingsNormalizationAcceptsEveryCatalogLanguage()
    {
        foreach (var language in LanguageCatalog.All)
        {
            var settings = new AppSettings();
            settings.Appearance.Language = language.Code;
            settings.Normalize();
            Assert.Equal(language.Code, settings.Appearance.Language);
        }

        var auto = new AppSettings();
        auto.Appearance.Language = "auto";
        auto.Normalize();
        Assert.Equal("auto", auto.Appearance.Language);
    }

    [Fact]
    public void EmbeddedPacksCoverEveryLanguageAndEveryEnglishKey()
    {
        LanguagePacks.EnsureLoaded();
        var english = LanguagePacks.English;
        Assert.True(english.Count >= 200, $"English pack has only {english.Count} keys.");
        Assert.Equal("FastVRecorder", english["App.Title"]);
        Assert.Equal("Open", english["Common.Open"]);

        foreach (var language in LanguageCatalog.All)
        {
            var pack = LanguagePacks.Find(language.Code);
            Assert.NotNull(pack);
            foreach (var key in english.Keys)
            {
                Assert.True(pack!.Strings.TryGetValue(key, out var value), $"{language.Code} is missing {key}");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{language.Code}.{key} is empty");
            }
        }
    }

    [Fact]
    public void RussianPackKeepsHandWrittenTranslations()
    {
        Assert.Equal("Открыть", LanguagePacks.Translate("ru", "Common.Open"));
        Assert.Equal("Настройки", LanguagePacks.Translate("ru", "Nav.Settings"));
    }

    [Fact]
    public void MissingKeyAndUnknownLanguageFallBackToEnglish()
    {
        Assert.Equal("No.Such.Key", LanguagePacks.Translate("de", "No.Such.Key"));
        Assert.Equal("Open", LanguagePacks.Translate("zz", "Common.Open"));
        Assert.Equal("Open", LanguagePacks.Translate(null, "Common.Open"));
    }

    [Fact]
    public void PortableOverrideReplacesAPackAndFallsBackForMissingKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "fvr-lang-override", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "xx.json"), """
                { "strings": { "Common.Open": "OVERRIDE-OPEN" } }
                """);
            Assert.Equal(1, LanguagePacks.LoadOverrides(root));
            Assert.Equal("OVERRIDE-OPEN", LanguagePacks.Translate("xx", "Common.Open"));
            Assert.Equal("Save", LanguagePacks.Translate("xx", "Common.Save"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

public sealed class PortableRootTests
{
    [Fact]
    public void StoredFoldersAreRelativeInsideRootAndAbsoluteOutside()
    {
        var root = Path.Combine(Path.GetTempPath(), "fvr-root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var portable = new PortableRoot(root);
            Assert.Equal(string.Empty, portable.ToStoredFolder(portable.DefaultRecordingsDir));
            var nested = Path.Combine(root, "Recordings", "sub");
            Assert.Equal(Path.Combine("Recordings", "sub"), portable.ToStoredFolder(nested));
            Assert.Equal(nested, portable.ResolveRecordingFolder(Path.Combine("Recordings", "sub")));

            var external = Path.Combine(Path.GetTempPath(), "fvr-external", Guid.NewGuid().ToString("N"));
            Assert.Equal(external, portable.ToStoredFolder(external));
            Assert.Equal(external, portable.ResolveRecordingFolder(external));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

public sealed class RecordingFolderResolverTests
{
    [Fact]
    public void ManualAddressesResolveToPortableStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "fvr-root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var portable = new PortableRoot(root);

            var empty = RecordingFolderResolver.Resolve(portable, "   ");
            Assert.True(empty.IsValid);
            Assert.Equal(string.Empty, empty.StoredPath);
            Assert.Equal(portable.DefaultRecordingsDir, empty.AbsolutePath);

            var relative = RecordingFolderResolver.Resolve(portable, Path.Combine("My Recordings", "sub"));
            Assert.True(relative.IsValid);
            Assert.Equal(Path.Combine("My Recordings", "sub"), relative.StoredPath);
            Assert.Equal(Path.Combine(root, "My Recordings", "sub"), relative.AbsolutePath);

            var external = Path.Combine(Path.GetTempPath(), "fvr-external", Guid.NewGuid().ToString("N"));
            var absolute = RecordingFolderResolver.Resolve(portable, external);
            Assert.True(absolute.IsValid);
            Assert.Equal(external, absolute.StoredPath);
            Assert.Equal(external, absolute.AbsolutePath);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
