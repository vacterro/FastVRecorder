using System.Buffers.Binary;
using FastVRecorder.Core.Audio;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class AudioPipelineHardeningTests
{
    [Fact]
    public void StereoDownmixAveragesChannels()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 2, 16, IsFloat: false),
            new TargetFormat(48000, 1, SampleEncoding.Pcm16));
        var input = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(0, 2), 1000);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(2, 2), 3000);

        var output = Array.Empty<byte>();
        var written = converter.Convert(input, ref output, out _);

        Assert.Equal(2, written);
        Assert.Equal(2000, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(0, 2)));
    }

    [Fact]
    public void ImpulseSurvivesOddChunkBoundaries()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 1, 16, IsFloat: false),
            new TargetFormat(48000, 1, SampleEncoding.Pcm16));
        var data = new byte[4800 * 2];
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2400 * 2, 2), 32767);

        var output = new byte[data.Length];
        var written = 0;
        var offset = 0;
        var sizes = new[] { 1, 7, 3, 11, 5 };
        var index = 0;
        while (offset < data.Length)
        {
            var take = Math.Min(sizes[index++ % sizes.Length], data.Length - offset);
            var chunkOutput = new byte[converter.MaxOutputBytes(take) + 16];
            var produced = converter.Convert(data.AsSpan(offset, take), ref chunkOutput, out _);
            Array.Copy(chunkOutput, 0, output, written, produced);
            written += produced;
            offset += take;
        }

        Assert.Equal(data.Length, written);
        Assert.InRange(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(2400 * 2, 2)), 32766, 32767);
    }

    [Fact]
    public void FloatInputIsClampedToPcmRange()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 1, 32, IsFloat: true),
            new TargetFormat(48000, 1, SampleEncoding.Pcm16));
        var input = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(0, 4), 2.5f);
        BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(4, 4), -2.5f);

        var output = Array.Empty<byte>();
        var written = converter.Convert(input, ref output, out var peak);

        Assert.Equal(4, written);
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(0, 2)));
        Assert.Equal(-short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(2, 2)));
        Assert.Equal(2.5f, peak, 3);
    }

    [Fact]
    public void Pcm24RoundTripKeepsAmplitudeAndFrameCount()
    {
        var converter = new SampleConverter(
            new SourceFormat(48000, 1, 16, IsFloat: false),
            new TargetFormat(48000, 1, SampleEncoding.Pcm24));
        var input = new byte[200];
        for (var i = 0; i < 100; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 2, 2), (short)(i * 300 - 15000));
        }

        var output = Array.Empty<byte>();
        var written = converter.Convert(input, ref output, out _);

        Assert.Equal(300, written);
        var first = ReadInt24(output, 0);
        var last = ReadInt24(output, 297);
        Assert.InRange(first, -3_850_000, -3_820_000);
        Assert.InRange(last, 3_750_000, 3_770_000);
    }

    [Fact]
    public void LongSineAcrossManyRingBufferChunksKeepsRms()
    {
        var format = new SourceFormat(48000, 1, 16, IsFloat: false);
        var converter = new SampleConverter(format, new TargetFormat(48000, 1, SampleEncoding.Pcm16));
        var output = new byte[format.BlockAlign * 48000 * 3 + 8192];
        var written = 0;
        double inputSumSquares = 0;
        var frame = 0;
        var random = new Random(123);
        while (frame < 48000 * 3)
        {
            var frames = Math.Min(random.Next(1, 97), 48000 * 3 - frame);
            var input = new byte[frames * 2];
            for (var i = 0; i < frames; i++, frame++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * 440 * frame / 48000.0) * 12000);
                BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 2, 2), value);
                inputSumSquares += value * value;
            }

            var chunk = new byte[converter.MaxOutputBytes(input.Length) + 16];
            var produced = converter.Convert(input, ref chunk, out _);
            Array.Copy(chunk, 0, output, written, produced);
            written += produced;
        }

        var sampleCount = written / 2;
        double outputSumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 2, 2));
            outputSumSquares += value * (double)value;
        }

        Assert.Equal(48000 * 3, sampleCount);
        var inputRms = Math.Sqrt(inputSumSquares / (48000 * 3));
        var outputRms = Math.Sqrt(outputSumSquares / sampleCount);
        Assert.InRange(outputRms, inputRms * 0.97, inputRms * 1.03);
    }

    private static int ReadInt24(byte[] buffer, int offset) =>
        (buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16)) << 8 >> 8;
}
