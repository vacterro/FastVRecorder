namespace FastVRecorder.Integration.Tests;

public abstract class TempDirectoryTest : IDisposable
{
    protected TempDirectoryTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "fvr-it", Guid.NewGuid().ToString("N"));
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
            // best effort
        }
    }
}
