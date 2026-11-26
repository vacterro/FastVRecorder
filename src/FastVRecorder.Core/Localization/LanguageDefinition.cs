namespace FastVRecorder.Core.Localization;

public sealed record LanguageDefinition(string Code, string NativeName, string EnglishName, bool RightToLeft = false);

/// <summary>
/// The 33 shipped UI languages. Codes are stable storage values; native names are shown in the
/// selector so every language is recognizable without translating the list itself.
/// </summary>
public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageDefinition> All { get; } = new LanguageDefinition[]
    {
        new("en", "English", "English"),
        new("ru", "Русский", "Russian"),
        new("uk", "Українська", "Ukrainian"),
        new("be", "Беларуская", "Belarusian"),
        new("bg", "Български", "Bulgarian"),
        new("sr", "Српски", "Serbian"),
        new("hr", "Hrvatski", "Croatian"),
        new("pl", "Polski", "Polish"),
        new("cs", "Čeština", "Czech"),
        new("sk", "Slovenčina", "Slovak"),
        new("ro", "Română", "Romanian"),
        new("hu", "Magyar", "Hungarian"),
        new("el", "Ελληνικά", "Greek"),
        new("tr", "Türkçe", "Turkish"),
        new("de", "Deutsch", "German"),
        new("fr", "Français", "French"),
        new("it", "Italiano", "Italian"),
        new("es", "Español", "Spanish"),
        new("pt", "Português", "Portuguese"),
        new("nl", "Nederlands", "Dutch"),
        new("sv", "Svenska", "Swedish"),
        new("no", "Norsk", "Norwegian"),
        new("da", "Dansk", "Danish"),
        new("fi", "Suomi", "Finnish"),
        new("et", "Eesti", "Estonian"),
        new("lv", "Latviešu", "Latvian"),
        new("lt", "Lietuvių", "Lithuanian"),
        new("is", "Íslenska", "Icelandic"),
        new("ca", "Català", "Catalan"),
        new("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new("ja", "日本語", "Japanese"),
        new("ko", "한국어", "Korean"),
        new("ar", "العربية", "Arabic", RightToLeft: true),
    };

    public static LanguageDefinition? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Maps a stored setting or OS culture name to a shipped language.</summary>
    public static LanguageDefinition Resolve(string? setting, string? systemLanguage)
    {
        var configured = (setting ?? "auto").Trim();
        if (!configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var direct = TryMatch(configured);
            if (direct is not null) return direct;
        }

        var os = TryMatch(systemLanguage);
        if (os is not null) return os;

        return Find("en")!;
    }

    public static bool TryNormalize(string? value, out string code)
    {
        var match = TryMatch(value);
        if (match is not null)
        {
            code = match.Code;
            return true;
        }

        code = "auto";
        return false;
    }

    private static LanguageDefinition? TryMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var code = value.Trim().Replace('_', '-');
        var exact = Find(code);
        if (exact is not null) return exact;

        var baseCode = code.Split('-')[0];
        return baseCode switch
        {
            "zh" => Find("zh-Hans"),
            "nb" or "nn" or "no" => Find("no"),
            _ => Find(baseCode),
        };
    }
}
