using System.Buffers.Binary;
using System.Text;

namespace FastVRecorder.Core.Audio;

/// <summary>Parsed description of a RIFF/WAVE file.</summary>
public sealed record WavInfo(
    int FormatTag,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    int BlockAlign,
    long DataOffset,
    long DeclaredDataBytes,
    long FileLength)
{
    public bool IsFloat => FormatTag == WavHeader.FormatIeeeFloat;

    /// <summary>Audio bytes physically present after the data chunk start, trimmed to whole frames.</summary>
    public long PhysicalDataBytes
    {
        get
        {
            var available = Math.Max(0, FileLength - DataOffset);
            return BlockAlign > 0 ? available - (available % BlockAlign) : 0;
        }
    }

    public TimeSpan Duration(long dataBytes) =>
        BlockAlign > 0 && SampleRate > 0
            ? TimeSpan.FromSeconds((double)(dataBytes / BlockAlign) / SampleRate)
            : TimeSpan.Zero;

    public TargetFormat? ToTargetFormat()
    {
        SampleEncoding? enc = (FormatTag, BitsPerSample) switch
        {
            (WavHeader.FormatPcm, 16) => SampleEncoding.Pcm16,
            (WavHeader.FormatPcm, 24) => SampleEncoding.Pcm24,
            (WavHeader.FormatIeeeFloat, 32) => SampleEncoding.Float32,
            _ => null,
        };
        return enc is null ? null : new TargetFormat(SampleRate, Channels, enc.Value);
    }
}

/// <summary>
/// Canonical WAV header writer/reader. The recorder always writes one of two fixed layouts so the
/// header can be patched in place and re-derived during crash recovery:
/// PCM   : RIFF(12) fmt(8+16) data(8)            -> data at 44
/// Float : RIFF(12) fmt(8+18) fact(8+4) data(8)  -> data at 58
/// </summary>
public static class WavHeader
{
    public const int FormatPcm = 1;
    public const int FormatIeeeFloat = 3;
    public const int FormatExtensible = 0xFFFE;

    /// <summary>Largest RIFF payload representable in a 32-bit size field.</summary>
    public const long MaxRiffSize = uint.MaxValue;

    public static int HeaderLength(TargetFormat format) => format.IsFloat ? 58 : 44;

    public static int DataSizeOffset(TargetFormat format) => HeaderLength(format) - 4;

    public const int FactFramesOffset = 46;

    /// <summary>Largest data chunk that keeps the RIFF size valid for this format, frame aligned.</summary>
    public static long MaxDataBytes(TargetFormat format)
    {
        var max = MaxRiffSize - (HeaderLength(format) - 8);
        return max - (max % format.BlockAlign);
    }

    public static byte[] Build(TargetFormat format, long dataBytes)
    {
        var header = new byte[HeaderLength(format)];
        Write(header, format, dataBytes);
        return header;
    }

    public static void Write(Span<byte> header, TargetFormat format, long dataBytes)
    {
        if (header.Length < HeaderLength(format)) throw new ArgumentException("Header buffer too small.", nameof(header));
        var data = (uint)Math.Clamp(dataBytes, 0, MaxDataBytes(format));
        var riff = (uint)(HeaderLength(format) - 8 + data);

        WriteAscii(header[0..], "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], riff);
        WriteAscii(header[8..], "WAVE");
        WriteAscii(header[12..], "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], format.IsFloat ? 18u : 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], (ushort)(format.IsFloat ? FormatIeeeFloat : FormatPcm));
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)format.AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)format.BitsPerSample);
        if (format.IsFloat)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header[36..], 0); // cbSize
            WriteAscii(header[38..], "fact");
            BinaryPrimitives.WriteUInt32LittleEndian(header[42..], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(header[46..], data / (uint)format.BlockAlign);
            WriteAscii(header[50..], "data");
            BinaryPrimitives.WriteUInt32LittleEndian(header[54..], data);
        }
        else
        {
            WriteAscii(header[36..], "data");
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], data);
        }
    }

    /// <summary>
    /// Walks RIFF chunks to find fmt and data. Tolerates a truncated/zero data size (crash) and
    /// unknown chunks. Returns false when the file is not a RIFF/WAVE stream at all.
    /// </summary>
    public static bool TryParse(Stream stream, out WavInfo? info, out string? error)
    {
        info = null;
        error = null;
        var length = stream.Length;
        if (length < 12)
        {
            error = "File is shorter than a RIFF header.";
            return false;
        }
        Span<byte> buf = stackalloc byte[40];
        stream.Position = 0;
        stream.ReadExactly(buf[..12]);
        if (!IsAscii(buf[0..4], "RIFF") || !IsAscii(buf[8..12], "WAVE"))
        {
            error = "Not a RIFF/WAVE file.";
            return false;
        }

        int? tag = null, channels = null, rate = null, bits = null, align = null;
        long pos = 12;
        while (pos + 8 <= length)
        {
            stream.Position = pos;
            stream.ReadExactly(buf[..8]);
            var id = Encoding.ASCII.GetString(buf[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
            var body = pos + 8;
            if (id == "fmt ")
            {
                if (size < 16 || body + 16 > length)
                {
                    error = "fmt chunk is truncated.";
                    return false;
                }
                var take = (int)Math.Min(size, 40);
                stream.ReadExactly(buf[..take]);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(buf[0..]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(buf[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(buf[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(buf[14..]);
                if (tag == FormatExtensible && take >= 40)
                {
                    // SubFormat GUID first two bytes carry the effective format tag.
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(buf[24..]);
                }
            }
            else if (id == "data")
            {
                if (tag is null)
                {
                    error = "data chunk precedes fmt chunk.";
                    return false;
                }
                info = new WavInfo(tag.Value, channels!.Value, rate!.Value, bits!.Value, align!.Value, body, size, length);
                if (info.Channels <= 0 || info.SampleRate <= 0 || info.BlockAlign <= 0)
                {
                    error = "fmt chunk carries zero channels, rate or block align.";
                    info = null;
                    return false;
                }
                return true;
            }
            if (size == 0 && id != "fact" && id != "LIST")
            {
                // Unknown empty chunk; continue scanning.
            }
            pos = body + size + (size & 1);
        }
        error = tag is null ? "No fmt chunk found." : "No data chunk found.";
        return false;
    }

    public static bool TryParse(string path, out WavInfo? info, out string? error)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return TryParse(fs, out info, out error);
    }

    private static void WriteAscii(Span<byte> dst, string s)
    {
        for (var i = 0; i < s.Length; i++) dst[i] = (byte)s[i];
    }

    private static bool IsAscii(ReadOnlySpan<byte> src, string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (src[i] != (byte)s[i]) return false;
        return true;
    }
}
