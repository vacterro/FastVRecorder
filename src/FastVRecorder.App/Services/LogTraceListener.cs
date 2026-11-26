using System.Diagnostics;
using System.Text;
using FastVRecorder.Core.Diagnostics;

namespace FastVRecorder.App.Services;

/// <summary>Routes WPF data-binding warnings into the application log.</summary>
public sealed class LogTraceListener : TraceListener
{
    private readonly ILog _log;
    private readonly StringBuilder _pending = new();

    public LogTraceListener(ILog log) => _log = log;

    public override void Write(string? message) => _pending.Append(message);

    public override void WriteLine(string? message)
    {
        _pending.Append(message);
        var text = _pending.ToString();
        _pending.Clear();
        _log.Warn("Binding: " + text);
    }

    public static void Attach(ILog log)
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new LogTraceListener(log));
        source.Switch.Level = SourceLevels.Warning;
    }
}
