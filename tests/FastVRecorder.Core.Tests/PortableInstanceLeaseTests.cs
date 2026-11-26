using FastVRecorder.Core.Files;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class PortableInstanceLeaseTests : TempDirectoryTest
{
    private string ConfigDir => Path.Combine(Root, "config");

    [Fact]
    public void SecondLeaseIsRefusedUntilTheFirstIsDisposed()
    {
        var first = PortableInstanceLease.TryAcquire(ConfigDir, out var firstState, out _);
        try
        {
            Assert.Equal(PortableInstanceLeaseState.Acquired, firstState);
            Assert.NotNull(first);

            var second = PortableInstanceLease.TryAcquire(ConfigDir, out var secondState, out _);
            Assert.Null(second);
            Assert.Equal(PortableInstanceLeaseState.AlreadyRunning, secondState);
        }
        finally
        {
            first?.Dispose();
        }

        var third = PortableInstanceLease.TryAcquire(ConfigDir, out var thirdState, out _);
        try
        {
            Assert.Equal(PortableInstanceLeaseState.Acquired, thirdState);
            Assert.NotNull(third);
        }
        finally
        {
            third?.Dispose();
        }
    }

    [Fact]
    public void PreExistingUnrelatedFileWithTheLeaseNameIsNotDeleted()
    {
        Directory.CreateDirectory(ConfigDir);
        var path = Path.Combine(ConfigDir, PortableInstanceLease.FileName);
        var userBytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(path, userBytes);

        var lease = PortableInstanceLease.TryAcquire(ConfigDir, out var state, out _);
        Assert.Equal(PortableInstanceLeaseState.Acquired, state);
        lease!.Dispose();

        Assert.True(File.Exists(path));
        Assert.Equal(userBytes, File.ReadAllBytes(path));
    }
}
