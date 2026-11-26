using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FastVRecorder.App.Services;

/// <summary>Windows tray icon with a themed state icon and a localized context menu.</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly string _mediaDir;
    private readonly Loc _loc;
    private readonly Dictionary<string, Drawing.Icon> _icons = new(StringComparer.OrdinalIgnoreCase);
    private Forms.ContextMenuStrip? _menu;
    private string _state = "idle";
    private bool _disposed;

    public TrayService(string mediaDir, Loc loc)
    {
        _mediaDir = mediaDir;
        _loc = loc;
        _icon = new NotifyIcon
        {
            Icon = LoadIcon("idle"),
            Text = "FastVRecorder",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowHideRequested?.Invoke();
        RebuildMenu(recording: false, paused: false, windowVisible: true);
    }

    public event Action? ShowHideRequested;
    public event Action? ToggleRecordRequested;
    public event Action? OpenFolderRequested;
    public event Action? OpenLastRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public void UpdateState(string state, string tooltip)
    {
        _icon.Icon = LoadIcon(state);
        _state = state;
        var text = string.IsNullOrWhiteSpace(tooltip) ? "FastVRecorder" : tooltip.Trim();
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void RebuildMenu(bool recording, bool paused, bool windowVisible)
    {
        _menu?.Dispose();
        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
        };

        menu.Items.Add(Create(windowVisible ? _loc["Tray.Hide"] : _loc["Tray.Show"], (_, _) => ShowHideRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Create(recording || paused ? _loc["Tray.Stop"] : _loc["Tray.Start"], (_, _) => ToggleRecordRequested?.Invoke()));
        menu.Items.Add(Create(_loc["Tray.OpenFolder"], (_, _) => OpenFolderRequested?.Invoke()));
        menu.Items.Add(Create(_loc["Tray.OpenLast"], (_, _) => OpenLastRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Create(_loc["Tray.Settings"], (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(Create(_loc["Tray.Exit"], (_, _) => ExitRequested?.Invoke()));

        _menu = menu;
        _icon.ContextMenuStrip = menu;
    }

    private static Forms.ToolStripMenuItem Create(string text, EventHandler onClick)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += onClick;
        return item;
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    private Drawing.Icon LoadIcon(string state)
    {
        if (_icons.TryGetValue(state, out var cached)) return cached;
        foreach (var candidate in new[]
                 {
                     Path.Combine(_mediaDir, $"tray-{state}.ico"),
                     Path.Combine(_mediaDir, "favicon.ico"),
                 })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var icon = new Drawing.Icon(candidate);
                    _icons[state] = icon;
                    return icon;
                }
            }
            catch (Exception)
            {
                // Fall through to the next candidate.
            }
        }

        var fallback = Drawing.SystemIcons.Application;
        _icons[state] = fallback;
        return fallback;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _menu?.Dispose();
        _icon.Dispose();
        foreach (var icon in _icons.Values.Distinct())
        {
            try
            {
                if (icon != Drawing.SystemIcons.Application) icon.Dispose();
            }
            catch
            {
                // shutdown
            }
        }
    }
}
