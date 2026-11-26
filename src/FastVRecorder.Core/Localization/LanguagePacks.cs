using System.Reflection;
using System.Text.Json;

namespace FastVRecorder.Core.Localization;

public sealed class LanguagePack
{
    public required string Code { get; init; }
    public required IReadOnlyDictionary<string, string> Strings { get; init; }
    public string Source { get; init; } = "embedded";
}

/// <summary>
/// Loads the embedded UI language packs (JSON under Languages/) and optional portable overrides
/// from &lt;app&gt;/languages. English is the fallback for every missing key.
/// </summary>
public static class LanguagePacks
{
    private const string ResourcePrefix = "FastVRecorder.Core.Languages.";
    private static readonly object Sync = new();
    private static Dictionary<string, LanguagePack>? _packs;
    private static readonly List<string> Problems = new();

    public static IReadOnlyList<string> LoadProblems
    {
        get
        {
            EnsureLoaded();
            lock (Sync) return Problems.ToList();
        }
    }

    public static IReadOnlyDictionary<string, string> English
    {
        get
        {
            EnsureLoaded();
            return _packs!["en"].Strings;
        }
    }

    public static IReadOnlyCollection<LanguagePack> All
    {
        get
        {
            EnsureLoaded();
            return _packs!.Values.ToList();
        }
    }

    public static LanguagePack? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        EnsureLoaded();
        return _packs!.TryGetValue(code, out var pack) ? pack : null;
    }

    public static string Translate(string? code, string key)
    {
        EnsureLoaded();
        if (!string.IsNullOrWhiteSpace(code) &&
            _packs!.TryGetValue(code, out var pack) &&
            pack.Strings.TryGetValue(key, out var value))
        {
            return value;
        }

        return _packs!["en"].Strings.TryGetValue(key, out var english) ? english : key;
    }

    public static void EnsureLoaded()
    {
        if (_packs is not null) return;
        lock (Sync)
        {
            if (_packs is not null) return;
            var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
            var assembly = typeof(LanguagePacks).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var code = resource[ResourcePrefix.Length..^".json".Length];
                try
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    if (stream is null) continue;
                    using var reader = new StreamReader(stream);
                    var pack = Parse(reader.ReadToEnd(), code, resource);
                    if (pack is not null) packs[code] = pack;
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
                {
                    Problems.Add($"{resource}: {ex.Message}");
                }
            }

            if (!packs.TryGetValue("en", out var english))
            {
                throw new InvalidOperationException("The embedded English language pack is missing.");
            }

            foreach (var (code, pack) in packs)
            {
                var missing = english.Strings.Keys.Count(k => !pack.Strings.ContainsKey(k));
                if (missing > 0) Problems.Add($"{code}: {missing} key(s) fall back to English.");
            }

            _packs = packs;
        }
    }

    /// <summary>Reads loose &lt;app&gt;/languages/*.json files; returns how many packs were replaced.</summary>
    public static int LoadOverrides(string? directory)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
        var applied = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            try
            {
                var pack = Parse(File.ReadAllText(file), code, file);
                if (pack is null) continue;
                lock (Sync)
                {
                    if (_packs!.TryGetValue(pack.Code, out var embedded))
                    {
                        // Portable overrides are partial by design: they replace keys, not the
                        // entire pack, so new embedded keys keep working after an app update.
                        var merged = new Dictionary<string, string>(embedded.Strings, StringComparer.Ordinal);
                        foreach (var (key, value) in pack.Strings) merged[key] = value;
                        _packs[pack.Code] = new LanguagePack
                        {
                            Code = pack.Code,
                            Strings = merged,
                            Source = pack.Source,
                        };
                    }
                    else
                    {
                        _packs[pack.Code] = pack;
                    }
                }

                applied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                lock (Sync)
                {
                    Problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        return applied;
    }

    private static LanguagePack? Parse(string json, string expectedCode, string source)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("strings", out var stringsElement) ||
            stringsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in stringsElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                strings[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        if (strings.Count == 0) return null;

        var code = expectedCode;
        if (document.RootElement.TryGetProperty("code", out var codeElement) &&
            codeElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(codeElement.GetString()))
        {
            code = codeElement.GetString()!;
        }

        return new LanguagePack { Code = code, Strings = strings, Source = source };
    }
}
