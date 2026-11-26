using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FastVRecorder.App.ViewModels;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Hotkeys;
using FastVRecorder.Core.Settings;

namespace FastVRecorder.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly PortableRoot _paths;
    private readonly FileLog _log;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, AppSettings settings, PortableRoot paths, FileLog log)
    {
        InitializeComponent();
        _vm = viewModel;
        _settings = settings;
        _paths = paths;
        _log = log;
        DataContext = viewModel;

        LoadIcon();
        RestoreGeometry();

        Loaded += (_, _) => ClampToScreen();
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void AllowClose() => _allowClose = true;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _vm.RefreshTrayMenu();
    }

    public void HideToTray()
    {
        Hide();
        _vm.RefreshTrayMenu();
    }

    public void ToggleVisibility()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowFromTray();
        }
        else if (IsActive)
        {
            HideToTray();
        }
        else
        {
            Activate();
            _vm.RefreshTrayMenu();
        }
    }

    private void LoadIcon()
    {
        try
        {
            var png = Path.Combine(_paths.MediaDir, "favicon.png");
            if (File.Exists(png))
            {
                Icon = new BitmapImage(new Uri(png, UriKind.Absolute));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Window icon cannot be loaded", ex);
        }
    }

    private void RestoreGeometry()
    {
        var window = _settings.Window;
        Width = window.Width is >= 340 and <= 2400 ? window.Width : 380;
        Height = window.Height is >= 380 and <= 1600 ? window.Height : 460;

        if (_settings.Application.RememberWindowPosition &&
            !double.IsNaN(window.Left) && !double.IsNaN(window.Top) &&
            !double.IsInfinity(window.Left) && !double.IsInfinity(window.Top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = window.Left;
            Top = window.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (window.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void ClampToScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var window = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        var visible = Rect.Intersect(screen, window);
        if (visible.Width < 80 || visible.Height < 60)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Left = Math.Max(screen.Left, screen.Left + (screen.Width - Width) / 2);
            Top = Math.Max(screen.Top, screen.Top + (screen.Height - Height) / 2);
        }
    }

    private void SaveGeometry()
    {
        _settings.Window.Page = _vm.CurrentPage;
        if (!_settings.Application.RememberWindowPosition) return;
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (bounds.IsEmpty || double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top)) return;
        _settings.Window.Left = bounds.Left;
        _settings.Window.Top = bounds.Top;
        _settings.Window.Width = bounds.Width;
        _settings.Window.Height = bounds.Height;
        _settings.Window.IsMaximized = WindowState == WindowState.Maximized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.Application.MinimizeToTray)
        {
            Dispatcher.BeginInvoke(new Action(HideToTray), DispatcherPriority.Background);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveGeometry();
        _vm.SaveSettingsNow();

        if (_allowClose || App.IsExiting)
        {
            return;
        }

        if (_settings.Application.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        App.IsExiting = true;
        Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()), DispatcherPriority.Background);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.IsCapturingHotkey)
        {
            var captureKey = e.Key == Key.System ? e.SystemKey : e.Key;
            _vm.Hotkeys.HandleCapturedKey(captureKey, Keyboard.Modifiers);
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return;
        var modifiers = ToHotkeyModifiers(Keyboard.Modifiers);
        if (_vm.TryExecuteLocalHotkey(virtualKey, modifiers))
        {
            e.Handled = true;
        }
    }

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;
        return result;
    }
}
