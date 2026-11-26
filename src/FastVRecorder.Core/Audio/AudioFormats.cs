namespace FastVRecorder.Core.Audio;

/// <summary>Sample encoding of the file the recorder writes to disk.</summary>
public enum SampleEncoding
{
    Pcm16,
    Pcm24,
    Float32,
}

/// <summary>The format written to disk. Immutable; validated before a recording starts.</summary>
public sealed record TargetFormat(int SampleRate, int Channels, SampleEncoding Encoding)
{
    public static readonly TargetFormat Standard = new(48000, 1, SampleEncoding.Pcm16);

    public const int MinSampleRate = 8000;
    public const int MaxSampleRate = 192000;

    public int BitsPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 16,
        SampleEncoding.Pcm24 => 24,
        SampleEncoding.Float32 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(Encoding)),
    };

    public int BytesPerSample => BitsPerSample / 8;
    public int BlockAlign => BytesPerSample * Channels;
    public int AverageBytesPerSecond => BlockAlign * SampleRate;
    public bool IsFloat => Encoding == SampleEncoding.Float32;

    public bool IsValid(out string? error)
    {
        if (SampleRate < MinSampleRate || SampleRate > MaxSampleRate)
        {
            error = $"Sample rate {SampleRate} Hz is outside {MinSampleRate}-{MaxSampleRate} Hz.";
            return false;
        }
        if (Channels is < 1 or > 2)
        {
            error = $"Channel count {Channels} is not supported (1 or 2).";
            return false;
        }
        if (!Enum.IsDefined(Encoding))
        {
            error = "Unknown sample encoding.";
            return false;
        }
        error = null;
        return true;
    }

    public string Describe()
    {
        var rate = SampleRate % 1000 == 0 ? $"{SampleRate / 1000} kHz" : $"{SampleRate / 1000.0:0.#} kHz";
        var ch = Channels == 1 ? "Mono" : "Stereo";
        var enc = Encoding switch
        {
            SampleEncoding.Pcm16 => "16-bit",
            SampleEncoding.Pcm24 => "24-bit",
            _ => "32-bit float",
        };
        return $"{rate} · {ch} · {enc}";
    }
}

/// <summary>Format delivered by a capture device (may differ from <see cref="TargetFormat"/>).</summary>
public readonly record struct SourceFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    public int BlockAlign => Channels * (BitsPerSample / 8);

    public bool IsSupported(out string? error)
    {
        if (SampleRate <= 0 || Channels <= 0)
        {
            error = "Capture format has no sample rate or channels.";
            return false;
        }
        if (IsFloat && BitsPerSample is not (32 or 64))
        {
            error = $"Unsupported float capture width {BitsPerSample}.";
            return false;
        }
        if (!IsFloat && BitsPerSample is not (8 or 16 or 24 or 32))
        {
            error = $"Unsupported PCM capture width {BitsPerSample}.";
            return false;
        }
        error = null;
        return true;
    }

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit {(IsFloat ? "float" : "PCM")}";
}
