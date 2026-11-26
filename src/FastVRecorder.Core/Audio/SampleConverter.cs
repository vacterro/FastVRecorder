using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Dsp;

namespace FastVRecorder.Core.Audio;

/// <summary>
/// Converts raw capture bytes (any supported <see cref="SourceFormat"/>) into whole frames of the
/// <see cref="TargetFormat"/>: decode to float, map channels, resample, encode. Float samples are
/// never reinterpreted as integers. Runs on the writer thread, never in the audio callback.
/// </summary>
public sealed class SampleConverter
{
    private readonly SourceFormat _src;
    private readonly TargetFormat _dst;
    private readonly WdlResampler? _resampler;
    private readonly byte[] _carry; // partial frame left over from the previous chunk
    private int _carryLength;
    private float[] _decoded = new float[4096];
    private float[] _mapped = new float[4096];
    private float[] _resampled = new float[4096];

    public SampleConverter(SourceFormat source, TargetFormat target)
    {
        if (!source.IsSupported(out var err)) throw new NotSupportedException(err);
        if (!target.IsValid(out err)) throw new ArgumentException(err, nameof(target));
        _src = source;
        _dst = target;
        _carry = new byte[source.BlockAlign];
        if (source.SampleRate != target.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(source.SampleRate, target.SampleRate);
        }
    }

    public SourceFormat Source => _src;
    public TargetFormat Target => _dst;

    /// <summary>Upper bound of output bytes produced for <paramref name="inputBytes"/> of input.</summary>
    public int MaxOutputBytes(int inputBytes)
    {
        var frames = (inputBytes + _carry.Length) / _src.BlockAlign + 1;
        var outFrames = (long)Math.Ceiling(frames * (double)_dst.SampleRate / _src.SampleRate) + 64;
        return (int)(outFrames * _dst.BlockAlign);
    }

    /// <summary>
    /// Converts <paramref name="input"/>, writing into <paramref name="output"/> (grown as needed).
    /// Returns the number of output bytes; <paramref name="peak"/> is the absolute peak (0..1+) of the input.
    /// </summary>
    public int Convert(ReadOnlySpan<byte> input, ref byte[] output, out float peak)
    {
        peak = 0f;
        var block = _src.BlockAlign;

        // Re-assemble a frame split across callbacks.
        int framesFromCarry = 0;
        Span<byte> carryFrame = stackalloc byte[0];
        if (_carryLength > 0)
        {
            var need = block - _carryLength;
            var take = Math.Min(need, input.Length);
            input[..take].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += take;
            input = input[take..];
            if (_carryLength < block) return 0;
            framesFromCarry = 1;
        }

        var frames = input.Length / block + framesFromCarry;
        var tail = input.Length % block;
        if (frames == 0)
        {
            input[^tail..].CopyTo(_carry);
            _carryLength = tail;
            return 0;
        }

        var srcSamples = frames * _src.Channels;
        EnsureCapacity(ref _decoded, srcSamples);
        var decodedSpan = _decoded.AsSpan(0, srcSamples);
        var offset = 0;
        if (framesFromCarry == 1)
        {
            Decode(_carry, decodedSpan[.._src.Channels]);
            offset = _src.Channels;
            _carryLength = 0;
        }
        Decode(input[..(input.Length - tail)], decodedSpan[offset..]);
        if (tail > 0)
        {
            input[^tail..].CopyTo(_carry);
            _carryLength = tail;
        }

        peak = Peak(decodedSpan);

        // Channel mapping.
        var dstCh = _dst.Channels;
        var mappedSamples = frames * dstCh;
        EnsureCapacity(ref _mapped, mappedSamples);
        var mapped = _mapped.AsSpan(0, mappedSamples);
        MapChannels(decodedSpan, _src.Channels, mapped, dstCh, frames);

        // Resampling.
        ReadOnlySpan<float> final = mapped;
        var outFrames = frames;
        if (_resampler is not null)
        {
            var maxOut = (int)Math.Ceiling(frames * (double)_dst.SampleRate / _src.SampleRate) + 64;
            EnsureCapacity(ref _resampled, maxOut * dstCh);
            _resampler.ResamplePrepare(frames, dstCh, out var inBuf);
            mapped.CopyTo(inBuf);
            outFrames = _resampler.ResampleOut(_resampled.AsSpan(0, maxOut * dstCh), frames, maxOut, dstCh);
            final = _resampled.AsSpan(0, outFrames * dstCh);
        }

        var outBytes = outFrames * _dst.BlockAlign;
        if (output.Length < outBytes) output = new byte[Math.Max(outBytes, output.Length * 2)];
        Encode(final, output.AsSpan(0, outBytes));
        return outBytes;
    }

    private void Decode(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var bits = _src.BitsPerSample;
        if (_src.IsFloat)
        {
            if (bits == 32)
            {
                MemoryMarshal.Cast<byte, float>(src)[..dst.Length].CopyTo(dst);
            }
            else
            {
                var d = MemoryMarshal.Cast<byte, double>(src);
                for (var i = 0; i < dst.Length; i++) dst[i] = (float)d[i];
            }
            // Sanitize NaN/Inf from misbehaving drivers so they cannot poison the file.
            for (var i = 0; i < dst.Length; i++)
                if (!float.IsFinite(dst[i])) dst[i] = 0f;
            return;
        }
        switch (bits)
        {
            case 8:
                for (var i = 0; i < dst.Length; i++) dst[i] = (src[i] - 128) / 128f;
                break;
            case 16:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] = BinaryPrimitives.ReadInt16LittleEndian(src[(i * 2)..]) / 32768f;
                break;
            case 24:
                for (var i = 0; i < dst.Length; i++)
                {
                    var j = i * 3;
                    var v = (src[j] | (src[j + 1] << 8) | (src[j + 2] << 16)) << 8 >> 8;
                    dst[i] = v / 8388608f;
                }
                break;
            case 32:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] = BinaryPrimitives.ReadInt32LittleEndian(src[(i * 4)..]) / 2147483648f;
                break;
            default:
                throw new NotSupportedException($"PCM width {bits}");
        }
    }

    internal static void MapChannels(ReadOnlySpan<float> src, int srcCh, Span<float> dst, int dstCh, int frames)
    {
        if (srcCh == dstCh)
        {
            src[..(frames * srcCh)].CopyTo(dst);
            return;
        }
        if (dstCh == 1)
        {
            var inv = 1f / srcCh;
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var b = f * srcCh;
                for (var c = 0; c < srcCh; c++) sum += src[b + c];
                dst[f] = sum * inv;
            }
            return;
        }
        // dstCh == 2
        for (var f = 0; f < frames; f++)
        {
            var b = f * srcCh;
            if (srcCh == 1)
            {
                dst[f * 2] = src[b];
                dst[f * 2 + 1] = src[b];
            }
            else
            {
                dst[f * 2] = src[b];
                dst[f * 2 + 1] = src[b + 1];
            }
        }
    }

    private void Encode(ReadOnlySpan<float> src, Span<byte> dst)
    {
        switch (_dst.Encoding)
        {
            case SampleEncoding.Pcm16:
                for (var i = 0; i < src.Length; i++)
                {
                    var v = Math.Clamp(src[i], -1f, 1f);
                    var s = (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(dst[(i * 2)..], s);
                }
                break;
            case SampleEncoding.Pcm24:
                for (var i = 0; i < src.Length; i++)
                {
                    var v = Math.Clamp(src[i], -1f, 1f);
                    var s = Math.Clamp((int)MathF.Round(v * 8388607f), -8388608, 8388607);
                    var j = i * 3;
                    dst[j] = (byte)s;
                    dst[j + 1] = (byte)(s >> 8);
                    dst[j + 2] = (byte)(s >> 16);
                }
                break;
            case SampleEncoding.Float32:
                MemoryMarshal.AsBytes(src).CopyTo(dst);
                break;
        }
    }

    internal static float Peak(ReadOnlySpan<float> samples)
    {
        var p = 0f;
        foreach (var s in samples)
        {
            var a = MathF.Abs(s);
            if (a > p) p = a;
        }
        return p;
    }

    private static void EnsureCapacity(ref float[] buffer, int needed)
    {
        if (buffer.Length < needed) buffer = new float[Math.Max(needed, buffer.Length * 2)];
    }
}
