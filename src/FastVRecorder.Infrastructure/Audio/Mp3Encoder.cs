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

    /// <summary>
    /// Encodes a finished WAV through an owned temporary file, validates that the result is
    /// decodable and its duration is plausible, then publishes it without overwriting. The source
    /// WAV is never touched here; the caller keeps it until it has committed the result.
    /// </summary>
    public static bool TryEncode(string wavPath, string mp3Path, int bitrateKbps, ILog log, out string? error)
    {
        var temporary = mp3Path + ".tmp-" + Guid.NewGuid().ToString("N") + ".mp3";
        try
        {
            MediaFoundationApi.Startup();
            using var source = new WaveFileReader(wavPath);
            MediaFoundationEncoder.EncodeToMp3(source, temporary, Math.Clamp(bitrateKbps, 32, 320) * 1000);

            var info = new FileInfo(temporary);
            if (!info.Exists || info.Length < 512)
            {
                error = "The MP3 encoder produced no usable data.";
                return false;
            }

            using (var check = new MediaFoundationReader(temporary))
            {
                if (check.TotalTime <= TimeSpan.Zero)
                {
                    error = "The MP3 output has no decodable duration.";
                    return false;
                }

                var sourceDuration = source.TotalTime;
                var tolerance = TimeSpan.FromSeconds(Math.Max(2.0, sourceDuration.TotalSeconds * 0.1));
                if (Math.Abs((check.TotalTime - sourceDuration).TotalSeconds) > tolerance.TotalSeconds)
                {
                    error = $"The MP3 duration {check.TotalTime} is not plausible for a {sourceDuration} source.";
                    return false;
                }
            }

            if (File.Exists(mp3Path))
            {
                error = "The MP3 destination already exists; refusing to overwrite it.";
                return false;
            }

            File.Move(temporary, mp3Path, overwrite: false);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"MP3 encoding failed for {Path.GetFileName(wavPath)}", ex);
            error = ex.Message;
            TryDelete(temporary);
            return false;
        }
        finally
        {
            TryDelete(temporary);
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
