using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Infrastructure.Audio;
using NAudio.Wave;
using Xunit;

namespace FastVRecorder.Integration.Tests;

public sealed class Mp3EncoderTests : TempDirectoryTest
{
    [Fact]
    public void EncodesDecodableMp3AndKeepsTheSourceWav()
    {
        if (!Mp3Encoder.IsAvailable) return;

        var wav = Path.Combine(Root, "source.wav");
        using (var writer = WavStreamWriter.CreateNew(wav, TargetFormat.Standard))
        {
            writer.Write(new byte[TargetFormat.Standard.AverageBytesPerSecond]);
            writer.Complete();
        }

        var mp3 = Path.Combine(Root, "source.mp3");
        Assert.True(Mp3Encoder.TryEncode(wav, mp3, 96, NullLog.Instance, out var error), error);
        Assert.True(new FileInfo(mp3).Length > 512);
        Assert.True(File.Exists(wav));
        using var reader = new MediaFoundationReader(mp3);
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.8, 1.4);
        Assert.Empty(Directory.GetFiles(Root, "*.tmp-*"));
    }

    [Fact]
    public void ExistingDestinationIsNeverOverwritten()
    {
        if (!Mp3Encoder.IsAvailable) return;

        var wav = Path.Combine(Root, "source.wav");
        using (var writer = WavStreamWriter.CreateNew(wav, TargetFormat.Standard))
        {
            writer.Write(new byte[TargetFormat.Standard.AverageBytesPerSecond / 10]);
            writer.Complete();
        }

        var mp3 = Path.Combine(Root, "taken.mp3");
        File.WriteAllText(mp3, "keep-me");

        Assert.False(Mp3Encoder.TryEncode(wav, mp3, 96, NullLog.Instance, out _));
        Assert.Equal("keep-me", File.ReadAllText(mp3));
        Assert.True(File.Exists(wav));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp-*"));
    }

    [Fact]
    public void InvalidSourceReturnsFalseWithoutLeavingOutput()
    {
        var wav = Path.Combine(Root, "not-a-wav.bin");
        File.WriteAllText(wav, "not audio");
        var mp3 = Path.Combine(Root, "broken.mp3");

        Assert.False(Mp3Encoder.TryEncode(wav, mp3, 96, NullLog.Instance, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(File.Exists(mp3));
    }
}
