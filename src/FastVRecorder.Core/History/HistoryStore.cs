using System.Text.Json;
using System.Text.Json.Serialization;
using FastVRecorder.Core.Files;

namespace FastVRecorder.Core.History;

public sealed class HistoryEntry
{
    public required string Path { get; set; }
    public DateTime CreatedLocal { get; set; }
    public double DurationSeconds { get; set; }
    public long SizeBytes { get; set; }
    public string Format { get; set; } = "WAV";
    public bool Recovered { get; set; }
    public bool Pinned { get; set; }
    public string? Note { get; set; }

    [JsonIgnore] public bool Missing { get; set; }
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Persistent, small JSON history index (config/history.json). Thread-safe.</summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 5000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<HistoryEntry> _entries = new();

    public HistoryStore(string path) => _path = path;

    public string? LastSaveError { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        List<HistoryEntry>? loaded = null;
        foreach (var candidate in new[] { _path, _path + ".bak" })
        {
            var text = AtomicFile.TryReadAllText(candidate);
            if (text is null) continue;
            try
            {
                loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(text, Json);
                if (loaded is not null) break;
            }
            catch (JsonException) { /* try backup */ }
        }
        lock (_lock) _entries = loaded?.Where(e => !string.IsNullOrWhiteSpace(e.Path)).ToList() ?? new();
    }

    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (_lock) return _entries.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.CreatedLocal).ToList();
    }

    public HistoryEntry? Latest()
    {
        lock (_lock) return _entries.OrderByDescending(e => e.CreatedLocal).FirstOrDefault();
    }

    /// <summary>
    /// Latest recording that actually exists, ordered by file write time (stable under clock
    /// rollback), with creation time as tie-breaker. Missing index entries are skipped.
    /// </summary>
    public HistoryEntry? LatestExisting()
    {
        List<HistoryEntry> copy;
        lock (_lock) copy = _entries.ToList();
        return copy
            .Where(e =>
            {
                try { return File.Exists(e.Path); }
                catch { return false; }
            })
            .OrderByDescending(e =>
            {
                try { return File.GetLastWriteTimeUtc(e.Path); }
                catch { return DateTime.MinValue; }
            })
            .ThenByDescending(e => e.CreatedLocal)
            .FirstOrDefault();
    }

    public void Add(HistoryEntry entry)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                // Trim the index only (never touches files); pinned entries are kept.
                var removable = _entries.Where(e => !e.Pinned).OrderBy(e => e.CreatedLocal).Take(_entries.Count - MaxEntries).ToList();
                foreach (var r in removable) _entries.Remove(r);
            }
        }
        Save();
    }

    /// <summary>Adds a batch of existing files in one save. Returns how many new paths were added.</summary>
    public int AddRange(IEnumerable<HistoryEntry> entries)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (_entries.Any(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;
                _entries.Add(entry);
                added++;
            }

            if (_entries.Count > MaxEntries)
            {
                var removable = _entries.Where(e => !e.Pinned).OrderBy(e => e.CreatedLocal).Take(_entries.Count - MaxEntries).ToList();
                foreach (var r in removable) _entries.Remove(r);
            }
        }

        if (added > 0) Save();
        return added;
    }

    public void Remove(string path)
    {
        lock (_lock) _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Update(string path, Action<HistoryEntry> mutate)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (e is null) return;
            mutate(e);
        }
        Save();
    }

    /// <summary>Marks missing files (does not delete entries). Safe to call off the UI thread.</summary>
    public void RefreshExistence()
    {
        // Mutate only under the store lock: Save() serializes the same entry objects under
        // this lock, so writing Missing/SizeBytes outside it could persist a half-updated or
        // torn view of the index.
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                try
                {
                    var fi = new FileInfo(e.Path);
                    e.Missing = !fi.Exists;
                    if (fi.Exists) e.SizeBytes = fi.Length;
                }
                catch { e.Missing = true; }
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        lock (_lock)
        {
            // Serialize and write while holding the same lock so a slower older snapshot can
            // never overwrite a newer one.
            try
            {
                var text = JsonSerializer.Serialize(_entries, Json);
                AtomicFile.WriteAllText(_path, text);
                LastSaveError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastSaveError = ex.Message;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
