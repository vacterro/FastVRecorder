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
        LanguagePacks.EnsureLoaded();
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRussian)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
    }
}
