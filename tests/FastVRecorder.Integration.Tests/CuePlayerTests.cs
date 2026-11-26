using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Infrastructure.Audio;
using FastVRecorder.Infrastructure.Notifications;
using Xunit;

namespace FastVRecorder.Integration.Tests;

public sealed class CuePlayerTests : TempDirectoryTest
{
    [Fact]
    public void MissingCue_DoesNotThrow()
    {
        var media = Path.Combine(Root, "media");
        Directory.CreateDirectory(media);
        using var player = new CuePlayer(media, NullLog.Instance);

        player.Play(CuePlayer.StartCue, 50);
        player.Play("does-not-exist", 100);
        player.Stop();
    }

    [Fact]
    public void WasapiCuePath_WithDeviceEnumeration_DoesNotThrow()
    {
        var media = Path.Combine(Root, "media");
        Directory.CreateDirectory(media);
        var path = Path.Combine(media, "start.wav");
        using (var writer = WavStreamWriter.CreateNew(path, TargetFormat.Standard))
        {
            writer.Write(new byte[TargetFormat.Standard.AverageBytesPerSecond / 20]);
            writer.Complete();
        }

        using var devices = new AudioDeviceService(NullLog.Instance);
        using var player = new CuePlayer(media, NullLog.Instance, devices);
        player.Play(CuePlayer.StartCue, 20, outputDeviceId: null);
        Thread.Sleep(300);
        player.Stop();
    }

    [Fact]
    public void ZeroVolume_IsANoOp()
    {
        var media = Path.Combine(Root, "media");
        Directory.CreateDirectory(media);
        using var player = new CuePlayer(media, NullLog.Instance);

        player.Play(CuePlayer.ErrorCue, 0);
    }

    [Fact]
    public void ExistingCue_PlaysOrFailsSoftly()
    {
        var media = Path.Combine(Root, "media");
        Directory.CreateDirectory(media);
        var path = Path.Combine(media, "done.wav");
        using (var writer = WavStreamWriter.CreateNew(path, TargetFormat.Standard))
        {
            writer.Write(new byte[TargetFormat.Standard.AverageBytesPerSecond / 10]);
            writer.Complete();
        }

        using var player = new CuePlayer(media, NullLog.Instance);
        player.Play(CuePlayer.DoneCue, 20);
        Thread.Sleep(100);
        player.Stop();
    }
}
