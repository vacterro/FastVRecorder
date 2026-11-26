using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace FastVRecorder.Infrastructure.Audio;

/// <summary>
/// MP3 output through Windows Media Foundation (present on desktop Windows, no external codec).
/// The recorder always produces a recoverable WAV first; MP3 is encoded after the stop.
/// </summary>
public static class Mp3Encoder
{
    private static readonly object ProbeLock = new();
    private static bool? _available;

    public static bool IsAvailable
    {
        get
        {
            lock (ProbeLock)
            {
                _available ??= Probe();
                return _available.Value;
            }
        }
    }

    private static bool Probe()
    {
        var wav = Path.Combine(Path.GetTempPath(), $"fvr-mf-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.wav");
        var mp3 = Path.ChangeExtension(wav, ".mp3");
        try
        {
            MediaFoundationApi.Startup();
            var format = TargetFormat.Standard;
            using (var writer = WavStreamWriter.CreateNew(wav, format))
            {
                var silence = new byte[format.BlockAlign * format.SampleRate / 10];
                writer.Write(silence);
                writer.Complete();
            }

            using (var reader = new WaveFileReader(wav))
            {
                MediaFoundationEncoder.EncodeToMp3(reader, mp3, 96_000);
            }

            return new FileInfo(mp3).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            TryDelete(wav);
            TryDelete(mp3);
        }
    }

    /// <summary>Encodes a finished WAV. Returns false and keeps the source on any failure.</summary>
    public static bool TryEncode(string wavPath, string mp3Path, int bitrateKbps, ILog log, out string? error)
    {
        try
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(wavPath);
            MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, Math.Clamp(bitrateKbps, 32, 320) * 1000);
            var info = new FileInfo(mp3Path);
            if (!info.Exists || info.Length < 128)
            {
                error = "The MP3 encoder produced no usable data.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"MP3 encoding failed for {Path.GetFileName(wavPath)}", ex);
            error = ex.Message;
            TryDelete(mp3Path);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // probe cleanup only
        }
    }
}
