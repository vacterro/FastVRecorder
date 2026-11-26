using System.Text.Json;
using FastVRecorder.Core.Files;

namespace FastVRecorder.Core.Recovery;

/// <summary>One in-progress recording known to the portable folder journal.</summary>
public sealed class JournalEntry
{
    public required string PartPath { get; set; }

    public string? MetaPath { get; set; }

    public string? FinalPath { get; set; }

    public DateTime StartedUtc { get; set; }

    public string Format { get; set; } = "WAV";
}

/// <summary>
/// config/active-recordings.json: lets the next process find partial recordings even when the
/// configured recording folder is on a temporarily unavailable drive. Best effort by design.
/// </summary>
public sealed class RecordingJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<JournalEntry> _entries;

    public RecordingJournal(string path)
    {
        _path = path;
        _entries = Load();
    }

    public IReadOnlyList<JournalEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    public void Add(JournalEntry entry)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => string.Equals(e.PartPath, entry.PartPath, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
            SaveLocked();
        }
    }

    public void Remove(string partPath)
    {
        lock (_lock)
        {
            if (_entries.RemoveAll(e => string.Equals(e.PartPath, partPath, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveLocked();
            }
        }
    }

    private List<JournalEntry> Load()
    {
        foreach (var candidate in new[] { _path, _path + ".bak" })
        {
            var text = AtomicFile.TryReadAllText(candidate);
            if (text is null)
            {
                continue;
            }

            try
            {
                var entries = JsonSerializer.Deserialize<List<JournalEntry>>(text, Json);
                if (entries is not null)
                {
                    return entries.Where(e => !string.IsNullOrWhiteSpace(e.PartPath)).ToList();
                }
            }
            catch (JsonException)
            {
                // try the backup file
            }
        }

        return new List<JournalEntry>();
    }

    private void SaveLocked()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The journal is an optimization; recovery also scans the recording folder.
        }
    }
}
