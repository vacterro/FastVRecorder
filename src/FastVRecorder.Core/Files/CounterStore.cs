using System.Globalization;
using System.Text.Json;

namespace FastVRecorder.Core.Files;

public interface ICounterStore
{
    /// <summary>Atomically allocates the next number for <paramref name="key"/>; never reuses a value.</summary>
    int Next(string key);

    /// <summary>Returns the value <see cref="Next"/> would allocate, without allocating.</summary>
    int Peek(string key);
}

/// <summary>Persistent counters stored as JSON next to the settings (atomic writes).</summary>
public sealed class JsonCounterStore : ICounterStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, int>? _values;

    public JsonCounterStore(string path) => _path = path;

    public int Next(string key)
    {
        lock (_lock)
        {
            var values = Load();
            var next = values.TryGetValue(key, out var v) ? Math.Max(1, v) : 1;
            values[key] = next + 1;
            Prune(values);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            return next;
        }
    }

    public int Peek(string key)
    {
        lock (_lock)
        {
            var values = Load();
            return values.TryGetValue(key, out var v) ? Math.Max(1, v) : 1;
        }
    }

    private Dictionary<string, int> Load()
    {
        if (_values is not null) return _values;
        foreach (var candidate in new[] { _path, _path + ".bak" })
        {
            var text = AtomicFile.TryReadAllText(candidate);
            if (text is null) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(text);
                if (parsed is not null) return _values = new Dictionary<string, int>(parsed, StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                // try backup
            }
        }
        return _values = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Keeps day-scoped counters for the last 14 days only.</summary>
    private static void Prune(Dictionary<string, int> values)
    {
        var cutoff = DateTime.Today.AddDays(-14);
        foreach (var key in values.Keys.Where(k => k.StartsWith("day:", StringComparison.Ordinal)).ToList())
        {
            if (DateTime.TryParseExact(key[4..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff)
                values.Remove(key);
        }
    }
}

public sealed class InMemoryCounterStore : ICounterStore
{
    private readonly Dictionary<string, int> _values = new();
    public int Next(string key)
    {
        lock (_values)
        {
            var n = _values.TryGetValue(key, out var v) ? v : 1;
            _values[key] = n + 1;
            return n;
        }
    }
    public int Peek(string key)
    {
        lock (_values) return _values.TryGetValue(key, out var v) ? v : 1;
    }
}
