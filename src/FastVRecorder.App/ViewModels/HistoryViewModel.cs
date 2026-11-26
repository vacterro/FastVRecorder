using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.History;
using FastVRecorder.Infrastructure.Shell;
using NAudio.Wave;

namespace FastVRecorder.App.ViewModels;

public sealed class HistoryItem
{
    public HistoryItem(Func<HistoryEntry> entry, bool missing, string status)
    {
        Entry = entry;
        Missing = missing;
        StatusText = status;
    }

    public Func<HistoryEntry> Entry { get; }
    public HistoryEntry Value => Entry();
    public string Path => Value.Path;
    public string Name => System.IO.Path.GetFileName(Value.Path);
    public string DateText => Value.CreatedLocal.ToString("yyyy-MM-dd HH:mm");
    public string DurationText => FormatDuration(TimeSpan.FromSeconds(Math.Max(0, Value.DurationSeconds)));
    public string SizeText => FormatSize(Value.SizeBytes);
    public string FormatText => Value.Format;
    public string StatusText { get; }
    public bool Missing { get; }
    public bool Recovered => Value.Recovered;
    public bool Incomplete => !Value.Recovered && !string.IsNullOrWhiteSpace(Value.Note);
    public string? Note => Value.Note;

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";

    public static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = 1024d * 1024d;
        const double gb = 1024d * 1024d * 1024d;
        if (bytes >= gb) return $"{bytes / gb:0.0} GB";
        if (bytes >= mb) return $"{bytes / mb:0.0} MB";
        return $"{bytes / kb:0} KB";
    }
}

/// <summary>History index with inline rename/delete and a single built-in player.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly HistoryStore _history;
    private readonly Loc _loc;
    private readonly DispatcherTimer _playTimer;
    private WaveOut? _output;
    private AudioFileReader? _reader;
    private string? _playingPath;
    private HistoryItem? _selected;
    private bool _isPlaying;
    private string _playbackText = string.Empty;
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;
    private bool _isRenaming;
    private string _renameText = string.Empty;
    private bool _isDeletePending;
    private string _deleteName = string.Empty;
    private bool _refreshing;

    public HistoryViewModel(HistoryStore history, Loc loc)
    {
        _history = history;
        _loc = loc;
        Items = new ObservableCollection<HistoryItem>();

        PlayPauseCommand = new RelayCommand(PlayPause, () => SelectedItem is { Missing: false } || _isPlaying);
        OpenCommand = new RelayCommand(OpenSelected, () => SelectedItem is { Missing: false });
        RevealCommand = new RelayCommand(RevealSelected, () => SelectedItem is { Missing: false });
        CopyPathCommand = new RelayCommand(CopySelectedPath, () => SelectedItem is not null);
        BeginRenameCommand = new RelayCommand(BeginRename, () => SelectedItem is { Missing: false });
        CommitRenameCommand = new RelayCommand(CommitRename, () => SelectedItem is not null);
        CancelRenameCommand = new RelayCommand(() => { IsRenaming = false; ErrorText = string.Empty; });
        DeleteRequestCommand = new RelayCommand(RequestDelete, () => SelectedItem is not null);
        ConfirmDeleteCommand = new RelayCommand(ConfirmDelete, () => SelectedItem is not null);
        CancelDeleteCommand = new RelayCommand(() => { IsDeletePending = false; });

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playTimer.Tick += (_, _) => UpdatePlaybackText();

        _history.Changed += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Refresh();
            else dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
        };
    }

    public ObservableCollection<HistoryItem> Items { get; }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand CommitRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand DeleteRequestCommand { get; }
    public RelayCommand ConfirmDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }

    public HistoryItem? SelectedItem
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            IsRenaming = false;
            IsDeletePending = false;
            ErrorText = string.Empty;
            if (_playingPath is not null && value is not null && !string.Equals(_playingPath, value.Path, StringComparison.OrdinalIgnoreCase))
            {
                StopPlayback();
            }

            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(HasSelection));
            RaiseCommandStates();
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayButtonText)); }
    }

    public string PlayButtonText => IsPlaying ? _loc["Hist.Stop"] : _loc["Hist.Play"];

    public string PlaybackText
    {
        get => _playbackText;
        private set => Set(ref _playbackText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public string ErrorText
    {
        get => _errorText;
        private set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => Set(ref _isRenaming, value);
    }

    public string RenameText
    {
        get => _renameText;
        set => Set(ref _renameText, value);
    }

    public bool IsDeletePending
    {
        get => _isDeletePending;
        private set => Set(ref _isDeletePending, value);
    }

    public string DeleteName
    {
        get => _deleteName;
        private set => Set(ref _deleteName, value);
    }

    public bool IsEmpty => Items.Count == 0;

    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var selectedPath = _selected?.Path;
            var entries = _history.Snapshot();
            Items.Clear();
            foreach (var entry in entries)
            {
                var missing = !File.Exists(entry.Path);
                var status = missing
                    ? _loc["Hist.Missing"]
                    : entry.Recovered ? _loc["Hist.Recovered"]
                    : !string.IsNullOrWhiteSpace(entry.Note) ? _loc["Hist.Incomplete"]
                    : string.Empty;
                Items.Add(new HistoryItem(() => entry, missing, status));
            }

            OnPropertyChanged(nameof(IsEmpty));
            _selected = selectedPath is null
                ? null
                : Items.FirstOrDefault(i => string.Equals(i.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(HasSelection));
            if (_playingPath is not null && Items.All(i => !string.Equals(i.Path, _playingPath, StringComparison.OrdinalIgnoreCase) || i.Missing))
            {
                StopPlayback();
            }

            RaiseCommandStates();
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void RefreshLocalized()
    {
        Refresh();
    }

    private void PlayPause()
    {
        var item = SelectedItem;
        if (item is null)
        {
            if (_isPlaying) StopPlayback();
            return;
        }

        if (_isPlaying && string.Equals(_playingPath, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            StopPlayback();
            return;
        }

        Play(item);
    }

    private void Play(HistoryItem item)
    {
        StopPlayback();
        if (item.Missing)
        {
            ErrorText = _loc["Hist.PlayFailed"] + _loc["Hist.Missing"];
            return;
        }

        try
        {
            var reader = new AudioFileReader(item.Path);
            var output = new WaveOut();
            output.Init(reader);
            output.PlaybackStopped += (_, _) =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                dispatcher.BeginInvoke(new Action(StopPlayback), DispatcherPriority.Background);
            };
            _reader = reader;
            _output = output;
            _playingPath = item.Path;
            IsPlaying = true;
            PlaybackText = "00:00 / " + HistoryItem.FormatDuration(reader.TotalTime);
            output.Play();
            _playTimer.Start();
            ErrorText = string.Empty;
            RaiseCommandStates();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            StopPlayback();
            ErrorText = _loc["Hist.PlayFailed"] + ex.Message;
        }
    }

    public void StopPlayback()
    {
        _playTimer.Stop();
        var output = _output;
        var reader = _reader;
        _output = null;
        _reader = null;
        var wasPlaying = IsPlaying;
        _playingPath = null;
        IsPlaying = false;
        PlaybackText = string.Empty;
        try { output?.Stop(); } catch { /* device may be gone */ }
        try { output?.Dispose(); } catch { /* best effort */ }
        try { reader?.Dispose(); } catch { /* best effort */ }
        if (wasPlaying) RaiseCommandStates();
    }

    private void UpdatePlaybackText()
    {
        try
        {
            if (_reader is not null && _output is not null)
            {
                PlaybackText = HistoryItem.FormatDuration(_reader.CurrentTime) + " / " + HistoryItem.FormatDuration(_reader.TotalTime);
            }
        }
        catch (ObjectDisposedException)
        {
            StopPlayback();
        }
    }

    private void OpenSelected()
    {
        var item = SelectedItem;
        if (item is null) return;
        ShellService.OpenFile(item.Path);
    }

    private void RevealSelected()
    {
        var item = SelectedItem;
        if (item is null) return;
        ShellService.RevealFile(item.Path);
    }

    private void CopySelectedPath()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (ShellService.CopyText(item.Path)) StatusText = _loc["Hist.PathCopied"];
        else ErrorText = _loc["Hist.CopyFailed"];
    }

    private void BeginRename()
    {
        var item = SelectedItem;
        if (item is null) return;
        IsDeletePending = false;
        RenameText = item.Name;
        IsRenaming = true;
        ErrorText = string.Empty;
        StatusText = string.Empty;
    }

    private void CommitRename()
    {
        var item = SelectedItem;
        if (item is null) return;
        var oldPath = item.Path;
        var name = (RenameText ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ErrorText = _loc["Hist.RenameFailed"] + "empty file name.";
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(oldPath)!;
            if (!Path.HasExtension(name)) name += Path.GetExtension(oldPath);
            var safe = Path.GetFileName(PathSafety.SanitizeComponent(name, Path.GetFileName(oldPath)));
            var target = Path.Combine(directory, safe);
            if (string.Equals(target, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                IsRenaming = false;
                return;
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                ErrorText = _loc["Hist.RenameFailed"] + "the file name already exists.";
                return;
            }

            File.Move(oldPath, target, overwrite: false);
            var entry = _history.Snapshot().FirstOrDefault(e => string.Equals(e.Path, oldPath, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                _history.Remove(oldPath);
                entry.Path = target;
                _history.Add(entry);
            }

            IsRenaming = false;
            StatusText = _loc["Common.Saved"];
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorText = _loc["Hist.RenameFailed"] + ex.Message;
        }
    }

    private void RequestDelete()
    {
        var item = SelectedItem;
        if (item is null) return;
        IsRenaming = false;
        DeleteName = item.Name;
        IsDeletePending = true;
    }

    private void ConfirmDelete()
    {
        var item = SelectedItem;
        if (item is null) return;
        IsDeletePending = false;
        if (string.Equals(_playingPath, item.Path, StringComparison.OrdinalIgnoreCase)) StopPlayback();
        if (ShellService.DeleteToRecycleBin(item.Path, out var error))
        {
            _history.Remove(item.Path);
            StatusText = _loc["Hist.Deleted"];
            Refresh();
        }
        else
        {
            ErrorText = _loc["Hist.DeleteFailed"] + error;
        }
    }

    private void RaiseCommandStates()
    {
        PlayPauseCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        RevealCommand.RaiseCanExecuteChanged();
        CopyPathCommand.RaiseCanExecuteChanged();
        BeginRenameCommand.RaiseCanExecuteChanged();
        CommitRenameCommand.RaiseCanExecuteChanged();
        DeleteRequestCommand.RaiseCanExecuteChanged();
        ConfirmDeleteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PlayButtonText));
    }
}
