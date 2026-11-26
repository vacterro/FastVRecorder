using System.Windows;
using System.Windows.Media;
using FastVRecorder.Core.Themes;

namespace FastVRecorder.App.Services;

/// <summary>Maps the shared Wintage 21-token contract onto the app's semantic brushes.</summary>
public sealed class ThemeService
{
    private readonly ThemeCatalog _catalog;

    public ThemeService(ThemeCatalog catalog) => _catalog = catalog;

    public ThemeCatalog Catalog => _catalog;

    public ThemeDefinition Current { get; private set; } = ThemeDefinition.GoldenDefault;

    public void Apply(string? slug) => Apply(_catalog.Resolve(slug));

    public void Apply(ThemeDefinition theme)
    {
        Current = theme;
        var t = theme.Tokens;
        var background = Token(t, "background", "#1A1810");
        var surface = Token(t, "surface", "#332E22");
        var accent = Token(t, "link", "#F0D060");

        Set("Brush.Background", background);
        Set("Brush.BackgroundSoft", Token(t, "backgroundSoft", "#232018"));
        Set("Brush.Surface", surface);
        Set("Brush.SurfaceElevated", Token(t, "surfaceRaised", "#3D372A"));
        Set("Brush.SurfaceAlt", Token(t, "surfaceAlt", "#453D30"));
        Set("Brush.Border", Token(t, "borderMuted", "#5A5040"));
        Set("Brush.BorderStrong", Token(t, "borderDark", "#100E08"));
        Set("Brush.Bevel", Token(t, "bevelLight", "#75663D"));
        Set("Brush.Accent", accent);
        Set("Brush.AccentHover", Lighten(accent, 0.16));
        Set("Brush.AccentText", background);
        Set("Brush.TextPrimary", Token(t, "textPrimary", "#D4C89A"));
        Set("Brush.TextSecondary", Token(t, "textSecondary", "#9C9371"));
        Set("Brush.TextMuted", Token(t, "textMuted", "#6E674E"));
        Set("Brush.Recording", Token(t, "dangerText", "#D66464"));
        Set("Brush.Success", Token(t, "success", "#4A7A20"));
        Set("Brush.Warning", Token(t, "warning", "#7A7A20"));
        Set("Brush.Error", Token(t, "danger", "#7A2020"));
        Set("Brush.Selection", Token(t, "selection", "#3D372A"));
        Set("Brush.Focus", Token(t, "borderHighlight", "#F0D060"));
        Set("Brush.Teal", Token(t, "accentTeal", "#008080"));
        Set("Brush.TealDeep", Token(t, "accentTealDeep", "#004C4C"));
    }

    private static string Token(IReadOnlyDictionary<string, string> tokens, string key, string fallback) =>
        tokens.TryGetValue(key, out var value) && value.Length == 7 ? value : fallback;

    private static void Set(string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static string Lighten(string hex, double amount)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        byte Blend(byte channel) => (byte)Math.Round(channel + (255 - channel) * amount);
        return $"#{Blend(color.R):X2}{Blend(color.G):X2}{Blend(color.B):X2}";
    }
}
