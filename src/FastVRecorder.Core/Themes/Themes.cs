using System.Text.Json;
using System.Text.RegularExpressions;

namespace FastVRecorder.Core.Themes;

/// <summary>
/// A Wintage theme (same JSON contract as Wintage `themes/*.json`): slug, label, order and the
/// closed set of 21 colour tokens.
/// </summary>
public sealed partial class ThemeDefinition
{
    public static readonly string[] TokenNames =
    {
        "background", "backgroundSoft", "surface", "surfaceRaised", "surfaceAlt",
        "borderDark", "borderHighlight", "bevelLight", "borderMuted",
        "textPrimary", "textSecondary", "textMuted",
        "accentTeal", "accentTealDeep",
        "success", "warning", "danger", "dangerText",
        "selection", "compareBack", "link",
    };

    public required string Slug { get; init; }
    public required string Label { get; init; }
    public int Order { get; init; }
    public required IReadOnlyDictionary<string, string> Tokens { get; init; }
    public string Origin { get; init; } = "built-in";

    public const string DefaultSlug = "goldendefault";

    /// <summary>Golden Default, copied from Wintage themes/goldendefault.json (canonical palette).</summary>
    public static ThemeDefinition GoldenDefault { get; } = new()
    {
        Slug = DefaultSlug,
        Label = "Golden Default",
        Order = 0,
        Origin = "built-in (Wintage goldendefault.json)",
        Tokens = new Dictionary<string, string>
        {
            ["background"] = "#1A1810",
            ["backgroundSoft"] = "#232018",
            ["surface"] = "#332E22",
            ["surfaceRaised"] = "#3D372A",
            ["surfaceAlt"] = "#453D30",
            ["borderDark"] = "#100E08",
            ["borderHighlight"] = "#F0D060",
            ["bevelLight"] = "#75663D",
            ["borderMuted"] = "#5A5040",
            ["textPrimary"] = "#D4C89A",
            ["textSecondary"] = "#9C9371",
            ["textMuted"] = "#6E674E",
            ["accentTeal"] = "#008080",
            ["accentTealDeep"] = "#004C4C",
            ["success"] = "#4A7A20",
            ["warning"] = "#7A7A20",
            ["danger"] = "#7A2020",
            ["dangerText"] = "#D66464",
            ["selection"] = "#3D372A",
            ["compareBack"] = "#14120C",
            ["link"] = "#F0D060",
        },
    };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,40}$")]
    private static partial Regex SlugPattern();

    /// <summary>Parses and validates a Wintage theme JSON document.</summary>
    public static bool TryParse(string json, string origin, out ThemeDefinition? theme, out string? error)
    {
        theme = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "Theme root is not an object."; return false; }
            var slug = root.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
            if (!SlugPattern().IsMatch(slug)) { error = $"Invalid slug '{slug}'."; return false; }
            var label = root.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : slug;
            var order = root.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var oi) ? oi : 100;
            if (!root.TryGetProperty("tokens", out var tokensEl) || tokensEl.ValueKind != JsonValueKind.Object)
            {
                error = "Missing tokens object."; return false;
            }
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in TokenNames)
            {
                if (!tokensEl.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
                {
                    error = $"Missing token '{name}'."; return false;
                }
                var hex = v.GetString()!;
                if (!HexColor().IsMatch(hex)) { error = $"Token '{name}' is not #RRGGBB: '{hex}'."; return false; }
                tokens[name] = hex.ToUpperInvariant();
            }
            theme = new ThemeDefinition { Slug = slug, Label = string.IsNullOrWhiteSpace(label) ? slug : label.Trim(), Order = order, Tokens = tokens, Origin = origin };
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = "Malformed JSON: " + ex.Message;
            return false;
        }
    }

    public static (byte R, byte G, byte B) ParseHex(string hex) =>
        (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
}

/// <summary>Loads built-in + embedded + portable themes. Golden Default is always available.</summary>
public sealed class ThemeCatalog
{
    private readonly List<ThemeDefinition> _themes = new();
    private readonly List<string> _problems = new();

    public IReadOnlyList<ThemeDefinition> Themes => _themes;
    public IReadOnlyList<string> Problems => _problems;

    /// <param name="embedded">(name, json) pairs shipped inside the executable.</param>
    /// <param name="portableDirectory">Optional folder with user/override themes (*.json).</param>
    public static ThemeCatalog Load(IEnumerable<(string Name, string Json)> embedded, string? portableDirectory)
    {
        var cat = new ThemeCatalog();
        var map = new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [ThemeDefinition.DefaultSlug] = ThemeDefinition.GoldenDefault,
        };
        foreach (var (name, json) in embedded)
        {
            if (ThemeDefinition.TryParse(json, "embedded " + name, out var t, out var err)) map[t!.Slug] = t;
            else cat._problems.Add($"{name}: {err}");
        }
        if (!string.IsNullOrEmpty(portableDirectory) && Directory.Exists(portableDirectory))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(portableDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string json;
                    try { json = File.ReadAllText(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        cat._problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                        continue;
                    }
                    if (ThemeDefinition.TryParse(json, file, out var t, out var err)) map[t!.Slug] = t;
                    else cat._problems.Add($"{Path.GetFileName(file)}: {err}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cat._problems.Add($"{portableDirectory}: {ex.Message}");
            }
        }
        // Golden Default first, then by order/label.
        cat._themes.AddRange(map.Values
            .OrderBy(t => t.Slug == ThemeDefinition.DefaultSlug ? 0 : 1)
            .ThenBy(t => t.Order)
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase));
        return cat;
    }

    public ThemeDefinition Resolve(string? slug) =>
        _themes.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase))
        ?? _themes.FirstOrDefault(t => t.Slug == ThemeDefinition.DefaultSlug)
        ?? ThemeDefinition.GoldenDefault;
}
