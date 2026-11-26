using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using FastVRecorder.Core.Hotkeys;
using FastVRecorder.Core.Settings;
using FastVRecorder.Infrastructure.Notifications;
using FastVRecorder.Infrastructure.Recording;
using FastVRecorder.Infrastructure.Shell;

namespace FastVRecorder.App.ViewModels;

/// <summary>Recorder page state, timer, meter and actions.</summary>
public sealed class RecorderViewModel : ObservableObject
{
    private readonly RecordingEngine _engine;
    private readonly AppSettings _settings;
    private readonly Loc _loc;
    private DateTime _savedUntil;
    private DateTime _nextSpaceRefresh;
    private string _lastFileName = string.Empty;
    private string _timerText = "00:00";
    private double _level;
    private string _freeSpaceText = string.Empty;
    private bool _isLowDisk;
    private RecordingState _state = RecordingState.Idle;
    private int _silentTicks;
    private bool _noSignal;

    public RecorderViewModel(RecordingEngine engine, SettingsViewModel settings, AppSettings appSettings, Loc loc)
    {
        _engine = engine;
        Settings = settings;
        _settings = appSettings;
        _loc = loc;
        ToggleCommand = new RelayCommand(Toggle, () => CanToggle);
        PauseCommand = new RelayCommand(Pause, () => CanPause);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        StatusText = _loc["Status.Ready"];
        RefreshFreeSpace();
    }

    public SettingsViewModel Settings { get; }

    public RelayCommand ToggleCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public string StatusText { get; private set; } = string.Empty;

    public string TimerText
    {
        get => _timerText;
        private set => Set(ref _timerText, value);
    }

    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    public string FileNameText { get; private set; } = string.Empty;

    public string FreeSpaceText
    {
        get => _freeSpaceText;
        private set => Set(ref _freeSpaceText, value);
    }

    public bool IsLowDisk
    {
        get => _isLowDisk;
        private set => Set(ref _isLowDisk, value);
    }

    /// <summary>True after ~5 s of recording with no input peaks above the noise floor.</summary>
    public bool ShowNoSignalWarning
    {
        get => _noSignal;
        private set
        {
            if (Set(ref _noSignal, value)) OnPropertyChanged(nameof(ShowNoSignalWarning));
        }
    }

    public string RecordButtonText => IsActive ? _loc["Rec.StopRecording"] : _loc["Rec.StartRecording"];

    public bool IsRecording => _state == RecordingState.Recording;
    public bool IsPaused => _state == RecordingState.Paused;
    public bool IsActive => IsRecording || IsPaused;
    public bool HasError => _state == RecordingState.Error;
    public bool CanToggle => _state is RecordingState.Idle or RecordingState.Recording or RecordingState.Paused or RecordingState.Error;
    public bool CanPause => _state is RecordingState.Recording or RecordingState.Paused;
    public RecordingState State => _state;

    public void SetLastFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _lastFileName = Path.GetFileName(path);
        FileNameText = _lastFileName;
        OnPropertyChanged(nameof(FileNameText));
    }

    public void RefreshState()
    {
        _state = _engine.State;
        var status = _state switch
        {
            RecordingState.Starting => _loc["Status.Starting"],
            RecordingState.Recording => _loc["Status.Recording"],
            RecordingState.Paused => _loc["Status.Paused"],
            RecordingState.Stopping => _loc["Status.Saving"],
            RecordingState.Finalizing => _loc["Status.Saving"],
            RecordingState.Recovering => _loc["Status.Recovering"],
            RecordingState.Error => _loc["Status.Error"],
            _ => _savedUntil > DateTime.UtcNow ? _loc["Status.Saved"] : _loc["Status.Ready"],
        };

        if (StatusText != status)
        {
            StatusText = status;
            OnPropertyChanged(nameof(StatusText));
        }

        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(RecordButtonText));
        ToggleCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
    }

    public void OnStarted()
    {
        _savedUntil = default;
        _silentTicks = 0;
        ShowNoSignalWarning = false;
        FileNameText = _engine.CurrentFileName;
        OnPropertyChanged(nameof(FileNameText));
        RefreshState();
    }

    public void OnCompleted(RecordingCompletedEventArgs args)
    {
        _savedUntil = DateTime.UtcNow.AddSeconds(4);
        _silentTicks = 0;
        ShowNoSignalWarning = false;
        _lastFileName = Path.GetFileName(args.FilePath);
        FileNameText = _lastFileName;
        OnPropertyChanged(nameof(FileNameText));
        RefreshState();
    }

    public void Tick()
    {
        if (IsActive)
        {
            Level = Math.Clamp(_engine.ReadLevel(), 0, 1);
        }
        else if (Level > 0)
        {
            Level = Math.Max(0, Level - 0.1);
        }

        if (IsRecording)
        {
            if (Level >= 0.004)
            {
                _silentTicks = 0;
                ShowNoSignalWarning = false;
            }
            else if (++_silentTicks >= 42)
            {
                ShowNoSignalWarning = true;
            }
        }
        else
        {
            _silentTicks = 0;
        }

        TimerText = FormatElapsed(_engine.Elapsed);

        if (DateTime.UtcNow >= _nextSpaceRefresh)
        {
            _nextSpaceRefresh = DateTime.UtcNow.AddSeconds(2);
            RefreshFreeSpace();
        }

        if (_savedUntil != default && DateTime.UtcNow > _savedUntil && _engine.State == RecordingState.Idle)
        {
            _savedUntil = default;
            StatusText = _loc["Status.Ready"];
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public void RefreshAll()
    {
        RefreshState();
        RefreshFreeSpace();
        var file = !string.IsNullOrEmpty(_engine.CurrentFileName)
            ? _engine.CurrentFileName
            : !string.IsNullOrEmpty(_lastFileName) ? _lastFileName : _engine.LastOutputPath;
        if (!string.IsNullOrEmpty(file))
        {
            _lastFileName = Path.GetFileName(file);
            FileNameText = _lastFileName;
            OnPropertyChanged(nameof(FileNameText));
        }
        else
        {
            FileNameText = _loc["Rec.NoFile"];
            OnPropertyChanged(nameof(FileNameText));
        }
    }

    public void RefreshFreeSpace()
    {
        try
        {
            var folder = Settings.FolderDisplay;
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
            {
                FreeSpaceText = _loc["Rec.FreeSpaceUnknown"];
                IsLowDisk = false;
                return;
            }

            var drive = new DriveInfo(root);
            var free = drive.AvailableFreeSpace;
            FreeSpaceText = FormatSize(free) + " " + _loc["Rec.FreeSpace"];
            IsLowDisk = free / (1024 * 1024) < _settings.Advanced.LowDiskSpaceMb;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            FreeSpaceText = _loc["Rec.FreeSpaceUnknown"];
            IsLowDisk = false;
        }
    }

    private async void Toggle()
    {
        if (!CanToggle) return;
        try
        {
            await _engine.ToggleAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private async void Pause()
    {
        if (!CanPause) return;
        try
        {
            await _engine.TogglePauseAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void OpenFolder()
    {
        try
        {
            ShellService.OpenFolder(Settings.FolderDisplay);
        }
        catch (Exception)
        {
            // The folder may be on a disconnected drive; the settings page reports the path.
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static string FormatSize(long bytes)
    {
        const double mb = 1024d * 1024d;
        const double gb = 1024d * 1024d * 1024d;
        if (bytes >= gb) return $"{bytes / gb:0.0} GB";
        if (bytes >= mb) return $"{bytes / mb:0} MB";
        return $"{bytes / 1024d:0} KB";
    }
}
