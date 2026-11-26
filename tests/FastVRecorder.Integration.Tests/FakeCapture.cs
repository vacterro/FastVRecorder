using FastVRecorder.Core.Audio;
using FastVRecorder.Infrastructure.Audio;

namespace FastVRecorder.Integration.Tests;

/// <summary>Deterministic capture source: 960 silent frames every 20 ms.</summary>
public sealed class FakeCaptureSource : IAudioCaptureSource
{
    private readonly SourceFormat _format;
    private Thread? _thread;
    private volatile bool _running;
    private bool _stoppedRaised;

    public FakeCaptureSource(SourceFormat format) => _format = format;

    public event CaptureAudioHandler? DataAvailable;
    public event Action<Exception?>? Stopped;

    public SourceFormat SourceFormat => _format;

    public string? DeviceName => "Fake capture source";

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "fake capture" };
        _thread.Start();
    }

    private void Loop()
    {
        var buffer = new byte[(_format.BitsPerSample / 8) * _format.Channels * 960];
        if (_format.BitsPerSample == 16 && !_format.IsFloat)
        {
            // A deterministic 1 kHz tone so tests can prove the engine preserves signal.
            double phase = 0;
            for (var frame = 0; frame < 960; frame++)
            {
                var value = (short)(Math.Sin(phase) * 8000);
                var offset = frame * _format.Channels * 2;
                buffer[offset] = (byte)value;
                buffer[offset + 1] = (byte)(value >> 8);
                phase += 2 * Math.PI * 1000 / _format.SampleRate;
            }
        }

        while (_running)
        {
            DataAvailable?.Invoke(buffer);
            Thread.Sleep(20);
        }
    }

    public void Stop()
    {
        _running = false;
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            _thread.Join(1000);
        }

        if (!_stoppedRaised)
        {
            _stoppedRaised = true;
            Stopped?.Invoke(null);
        }
    }

    public void Dispose() => Stop();
}

public sealed class FakeCaptureFactory : IAudioCaptureFactory
{
    private readonly SourceFormat _format;
    private readonly bool _fail;

    public FakeCaptureFactory(SourceFormat format, bool fail = false)
    {
        _format = format;
        _fail = fail;
    }

    public int Created { get; private set; }

    public IAudioCaptureSource? TryCreate(string? deviceId, out string? error)
    {
        if (_fail)
        {
            error = "No microphone is available.";
            return null;
        }

        Created++;
        error = null;
        return new FakeCaptureSource(_format);
    }
}