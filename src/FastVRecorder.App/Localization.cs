using System.ComponentModel;
using System.Windows;
using FastVRecorder.Core.Localization;

namespace FastVRecorder.App;

/// <summary>
/// Runtime UI language table backed by the embedded Core language packs. Indexer bindings are
/// invalidated when the language changes, so switching requires no restart.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private string _language = "en";

    private Loc()
    {
        // Packs are lazy: the first translation pays for English and the active language only.
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language => _language;

    public bool IsRussian => string.Equals(_language, "ru", StringComparison.OrdinalIgnoreCase);

    public bool IsRightToLeft => LanguageCatalog.Find(_language)?.RightToLeft ?? false;

    public FlowDirection FlowDirection => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public IReadOnlyList<LanguageDefinition> Languages => LanguageCatalog.All;

    public string this[string key] => LanguagePacks.Translate(_language, key);

    public string Get(string key) => this[key];

    public void SetLanguage(string setting, string? systemLanguage)
    {
        var resolved = LanguageCatalog.Resolve(setting, systemLanguage).Code;
        if (string.Equals(_language, resolved, StringComparison.OrdinalIgnoreCase)) return;
        _language = resolved;
        // An empty property name invalidates every binding (indexer, Language, IsRussian,
        // IsRightToLeft, FlowDirection) with one notification. Five separate notifications made
        // one language switch rebuild the device list and re-register every hotkey five times.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
