namespace FastVRecorder.Core.Files;

public enum PortableInstanceLeaseState
{
    Acquired,
    AlreadyRunning,
    Unavailable,
}

/// <summary>
/// Cross-session single-instance lease for one portable root. Windows file sharing enforces the
/// exclusion even when two processes run in different Windows sessions (a Local\ mutex does not).
/// The lease file is advisory: a stale file from a crash is reusable, and a pre-existing file the
/// application did not create is never deleted.
/// </summary>
public sealed class PortableInstanceLease : IDisposable
{
    public const string FileName = ".fastvrecorder-instance.lock";

    private FileStream? _stream;
    private readonly string _path;
    private readonly bool _deleteOnDispose;

    private PortableInstanceLease(FileStream stream, string path, bool deleteOnDispose)
    {
        _stream = stream;
        _path = path;
        _deleteOnDispose = deleteOnDispose;
    }

    public static PortableInstanceLease? TryAcquire(
        string configDirectory,
        out PortableInstanceLeaseState state,
        out string? error)
    {
        error = null;
        state = PortableInstanceLeaseState.Unavailable;
        try
        {
            Directory.CreateDirectory(configDirectory);
            var path = Path.Combine(configDirectory, FileName);
            var existed = File.Exists(path);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            state = PortableInstanceLeaseState.Acquired;
            return new PortableInstanceLease(stream, path, deleteOnDispose: !existed);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            state = PortableInstanceLeaseState.AlreadyRunning;
            error = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state = PortableInstanceLeaseState.Unavailable;
            error = ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
        if (!_deleteOnDispose) return;
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
