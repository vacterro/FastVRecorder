using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Infrastructure.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FastVRecorder.Infrastructure.Notifications;

/// <summary>
/// Plays short cue WAVs asynchronously through the selected render endpoint (WASAPI shared mode,
/// with a WaveOut fallback). Never throws into the caller, never queues sounds, and never
/// recurses on its own failure.
/// </summary>
public sealed class CuePlayer : IDisposable
{
    public const string StartCue = "start";
    public const string DoneCue = "done";
    public const string ErrorCue = "error";

    private readonly string _mediaDir;
    private readonly ILog _log;
    private readonly AudioDeviceService? _devices;
    private readonly object _sync = new();
    private readonly HashSet<string> _missingWarned = new(StringComparer.OrdinalIgnoreCase);
    private WasapiPlayer? _wasapi;
    private WaveOut? _waveOut;
    private MMDevice? _device;
    private AudioFileReader? _reader;
    private bool _disposed;

    public CuePlayer(string mediaDir, ILog log, AudioDeviceService? devices = null)
    {
        _mediaDir = mediaDir;
        _log = log;
        _devices = devices;
    }

    public void Play(string cue, int volumePercent, string? outputDeviceId = null)
    {
        if (_disposed || volumePercent <= 0) return;
        var path = Path.Combine(_mediaDir, cue + ".wav");
        if (!File.Exists(path))
        {
            if (_missingWarned.Add(cue))
            {
                _log.Warn($"Notification sound '{cue}.wav' is missing; notifications for it are disabled.");
            }

            return;
        }

        var volume = Math.Clamp(volumePercent / 100f, 0f, 1f);
        lock (_sync)
        {
            StopLocked();
            if (_devices is not null && TryStartWasapi(path, volume, outputDeviceId))
            {
                return;
            }

            StartWaveOut(path, volume);
        }
    }

    private bool TryStartWasapi(string path, float volume, string? outputDeviceId)
    {
        try
        {
            _device = _devices!.ResolveRenderDevice(outputDeviceId);
            var builder = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(100);
            if (_device is not null) builder = builder.WithDevice(_device);
            var player = builder.Build();
            var reader = new AudioFileReader(path) { Volume = volume };
            player.Init(reader);
            player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    _log.Warn("Notification playback stopped with an error", e.Exception);
                }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_wasapi, player)) StopLocked();
                    }
                });
            };
            _reader = reader;
            _wasapi = player;
            player.Play();
            _log.Info($"Notification '{Path.GetFileNameWithoutExtension(path)}' -> WASAPI output '{_device?.FriendlyName ?? "system default"}'.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("WASAPI notification output failed; using WaveOut", ex);
            StopLocked();
            return false;
        }
    }

    private void StartWaveOut(string path, float volume)
    {
        try
        {
            var reader = new AudioFileReader(path) { Volume = volume };
            var output = new WaveOut();
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    _log.Warn("Notification playback stopped with an error", e.Exception);
                }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_waveOut, output)) StopLocked();
                    }
                });
            };
            output.Init(reader);
            _reader = reader;
            _waveOut = output;
            output.Play();
            _log.Info($"Notification '{Path.GetFileNameWithoutExtension(path)}' -> WaveOut default output.");
        }
        catch (Exception ex)
        {
            _log.Warn("Cannot play notification sound", ex);
            StopLocked();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        var wasapi = _wasapi;
        var waveOut = _waveOut;
        var reader = _reader;
        var device = _device;
        _wasapi = null;
        _waveOut = null;
        _reader = null;
        _device = null;
        try { wasapi?.Stop(); } catch { /* best effort */ }
        try { wasapi?.Dispose(); } catch { /* best effort */ }
        try { waveOut?.Stop(); } catch { /* best effort */ }
        try { waveOut?.Dispose(); } catch { /* best effort */ }
        try { reader?.Dispose(); } catch { /* best effort */ }
        try { device?.Dispose(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
