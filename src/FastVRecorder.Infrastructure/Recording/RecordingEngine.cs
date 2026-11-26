using System.Diagnostics;
using System.Text.Json;
using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.History;
using FastVRecorder.Core.Recovery;
using FastVRecorder.Core.Settings;
using FastVRecorder.Infrastructure.Audio;

namespace FastVRecorder.Infrastructure.Recording;

/// <summary>
/// Owns the complete recording lifecycle behind one serialized gate: device resolution, name
/// reservation, recoverable part file, bounded queue, writer thread, structural verification,
/// atomic rename, segment rollover, MP3 finalization and history registration.
/// </summary>
public sealed class RecordingEngine : IDisposable
{
    private const long SegmentDataLimitBytes = 2L * 1024 * 1024 * 1024; // 2 GiB, keeps RIFF very safe
    private const int QueueSeconds = 8;

    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = true };

    private readonly PortableRoot _paths;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly FileNamer _namer;
    private readonly HistoryStore _history;
    private readonly RecordingJournal _journal;
    private readonly ILog _log;
    private readonly Func<AppSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private RecordingState _state = RecordingState.Idle;
    private BoundedAudioQueue? _queue;
    private IAudioCaptureSource? _source;
    private SampleConverter? _converter;
    private Thread? _writerThread;
    private WavStreamWriter? _writer;
    private ReservedTarget? _target;
    private TargetFormat _targetFormat = TargetFormat.Standard;
    private SourceFormat _sourceFormat;
    private OutputFormat _outputFormat;
    private int _mp3Kbps;
    private string _directory = string.Empty;
    private string _recordingId = string.Empty;
    private string _sessionId = string.Empty;
    private string? _activePartPath;
    private DateTime _startedLocal;
    private long _finalizedBytes;
    private long _liveBytes;
    private volatile bool _stopWriter;
    private volatile bool _incomplete;
    private string? _incompleteReason;
    private ManualResetEventSlim? _writerDone;
    private bool _pendingWriterFinalization;
    private TimeSpan _writerJoinTimeout = TimeSpan.FromSeconds(25);
    internal Action? WriterLoopHook;

    /// <summary>Test seam: shrink the queue so an overflow is deterministic.</summary>
    internal int? QueueCapacityBytesOverride;
    private int _levelScaled;
    private readonly List<SegmentInfo> _segments = new();
    private readonly List<string> _completedFiles = new();
    private bool _disposed;

    public RecordingEngine(
        PortableRoot paths,
        IAudioCaptureFactory captureFactory,
        FileNamer namer,
        HistoryStore history,
        RecordingJournal journal,
        ILog log,
        Func<AppSettings> settings)
    {
        _paths = paths;
        _captureFactory = captureFactory;
        _namer = namer;
        _history = history;
        _journal = journal;
        _log = log;
        _settings = settings;
    }

    public event EventHandler<StateChangedEventArgs>? StateChanged;
    public event EventHandler<LevelUpdatedEventArgs>? LevelUpdated;
    public event EventHandler<RecordingErrorEventArgs>? Error;
    public event EventHandler<RecordingCompletedEventArgs>? Completed;
    public event EventHandler<RecordingStartedEventArgs>? Started;

    public RecordingState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string LastError { get; private set; } = string.Empty;

    public string SessionId => _sessionId;

    /// <summary>Test seam: bounded wait for the writer thread; production default is 25 seconds.</summary>
    internal TimeSpan WriterJoinTimeout
    {
        get => _writerJoinTimeout;
        set => _writerJoinTimeout = value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    public string LastOutputPath { get; private set; } = string.Empty;

    public string FormatText { get; private set; } = string.Empty;

    public string FolderPath => _directory;

    /// <summary>True while the given part belongs to this engine's current writing session.</summary>
    public bool IsPartActive(string partPath) =>
        string.Equals(Volatile.Read(ref _activePartPath), partPath, StringComparison.OrdinalIgnoreCase);

    public string CurrentFileName
    {
        get
        {
            var target = _target;
            if (target is null)
            {
                return string.Empty;
            }

            return target.BaseName + (_outputFormat == OutputFormat.Mp3 ? ".mp3" : ".wav");
        }
    }

    /// <summary>Duration confirmed by bytes written, independent of UI ticks and pause time.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            var rate = _targetFormat.AverageBytesPerSecond;
            if (rate <= 0) return TimeSpan.Zero;
            var bytes = Interlocked.Read(ref _finalizedBytes) + Interlocked.Read(ref _liveBytes);
            return TimeSpan.FromSeconds((double)bytes / rate);
        }
    }

    /// <summary>Polls the last peak (0..1+) and resets it so the meter decays between chunks.</summary>
    public float ReadLevel()
    {
        var value = Interlocked.Exchange(ref _levelScaled, 0);
        return value / 10000f;
    }

    public Task<bool> StartAsync() => Task.Run(StartCore);

    public Task StopAsync() => Task.Run(() => { StopCore("Stopped by user", failure: false); });

    public Task<bool> ToggleAsync() => Task.Run(ToggleCore);

    public Task<bool> TogglePauseAsync() => Task.Run(TogglePauseCore);

    /// <summary>Shows the RECOVERING state while the startup crash scan runs.</summary>
    public void BeginRecoveryScan()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(2))) return;
        try
        {
            if (State == RecordingState.Idle) Transition(RecordingState.Recovering);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void EndRecoveryScan()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(2))) return;
        try
        {
            if (State == RecordingState.Recovering) Transition(RecordingState.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- start

    private bool StartCore()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(20)))
        {
            return false;
        }

        try
        {
            return StartCoreLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller already holds the serialized gate.</summary>
    private bool StartCoreLocked()
    {
            if (_pendingWriterFinalization)
            {
                LastError = "The previous recording writer is still owned by an unfinished session. Wait for it to finish or restart the application.";
                Error?.Invoke(this, new RecordingErrorEventArgs
                {
                    Message = LastError,
                    IsFatal = false,
                    SessionId = _sessionId,
                    Outcome = RecordingOutcome.FailedRecoverable,
                });
                return false;
            }

            if (State is not (RecordingState.Idle or RecordingState.Error))
            {
                return false;
            }

            var settings = _settings();
            LastError = string.Empty;
            _segments.Clear();
            _completedFiles.Clear();
            Interlocked.Exchange(ref _finalizedBytes, 0);
            Interlocked.Exchange(ref _liveBytes, 0);
            _incomplete = false;
            _incompleteReason = null;
            _stopWriter = false;
            _outputFormat = settings.Recording.Format;
            _mp3Kbps = settings.Recording.Mp3BitrateKbps;
            _targetFormat = settings.Recording.EffectiveFormat();
            FormatText = settings.Recording.DescribeOutput();
            _startedLocal = DateTime.Now;
            _recordingId = Guid.NewGuid().ToString("N");
            _sessionId = _recordingId;
            _writerDone?.Dispose();
            _writerDone = new ManualResetEventSlim(false);

            Transition(RecordingState.Starting);

            try
            {
                if (_outputFormat == OutputFormat.Flac)
                {
                    throw new InvalidOperationException("FLAC is not available in this build. Choose WAV or MP3.");
                }

                if (_outputFormat == OutputFormat.Mp3 && !Mp3Encoder.IsAvailable)
                {
                    throw new InvalidOperationException("MP3 encoding is not available on this system. Choose WAV.");
                }

                if (!_targetFormat.IsValid(out var formatError))
                {
                    throw new InvalidOperationException(formatError ?? "Invalid target format.");
                }

                var folder = _paths.ResolveRecordingFolder(settings.Files.RecordingFolder);
                EnsureWritableFolder(folder);
                EnsureFreeSpace(folder, settings.Advanced.LowDiskSpaceMb);
                _directory = folder;

                _target = _namer.Reserve(
                    folder,
                    settings.Files.ToNamingOptions(),
                    _outputFormat == OutputFormat.Mp3 ? new[] { ".mp3" } : null);
                Volatile.Write(ref _activePartPath, _target.PartPath);

                _writer = OpenWriter(_target);
                WriteMetadata(_target);

                _journal.Add(new JournalEntry
                {
                    PartPath = _target.PartPath,
                    MetaPath = _target.MetaPath,
                    FinalPath = DesiredOutputPath(_target),
                    StartedUtc = DateTime.UtcNow,
                    Format = _outputFormat == OutputFormat.Mp3 ? "MP3" : "WAV",
                });

                _source = _captureFactory.TryCreate(settings.Recording.DeviceId, out var captureError)
                    ?? throw new InvalidOperationException(captureError ?? "The selected microphone is not available. Choose another device in Settings.");
                _sourceFormat = _source.SourceFormat;
                if (!_sourceFormat.IsSupported(out var sourceError))
                {
                    throw new NotSupportedException($"The microphone reports an unsupported format: {sourceError}");
                }

                _log.Info($"Capture device: {_source.DeviceName ?? "(unknown)"} / {_sourceFormat}");

                _converter = new SampleConverter(_sourceFormat, _targetFormat);
                _queue = new BoundedAudioQueue(QueueCapacityBytesOverride ?? Math.Max(
                    _sourceFormat.BlockAlign * _sourceFormat.SampleRate * QueueSeconds,
                    1 << 20));
                _source.DataAvailable += OnDataAvailable;
                _source.Stopped += OnCaptureStopped;

                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "FastVRecorder writer",
                };
                _writerThread.Start();
                _source.Start();

                Transition(RecordingState.Recording);
                _log.Info($"Recording started: {_target.PartPath} ({_sourceFormat} -> {_targetFormat.Describe()})");
                Started?.Invoke(this, new RecordingStartedEventArgs
                {
                    FormatText = FormatText,
                    OutputPath = DesiredOutputPath(_target),
                    SessionId = _sessionId,
                });
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Recording start failed", ex);
                LastError = ex.Message;
                Error?.Invoke(this, new RecordingErrorEventArgs
                {
                    Message = ex.Message,
                    Exception = ex,
                    IsFatal = true,
                    SessionId = _sessionId,
                    Outcome = RecordingOutcome.FailedNoOutput,
                });
                var cleaned = CleanupFailedStart(ex);
                Transition(cleaned ? RecordingState.Idle : RecordingState.Error);
                return false;
            }
    }

    private bool CleanupFailedStart(Exception error)
    {
        try
        {
            DetachCapture();
        }
        catch (Exception ex)
        {
            _log.Warn("Capture cleanup after failed start reported an error", ex);
        }

        _stopWriter = true;
        _queue?.Complete();
        var thread = _writerThread;
        var writerDone = _writerDone;
        var stopped = thread is null || thread == Thread.CurrentThread ||
                      (writerDone is not null
                          ? writerDone.Wait(_writerJoinTimeout)
                          : thread.Join(_writerJoinTimeout));
        if (stopped && thread is not null && thread != Thread.CurrentThread)
        {
            stopped = thread.Join(TimeSpan.FromSeconds(2));
        }

        if (!stopped)
        {
            _pendingWriterFinalization = true;
            _incomplete = true;
            _incompleteReason = "A failed start left a writer thread alive; its partial file is preserved and no new recording may start until it exits.";
            LastError = _incompleteReason;
            _log.Error(_incompleteReason);
            StartDeferredFinalization();
            return false;
        }

        var target = _target;
        var writer = _writer;
        if (writer is not null)
        {
            try
            {
                writer.Complete();
                if (writer.DataBytes > 0 && target is not null)
                {
                    CommitSegment(writer);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Could not salvage audio after a failed start", ex);
            }

            writer.Dispose();
        }

        if (target is not null)
        {
            if (writer is null || writer.DataBytes == 0)
            {
                TryDelete(target.PartPath);
                TryDelete(target.MetaPath);
            }

            _journal.Remove(target.PartPath);
        }

        ResetSession();
        return true;
    }

    // ---------------------------------------------------------------- stop

    private bool ToggleCore()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(20))) return false;
        try
        {
            var state = State;
            if (state is RecordingState.Recording or RecordingState.Paused)
            {
                StopCoreLocked("Stopped by user", failure: false);
                return true;
            }

            if (state is RecordingState.Idle or RecordingState.Error)
            {
                return StartCoreLocked();
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TogglePauseCore()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(10))) return false;
        try
        {
            if (State == RecordingState.Recording)
            {
                Transition(RecordingState.Paused);
                DetachCapture();
                _log.Info("Recording paused.");
                return true;
            }

            if (State == RecordingState.Paused)
            {
                try
                {
                    ResumeCapture();
                    Transition(RecordingState.Recording);
                    _log.Info("Recording resumed.");
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Error("Cannot resume recording", ex);
                    LastError = "Cannot resume the microphone: " + ex.Message;
                    StopCoreLocked(LastError, failure: true);
                    return false;
                }
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopCore(string? reason, bool failure)
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(20))) return;
        try
        {
            StopCoreLocked(reason, failure);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopCoreLocked(string? reason, bool failure)
    {
        if (State is RecordingState.Idle or RecordingState.Error or RecordingState.Recovering)
        {
            return;
        }

        Transition(RecordingState.Stopping);
        DetachCapture();
        _stopWriter = true;
        _queue?.Complete();

        var writerThread = _writerThread;
        var writerDone = _writerDone;
        var stopped = writerDone is not null
            ? writerDone.Wait(_writerJoinTimeout)
            : writerThread is null || writerThread.Join(_writerJoinTimeout);

        if (stopped && writerThread is not null && writerThread != Thread.CurrentThread)
        {
            // The event is set in the writer's finally block; ensure the thread actually left.
            stopped = writerThread.Join(TimeSpan.FromSeconds(2));
        }

        if (!stopped)
        {
            _pendingWriterFinalization = true;
            _incomplete = true;
            _incompleteReason = $"The recording writer did not stop within {_writerJoinTimeout.TotalSeconds:0.#} s. Partial audio is preserved for recovery; no new recording will start until the writer finishes.";
            LastError = _incompleteReason;
            _log.Error(_incompleteReason);
            Error?.Invoke(this, new RecordingErrorEventArgs
            {
                Message = LastError,
                IsFatal = true,
                SessionId = _sessionId,
                Outcome = RecordingOutcome.FailedRecoverable,
            });
            Transition(RecordingState.Error);
            StartDeferredFinalization();
            return;
        }

        FinishStoppedSession(reason, failure);
    }

    private void FinishStoppedSession(string? reason, bool failure)
    {
        string? finalizeError = null;
        try
        {
            FinalizeCurrentSegment();
        }
        catch (Exception ex)
        {
            finalizeError = ex.Message;
            _incomplete = true;
            _incompleteReason ??= "The WAV could not be finalized: " + ex.Message;
            _log.Error("Finalizing the recording failed", ex);
        }

        string? encodeError = null;
        if (_outputFormat == OutputFormat.Mp3 && _segments.Count > 0)
        {
            Transition(RecordingState.Finalizing);
            try
            {
                encodeError = EncodeSegments();
            }
            catch (Exception ex)
            {
                encodeError = "MP3 finalization failed: " + ex.Message;
                _incomplete = true;
                _incompleteReason ??= encodeError;
                _log.Error("MP3 finalization failed after the recording stopped", ex);
            }
        }

        var dropped = _queue?.DroppedBytes ?? 0;
        if (dropped > 0)
        {
            _incomplete = true;
            _incompleteReason ??= $"The audio queue overflowed; {dropped:N0} bytes were dropped.";
            _log.Warn(_incompleteReason);
        }

        var note = _incomplete ? _incompleteReason ?? "The recording may be incomplete." : null;
        if (!string.IsNullOrEmpty(reason) && failure)
        {
            note = string.IsNullOrEmpty(note) ? reason : note + " " + reason;
        }

        var files = _completedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var outcome = DetermineOutcome(failure, finalizeError, encodeError, _incomplete, files.Count > 0);
        if (files.Count > 0)
        {
            long size = 0;
            foreach (var file in files)
            {
                try { size += new FileInfo(file).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            LastOutputPath = files[^1];
            if (outcome != RecordingOutcome.Success)
            {
                foreach (var file in files)
                {
                    _history.Update(file, entry => entry.Note = note);
                }
            }

            Completed?.Invoke(this, new RecordingCompletedEventArgs
            {
                FilePath = files[^1],
                Duration = TotalDuration(),
                SizeBytes = size,
                Files = files,
                Note = note,
                SessionId = _sessionId,
                Outcome = outcome,
            });
        }
        else
        {
            var message = failure
                ? reason ?? "Recording failed before any audio could be saved."
                : "No audio was recorded; nothing was saved.";
            LastError = message;
            Error?.Invoke(this, new RecordingErrorEventArgs
            {
                Message = message,
                IsFatal = true,
                SessionId = _sessionId,
                Outcome = outcome,
            });
        }

        if (outcome != RecordingOutcome.Success)
        {
            if (encodeError is not null)
            {
                LastError = $"MP3 encoding failed: {encodeError}. The WAV was kept.";
            }
            else if (finalizeError is not null)
            {
                LastError = $"Finalizing the recording failed: {finalizeError}";
            }
            else if (_incomplete)
            {
                LastError = _incompleteReason ?? "The recording is incomplete.";
            }
            else
            {
                LastError = reason ?? "Recording stopped after an error.";
            }

            Transition(RecordingState.Error);
            Transition(RecordingState.Idle);
        }
        else
        {
            Transition(RecordingState.Idle);
        }

        _log.Info($"Recording stopped ({outcome}). Files: {string.Join(", ", files)}");
        ResetSession();
    }

    private static RecordingOutcome DetermineOutcome(
        bool failure,
        string? finalizeError,
        string? encodeError,
        bool incomplete,
        bool hasFiles)
    {
        if (hasFiles)
        {
            return failure || finalizeError is not null || encodeError is not null || incomplete
                ? RecordingOutcome.PartialSaved
                : RecordingOutcome.Success;
        }

        return failure || finalizeError is not null
            ? RecordingOutcome.FailedRecoverable
            : RecordingOutcome.CancelledNoAudio;
    }

    /// <summary>
    /// A timed-out writer still owns the active writer/queue/target. Wait for it to exit, then
    /// finalize under the gate. Recording stays blocked until this completes.
    /// </summary>
    private void StartDeferredFinalization()
    {
        var writerDone = _writerDone;
        if (writerDone is null) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                writerDone.Wait();
                if (!_gate.Wait(TimeSpan.FromSeconds(60)))
                {
                    _log.Error("Deferred writer finalization could not acquire the engine gate.");
                    return;
                }

                try
                {
                    if (!_pendingWriterFinalization) return;
                    var finalizeError = (string?)null;
                    try
                    {
                        FinalizeCurrentSegment();
                    }
                    catch (Exception ex)
                    {
                        finalizeError = ex.Message;
                        _log.Error("Deferred finalization failed; partial data remains recoverable", ex);
                    }

                    if (_outputFormat == OutputFormat.Mp3 && _segments.Count > 0)
                    {
                        var encodeError = EncodeSegments();
                        if (encodeError is not null && finalizeError is null)
                        {
                            finalizeError = "MP3 encoding failed: " + encodeError;
                        }
                    }

                    var files = _completedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (files.Count > 0)
                    {
                        LastOutputPath = files[^1];
                        foreach (var file in files)
                        {
                            _history.Update(file, entry => entry.Note ??= "Saved after a delayed writer shutdown.");
                        }
                        _log.Info("Deferred writer finalization completed: " + files[^1]);
                    }
                    else
                    {
                        LastError = finalizeError ?? "The writer exited but no completed audio file remained; the .part data is recoverable.";
                        _log.Error(LastError);
                    }

                    Transition(RecordingState.Idle);
                    ResetSession();
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                _log.Error("Deferred writer finalization crashed", ex);
            }
        });
    }

    private string? EncodeSegments()
    {
        string? firstError = null;
        var settings = _settings();
        foreach (var segment in _segments.ToList())
        {
            if (!File.Exists(segment.WavPath))
            {
                continue;
            }

            var mp3Path = Path.ChangeExtension(segment.WavPath, ".mp3");
            if (File.Exists(mp3Path))
            {
                mp3Path = FileNamer.NextFreePath(mp3Path);
            }

            if (Mp3Encoder.TryEncode(segment.WavPath, mp3Path, _mp3Kbps, _log, out var error))
            {
                var size = new FileInfo(mp3Path).Length;
                AddHistory(mp3Path, segment.Duration, size, "MP3", note: null);
                _completedFiles.Add(mp3Path);
                TryDelete(segment.WavPath);
            }
            else
            {
                firstError ??= error ?? "unknown encoder error";
                var wavSize = new FileInfo(segment.WavPath).Length;
                AddHistory(segment.WavPath, segment.Duration, wavSize, "WAV", "MP3 encoding failed; the WAV was kept.");
                _completedFiles.Add(segment.WavPath);
            }
        }

        return firstError;
    }

    private void FinalizeCurrentSegment()
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Complete();
            CommitSegment(writer);
        }
        finally
        {
            writer.Dispose();
            _writer = null;
            _target = null;
        }
    }

    private void CommitSegment(WavStreamWriter writer)
    {
        var target = _target ?? throw new InvalidOperationException("No reserved target.");
        var dataBytes = writer.DataBytes;
        if (dataBytes <= 0)
        {
            TryDelete(target.PartPath);
            TryDelete(target.MetaPath);
            _journal.Remove(target.PartPath);
            return;
        }

        if (!WavVerifier.Verify(target.PartPath, _targetFormat, dataBytes, out var verifyError))
        {
            throw new IOException($"Saved audio failed structural verification: {verifyError}");
        }

        var final = target.FinalPath;
        if (File.Exists(final))
        {
            final = FileNamer.NextFreePath(final);
        }

        Directory.CreateDirectory(target.Directory);
        File.Move(target.PartPath, final, overwrite: false);
        TryDelete(target.MetaPath);
        _journal.Remove(target.PartPath);

        var duration = (double)dataBytes / _targetFormat.AverageBytesPerSecond;
        _segments.Add(new SegmentInfo(final, duration, dataBytes));
        Interlocked.Add(ref _finalizedBytes, dataBytes);
        Interlocked.Exchange(ref _liveBytes, 0);

        if (_outputFormat == OutputFormat.Wav)
        {
            AddHistory(final, duration, new FileInfo(final).Length, "WAV", note: null);
            _completedFiles.Add(final);
        }

        _log.Info($"Segment finalized: {final} ({duration:0.0} s)");
    }

    private void AddHistory(string path, double durationSeconds, long sizeBytes, string format, string? note)
    {
        try
        {
            _history.Add(new HistoryEntry
            {
                Path = path,
                CreatedLocal = _startedLocal,
                DurationSeconds = durationSeconds,
                SizeBytes = sizeBytes,
                Format = format,
                Recovered = false,
                Note = note,
            });
        }
        catch (Exception ex)
        {
            _log.Error("Cannot add the recording to history", ex);
        }
    }

    private TimeSpan TotalDuration()
    {
        var seconds = _segments.Sum(s => s.Duration);
        return TimeSpan.FromSeconds(seconds);
    }

    // ---------------------------------------------------------------- writer

    private WavStreamWriter OpenWriter(ReservedTarget target) =>
        WavStreamWriter.Adopt(target.TakeStream(), target.PartPath, _targetFormat, SegmentDataLimitBytes);

    private void WriterLoop()
    {
        var input = new byte[64 * 1024];
        var output = new byte[128 * 1024];
        var checkpoint = TimeSpan.FromSeconds(Math.Clamp(_settings().Advanced.RecoveryFlushSeconds, 0.5, 10));
        var sinceCheckpoint = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                WriterLoopHook?.Invoke();
                var queue = _queue;
                if (queue is null)
                {
                    break;
                }

                var read = queue.Read(input, 50);
                if (read == 0)
                {
                    if (_stopWriter && queue.BufferedBytes == 0)
                    {
                        break;
                    }

                    continue;
                }

                var converter = _converter ?? throw new InvalidOperationException("Converter missing.");
                var writer = _writer ?? throw new InvalidOperationException("Writer missing.");
                var produced = converter.Convert(new ReadOnlySpan<byte>(input, 0, read), ref output, out var peak);
                if (produced > 0)
                {
                    if (writer.DataBytes + produced > writer.MaxDataBytes)
                    {
                        RolloverSegment();
                        writer = _writer ?? throw new InvalidOperationException("Writer missing after rollover.");
                    }

                    writer.Write(new ReadOnlySpan<byte>(output, 0, produced));
                    Interlocked.Exchange(ref _liveBytes, writer.DataBytes);
                    PublishPeak(peak);
                }

                if (sinceCheckpoint.Elapsed >= checkpoint)
                {
                    writer.Checkpoint();
                    sinceCheckpoint.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            _incomplete = true;
            _incompleteReason = "A disk write failed: " + ex.Message;
            _log.Error("Recording writer failed", ex);
            try
            {
                _writer?.Complete();
            }
            catch (Exception finalizeException)
            {
                _log.Error("The writer could not finalize after a failure", finalizeException);
            }

            var queue = _queue;
            queue?.Complete();
            ThreadPool.QueueUserWorkItem(_ => FatalStop("Recording stopped after a disk write error: " + ex.Message, ex));
        }
        finally
        {
            try { _writerDone?.Set(); } catch (ObjectDisposedException) { }
        }
    }

    private void RolloverSegment()
    {
        var writer = _writer ?? throw new InvalidOperationException("No writer to roll over.");
        writer.Complete();
        CommitSegment(writer);
        writer.Dispose();
        _writer = null;
        _target = null;

        var settings = _settings();
        _target = _namer.Reserve(
            _directory,
            settings.Files.ToNamingOptions(),
            _outputFormat == OutputFormat.Mp3 ? new[] { ".mp3" } : null);
        Volatile.Write(ref _activePartPath, _target.PartPath);
        _writer = OpenWriter(_target);
        WriteMetadata(_target);
        _journal.Add(new JournalEntry
        {
            PartPath = _target.PartPath,
            MetaPath = _target.MetaPath,
            FinalPath = DesiredOutputPath(_target),
            StartedUtc = DateTime.UtcNow,
            Format = _outputFormat == OutputFormat.Mp3 ? "MP3" : "WAV",
        });
        _log.Info("Segment limit reached; continuing in " + _target.PartPath);
    }

    private void PublishPeak(float peak)
    {
        var scaled = (int)Math.Clamp(peak * 10000f, 0f, 10000f);
        if (Interlocked.Exchange(ref _levelScaled, scaled) != scaled)
        {
            LevelUpdated?.Invoke(this, new LevelUpdatedEventArgs { Peak = peak });
        }
    }

    // ---------------------------------------------------------------- capture

    private void OnDataAvailable(ReadOnlySpan<byte> buffer)
    {
        if (_stopWriter || buffer.IsEmpty) return;
        var queue = _queue;
        if (queue is null) return;
        queue.TryWrite(buffer);
    }

    private void OnCaptureStopped(Exception? exception)
    {
        if (exception is null) return;
        _incomplete = true;
        _incompleteReason = "The microphone stopped unexpectedly: " + exception.Message;
        ThreadPool.QueueUserWorkItem(_ => FatalStop("The microphone stopped unexpectedly: " + exception.Message, exception));
    }

    private void FatalStop(string message, Exception? exception)
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(20))) return;
        try
        {
            if (State is RecordingState.Recording or RecordingState.Paused or RecordingState.Starting)
            {
                LastError = message;
                // The stop path owns the single completion/error event for this session.
                StopCoreLocked(message, failure: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DetachCapture()
    {
        var source = _source;
        _source = null;
        if (source is not null)
        {
            try
            {
                source.DataAvailable -= OnDataAvailable;
                source.Stopped -= OnCaptureStopped;
                source.Stop();
            }
            finally
            {
                source.Dispose();
            }
        }
    }

    private void ResumeCapture()
    {
        var settings = _settings();
        var source = _captureFactory.TryCreate(settings.Recording.DeviceId, out var captureError)
            ?? throw new InvalidOperationException(captureError ?? "The microphone is no longer available.");
        var format = source.SourceFormat;
        if (format != _sourceFormat)
        {
            _log.Info($"Capture format changed on resume: {_sourceFormat} -> {format}");
            _sourceFormat = format;
            _converter = new SampleConverter(format, _targetFormat);
        }

        _source = source;
        source.DataAvailable += OnDataAvailable;
        source.Stopped += OnCaptureStopped;
        source.Start();
    }

    // ---------------------------------------------------------------- metadata

    private string DesiredOutputPath(ReservedTarget target) =>
        _outputFormat == OutputFormat.Mp3
            ? Path.Combine(target.Directory, target.BaseName + ".mp3")
            : target.FinalPath;

    private void WriteMetadata(ReservedTarget target)
    {
        var metadata = new RecoveryMetadata
        {
            RecordingId = _recordingId,
            BaseName = target.BaseName,
            IntendedOutputPath = DesiredOutputPath(target),
            PartPath = target.PartPath,
            Format = _targetFormat.Encoding.ToString(),
            SampleRate = _targetFormat.SampleRate,
            Channels = _targetFormat.Channels,
            BitsPerSample = _targetFormat.BitsPerSample,
            BlockAlign = _targetFormat.BlockAlign,
            ByteRate = _targetFormat.AverageBytesPerSecond,
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            AppVersion = typeof(RecordingEngine).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        };
        AtomicFile.WriteAllText(target.MetaPath, JsonSerializer.Serialize(metadata, MetadataJson));
    }

    // ---------------------------------------------------------------- helpers

    private void Transition(RecordingState next)
    {
        RecordingState old;
        lock (_stateLock)
        {
            if (_state == next) return;
            old = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new StateChangedEventArgs { OldState = old, NewState = next });
    }

    private void ResetSession()
    {
        _queue = null;
        _source = null;
        _converter = null;
        _writer = null;
        _target = null;
        Volatile.Write(ref _activePartPath, null);
        _writerThread = null;
        _stopWriter = false;
        _incomplete = false;
        _incompleteReason = null;
        _pendingWriterFinalization = false;
        try { _writerDone?.Dispose(); } catch { }
        _writerDone = null;
        Interlocked.Exchange(ref _liveBytes, 0);
        Interlocked.Exchange(ref _levelScaled, 0);
    }

    private static void EnsureWritableFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".fvr-write-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The recording folder is unavailable: {folder}. {ex.Message}", ex);
        }
    }

    private static void EnsureFreeSpace(string folder, int thresholdMb)
    {
        long freeMb;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return;
            freeMb = new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // UNC or unusual mount: do not block on an unknown value
        }

        if (freeMb < thresholdMb)
        {
            throw new IOException($"Not enough free disk space in {folder}: {freeMb:N0} MB available, {thresholdMb:N0} MB required.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; recovery never depends on deleting a file.
        }
    }

    private readonly record struct SegmentInfo(string WavPath, double Duration, long DataBytes);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (State is not (RecordingState.Idle or RecordingState.Error))
            {
                StopCore("Application is closing", failure: false);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Stop during shutdown failed", ex);
        }
    }
}
