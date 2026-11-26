namespace FastVRecorder.Infrastructure.Audio;

/// <summary>
/// Fixed-size single-producer/single-consumer byte queue between the WASAPI callback and the
/// writer thread. The callback never blocks; a full queue is counted so the engine can report
/// overflow instead of pretending the audio is complete.
/// </summary>
public sealed class BoundedAudioQueue
{
    private readonly byte[] _buffer;
    private readonly object _sync = new();
    private int _read;
    private int _write;
    private int _count;
    private bool _completed;
    private long _droppedBytes;

    public BoundedAudioQueue(int capacityBytes)
    {
        if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        _buffer = new byte[capacityBytes];
    }

    public int Capacity => _buffer.Length;

    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    public int BufferedBytes
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Copies as much as fits; returns false when the call was (partially) dropped.</summary>
    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        lock (_sync)
        {
            var space = _buffer.Length - _count;
            if (space < data.Length)
            {
                Interlocked.Add(ref _droppedBytes, data.Length);
                return false;
            }

            var first = Math.Min(data.Length, _buffer.Length - _write);
            data[..first].CopyTo(_buffer.AsSpan(_write));
            data[first..].CopyTo(_buffer);
            _write = (_write + data.Length) % _buffer.Length;
            _count += data.Length;
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    /// <summary>Waits for data or completion; returns 0 on timeout or when finished and empty.</summary>
    public int Read(Span<byte> destination, int timeoutMilliseconds)
    {
        lock (_sync)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMilliseconds);
            while (_count == 0 && !_completed)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return 0;
                }

                Monitor.Wait(_sync, (int)remaining);
            }

            if (_count == 0)
            {
                return 0;
            }

            var take = Math.Min(_count, destination.Length);
            var first = Math.Min(take, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(destination);
            if (take > first)
            {
                _buffer.AsSpan(0, take - first).CopyTo(destination[first..]);
            }

            _read = (_read + take) % _buffer.Length;
            _count -= take;
            Monitor.PulseAll(_sync);
            return take;
        }
    }

    public void Complete()
    {
        lock (_sync)
        {
            _completed = true;
            Monitor.PulseAll(_sync);
        }
    }
}
