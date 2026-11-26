using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FastVRecorder.Infrastructure.Audio;

/// <summary>
/// Isolated WASAPI shared-mode capture adapter over NAudio 3.x's stable
/// <see cref="WasapiRecorder"/>. Raw mode is requested so Windows sound enhancements
/// (AGC / noise suppression APOs) do not alter the recorded signal; when a driver rejects
/// raw mode the adapter falls back to the normal shared pipeline.
/// </summary>
public sealed class WasapiCaptureSource : IAudioCaptureSource
{
    private readonly WasapiRecorder _recorder;
    private readonly MMDevice _device;
    private readonly ILog _log;
    private readonly ManualResetEventSlim _stopped = new(false);
    private bool _stopping;
    private bool _disposed;

    public WasapiCaptureSource(MMDevice device, ILog log)
    {
        _device = device;
        _log = log;
        _recorder = Build(device, log);
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    private static WasapiRecorder Build(MMDevice device, ILog log)
    {
        WasapiRecorderBuilder Builder() => new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(100)
            .WithMmcssThreadPriority("Audio");

        try
        {
            return Builder().WithRawMode().Build();
        }
        catch (Exception ex)
        {
            log.Warn("Raw WASAPI capture is unavailable; using the shared pipeline instead", ex);
            return Builder().Build();
        }
    }

    public event CaptureAudioHandler? DataAvailable;

    /// <summary>Raised with the device exception; null means a requested stop finished normally.</summary>
    public event Action<Exception?>? Stopped;

    public WaveFormat WaveFormat => _recorder.WaveFormat;

    public string? DeviceName => _recorder.DeviceFriendlyName;

    public SourceFormat SourceFormat
    {
        get
        {
            var format = WaveFormat;
            if (format is WaveFormatExtensible extensible)
            {
                format = extensible.ToStandardWaveFormat();
            }

            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
            return new SourceFormat(format.SampleRate, format.Channels, format.BitsPerSample, isFloat);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stopping = false;
        _stopped.Reset();
        _recorder.StartRecording();
    }

    /// <summary>Stops the device and waits for the capture callback to finish (bounded).</summary>
    public void Stop()
    {
        if (_disposed) return;
        _stopping = true;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception ex)
        {
            _log.Warn("Stopping WASAPI capture reported an error", ex);
        }

        if (!_stopped.Wait(TimeSpan.FromSeconds(3)))
        {
            _log.Warn("WASAPI capture stop did not acknowledge within 3 s.");
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_stopping || buffer.IsEmpty) return;
        try
        {
            DataAvailable?.Invoke(buffer);
        }
        catch (Exception ex)
        {
            _log.Error("Capture data handler failed", ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopped.Set();
        var exception = _stopping ? null : e.Exception;
        if (exception is not null)
        {
            _log.Error("WASAPI capture stopped unexpectedly", exception);
        }

        try
        {
            Stopped?.Invoke(exception);
        }
        catch (Exception ex)
        {
            _log.Error("Capture stop handler failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _recorder.DataAvailable -= OnDataAvailable;
            _recorder.RecordingStopped -= OnRecordingStopped;
            _recorder.Dispose();
        }
        finally
        {
            _stopped.Dispose();
            try { _device.Dispose(); } catch { /* COM cleanup */ }
        }
    }
}
