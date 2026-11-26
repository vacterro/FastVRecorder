using FastVRecorder.Core.Diagnostics;

namespace FastVRecorder.Infrastructure.Audio;

public sealed class WasapiCaptureFactory : IAudioCaptureFactory
{
    private readonly AudioDeviceService _devices;
    private readonly ILog _log;

    public WasapiCaptureFactory(AudioDeviceService devices, ILog log)
    {
        _devices = devices;
        _log = log;
    }

    public IAudioCaptureSource? TryCreate(string? deviceId, out string? error)
    {
        var device = _devices.ResolveDevice(deviceId);
        if (device is null)
        {
            error = "The selected microphone is not available. Choose another device in Settings.";
            return null;
        }

        try
        {
            error = null;
            return new WasapiCaptureSource(device, _log);
        }
        catch (Exception ex)
        {
            _log.Error("Cannot open the microphone", ex);
            try { device.Dispose(); } catch { /* COM cleanup */ }
            error = "The microphone could not be opened: " + ex.Message;
            return null;
        }
    }
}
