using FastVRecorder.Core.Diagnostics;
using NAudio.CoreAudioApi;

namespace FastVRecorder.Infrastructure.Audio;

public sealed record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault)
{
    public override string ToString() => IsDefault ? FriendlyName + " (default)" : FriendlyName;
}

/// <summary>WASAPI capture endpoint enumeration and resolution (shared mode).</summary>
public sealed class AudioDeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ILog _log;
    private bool _disposed;

    public AudioDeviceService(ILog log) => _log = log;

    /// <summary>Empty device id means "follow the Windows default at start time".</summary>
    public const string DefaultDeviceId = "default";

    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
    {
        var result = new List<AudioDeviceInfo>();
        string? defaultId = null;
        try
        {
            using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            defaultId = def.ID;
        }
        catch (Exception ex)
        {
            _log.Warn("No default capture endpoint: " + ex.Message);
        }

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Cannot enumerate capture devices", ex);
        }

        return result
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Active render endpoints for the notification output selector.</summary>
    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        var result = new List<AudioDeviceInfo>();
        string? defaultId = null;
        try
        {
            using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            defaultId = def.ID;
        }
        catch (Exception ex)
        {
            _log.Warn("No default render endpoint: " + ex.Message);
        }

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Cannot enumerate render devices", ex);
        }

        return result
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves the stored endpoint. Returns null when a specifically chosen device is gone.</summary>
    public MMDevice? ResolveDevice(string? storedId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storedId) || string.Equals(storedId, DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }

            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (string.Equals(device.ID, storedId, StringComparison.Ordinal))
                {
                    return device;
                }

                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Cannot resolve capture device", ex);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.Dispose();
    }

    /// <summary>Resolves a render endpoint; empty id means the Windows default output.</summary>
    public MMDevice? ResolveRenderDevice(string? storedId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storedId) || string.Equals(storedId, DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            }

            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (string.Equals(device.ID, storedId, StringComparison.Ordinal))
                {
                    return device;
                }

                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Cannot resolve render device", ex);
        }

        return null;
    }
}
