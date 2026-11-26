using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace FastVRecorder.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

public interface ILog
{
    void Write(LogLevel level, string message, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Info(this ILog log, string m) => log.Write(LogLevel.Info, m);
    public static void Warn(this ILog log, string m, Exception? ex = null) => log.Write(LogLevel.Warn, m, ex);
    public static void Error(this ILog log, string m, Exception? ex = null) => log.Write(LogLevel.Error, m, ex);
    public static void Debug(this ILog log, string m) => log.Write(LogLevel.Debug, m);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message, Exception? ex = null) { }
}

/// <summary>
/// Small rolling file log. Writes are queued and flushed by a background task so callers
/// (including the audio writer thread) never block on disk I/O for logging.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _thread;
    public bool Enabled { get; set; } = true;
    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    public FileLog(string path, long maxBytes = 1 << 20)
    {
        _path = path;
        _maxBytes = maxBytes;
        _thread = new Thread(Pump) { IsBackground = true, Name = "FastVRecorder log" };
        _thread.Start();
    }

    public string FilePath => _path;

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (!Enabled || level < MinLevel) return;
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ').Append(message);
        if (ex is not null) sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        _queue.TryAdd(sb.ToString());
    }

    private void Pump()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > _maxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }
                var batch = new StringBuilder(line).Append(Environment.NewLine);
                while (_queue.TryTake(out var more)) batch.Append(more).Append(Environment.NewLine);
                File.AppendAllText(_path, batch.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the app (read-only portable folder etc.).
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
