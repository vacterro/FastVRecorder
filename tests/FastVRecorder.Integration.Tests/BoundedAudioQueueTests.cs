using FastVRecorder.Infrastructure.Audio;
using Xunit;

namespace FastVRecorder.Integration.Tests;

public sealed class BoundedAudioQueueTests
{
    [Fact]
    public void PreservesByteOrderAcrossPartialReadsAndWraparound()
    {
        var queue = new BoundedAudioQueue(16);
        var first = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var second = new byte[] { 11, 12, 13, 14, 15, 16 };

        Assert.True(queue.TryWrite(first));
        var read = new byte[4];
        Assert.Equal(4, queue.Read(read, 0));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, read);

        Assert.True(queue.TryWrite(second)); // wraps around the ring
        var rest = new byte[12];
        var n = 0;
        while (n < rest.Length)
        {
            var got = queue.Read(rest.AsSpan(n), 100);
            if (got == 0) break;
            n += got;
        }

        Assert.Equal(12, n);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, rest);
    }

    [Fact]
    public void QueuedDataSurvivesWhileMoreDataArrives()
    {
        var queue = new BoundedAudioQueue(32);
        var chunkA = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var chunkB = Enumerable.Range(100, 12).Select(i => (byte)i).ToArray();

        Assert.True(queue.TryWrite(chunkA));
        var first = new byte[6];
        Assert.Equal(6, queue.Read(first, 0));
        Assert.True(queue.TryWrite(chunkB));

        var rest = new byte[18];
        var n = 0;
        while (n < rest.Length)
        {
            var got = queue.Read(rest.AsSpan(n), 100);
            if (got == 0) break;
            n += got;
        }

        Assert.Equal(18, n);
        Assert.Equal(chunkA.Skip(6).Concat(chunkB), rest);
    }

    [Fact]
    public void OverflowIsCountedAndOldDataIsNotCorrupted()
    {
        var queue = new BoundedAudioQueue(8);
        Assert.True(queue.TryWrite(new byte[] { 1, 2, 3, 4, 5, 6 }));
        var read = new byte[2];
        Assert.Equal(2, queue.Read(read, 0));
        Assert.True(queue.TryWrite(new byte[] { 7, 8, 9, 10 }));

        Assert.False(queue.TryWrite(new byte[] { 11, 12, 13 }));
        Assert.Equal(3, queue.DroppedBytes);

        var rest = new byte[8];
        var n = 0;
        while (n < 8)
        {
            var got = queue.Read(rest.AsSpan(n), 50);
            if (got == 0) break;
            n += got;
        }

        Assert.Equal(new byte[] { 3, 4, 5, 6, 7, 8, 9, 10 }, rest[..n]);
    }
}
