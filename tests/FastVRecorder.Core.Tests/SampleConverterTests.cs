using System.Buffers.Binary;
using FastVRecorder.Core.Audio;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class SampleConverterTests
{
    [Fact]
    public void FloatStereo_ConvertsToMonoPcm16_WithoutReinterpretingBits()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 2, 32, IsFloat: true),
            new TargetFormat(48000, 1, SampleEncoding.Pcm16));

        var frame = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(0, 4), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(4, 4), -0.5f);

        var output = Array.Empty<byte>();
        var written = converter.Convert(frame, ref output, out var peak);

        Assert.Equal(2, written);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(0, 2)));
        Assert.Equal(0.5f, peak, 3);
    }

    [Fact]
    public void Pcm16_ConvertsToPcm24_WithExpectedAmplitude()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 1, 16, IsFloat: false),
            new TargetFormat(48000, 1, SampleEncoding.Pcm24));

        var input = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(0, 2), 1000);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(2, 2), -1000);

        var output = Array.Empty<byte>();
        var written = converter.Convert(input, ref output, out var peak);

        Assert.Equal(6, written);
        var first = ReadInt24(output, 0);
        var second = ReadInt24(output, 3);
        Assert.InRange(first, 255_000, 256_500);
        Assert.InRange(second, -256_500, -255_000);
        Assert.InRange(peak, 0.02f, 0.04f);
    }

    [Fact]
    public void SplitFrames_AreReassembledAcrossChunks()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 2, 16, IsFloat: false),
            new TargetFormat(48000, 2, SampleEncoding.Pcm16));

        var frame = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(0, 2), 1000);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(2, 2), -1000);
        var output = Array.Empty<byte>();
        var total = 0;
        foreach (var b in frame)
        {
            total += converter.Convert(new[] { b }, ref output, out _);
        }

        Assert.Equal(4, total);
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(0, 2)));
        Assert.Equal(-1000, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(2, 2)));
    }

    [Fact]
    public void Resampling_ProducesApproximatelyTheRequestedRate()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 1, 16, IsFloat: false),
            new TargetFormat(24000, 1, SampleEncoding.Pcm16));

        var input = new byte[4800 * 2];
        var output = Array.Empty<byte>();
        var written = converter.Convert(input, ref output, out _);

        Assert.Equal(0, written % 2);
        var frames = written / 2;
        Assert.InRange(frames, 2200, 2600);
    }

    [Fact]
    public void NaNSamples_AreSanitized()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 1, 32, IsFloat: true),
            new TargetFormat(48000, 1, SampleEncoding.Pcm16));

        var input = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(input, float.NaN);
        var output = Array.Empty<byte>();
        var written = converter.Convert(input, ref output, out var peak);

        Assert.Equal(2, written);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(0, 2)));
        Assert.Equal(0f, peak);
    }

    private static int ReadInt24(byte[] buffer, int offset) =>
        (buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16)) << 8 >> 8;
}
