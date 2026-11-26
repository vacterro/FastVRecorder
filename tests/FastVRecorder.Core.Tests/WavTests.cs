using System.Buffers.Binary;
using FastVRecorder.Core.Audio;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class WavTests : TempDirectoryTest
{
    [Theory]
    [InlineData(SampleEncoding.Pcm16)]
    [InlineData(SampleEncoding.Pcm24)]
    [InlineData(SampleEncoding.Float32)]
    public void Header_BuildAndParse_RoundTrips(SampleEncoding encoding)
    {
        var format = new TargetFormat(48000, 1, encoding);
        var header = WavHeader.Build(format, 12345);
        using var stream = new MemoryStream(header);

        Assert.True(WavHeader.TryParse(stream, out var info, out var error), error);
        Assert.NotNull(info);
        Assert.Equal(format.SampleRate, info!.SampleRate);
        Assert.Equal(format.Channels, info.Channels);
        Assert.Equal(format.BitsPerSample, info.BitsPerSample);
        Assert.Equal(format.BlockAlign, info.BlockAlign);
        Assert.Equal(12345, info.DeclaredDataBytes);
        Assert.Equal(WavHeader.HeaderLength(format), info.DataOffset);
        Assert.Equal(encoding == SampleEncoding.Float32, info.IsFloat);
    }

    [Fact]
    public void Writer_ProducesVerifiedFileAndPatchesSizes()
    {
        var path = Path.Combine(Root, "test.wav");
        var format = TargetFormat.Standard;
        var data = new byte[format.AverageBytesPerSecond]; // exactly one second
        Random.Shared.NextBytes(data);

        using (var writer = WavStreamWriter.CreateNew(path, format))
        {
            writer.Write(data);
            Assert.Equal(data.Length, writer.DataBytes);
            Assert.Equal(TimeSpan.FromSeconds(1), writer.Duration);
            writer.Checkpoint();
            writer.Complete();
        }

        Assert.True(WavVerifier.Verify(path, format, data.Length, out var error), error);
        Assert.True(WavHeader.TryParse(path, out var info, out error), error);
        Assert.Equal(data.Length, info!.DeclaredDataBytes);
        Assert.Equal(WavHeader.HeaderLength(format) + data.Length, info.FileLength);
    }

    [Fact]
    public void Writer_RefusesToOverwriteExistingFile()
    {
        var path = Path.Combine(Root, "exists.wav");
        File.WriteAllText(path, "keep me");

        Assert.Throws<IOException>(() => WavStreamWriter.CreateNew(path, TargetFormat.Standard));
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public void Writer_RejectsNonFrameWritesAndOversizedSegments()
    {
        var path = Path.Combine(Root, "frames.wav");
        using var writer = WavStreamWriter.CreateNew(path, TargetFormat.Standard, maxDataBytes: TargetFormat.Standard.BlockAlign * 4);

        Assert.Throws<ArgumentException>(() => writer.Write(new byte[3]));
        writer.Write(new byte[TargetFormat.Standard.BlockAlign * 4]);
        Assert.Throws<InvalidOperationException>(() => writer.Write(new byte[TargetFormat.Standard.BlockAlign]));
    }

    [Fact]
    public void Parser_RejectsNonWavAndTruncatedFiles()
    {
        var path = Path.Combine(Root, "bad.bin");
        File.WriteAllBytes(path, new byte[40]);
        Assert.False(WavHeader.TryParse(path, out _, out _));

        using var stream = new MemoryStream(new byte[8]);
        Assert.False(WavHeader.TryParse(stream, out _, out _));
    }

    [Fact]
    public void MaxDataBytes_StaysBelowTheRiffLimitAndIsFrameAligned()
    {
        var format = new TargetFormat(48000, 1, SampleEncoding.Pcm24);
        var max = WavHeader.MaxDataBytes(format);
        Assert.True(max < WavHeader.MaxRiffSize);
        Assert.Equal(0, max % format.BlockAlign);
    }

    [Fact]
    public void FloatWriter_EmitsFactChunk()
    {
        var path = Path.Combine(Root, "float.wav");
        var format = new TargetFormat(48000, 1, SampleEncoding.Float32);
        using (var writer = WavStreamWriter.CreateNew(path, format))
        {
            writer.Write(new byte[format.BlockAlign * 480]);
            writer.Complete();
        }

        var bytes = File.ReadAllBytes(path);
        var text = System.Text.Encoding.ASCII.GetString(bytes, 0, 58);
        Assert.Contains("fact", text);
        Assert.Equal((uint)480, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(WavHeader.FactFramesOffset, 4)));
    }
}
