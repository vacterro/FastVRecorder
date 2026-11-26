namespace FastVRecorder.Core.Tests;

public abstract class TempDirectoryTest : IDisposable
{
    protected TempDirectoryTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "fvr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    protected string Root { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // test cleanup is best effort
        }
    }
}
