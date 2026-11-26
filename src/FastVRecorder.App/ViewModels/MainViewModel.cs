using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.History;
using FastVRecorder.Core.Hotkeys;
using FastVRecorder.Core.Settings;
using FastVRecorder.App.Services;
using FastVRecorder.Infrastructure.Audio;
using FastVRecorder.Infrastructure.Hotkeys;
using FastVRecorder.Infrastructure.Notifications;
using FastVRecorder.Infrastructure.Recovery;
using FastVRecorder.Infrastructure.Recording;
using FastVRecorder.Infrastructure.Shell;
using FastVRecorder.Infrastructure.Startup;
using NAudio.Wave;

namespace FastVRecorder.App.ViewModels;

/// <summary>Composition root for the window: navigation, recording state, banners and auto-save.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HistoryStore _history;
    private readonly RecordingEngine _engine;
    private readonly CuePlayer _cues;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly TrayService _tray;
    private readonly Loc _loc;
    private readonly FileLog _log;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveTimer;

    private UiPage _currentPage = UiPage.Recorder;
    private string _bannerText = string.Empty;
    private string _bannerSeverity = "info";
    private DateTime _bannerUntil;
    private string? _activeSessionId;
    private int _tickCount;

    public MainViewModel(
        PortableRoot paths,
        AppSettings settings,
        SettingsStore store,
        HistoryStore history,
        RecordingEngine engine,
        AudioDeviceService devices,
        FileNamer namer,
        CuePlayer cues,
        GlobalHotkeyService hotkeys,
        StartupService startup,
        ThemeService theme,
        TrayService tray,
        FileLog log,
        Loc loc)
    {
        _settings = settings;
        _store = store;
        _history = history;
        _engine = engine;
        _cues = cues;
        _hotkeys = hotkeys;
        _tray = tray;
        _loc = loc;
        _log = log;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettingsNow();
        };

        Settings = new SettingsViewModel(settings, paths, namer, devices, startup, theme, loc, cues, log, SaveSettingsNow);
        Recorder = new RecorderViewModel(engine, Settings, settings, loc);
        History = new HistoryViewModel(history, loc);
        Hotkeys = new HotkeysViewModel(settings, hotkeys, loc, SaveSettingsNow);
        _hotkeys.Pressed += (_, action) => Dispatch(() => ExecuteHotkeyAction(action));

        ToggleRecordingCommand = new RelayCommand(ToggleRecording, () => Recorder.CanToggle);
        PauseCommand = new RelayCommand(Pause, () => Recorder.CanPause);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OpenLastCommand = new RelayCommand(OpenLast);
        ShowRecorderCommand = new RelayCommand(() => ShowPage(UiPage.Recorder));
        ShowHistoryCommand = new RelayCommand(() => ShowPage(UiPage.History));
        ShowHotkeysCommand = new RelayCommand(() => ShowPage(UiPage.Hotkeys));
        ShowSettingsCommand = new RelayCommand(() => ShowPage(UiPage.Settings));
        DismissBannerCommand = new RelayCommand(() => { BannerText = string.Empty; });
        RetryRecoveryCommand = new RelayCommand(RetryRecovery);
        RescanHistoryCommand = new RelayCommand(RescanHistory);

        _engine.StateChanged += (_, e) => Dispatch(() => OnStateChanged(e));
        _engine.Started += (_, e) => Dispatch(() => OnRecordingStarted(e));
        _engine.Completed += (_, e) => Dispatch(() => OnRecordingCompleted(e));
        _engine.Error += (_, e) => Dispatch(() => OnEngineError(e));

        Settings.Changed += (_, _) =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
            Recorder.RefreshFreeSpace();
        };

        Settings.Reloaded += (_, _) => Dispatch(OnSettingsReloaded);
        Hotkeys.Changed += (_, _) => Settings.RefreshHotkeys();
        _loc.PropertyChanged += (_, _) => Dispatch(OnLanguageChanged);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        if (!paths.CanWriteConfig(out _))
        {
            Settings.StorageWarning = _loc["Set.ReadOnly"];
            ShowBanner(_loc["Set.ReadOnly"], "warning");
        }
    }

    public SettingsViewModel Settings { get; }
    public RecorderViewModel Recorder { get; }
    public HistoryViewModel History { get; }
    public HotkeysViewModel Hotkeys { get; }

    public RelayCommand ToggleRecordingCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenLastCommand { get; }
    public RelayCommand ShowRecorderCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }
    public RelayCommand ShowHotkeysCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand DismissBannerCommand { get; }
    public RelayCommand RetryRecoveryCommand { get; }
    public RelayCommand RescanHistoryCommand { get; }

    public event Action? ShowHideRequested;

    /// <summary>Set by the composition root to run one recovery scan on demand.</summary>
    public Func<RecoveryReport>? RecoveryRetry { get; set; }

    private bool _recoveryRetryBusy;

    /// <summary>Provided by the window so the tray menu can say Show or Hide.</summary>
    public Func<bool>? IsWindowVisible { get; set; }

    public UiPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(CurrentVm));
            OnPropertyChanged(nameof(IsRecorderPage));
            OnPropertyChanged(nameof(IsHistoryPage));
            OnPropertyChanged(nameof(IsHotkeysPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }

    public object CurrentVm => CurrentPage switch
    {
        UiPage.History => History,
        UiPage.Hotkeys => Hotkeys,
        UiPage.Settings => Settings,
        _ => Recorder,
    };

    public bool IsRecorderPage => CurrentPage == UiPage.Recorder;
    public bool IsHistoryPage => CurrentPage == UiPage.History;
    public bool IsHotkeysPage => CurrentPage == UiPage.Hotkeys;
    public bool IsSettingsPage => CurrentPage == UiPage.Settings;

    public string BannerText
    {
        get => _bannerText;
        private set
        {
            if (Set(ref _bannerText, value))
            {
                OnPropertyChanged(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerText);

    public string BannerSeverity
    {
        get => _bannerSeverity;
        private set => Set(ref _bannerSeverity, value);
    }

    public void Initialize()
    {
        if (Settings.Devices.Count == 0)
        {
            Settings.RefreshDevices();
        }

        _log.Info($"Microphone list: {Settings.Devices.Count} item(s); selected={Settings.SelectedDeviceId}");
        Recorder.SetLastFile(_history.Latest()?.Path);
        Recorder.RefreshAll();
        UpdateTray(rebuildMenu: true);
        if (Hotkeys.HasError) ShowBanner(_loc["Msg.HotkeyConflict"] + Hotkeys.ErrorText, "warning");

        if (_settings.Recording.Format == OutputFormat.Flac)
        {
            _settings.Recording.Format = OutputFormat.Wav;
        }

        // Media Foundation probing writes and encodes a probe file. Keep it off the startup
        // critical path and reconcile the format picker when the background probe finishes.
        _ = Task.Run(() =>
            {
                var probeWatch = System.Diagnostics.Stopwatch.StartNew();
                var available = Mp3Encoder.IsAvailable;
                _log.Info($"Media Foundation probe: available={available} in {probeWatch.ElapsedMilliseconds} ms");
                return available;
            })
            .ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) return;
                var available = task.Result;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Settings.UpdateMp3Availability(available);
                    if (!available && _settings.Recording.Format == OutputFormat.Mp3)
                    {
                        _settings.Recording.Format = OutputFormat.Wav;
                        _saveTimer.Stop();
                        _saveTimer.Start();
                        ShowBanner(_loc["Set.Mp3Unavailable"], "warning");
                    }
                }));
            }, TaskScheduler.Default);
    }

    public void ShowPage(UiPage page)
    {
        if (page != UiPage.Settings) Settings.StopMicTest();
        CurrentPage = page;
        if (page == UiPage.Recorder) Recorder.RefreshAll();
        if (page == UiPage.History) History.Refresh();
        if (page == UiPage.Settings) Settings.RefreshDevices();
    }

    private async void RescanHistory()
    {
        try
        {
            var folder = Settings.FolderDisplay;
            var known = _history.Snapshot().Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var discovered = await Task.Run(() => DiscoverRecordings(folder, known));
            var added = _history.AddRange(discovered);
            History.Refresh();
            ShowBanner(_loc["Hist.RescanDone"] + " " + added, "info");
        }
        catch (Exception ex)
        {
            ShowBanner(_loc["Hist.RescanFailed"] + " " + ex.Message, "error");
        }
    }

    private static List<HistoryEntry> DiscoverRecordings(string root, HashSet<string> known)
    {
        var result = new List<HistoryEntry>();
        if (!Directory.Exists(root)) return result;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config", "media", "themes", "languages", "docs", "bin", "obj" };
        while (queue.Count > 0 && result.Count < 2000)
        {
            var (folder, depth) = queue.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.wav"); }
            catch { continue; }
            foreach (var file in files.Concat(SafeEnumerate(folder, "*.mp3")))
            {
                if (result.Count >= 2000) break;
                if (known.Contains(file)) continue;
                try
                {
                    double duration = 0;
                    try { using var reader = new AudioFileReader(file); duration = reader.TotalTime.TotalSeconds; }
                    catch { }
                    result.Add(new HistoryEntry
                    {
                        Path = file,
                        CreatedLocal = File.GetLastWriteTime(file),
                        DurationSeconds = duration,
                        SizeBytes = new FileInfo(file).Length,
                        Format = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(),
                        Note = "Indexed by folder rescan.",
                    });
                    known.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            if (depth < 4)
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(folder))
                    {
                        var name = Path.GetFileName(sub);
                        if (!skipped.Contains(name)) queue.Enqueue((sub, depth + 1));
                    }
                }
                catch { }
            }
        }

        return result;
    }

    private static IEnumerable<string> SafeEnumerate(string folder, string pattern)
    {
        try { return Directory.EnumerateFiles(folder, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private async void RetryRecovery()
    {
        if (_recoveryRetryBusy || RecoveryRetry is null) return;
        _recoveryRetryBusy = true;
        try
        {
            ShowBanner(_loc["Status.Recovering"], "info");
            var report = await Task.Run(RecoveryRetry);
            ReportRecovery(report);
        }
        catch (Exception ex)
        {
            ShowBanner(_loc["Msg.RecoveryFailed"] + ex.Message, "error");
        }
        finally
        {
            _recoveryRetryBusy = false;
        }
    }

    public void ShowWarning(string message) => ShowBanner(message, "warning");

    public void ShowError(string message) => ShowBanner(message, "error");

    public void ReportHotkeyError(string message) => ShowBanner(_loc["Msg.HotkeyConflict"] + message, "warning");

    public void ReportRecovery(RecoveryReport report)
    {
        Dispatch(() =>
        {
            History.Refresh();
            if (report.Recovered.Count > 0)
            {
                var first = Path.GetFileName(report.Recovered[0].Path);
                ShowBanner(_loc["Msg.Recovered"] + report.Recovered.Count + " — " + first, "info");
            }

            if (report.Failed.Count > 0)
            {
                ShowBanner(_loc["Msg.RecoveryFailed"] + report.Failed[0].Error, "warning");
            }
        });
    }

    public void ToggleRecording()
    {
        if (!Recorder.CanToggle) return;
        _ = ToggleRecordingAsync();
    }

    public async Task ToggleRecordingAsync()
    {
        try
        {
            await _engine.ToggleAsync();
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message, "error");
        }
    }

    public void Pause()
    {
        _ = PauseAsync();
    }

    private async Task PauseAsync()
    {
        try
        {
            await _engine.TogglePauseAsync();
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message, "error");
        }
    }

    public void OpenFolder()
    {
        try
        {
            ShellService.OpenFolder(Settings.FolderDisplay);
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message, "error");
        }
    }

    public void OpenLast()
    {
        var latest = _history.LatestExisting();
        if (latest is null)
        {
            ShowBanner(_loc["Msg.NothingToOpen"], "info");
            return;
        }

        if (!File.Exists(latest.Path))
        {
            ShowBanner(_loc["Hist.Missing"] + ": " + latest.Path, "warning");
            History.Refresh();
            return;
        }

        try
        {
            ShellService.OpenFile(latest.Path);
            _log.Info("Opening last recording in the default player: " + latest.Path);
        }
        catch (Exception ex)
        {
            ShowBanner(_loc["Hist.PlayFailed"] + ex.Message, "error");
        }
    }

    public void ShowHideWindow() => ShowHideRequested?.Invoke();

    /// <summary>Refreshes the tray menu after the window becomes visible or hidden.</summary>
    public void RefreshTrayMenu() => UpdateTray(rebuildMenu: true);

    public void ExecuteHotkeyAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleRecording:
                ToggleRecording();
                break;
            case HotkeyAction.StartRecording:
                if (_engine.State is RecordingState.Idle or RecordingState.Error) _ = _engine.StartAsync();
                break;
            case HotkeyAction.StopRecording:
                if (_engine.State is RecordingState.Recording or RecordingState.Paused) _ = _engine.StopAsync();
                break;
            case HotkeyAction.PauseResume:
                Pause();
                break;
            case HotkeyAction.ShowHideWindow:
                ShowHideWindow();
                break;
            case HotkeyAction.OpenRecordingFolder:
                OpenFolder();
                break;
            case HotkeyAction.OpenLastRecording:
                OpenLast();
                break;
            case HotkeyAction.ToggleAlwaysOnTop:
                Settings.AlwaysOnTop = !Settings.AlwaysOnTop;
                break;
        }
    }

    /// <summary>Handles a focused-window shortcut; returns true when a local binding matched.</summary>
    public bool TryExecuteLocalHotkey(int virtualKey, HotkeyModifiers modifiers)
    {
        if (virtualKey == 0) return false;
        var gesture = new HotkeyGesture(modifiers, virtualKey);
        foreach (var entry in _settings.Hotkeys)
        {
            if (entry.Scope != HotkeyScope.Local) continue;
            if (!HotkeyGesture.TryParse(entry.Gesture, out var configured) || configured.IsEmpty) continue;
            if (configured == gesture)
            {
                ExecuteHotkeyAction(entry.Action);
                return true;
            }
        }

        return false;
    }

    public bool IsCapturingHotkey => Hotkeys.CaptureRow is not null;

    public void SaveSettingsNow()
    {
        if (!_store.CanSave && _store.TryReloadAfterAccessRestored(out var reloaded) && reloaded is not null)
        {
            // Adopt the re-read document into the live instance so every later save (including
            // the shutdown saves) persists the newest values instead of the stale in-memory copy.
            _settings.CopyFrom(reloaded);
            Settings.RefreshFromSettings();
            if (_store.Save(_settings))
            {
                Settings.StorageWarning = _loc["Msg.SettingsReloaded"];
                ShowBanner(Settings.StorageWarning, "info");
            }
            else
            {
                Settings.StorageWarning = _store.LastSaveError ?? _loc["Msg.SettingsReadOnly"];
                ShowBanner(Settings.StorageWarning, "warning");
            }

            return;
        }

        if (_store.Save(_settings))
        {
            Settings.StorageWarning = string.Empty;
        }
        else
        {
            Settings.StorageWarning = _store.CanSave ? _loc["Msg.SettingsReadOnly"] : _store.LastSaveError ?? _loc["Msg.SettingsReadOnly"];
            ShowBanner(Settings.StorageWarning, "warning");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _saveTimer.Stop();
        History.StopPlayback();
        Settings.StopMicTest();
    }

    private void OnTick()
    {
        Recorder.Tick();

        if (_bannerUntil != default && DateTime.UtcNow > _bannerUntil)
        {
            _bannerUntil = default;
            BannerText = string.Empty;
        }

        if (++_tickCount % 8 == 0)
        {
            UpdateTray(rebuildMenu: false);
        }
    }

    private void OnStateChanged(StateChangedEventArgs e)
    {
        if (e.NewState is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused)
        {
            Settings.StopMicTest();
        }

        Recorder.RefreshState();
        Settings.IsRecordingActive = Recorder.IsActive;
        ToggleRecordingCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        UpdateTray(rebuildMenu: true);
        if (e.NewState == RecordingState.Idle)
        {
            History.Refresh();
        }
    }

    private void OnRecordingStarted(RecordingStartedEventArgs e)
    {
        _activeSessionId = e.SessionId;
        Recorder.OnStarted();
        History.StopPlayback();
        if (_settings.Notifications.SoundsEnabled && _settings.Notifications.PlayStart)
        {
            _cues.Play(CuePlayer.StartCue, EffectiveVolume(_settings.Notifications.StartVolume), _settings.Notifications.OutputDevice);
        }

        UpdateTray(rebuildMenu: true);
    }

    private void OnRecordingCompleted(RecordingCompletedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_activeSessionId) && !string.Equals(e.SessionId, _activeSessionId, StringComparison.Ordinal))
        {
            return; // stale event from an older session
        }

        Recorder.OnCompleted(e);
        History.Refresh();
        var success = e.Outcome == RecordingOutcome.Success;
        if (!string.IsNullOrWhiteSpace(e.Note))
        {
            ShowBanner(_loc["Msg.RecordingIncomplete"] + e.Note, success ? "success" : "warning");
        }
        else
        {
            ShowBanner(_loc["Msg.SavedTo"] + e.FilePath, "success");
        }

        if (success)
        {
            if (_settings.Notifications.SoundsEnabled && _settings.Notifications.PlayDone)
            {
                _cues.Play(CuePlayer.DoneCue, EffectiveVolume(_settings.Notifications.DoneVolume), _settings.Notifications.OutputDevice);
            }
        }
        else if (_settings.Notifications.SoundsEnabled && _settings.Notifications.PlayError)
        {
            _cues.Play(CuePlayer.ErrorCue, EffectiveVolume(_settings.Notifications.ErrorVolume), _settings.Notifications.OutputDevice);
        }

        if (_settings.Notifications.WindowsNotification)
        {
            _tray.ShowBalloon(_loc["App.Title"], Path.GetFileName(e.FilePath));
        }

        if (success && _settings.Files.OpenFolderAfterSave)
        {
            try
            {
                ShellService.OpenFolder(Settings.FolderDisplay);
            }
            catch
            {
                // Folder may have been removed; the file still saved.
            }
        }

        _activeSessionId = null;
        ToggleRecordingCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        UpdateTray(rebuildMenu: true);
    }

    private void OnEngineError(RecordingErrorEventArgs e)
    {
        if (!string.IsNullOrEmpty(_activeSessionId) && !string.IsNullOrEmpty(e.SessionId) &&
            !string.Equals(e.SessionId, _activeSessionId, StringComparison.Ordinal))
        {
            return; // stale failure from an older session
        }

        var cancelled = e.Outcome == RecordingOutcome.CancelledNoAudio;
        var message = string.IsNullOrWhiteSpace(e.Message) ? _loc["Msg.NoAudio"] : e.Message;
        if (cancelled)
        {
            ShowBanner(_loc["Msg.NoAudio"], "info");
        }
        else
        {
            var prefix = _activeSessionId is null ? _loc["Msg.StartFailed"] : _loc["Msg.RecordFailed"];
            ShowBanner(prefix + message, "error");
            if (_settings.Notifications.SoundsEnabled && _settings.Notifications.PlayError)
            {
                _cues.Play(CuePlayer.ErrorCue, EffectiveVolume(_settings.Notifications.ErrorVolume), _settings.Notifications.OutputDevice);
            }
            if (_settings.Notifications.WindowsNotification)
            {
                _tray.ShowBalloon(_loc["App.Title"], message);
            }
        }

        _activeSessionId = null;
        Recorder.RefreshState();
        if (e.IsFatal && _engine.State == RecordingState.Idle)
        {
            History.Refresh();
        }

        UpdateTray(rebuildMenu: true);
    }

    private void OnSettingsReloaded()
    {
        Hotkeys.ReloadFromSettings(out var hotkeyError);
        if (hotkeyError is not null) ShowBanner(_loc["Msg.HotkeyConflict"] + hotkeyError, "warning");
        Settings.RefreshFromSettings();
        Recorder.RefreshAll();
        UpdateTray(rebuildMenu: true);
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(string.Empty);
        Settings.RebuildChoices();
        Recorder.RefreshState();
        Recorder.RefreshAll();
        History.RefreshLocalized();
        Hotkeys.RefreshLocalized();
        UpdateTray(rebuildMenu: true);
    }

    private void UpdateTray(bool rebuildMenu)
    {
        var state = _engine.State;
        var icon = state switch
        {
            RecordingState.Recording => "recording",
            RecordingState.Paused => "paused",
            RecordingState.Error => "error",
            _ => string.IsNullOrWhiteSpace(_engine.LastError) ? "idle" : "error",
        };

        var tooltip = "FastVRecorder · " + Recorder.StatusText;
        if (Recorder.IsActive) tooltip += " · " + Recorder.TimerText;
        _tray.UpdateState(icon, tooltip);
        if (rebuildMenu)
        {
            _tray.RebuildMenu(Recorder.IsActive, Recorder.IsPaused, IsWindowVisible?.Invoke() ?? true);
        }
    }

    private int EffectiveVolume(int perEventPercent) =>
        _settings.Notifications.Volume * Math.Clamp(perEventPercent, 0, 100) / 100;

    private void ShowBanner(string text, string severity)
    {
        BannerSeverity = severity;
        BannerText = text;
        _bannerUntil = severity is "success" or "info" ? DateTime.UtcNow.AddSeconds(7) : default;
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
