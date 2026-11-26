using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FastVRecorder.App.Services;
using FastVRecorder.App.ViewModels;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.History;
using FastVRecorder.Core.Localization;
using FastVRecorder.Core.Recovery;
using FastVRecorder.Core.Settings;
using FastVRecorder.Core.Themes;
using FastVRecorder.Infrastructure.Audio;
using FastVRecorder.Infrastructure.Hotkeys;
using FastVRecorder.Infrastructure.Notifications;
using FastVRecorder.Infrastructure.Recovery;
using FastVRecorder.Infrastructure.Recording;
using FastVRecorder.Infrastructure.Startup;

namespace FastVRecorder.App;

public partial class App : Application
{
    public static bool IsExiting { get; set; }
    public static App Instance => (App)Current;

    private Mutex? _mutex;
    private PortableRoot? _paths;
    private FileLog? _log;
    private SettingsStore? _store;
    private AppSettings? _settings;
    private HistoryStore? _history;
    private RecordingJournal? _journal;
    private AudioDeviceService? _devices;
    private RecordingEngine? _engine;
    private CuePlayer? _cues;
    private GlobalHotkeyService? _hotkeys;
    private StartupService? _startup;
    private RecoveryService? _recovery;
    private TrayService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(initiallyOwned: true, @"Local\FastVRecorder.SingleInstance", out var created);
        if (!created)
        {
            var russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
            MessageBox.Show(
                russian ? "FastVRecorder уже запущен." : "FastVRecorder is already running.",
                "FastVRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log?.Error("Unhandled process exception", args.ExceptionObject as Exception);

        try
        {
            Compose(e.Args);
        }
        catch (Exception ex)
        {
            _log?.Error("FastVRecorder failed to start", ex);
            MessageBox.Show("FastVRecorder could not start: " + ex.Message, "FastVRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void Compose(string[] args)
    {
        _paths = PortableRoot.FromExecutable();
        _log = new FileLog(_paths.LogPath);
        _store = new SettingsStore(_paths.SettingsPath);
        _settings = _store.Load();
        _log.Info($"FastVRecorder starting from {_paths.Root} (settings: {_store.LoadStatus})");
        _log.Enabled = _settings.Advanced.DiagnosticsLog;
        LogTraceListener.Attach(_log);

        var loc = Loc.Instance;
        var languageOverrides = LanguagePacks.LoadOverrides(_paths.LanguagesDir);
        if (languageOverrides > 0) _log.Info($"Loaded {languageOverrides} portable language override(s).");
        foreach (var problem in LanguagePacks.LoadProblems) _log.Warn("Language: " + problem);
        loc.SetLanguage(_settings.Appearance.Language, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

        var catalog = ThemeCatalog.Load(Array.Empty<(string, string)>(), _paths.ThemesDir);
        foreach (var problem in catalog.Problems) _log.Warn("Theme: " + problem);
        var theme = new ThemeService(catalog);
        theme.Apply(_settings.Appearance.Theme);

        _history = new HistoryStore(_paths.HistoryPath);
        _history.Load();

        var counters = new JsonCounterStore(_paths.CountersPath);
        var namer = new FileNamer(counters);
        _journal = new RecordingJournal(_paths.JournalPath);
        _devices = new AudioDeviceService(_log);
        _engine = new RecordingEngine(_paths, new WasapiCaptureFactory(_devices, _log), namer, _history, _journal, _log, () => _settings);
        _cues = new CuePlayer(_paths.MediaDir, _log, _devices);
        _hotkeys = new GlobalHotkeyService(_log);
        _startup = new StartupService(Environment.ProcessPath ?? Path.Combine(_paths.Root, "FastVRecorder.exe"), _paths.Root);
        _recovery = new RecoveryService(_paths, _journal, _history, _log, () => _settings, _engine.IsPartActive);
        _tray = new TrayService(_paths.MediaDir, loc);

        _viewModel = new MainViewModel(
            _paths,
            _settings,
            _store,
            _history,
            _engine,
            _devices,
            namer,
            _cues,
            _hotkeys,
            _startup,
            theme,
            _tray,
            _log,
            loc);

        _tray.ShowHideRequested += () => _window?.ToggleVisibility();
        _tray.ToggleRecordRequested += () => _viewModel?.ToggleRecording();
        _tray.OpenFolderRequested += () => _viewModel?.OpenFolder();
        _tray.OpenLastRequested += () => _viewModel?.OpenLast();
        _tray.SettingsRequested += () =>
        {
            _window?.ShowFromTray();
            _viewModel?.ShowPage(UiPage.Settings);
        };
        _tray.ExitRequested += ExitApplication;

        _window = new MainWindow(_viewModel, _settings, _paths, _log);
        MainWindow = _window;
        _viewModel.IsWindowVisible = () => _window.IsVisible;
        _window.Show();
        _viewModel.Initialize();

        var pageArgument = args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (pageArgument is not null && Enum.TryParse<UiPage>(pageArgument[7..], ignoreCase: true, out var requestedPage))
        {
            _viewModel.ShowPage(requestedPage);
        }
        else if (_settings.Window.Page != UiPage.Recorder)
        {
            _viewModel.ShowPage(_settings.Window.Page);
        }

        if (_settings.Application.StartMinimized)
        {
            if (_settings.Application.MinimizeToTray)
            {
                _window.HideToTray();
            }
            else
            {
                _window.WindowState = WindowState.Minimized;
            }
        }

        // Re-apply hotkeys after the window (and its message window) exists.
        _viewModel.Hotkeys.ReloadFromSettings(out var hotkeyError);
        if (hotkeyError is not null) _viewModel.ReportHotkeyError(hotkeyError);

        // Crash recovery runs off the UI thread after the window is visible.
        _engine.BeginRecoveryScan();
        _ = Task.Run(() => _recovery.RecoverAll())
            .ContinueWith(task =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _engine.EndRecoveryScan();
                    if (task.IsCompletedSuccessfully) _viewModel?.ReportRecovery(task.Result);
                }), DispatcherPriority.Background);
                if (!task.IsCompletedSuccessfully)
                {
                    _log?.Error("Recovery scan failed", task.Exception);
                }
            }, TaskScheduler.Default);
    }

    public void ExitApplication()
    {
        IsExiting = true;
        try
        {
            _viewModel?.SaveSettingsNow();
        }
        catch
        {
            // shutting down
        }

        try
        {
            _window?.AllowClose();
            _window?.Close();
        }
        catch
        {
            // shutting down
        }

        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        try
        {
            _viewModel?.ShowError(Loc.Instance["Msg.UnexpectedError"] + e.Exception.Message);
        }
        catch
        {
            // never rethrow from the handler
        }

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.SaveSettingsNow();
        }
        catch
        {
            // shutdown
        }

        _viewModel?.Dispose();
        _engine?.Dispose();
        _hotkeys?.Dispose();
        _cues?.Dispose();
        _tray?.Dispose();
        _devices?.Dispose();
        _log?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* not owner */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
