using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FastVRecorder.App.Services;
using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.Hotkeys;
using FastVRecorder.Core.Localization;
using FastVRecorder.Core.Settings;
using FastVRecorder.Infrastructure.Audio;
using FastVRecorder.Infrastructure.Notifications;
using FastVRecorder.Infrastructure.Shell;
using FastVRecorder.Infrastructure.Startup;
using FastVRecorder.Core.Themes;

namespace FastVRecorder.App.ViewModels;

/// <summary>Editable view over the portable settings document; every change auto-saves.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;
    private readonly PortableRoot _paths;
    private readonly FileNamer _namer;
    private readonly AudioDeviceService _devices;
    private readonly StartupService _startup;
    private readonly ThemeService _theme;
    private readonly Loc _loc;
    private readonly CuePlayer _cues;
    private readonly FileLog _log;
    private readonly Action _saveNow;

    private string _statusText = string.Empty;
    private string _storageWarning = string.Empty;
    private bool _resetArmed;
    private bool _mp3Available;

    private WasapiCaptureSource? _testSource;
    private SampleConverter? _testConverter;
    private DispatcherTimer? _testTimer;
    private byte[] _testOutput = Array.Empty<byte>();
    private int _testPeakScaled;
    private DateTime _testStarted;
    private bool _isMicTesting;
    private double _micTestLevel;
    private string _micTestText = string.Empty;
    private string _folderInput = string.Empty;

    public SettingsViewModel(
        AppSettings settings,
        PortableRoot paths,
        FileNamer namer,
        AudioDeviceService devices,
        StartupService startup,
        ThemeService theme,
        Loc loc,
        CuePlayer cues,
        FileLog log,
        Action saveNow)
    {
        _s = settings;
        _paths = paths;
        _namer = namer;
        _devices = devices;
        _startup = startup;
        _theme = theme;
        _loc = loc;
        _cues = cues;
        _log = log;
        _saveNow = saveNow;

        Devices = new ObservableCollection<Choice<string>>();
        FormatChoices = new ObservableCollection<Choice<OutputFormat>>();
        QualityChoices = new ObservableCollection<Choice<QualityPreset>>();
        SampleRateChoices = new ObservableCollection<Choice<int>>();
        ChannelChoices = new ObservableCollection<Choice<int>>();
        EncodingChoices = new ObservableCollection<Choice<SampleEncoding>>();
        BitrateChoices = new ObservableCollection<Choice<int>>();
        NamingChoices = new ObservableCollection<Choice<NamingPreset>>();
        ThemeChoices = new ObservableCollection<Choice<string>>();
        LanguageChoices = new ObservableCollection<Choice<string>>();
        ScopeChoices = new ObservableCollection<Choice<HotkeyScope>>();
        OutputDevices = new ObservableCollection<Choice<string>>();

        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        OpenFolderCommand = new RelayCommand(() => ShellService.OpenFolder(FolderDisplay));
        PreviewStartCommand = new RelayCommand(() => PreviewCue(CuePlayer.StartCue, _s.Notifications.StartVolume));
        PreviewDoneCommand = new RelayCommand(() => PreviewCue(CuePlayer.DoneCue, _s.Notifications.DoneVolume));
        PreviewErrorCommand = new RelayCommand(() => PreviewCue(CuePlayer.ErrorCue, _s.Notifications.ErrorVolume));
        OpenLogsCommand = new RelayCommand(() => ShellService.OpenFolder(Path.GetDirectoryName(_paths.LogPath) ?? _paths.ConfigDir));
        OpenConfigCommand = new RelayCommand(() => ShellService.OpenFolder(_paths.ConfigDir));
        ExportCommand = new RelayCommand(ExportSettings);
        ImportCommand = new RelayCommand(ImportSettings);
        RestoreAppearanceCommand = new RelayCommand(RestoreAppearance);
        ArmResetCommand = new RelayCommand(() => ResetArmed = true);
        CancelResetCommand = new RelayCommand(() => ResetArmed = false);
        ConfirmResetCommand = new RelayCommand(ResetSettings);
        ToggleMicTestCommand = new RelayCommand(ToggleMicTest);
        ApplyFolderCommand = new RelayCommand(CommitFolderInput);

        RebuildChoices();
        _s.Application.StartWithWindows = _startup.IsEnabled;
    }

    public event EventHandler? Changed;
    public event EventHandler? Reloaded;

    public ObservableCollection<Choice<string>> Devices { get; }
    public ObservableCollection<Choice<OutputFormat>> FormatChoices { get; }
    public ObservableCollection<Choice<QualityPreset>> QualityChoices { get; }
    public ObservableCollection<Choice<int>> SampleRateChoices { get; }
    public ObservableCollection<Choice<int>> ChannelChoices { get; }
    public ObservableCollection<Choice<SampleEncoding>> EncodingChoices { get; }
    public ObservableCollection<Choice<int>> BitrateChoices { get; }
    public ObservableCollection<Choice<NamingPreset>> NamingChoices { get; }
    public ObservableCollection<Choice<string>> ThemeChoices { get; }
    public ObservableCollection<Choice<string>> LanguageChoices { get; }
    public ObservableCollection<Choice<HotkeyScope>> ScopeChoices { get; }
    public ObservableCollection<Choice<string>> OutputDevices { get; }

    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand PreviewStartCommand { get; }
    public RelayCommand PreviewDoneCommand { get; }
    public RelayCommand PreviewErrorCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenConfigCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand RestoreAppearanceCommand { get; }
    public RelayCommand ArmResetCommand { get; }
    public RelayCommand CancelResetCommand { get; }
    public RelayCommand ConfirmResetCommand { get; }
    public RelayCommand ToggleMicTestCommand { get; }
    public RelayCommand ApplyFolderCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public string StorageWarning
    {
        get => _storageWarning;
        set { if (Set(ref _storageWarning, value)) OnPropertyChanged(nameof(HasStorageWarning)); }
    }

    public bool HasStorageWarning => !string.IsNullOrWhiteSpace(StorageWarning);

    public bool ResetArmed
    {
        get => _resetArmed;
        private set => Set(ref _resetArmed, value);
    }

    public string VersionText => "v" + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public bool IsMicTesting
    {
        get => _isMicTesting;
        private set
        {
            if (Set(ref _isMicTesting, value))
            {
                OnPropertyChanged(nameof(MicTestButtonText));
            }
        }
    }

    public string MicTestButtonText => IsMicTesting ? _loc["Set.MicTestStop"] : _loc["Set.MicTest"];

    public double MicTestLevel
    {
        get => _micTestLevel;
        private set => Set(ref _micTestLevel, value);
    }

    public string MicTestText
    {
        get => _micTestText;
        private set { if (Set(ref _micTestText, value)) OnPropertyChanged(nameof(HasMicTestText)); }
    }

    public bool HasMicTestText => !string.IsNullOrWhiteSpace(MicTestText);

    private void ToggleMicTest()
    {
        if (IsMicTesting)
        {
            StopMicTest();
            MicTestText = _loc["Set.MicTestStopped"];
            return;
        }

        try
        {
            var device = _devices.ResolveDevice(SelectedDeviceId)
                ?? throw new InvalidOperationException("The selected microphone is not available.");
            var source = new WasapiCaptureSource(device, _log);
            _testConverter = new SampleConverter(source.SourceFormat, TargetFormat.Standard);
            _testOutput = Array.Empty<byte>();
            _testPeakScaled = 0;
            _testSource = source;
            source.DataAvailable += OnTestData;
            source.Start();
            _testStarted = DateTime.UtcNow;
            IsMicTesting = true;
            MicTestLevel = 0;
            MicTestText = _loc["Set.MicTestListening"];

            _testTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _testTimer.Tick -= OnMicTestTick;
            _testTimer.Tick += OnMicTestTick;
            _testTimer.Start();
        }
        catch (Exception ex)
        {
            StopMicTest();
            MicTestText = _loc["Set.MicTestFailed"] + ex.Message;
        }
    }

    private void OnTestData(ReadOnlySpan<byte> buffer)
    {
        var converter = _testConverter;
        if (converter is null) return;
        try
        {
            converter.Convert(buffer, ref _testOutput, out var peak);
            Interlocked.Exchange(ref _testPeakScaled, (int)Math.Clamp(peak * 10000f, 0f, 10000f));
        }
        catch
        {
            // A byte race while stopping is harmless for a level preview.
        }
    }

    private void OnMicTestTick(object? sender, EventArgs e)
    {
        var peak = Interlocked.Exchange(ref _testPeakScaled, 0) / 10000f;
        MicTestLevel = Math.Clamp(peak, 0, 1);
        if (peak >= 0.004)
        {
            MicTestText = _loc["Set.MicTestSignal"] + $" {peak * 100:0}%";
        }
        else if (DateTime.UtcNow - _testStarted > TimeSpan.FromSeconds(2))
        {
            MicTestText = _loc["Set.MicTestNoSignal"];
        }

        if (DateTime.UtcNow - _testStarted > TimeSpan.FromSeconds(15))
        {
            StopMicTest();
            MicTestText = _loc["Set.MicTestStopped"];
        }
    }

    public void StopMicTest()
    {
        _testTimer?.Stop();
        var source = _testSource;
        _testSource = null;
        _testConverter = null;
        if (source is not null)
        {
            try
            {
                source.DataAvailable -= OnTestData;
                source.Stop();
            }
            catch
            {
                // device teardown is best effort
            }
            finally
            {
                source.Dispose();
            }
        }

        IsMicTesting = false;
        MicTestLevel = 0;
    }

    public string ToggleHotkeyText => HotkeyDisplay(HotkeyAction.ToggleRecording);

    public string HotkeyDisplay(HotkeyAction action)
    {
        var entry = _s.Hotkeys.FirstOrDefault(h => h is not null && h.Action == action);
        return HotkeyGesture.TryParse(entry?.Gesture, out var gesture) && !gesture.IsEmpty
            ? gesture.ToString()
            : _loc["Common.Unassigned"];
    }

    public void RefreshHotkeys()
    {
        OnPropertyChanged(nameof(ToggleHotkeyText));
    }

    // ------------------------------------------------------------- devices

    public void RefreshDevices()
    {
        var selected = _s.Recording.DeviceId;
        Devices.Clear();
        Devices.Add(new Choice<string>(AudioDeviceService.DefaultDeviceId, _loc["Rec.SystemDefault"]));
        foreach (var device in _devices.GetCaptureDevices())
        {
            Devices.Add(new Choice<string>(device.Id, device.FriendlyName));
        }

        OnPropertyChanged(nameof(SelectedDeviceId));
        OnPropertyChanged(nameof(SelectedDeviceChoice));
        OnPropertyChanged(nameof(SelectedDeviceLabel));
        if (selected != AudioDeviceService.DefaultDeviceId && Devices.All(d => d.Value != selected))
        {
            // Keep the stored id selected even if the endpoint is temporarily gone; the item is
            // represented by the remembered name.
            if (!string.IsNullOrWhiteSpace(_s.Recording.DeviceName))
            {
                Devices.Add(new Choice<string>(selected, _s.Recording.DeviceName + " (offline)"));
            }
        }
    }

    public string SelectedDeviceId
    {
        get => string.IsNullOrWhiteSpace(_s.Recording.DeviceId) ? AudioDeviceService.DefaultDeviceId : _s.Recording.DeviceId;
        set
        {
            if (string.IsNullOrEmpty(value) || SelectedDeviceId == value) return;
            _s.Recording.DeviceId = value;
            var choice = Devices.FirstOrDefault(d => d.Value == value);
            _s.Recording.DeviceName = value == AudioDeviceService.DefaultDeviceId ? string.Empty : choice?.Label ?? string.Empty;
            OnPropertyChanged(nameof(SelectedDeviceId));
            OnPropertyChanged(nameof(SelectedDeviceLabel));
            NotifyChanged();
        }
    }

    public string SelectedDeviceLabel => Devices.FirstOrDefault(d => d.Value == SelectedDeviceId)?.Label ?? _loc["Rec.SystemDefault"];

    public Choice<string>? SelectedDeviceChoice
    {
        get => Devices.FirstOrDefault(d => string.Equals(d.Value, SelectedDeviceId, StringComparison.Ordinal));
        set
        {
            if (value is not null) SelectedDeviceId = value.Value;
        }
    }

    // ------------------------------------------------------------- format

    public OutputFormat SelectedFormat
    {
        get => _s.Recording.Format;
        set
        {
            if (_s.Recording.Format == value) return;
            _s.Recording.Format = value;
            if (value == OutputFormat.Mp3)
            {
                _s.Recording.Encoding = SampleEncoding.Pcm16;
            }

            OnPropertyChanged(nameof(SelectedFormat));
            OnPropertyChanged(nameof(ShowCustomFormat));
            OnPropertyChanged(nameof(ShowMp3Bitrate));
            OnPropertyChanged(nameof(OutputDescription));
            NotifyChanged();
        }
    }

    public QualityPreset SelectedQuality
    {
        get => _s.Recording.Preset;
        set
        {
            if (_s.Recording.Preset == value) return;
            _s.Recording.Preset = value;
            if (value != QualityPreset.Custom)
            {
                var format = _s.Recording.EffectiveFormat();
                _s.Recording.SampleRate = format.SampleRate;
                _s.Recording.Channels = format.Channels;
                _s.Recording.Encoding = format.Encoding;
            }

            OnPropertyChanged(nameof(SelectedQuality));
            OnPropertyChanged(nameof(SelectedSampleRate));
            OnPropertyChanged(nameof(SelectedChannels));
            OnPropertyChanged(nameof(SelectedEncoding));
            OnPropertyChanged(nameof(ShowCustomFormat));
            OnPropertyChanged(nameof(OutputDescription));
            NotifyChanged();
        }
    }

    public int SelectedSampleRate
    {
        get => _s.Recording.SampleRate;
        set
        {
            if (_s.Recording.SampleRate == value || value <= 0) return;
            _s.Recording.SampleRate = value;
            _s.Recording.Preset = QualityPreset.Custom;
            OnPropertyChanged(nameof(SelectedSampleRate));
            OnPropertyChanged(nameof(SelectedQuality));
            OnPropertyChanged(nameof(ShowCustomFormat));
            OnPropertyChanged(nameof(OutputDescription));
            NotifyChanged();
        }
    }

    public int SelectedChannels
    {
        get => _s.Recording.Channels;
        set
        {
            if (_s.Recording.Channels == value || value is < 1 or > 2) return;
            _s.Recording.Channels = value;
            _s.Recording.Preset = QualityPreset.Custom;
            OnPropertyChanged(nameof(SelectedChannels));
            OnPropertyChanged(nameof(SelectedQuality));
            OnPropertyChanged(nameof(ShowCustomFormat));
            OnPropertyChanged(nameof(OutputDescription));
            NotifyChanged();
        }
    }

    public SampleEncoding SelectedEncoding
    {
        get => _s.Recording.Encoding;
        set
        {
            if (_s.Recording.Encoding == value) return;
            _s.Recording.Encoding = value;
            _s.Recording.Preset = QualityPreset.Custom;
            OnPropertyChanged(nameof(SelectedEncoding));
            OnPropertyChanged(nameof(SelectedQuality));
            OnPropertyChanged(nameof(ShowCustomFormat));
            OnPropertyChanged(nameof(OutputDescription));
            NotifyChanged();
        }
    }

    public int SelectedMp3Bitrate
    {
        get => _s.Recording.Mp3BitrateKbps;
        set
        {
            if (_s.Recording.Mp3BitrateKbps == value || value <= 0) return;
            _s.Recording.Mp3BitrateKbps = value;
            OnPropertyChanged(nameof(SelectedMp3Bitrate));
            OnPropertyChanged(nameof(OutputDescription));
            NotifyChanged();
        }
    }

    public bool ShowCustomFormat => _s.Recording.Preset == QualityPreset.Custom && _s.Recording.Format != OutputFormat.Mp3;
    public bool ShowMp3Bitrate => _s.Recording.Format == OutputFormat.Mp3;
    public string OutputDescription => _s.Recording.DescribeOutput();

    public Choice<OutputFormat>? SelectedFormatChoice
    {
        get => FormatChoices.FirstOrDefault(c => c.Value == SelectedFormat);
        set { if (value is not null) SelectedFormat = value.Value; }
    }

    public Choice<QualityPreset>? SelectedQualityChoice
    {
        get => QualityChoices.FirstOrDefault(c => c.Value == SelectedQuality);
        set { if (value is not null) SelectedQuality = value.Value; }
    }

    public Choice<int>? SelectedSampleRateChoice
    {
        get => SampleRateChoices.FirstOrDefault(c => c.Value == SelectedSampleRate);
        set { if (value is not null) SelectedSampleRate = value.Value; }
    }

    public Choice<int>? SelectedChannelsChoice
    {
        get => ChannelChoices.FirstOrDefault(c => c.Value == SelectedChannels);
        set { if (value is not null) SelectedChannels = value.Value; }
    }

    public Choice<SampleEncoding>? SelectedEncodingChoice
    {
        get => EncodingChoices.FirstOrDefault(c => c.Value == SelectedEncoding);
        set { if (value is not null) SelectedEncoding = value.Value; }
    }

    public Choice<int>? SelectedMp3BitrateChoice
    {
        get => BitrateChoices.FirstOrDefault(c => c.Value == SelectedMp3Bitrate);
        set { if (value is not null) SelectedMp3Bitrate = value.Value; }
    }

    public Choice<NamingPreset>? SelectedNamingChoice
    {
        get => NamingChoices.FirstOrDefault(c => c.Value == SelectedNamingPreset);
        set { if (value is not null) SelectedNamingPreset = value.Value; }
    }

    public Choice<string>? SelectedThemeChoice
    {
        get => ThemeChoices.FirstOrDefault(c => string.Equals(c.Value, SelectedTheme, StringComparison.OrdinalIgnoreCase));
        set { if (value is not null) SelectedTheme = value.Value; }
    }

    public Choice<string>? SelectedLanguageChoice
    {
        get => LanguageChoices.FirstOrDefault(c => string.Equals(c.Value, SelectedLanguage, StringComparison.OrdinalIgnoreCase));
        set { if (value is not null) SelectedLanguage = value.Value; }
    }

    private bool _isRecordingActive;
    public bool IsRecordingActive
    {
        get => _isRecordingActive;
        set { if (Set(ref _isRecordingActive, value)) OnPropertyChanged(nameof(IsRecordingIdle)); }
    }

    public bool IsRecordingIdle => !IsRecordingActive;

    // ------------------------------------------------------------- files

    public string FolderDisplay => _paths.ResolveRecordingFolder(_s.Files.RecordingFolder);

    /// <summary>Editable text; committed on Enter or focus loss so typing never touches the disk.</summary>
    public string FolderInput
    {
        get => _folderInput;
        set => Set(ref _folderInput, value ?? string.Empty);
    }

    public void CommitFolderInput()
    {
        var resolution = RecordingFolderResolver.Resolve(_paths, FolderInput);
        if (!resolution.IsValid)
        {
            StatusText = _loc["Set.FolderInvalid"] + " " + resolution.Error;
            return;
        }

        _s.Files.RecordingFolder = resolution.StoredPath;
        if (!Directory.Exists(resolution.AbsolutePath))
        {
            try
            {
                Directory.CreateDirectory(resolution.AbsolutePath);
                StatusText = _loc["Set.Saved"];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                StatusText = _loc["Set.FolderUnavailable"];
            }
        }
        else
        {
            StatusText = _loc["Set.Saved"];
        }

        FolderInput = FolderDisplay;
        OnPropertyChanged(nameof(FolderDisplay));
        NotifyChanged();
    }

    public NamingPreset SelectedNamingPreset
    {
        get => _s.Files.NamingPreset;
        set
        {
            if (_s.Files.NamingPreset == value) return;
            _s.Files.NamingPreset = value;
            OnPropertyChanged(nameof(SelectedNamingPreset));
            NotifyChanged();
        }
    }

    public string Prefix
    {
        get => _s.Files.Prefix;
        set
        {
            var text = value ?? string.Empty;
            if (_s.Files.Prefix == text) return;
            _s.Files.Prefix = text;
            OnPropertyChanged(nameof(Prefix));
            NotifyChanged();
        }
    }

    public bool IncrementalNumbering
    {
        get => _s.Files.IncrementalNumbering;
        set
        {
            if (_s.Files.IncrementalNumbering == value) return;
            _s.Files.IncrementalNumbering = value;
            OnPropertyChanged(nameof(IncrementalNumbering));
            NotifyChanged();
        }
    }

    public bool DateFolders
    {
        get => _s.Files.DateFolders;
        set
        {
            if (_s.Files.DateFolders == value) return;
            _s.Files.DateFolders = value;
            OnPropertyChanged(nameof(DateFolders));
            NotifyChanged();
        }
    }

    public bool OpenFolderAfterSave
    {
        get => _s.Files.OpenFolderAfterSave;
        set
        {
            if (_s.Files.OpenFolderAfterSave == value) return;
            _s.Files.OpenFolderAfterSave = value;
            OnPropertyChanged(nameof(OpenFolderAfterSave));
            NotifyChanged();
        }
    }

    public string PreviewText
    {
        get
        {
            try
            {
                var extension = _s.Recording.Format == OutputFormat.Mp3 ? ".mp3" : ".wav";
                return _namer.Preview(_s.Files.ToNamingOptions(), extension);
            }
            catch
            {
                return "—";
            }
        }
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _loc["Set.Folder"],
            InitialDirectory = Directory.Exists(FolderDisplay) ? FolderDisplay : _paths.Root,
        };
        if (dialog.ShowDialog() != true) return;
        _s.Files.RecordingFolder = _paths.ToStoredFolder(dialog.FolderName);
        OnPropertyChanged(nameof(FolderDisplay));
        FolderInput = FolderDisplay;
        StatusText = Directory.Exists(dialog.FolderName) ? _loc["Set.Saved"] : _loc["Set.FolderUnavailable"];
        NotifyChanged();
    }

    // ------------------------------------------------------------- notifications

    public bool SoundsEnabled
    {
        get => _s.Notifications.SoundsEnabled;
        set { if (_s.Notifications.SoundsEnabled == value) return; _s.Notifications.SoundsEnabled = value; OnPropertyChanged(nameof(SoundsEnabled)); NotifyChanged(); }
    }

    public double Volume
    {
        get => _s.Notifications.Volume;
        set
        {
            var volume = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (_s.Notifications.Volume == volume) return;
            _s.Notifications.Volume = volume;
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(VolumeText));
            NotifyChanged();
        }
    }

    public string VolumeText => $"{_s.Notifications.Volume}%";

    public bool PlayStart
    {
        get => _s.Notifications.PlayStart;
        set { if (_s.Notifications.PlayStart == value) return; _s.Notifications.PlayStart = value; OnPropertyChanged(nameof(PlayStart)); NotifyChanged(); }
    }

    public bool PlayDone
    {
        get => _s.Notifications.PlayDone;
        set { if (_s.Notifications.PlayDone == value) return; _s.Notifications.PlayDone = value; OnPropertyChanged(nameof(PlayDone)); NotifyChanged(); }
    }

    public bool PlayError
    {
        get => _s.Notifications.PlayError;
        set { if (_s.Notifications.PlayError == value) return; _s.Notifications.PlayError = value; OnPropertyChanged(nameof(PlayError)); NotifyChanged(); }
    }

    public bool WindowsNotification
    {
        get => _s.Notifications.WindowsNotification;
        set { if (_s.Notifications.WindowsNotification == value) return; _s.Notifications.WindowsNotification = value; OnPropertyChanged(nameof(WindowsNotification)); NotifyChanged(); }
    }

    public string SelectedOutputDeviceId
    {
        get => _s.Notifications.OutputDevice;
        set
        {
            var id = value ?? string.Empty;
            if (_s.Notifications.OutputDevice == id) return;
            _s.Notifications.OutputDevice = id;
            OnPropertyChanged(nameof(SelectedOutputDeviceId));
            OnPropertyChanged(nameof(SelectedOutputDeviceChoice));
            NotifyChanged();
        }
    }

    public Choice<string>? SelectedOutputDeviceChoice
    {
        get => OutputDevices.FirstOrDefault(c => string.Equals(c.Value, SelectedOutputDeviceId, StringComparison.Ordinal));
        set { if (value is not null) SelectedOutputDeviceId = value.Value; }
    }

    private void PreviewCue(string cue, int perEventVolume)
    {
        var volume = _s.Notifications.Volume * Math.Clamp(perEventVolume, 0, 100) / 100;
        _cues.Play(cue, volume, _s.Notifications.OutputDevice);
    }

    // ------------------------------------------------------------- application

    public bool StartWithWindows
    {
        get => _s.Application.StartWithWindows;
        set
        {
            if (_s.Application.StartWithWindows == value) return;
            if (!_startup.SetEnabled(value, out var error))
            {
                StatusText = _loc["Set.StartupFailed"] + error;
                OnPropertyChanged();
                return;
            }

            _s.Application.StartWithWindows = value;
            OnPropertyChanged(nameof(StartWithWindows));
            StatusText = _loc["Set.Saved"];
            NotifyChanged();
        }
    }

    public bool StartMinimized
    {
        get => _s.Application.StartMinimized;
        set { if (_s.Application.StartMinimized == value) return; _s.Application.StartMinimized = value; OnPropertyChanged(nameof(StartMinimized)); NotifyChanged(); }
    }

    public bool CloseToTray
    {
        get => _s.Application.CloseToTray;
        set { if (_s.Application.CloseToTray == value) return; _s.Application.CloseToTray = value; OnPropertyChanged(nameof(CloseToTray)); NotifyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => _s.Application.MinimizeToTray;
        set { if (_s.Application.MinimizeToTray == value) return; _s.Application.MinimizeToTray = value; OnPropertyChanged(nameof(MinimizeToTray)); NotifyChanged(); }
    }

    public bool AlwaysOnTop
    {
        get => _s.Application.AlwaysOnTop;
        set { if (_s.Application.AlwaysOnTop == value) return; _s.Application.AlwaysOnTop = value; OnPropertyChanged(nameof(AlwaysOnTop)); NotifyChanged(); }
    }

    public bool RememberWindowPosition
    {
        get => _s.Application.RememberWindowPosition;
        set { if (_s.Application.RememberWindowPosition == value) return; _s.Application.RememberWindowPosition = value; OnPropertyChanged(nameof(RememberWindowPosition)); NotifyChanged(); }
    }

    public bool ShowTaskbarIcon
    {
        get => _s.Application.ShowTaskbarIcon;
        set { if (_s.Application.ShowTaskbarIcon == value) return; _s.Application.ShowTaskbarIcon = value; OnPropertyChanged(nameof(ShowTaskbarIcon)); NotifyChanged(); }
    }

    // ------------------------------------------------------------- appearance

    public string SelectedTheme
    {
        get => _s.Appearance.Theme;
        set
        {
            if (string.Equals(_s.Appearance.Theme, value, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(value)) return;
            _s.Appearance.Theme = value;
            _theme.Apply(value);
            OnPropertyChanged(nameof(SelectedTheme));
            StatusText = _loc["Set.Saved"];
            NotifyChanged();
        }
    }

    public string SelectedLanguage
    {
        get => _s.Appearance.Language;
        set
        {
            if (string.Equals(_s.Appearance.Language, value, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(value)) return;
            _s.Appearance.Language = value;
            _loc.SetLanguage(value, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            RebuildChoices();
            OnPropertyChanged(nameof(SelectedLanguage));
            RaiseAllChanged();
            NotifyChanged();
        }
    }

    private void RestoreAppearance()
    {
        _s.Appearance.Theme = ThemeDefinition.DefaultSlug;
        _s.Appearance.Language = "auto";
        _theme.Apply(ThemeDefinition.DefaultSlug);
        _loc.SetLanguage("auto", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        RebuildChoices();
        RaiseAllChanged();
        StatusText = _loc["Set.Saved"];
        NotifyChanged();
    }

    // ------------------------------------------------------------- advanced

    public double FlushSeconds
    {
        get => _s.Advanced.RecoveryFlushSeconds;
        set
        {
            var seconds = Math.Clamp(Math.Round(value, 1), 0.5, 10);
            if (Math.Abs(_s.Advanced.RecoveryFlushSeconds - seconds) < 0.01) return;
            _s.Advanced.RecoveryFlushSeconds = seconds;
            OnPropertyChanged(nameof(FlushSeconds));
            OnPropertyChanged(nameof(FlushSecondsText));
            NotifyChanged();
        }
    }

    public string FlushSecondsText => $"{_s.Advanced.RecoveryFlushSeconds:0.0} {_loc["Set.Seconds"]}";

    public int LowDiskMb
    {
        get => _s.Advanced.LowDiskSpaceMb;
        set
        {
            var mb = Math.Clamp(value, 10, 1_000_000);
            if (_s.Advanced.LowDiskSpaceMb == mb) return;
            _s.Advanced.LowDiskSpaceMb = mb;
            OnPropertyChanged(nameof(LowDiskMb));
            NotifyChanged();
        }
    }

    public bool DiagnosticsEnabled
    {
        get => _s.Advanced.DiagnosticsLog;
        set
        {
            if (_s.Advanced.DiagnosticsLog == value) return;
            _s.Advanced.DiagnosticsLog = value;
            _log.Enabled = value;
            OnPropertyChanged(nameof(DiagnosticsEnabled));
            NotifyChanged();
        }
    }

    private void ExportSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = _loc["Set.Export"],
            Filter = "JSON (*.json)|*.json",
            FileName = "fastvrecorder-settings.json",
            InitialDirectory = _paths.ConfigDir,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, SettingsStore.Serialize(_s));
            StatusText = _loc["Set.ExportDone"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = _loc["Set.ImportFailed"] + ex.Message;
        }
    }

    private void ImportSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _loc["Set.Import"],
            Filter = "JSON (*.json)|*.json",
            InitialDirectory = _paths.ConfigDir,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var text = File.ReadAllText(dialog.FileName);
            var imported = SettingsStore.Deserialize(text);
            if (imported is null)
            {
                StatusText = _loc["Set.ImportFailed"] + "not a settings document.";
                return;
            }

            imported.Normalize();
            _s.Recording = imported.Recording;
            _s.Files = imported.Files;
            _s.Notifications = imported.Notifications;
            _s.Application = imported.Application;
            _s.Appearance = imported.Appearance;
            _s.Advanced = imported.Advanced;
            _s.Window = imported.Window;
            _s.Hotkeys = imported.Hotkeys;
            _s.SchemaVersion = imported.SchemaVersion;
            ApplyAfterReload();
            StatusText = _loc["Set.ImportDone"];
            NotifyChanged();
            Reloaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText = _loc["Set.ImportFailed"] + ex.Message;
        }
    }

    private void ResetSettings()
    {
        var defaults = new AppSettings();
        defaults.Normalize();
        _s.Recording = defaults.Recording;
        _s.Files = defaults.Files;
        _s.Notifications = defaults.Notifications;
        _s.Application = defaults.Application;
        _s.Appearance = defaults.Appearance;
        _s.Advanced = defaults.Advanced;
        _s.Window = defaults.Window;
        _s.Hotkeys = defaults.Hotkeys;
        ApplyAfterReload();
        StatusText = _loc["Set.ResetDone"];
        ResetArmed = false;
        NotifyChanged();
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAfterReload()
    {
        _theme.Apply(_s.Appearance.Theme);
        _loc.SetLanguage(_s.Appearance.Language, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        _log.Enabled = _s.Advanced.DiagnosticsLog;
        _startup.SetEnabled(_s.Application.StartWithWindows, out _);
        _s.Application.StartWithWindows = _startup.IsEnabled;
        RebuildChoices();
        RaiseAllChanged();
        _saveNow();
    }

    // ------------------------------------------------------------- helpers

    public void RefreshFromSettings()
    {
        RebuildChoices();
        FolderInput = FolderDisplay;
        OnPropertyChanged(nameof(FolderInput));
        RaiseAllChanged();
    }

    /// <summary>
    /// Rebuilds the format picker. MP3 is offered when the background probe proved Media
    /// Foundation available, or while it is the saved choice (so the ComboBox binding never
    /// loses its selected item before the probe finishes).
    /// </summary>
    private void RebuildFormatChoices()
    {
        FormatChoices.Clear();
        FormatChoices.Add(new Choice<OutputFormat>(OutputFormat.Wav, "WAV"));
        if (_mp3Available || _s.Recording.Format == OutputFormat.Mp3)
        {
            FormatChoices.Add(new Choice<OutputFormat>(OutputFormat.Mp3, "MP3"));
        }

        OnPropertyChanged(nameof(Mp3Available));
    }

    /// <summary>Called once the deferred Media Foundation probe completes (UI thread).</summary>
    public void UpdateMp3Availability(bool available)
    {
        _mp3Available = available;
        Try(RebuildFormatChoices);
    }

    public bool Mp3Available => _mp3Available;

    public void RebuildChoices()
    {
        Try(() =>
        {
            RefreshDevices();
            RebuildFormatChoices();

            QualityChoices.Clear();
            QualityChoices.Add(new Choice<QualityPreset>(QualityPreset.Voice, _loc["Set.QualityVoice"]));
            QualityChoices.Add(new Choice<QualityPreset>(QualityPreset.Standard, _loc["Set.QualityStandard"]));
            QualityChoices.Add(new Choice<QualityPreset>(QualityPreset.HighQuality, _loc["Set.QualityHigh"]));
            QualityChoices.Add(new Choice<QualityPreset>(QualityPreset.Custom, _loc["Set.QualityCustom"]));

            SampleRateChoices.Clear();
            foreach (var rate in new[] { 16000, 22050, 24000, 32000, 44100, 48000 })
            {
                SampleRateChoices.Add(new Choice<int>(rate, $"{rate / 1000.0:0.#} kHz"));
            }

            ChannelChoices.Clear();
            ChannelChoices.Add(new Choice<int>(1, _loc["Set.Mono"]));
            ChannelChoices.Add(new Choice<int>(2, _loc["Set.Stereo"]));

            EncodingChoices.Clear();
            EncodingChoices.Add(new Choice<SampleEncoding>(SampleEncoding.Pcm16, "16-bit"));
            EncodingChoices.Add(new Choice<SampleEncoding>(SampleEncoding.Pcm24, "24-bit"));
            EncodingChoices.Add(new Choice<SampleEncoding>(SampleEncoding.Float32, "32-bit float"));

            BitrateChoices.Clear();
            foreach (var kbps in new[] { 64, 96, 128, 192 })
            {
                BitrateChoices.Add(new Choice<int>(kbps, $"{kbps} kbps"));
            }

            NamingChoices.Clear();
            NamingChoices.Add(new Choice<NamingPreset>(NamingPreset.DateTime, _loc["Set.NamingTimestamp"]));
            NamingChoices.Add(new Choice<NamingPreset>(NamingPreset.Counter, _loc["Set.NamingCounter"]));
            NamingChoices.Add(new Choice<NamingPreset>(NamingPreset.PrefixDateCounter, _loc["Set.NamingVoiceDate"]));

            ThemeChoices.Clear();
            foreach (var theme in _theme.Catalog.Themes)
            {
                ThemeChoices.Add(new Choice<string>(theme.Slug, theme.Label));
            }

            LanguageChoices.Clear();
            LanguageChoices.Add(new Choice<string>("auto", _loc["Set.LanguageAuto"]));
            foreach (var language in LanguageCatalog.All)
            {
                LanguageChoices.Add(new Choice<string>(language.Code, language.NativeName));
            }

            ScopeChoices.Clear();
            ScopeChoices.Add(new Choice<HotkeyScope>(HotkeyScope.Global, _loc["Keys.Global"]));
            ScopeChoices.Add(new Choice<HotkeyScope>(HotkeyScope.Local, _loc["Keys.Local"]));

            OutputDevices.Clear();
            OutputDevices.Add(new Choice<string>(string.Empty, _loc["Set.SystemDefaultOutput"]));
            foreach (var output in _devices.GetRenderDevices())
            {
                OutputDevices.Add(new Choice<string>(output.Id, output.FriendlyName));
            }

            FolderInput = FolderDisplay;
            RaiseAllChanged();
        });
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Choice refresh is cosmetic; never break the window.
        }
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(OutputDescription));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(SelectedDeviceChoice));
        OnPropertyChanged(nameof(SelectedFormatChoice));
        OnPropertyChanged(nameof(SelectedQualityChoice));
        OnPropertyChanged(nameof(SelectedSampleRateChoice));
        OnPropertyChanged(nameof(SelectedChannelsChoice));
        OnPropertyChanged(nameof(SelectedEncodingChoice));
        OnPropertyChanged(nameof(SelectedMp3BitrateChoice));
        OnPropertyChanged(nameof(SelectedNamingChoice));
        OnPropertyChanged(nameof(SelectedThemeChoice));
        OnPropertyChanged(nameof(SelectedLanguageChoice));
        OnPropertyChanged(nameof(SelectedOutputDeviceChoice));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
