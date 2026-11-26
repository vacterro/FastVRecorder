using System.Buffers.Binary;

namespace FastVRecorder.Core.Audio;

/// <summary>
/// Streams a WAV file to disk. The header is valid from the first byte and its size fields are
/// re-patched on every <see cref="Checkpoint"/>, so a crash leaves a playable file whose header is
/// at most one checkpoint interval stale (recovery fixes the remainder from the file length).
/// Not thread-safe: owned by the single writer thread.
/// </summary>
public sealed class WavStreamWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _patch = new byte[4];
    private long _dataBytes;
    private bool _completed;

    public TargetFormat Format { get; }
    public string Path { get; }
    public long DataBytes => _dataBytes;
    public long Frames => _dataBytes / Format.BlockAlign;
    public long MaxDataBytes { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / Format.SampleRate);

    private WavStreamWriter(FileStream stream, string path, TargetFormat format, long maxDataBytes)
    {
        _stream = stream;
        Path = path;
        Format = format;
        MaxDataBytes = Math.Min(maxDataBytes, WavHeader.MaxDataBytes(format));
        MaxDataBytes -= MaxDataBytes % format.BlockAlign;
        _stream.Write(WavHeader.Build(format, 0));
    }

    /// <summary>Creates a new file; never overwrites (FileMode.CreateNew).</summary>
    public static WavStreamWriter CreateNew(string path, TargetFormat format, long maxDataBytes = long.MaxValue)
    {
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.None);
        return Adopt(fs, path, format, maxDataBytes);
    }

    /// <summary>Takes ownership of an already reserved, empty stream.</summary>
    public static WavStreamWriter Adopt(FileStream stream, string path, TargetFormat format, long maxDataBytes = long.MaxValue)
    {
        if (!format.IsValid(out var err)) throw new ArgumentException(err, nameof(format));
        if (stream.Length != 0) throw new IOException($"Reserved file '{path}' is not empty.");
        return new WavStreamWriter(stream, path, format, maxDataBytes);
    }

    public long RemainingCapacity => MaxDataBytes - _dataBytes;

    /// <summary>Appends whole frames. Throws if the segment limit would be exceeded.</summary>
    public void Write(ReadOnlySpan<byte> frames)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (frames.Length % Format.BlockAlign != 0)
            throw new ArgumentException("Write must contain whole sample frames.", nameof(frames));
        if (_dataBytes + frames.Length > MaxDataBytes)
            throw new InvalidOperationException("Segment size limit reached; roll over to a new segment first.");
        _stream.Write(frames);
        _dataBytes += frames.Length;
    }

    /// <summary>Patches header sizes and forces data to the physical disk.</summary>
    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        PatchHeader();
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>Final header patch + flush + close. Idempotent.</summary>
    public void Complete()
    {
        if (_completed) return;
        try
        {
            PatchHeader();
            _stream.Flush(flushToDisk: true);
        }
        finally
        {
            _completed = true;
            _stream.Dispose();
        }
    }

    private void PatchHeader()
    {
        var end = _stream.Position;
        var hdrLen = WavHeader.HeaderLength(Format);
        _stream.Flush(flushToDisk: false);
        WriteUInt32At(4, (uint)(hdrLen - 8 + _dataBytes));
        if (Format.IsFloat) WriteUInt32At(WavHeader.FactFramesOffset, (uint)Frames);
        WriteUInt32At(WavHeader.DataSizeOffset(Format), (uint)_dataBytes);
        _stream.Position = end;
    }

    private void WriteUInt32At(long offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_patch, value);
        _stream.Position = offset;
        _stream.Write(_patch, 0, 4);
    }

    /// <summary>Abandons the file without patching (used when nothing useful was written).</summary>
    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        try { _stream.Dispose(); } catch { /* best effort */ }
    }
}

/// <summary>Structural verification of a finished WAV file.</summary>
public static class WavVerifier
{
    public static bool Verify(string path, TargetFormat expected, long expectedDataBytes, out string? error)
    {
        try
        {
            if (!WavHeader.TryParse(path, out var info, out error)) return false;
            var fmt = info!.ToTargetFormat();
            if (fmt != expected)
            {
                error = $"Header format {fmt?.Describe() ?? "unknown"} does not match {expected.Describe()}.";
                return false;
            }
            if (info.DeclaredDataBytes != expectedDataBytes)
            {
                error = $"Header declares {info.DeclaredDataBytes} data bytes, expected {expectedDataBytes}.";
                return false;
            }
            if (info.FileLength - info.DataOffset < expectedDataBytes)
            {
                error = $"File holds {info.FileLength - info.DataOffset} data bytes, expected {expectedDataBytes}.";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
