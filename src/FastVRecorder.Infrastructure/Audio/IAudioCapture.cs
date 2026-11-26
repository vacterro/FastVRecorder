using FastVRecorder.Core.Audio;

namespace FastVRecorder.Infrastructure.Audio;

public delegate void CaptureAudioHandler(ReadOnlySpan<byte> buffer);

public interface IAudioCaptureSource : IDisposable
{
    event CaptureAudioHandler? DataAvailable;

    /// <summary>Raised with the device exception; null means a requested stop finished normally.</summary>
    event Action<Exception?>? Stopped;

    SourceFormat SourceFormat { get; }

    /// <summary>Friendly endpoint name for diagnostics; null for synthetic sources.</summary>
    string? DeviceName { get; }

    void Start();

    void Stop();
}

/// <summary>Creates a capture source for a stored device id (empty = Windows default).</summary>
public interface IAudioCaptureFactory
{
    IAudioCaptureSource? TryCreate(string? deviceId, out string? error);
}
