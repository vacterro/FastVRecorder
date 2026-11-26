namespace FastVRecorder.Infrastructure.Recording;

/// <summary>Recording engine state machine states.</summary>
public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
    Finalizing,
    Recovering,
    Error,
}

/// <summary>Raised when the engine transitions between states.</summary>
public sealed class StateChangedEventArgs : EventArgs
{
    public required RecordingState OldState { get; init; }
    public required RecordingState NewState { get; init; }
}

/// <summary>Raised periodically with the peak input level for the UI meter.</summary>
public sealed class LevelUpdatedEventArgs : EventArgs
{
    /// <summary>Absolute peak in 0..1+ range.</summary>
    public required float Peak { get; init; }
}

/// <summary>Raised when a non-fatal or fatal error occurs during recording.</summary>
public sealed class RecordingErrorEventArgs : EventArgs
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
    public bool IsFatal { get; init; }
}

/// <summary>Raised when a recording completes (or is stopped) successfully.</summary>
public sealed class RecordingCompletedEventArgs : EventArgs
{
    public required string FilePath { get; init; }
    public required TimeSpan Duration { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>Every output file produced by this session (segments included).</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>Non-null when the save succeeded but data may be incomplete (overflow, device loss).</summary>
    public string? Note { get; init; }
}

/// <summary>Raised after the capture device and the writer are both running.</summary>
public sealed class RecordingStartedEventArgs : EventArgs
{
    public required string FormatText { get; init; }
    public required string OutputPath { get; init; }
}

