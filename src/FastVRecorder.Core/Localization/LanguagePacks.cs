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
///
/// Packs are parsed lazily: startup pays only for English and the active language, while
/// <see cref="All"/>/<see cref="EnsureLoaded"/> still materialize every pack for tooling and
/// completeness tests.
/// </summary>
public static class LanguagePacks
{
    private const string ResourcePrefix = "FastVRecorder.Core.Languages.";
    private static readonly object Sync = new();
    // Case-insensitive so a pack file's own `code` casing can never create a duplicate.
    private static readonly Dictionary<string, LanguagePack> Packs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Problems = new();
    private static bool _diagnosed;

    public static IReadOnlyList<string> LoadProblems
    {
        get
        {
            lock (Sync) return Problems.ToList();
        }
    }

    public static IReadOnlyDictionary<string, string> English
    {
        get
        {
            EnsureEnglish();
            lock (Sync) return Packs["en"].Strings;
        }
    }

    public static IReadOnlyCollection<LanguagePack> All
    {
        get
        {
            EnsureAll();
            lock (Sync) return Packs.Values.ToList();
        }
    }

    public static LanguagePack? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        EnsurePack(code);
        lock (Sync) return Packs.TryGetValue(code, out var pack) ? pack : null;
    }

    public static string Translate(string? code, string key)
    {
        EnsureEnglish();
        if (!string.IsNullOrWhiteSpace(code) && !string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
        {
            EnsurePack(code);
        }

        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(code) &&
                Packs.TryGetValue(code, out var pack) &&
                pack.Strings.TryGetValue(key, out var value))
            {
                return value;
            }

            return Packs["en"].Strings.TryGetValue(key, out var english) ? english : key;
        }
    }

    /// <summary>Materializes every embedded pack (tests, tooling, diagnostics).</summary>
    public static void EnsureLoaded()
    {
        EnsureAll();
    }

    private static void EnsureEnglish()
    {
        lock (Sync)
        {
            if (!Packs.ContainsKey("en"))
            {
                LoadEmbeddedPackLocked("en");
            }

            if (!Packs.ContainsKey("en"))
            {
                throw new InvalidOperationException("The embedded English language pack is missing.");
            }
        }
    }

    private static void EnsurePack(string code)
    {
        EnsureEnglish();
        lock (Sync)
        {
            if (Packs.ContainsKey(code)) return;
            LoadEmbeddedPackLocked(code);
        }
    }

    private static void EnsureAll()
    {
        EnsureEnglish();
        var assembly = typeof(LanguagePacks).Assembly;
        var codes = assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                               resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(resource => resource[ResourcePrefix.Length..^".json".Length])
            .ToList();
        lock (Sync)
        {
            foreach (var code in codes)
            {
                if (!Packs.ContainsKey(code))
                {
                    LoadEmbeddedPackLocked(code);
                }
            }

            DiagnoseLocked();
        }
    }

    private static void LoadEmbeddedPackLocked(string code)
    {
        var assembly = typeof(LanguagePacks).Assembly;
        var wanted = ResourcePrefix + code + ".json";
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase));
        if (resource is null) return;
        try
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) return;
            using var reader = new StreamReader(stream);
            var pack = Parse(reader.ReadToEnd(), code, resource);
            if (pack is not null)
            {
                Packs[pack.Code] = pack;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Problems.Add($"{resource}: {ex.Message}");
        }
    }

    private static void DiagnoseLocked()
    {
        if (_diagnosed) return;
        _diagnosed = true;
        if (!Packs.TryGetValue("en", out var english)) return;
        foreach (var (code, pack) in Packs)
        {
            var missing = english.Strings.Keys.Count(key => !pack.Strings.ContainsKey(key));
            if (missing > 0)
            {
                Problems.Add($"{code}: {missing} key(s) fall back to English.");
            }
        }
    }

    /// <summary>Reads loose &lt;app&gt;/languages/*.json files; returns how many packs were replaced.</summary>
    public static int LoadOverrides(string? directory)
    {
        EnsureEnglish();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
        var applied = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            try
            {
                var pack = Parse(File.ReadAllText(file), code, file);
                if (pack is null) continue;
                EnsurePack(pack.Code);
                lock (Sync)
                {
                    if (Packs.TryGetValue(pack.Code, out var embedded))
                    {
                        // Portable overrides are partial by design: they replace keys, not the
                        // entire pack, so new embedded keys keep working after an app update.
                        var merged = new Dictionary<string, string>(embedded.Strings, StringComparer.Ordinal);
                        foreach (var (key, value) in pack.Strings) merged[key] = value;
                        Packs[pack.Code] = new LanguagePack
                        {
                            Code = pack.Code,
                            Strings = merged,
                            Source = pack.Source,
                        };
                    }
                    else
                    {
                        Packs[pack.Code] = pack;
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
