using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FastVRecorder.Core.Diagnostics;
using FastVRecorder.Core.Hotkeys;

namespace FastVRecorder.Infrastructure.Hotkeys;

/// <summary>
/// Registers global shortcuts with RegisterHotKey + MOD_NOREPEAT on a message-only window.
/// Replacements are transactional: a failed registration restores the previous working set.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly ILog _log;
    private readonly HotkeyWindow _window;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private List<HotkeyBinding> _current = new();
    private bool _suspended;
    private bool _disposed;

    public GlobalHotkeyService(ILog log)
    {
        _log = log;
        _window = new HotkeyWindow(id => Dispatch(id));
        _window.CreateHandle(new CreateParams
        {
            Caption = "FastVRecorder.Hotkeys",
            Parent = new IntPtr(-3), // HWND_MESSAGE: message-only window
        });
    }

    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>
    /// When suspended, WM_HOTKEY messages are swallowed and global registrations are released so
    /// the hotkey editor can capture any combination (including one currently assigned).
    /// </summary>
    public void Suspend(bool suspended)
    {
        if (_disposed) return;
        _suspended = suspended;
        if (suspended)
        {
            UnregisterAll();
        }
        else if (_registered.Count == 0)
        {
            RegisterAll(_current);
        }
    }

    public IReadOnlyList<HotkeyBinding> Current => _current;

    /// <summary>Replaces every global registration. Returns false and restores the old set on failure.</summary>
    public bool Apply(IReadOnlyList<HotkeyBinding> bindings, out string? error)
    {
        var previous = _current.ToList();
        UnregisterAll();
        var accepted = new List<HotkeyBinding>();
        var failures = new List<string>();

        foreach (var binding in bindings)
        {
            if (binding.Scope != HotkeyScope.Global || binding.Gesture.IsEmpty)
            {
                continue;
            }

            var id = HotkeyId(binding.Action);
            if (_registered.ContainsKey(id))
            {
                failures.Add($"{HotkeyDefaults.Title(binding.Action)}: {binding.Gesture} duplicates another command.");
                continue;
            }

            var modifiers = (uint)binding.Gesture.Modifiers | ModNoRepeat;
            if (RegisterHotKey(_window.Handle, id, modifiers, (uint)binding.Gesture.VirtualKey))
            {
                _registered[id] = binding.Action;
                accepted.Add(binding);
            }
            else
            {
                var code = Marshal.GetLastWin32Error();
                var reason = new Win32Exception(code).Message;
                failures.Add($"{binding.Gesture} is already registered by another application or is reserved by Windows ({reason}). Choose another shortcut.");
            }
        }

        if (failures.Count > 0)
        {
            UnregisterAll();
            var restored = RegisterAll(previous);
            error = string.Join(" ", failures);
            if (!restored)
            {
                error += " The previous shortcuts could not be restored; reassign them.";
            }

            _log.Warn("Hotkey update rejected: " + error);
            return false;
        }

        _current = accepted;
        error = null;
        _log.Info("Global hotkeys applied: " + (accepted.Count == 0 ? "none" : string.Join(", ", accepted)));
        return true;
    }

    public void Clear()
    {
        UnregisterAll();
        _current = new List<HotkeyBinding>();
    }

    private bool RegisterAll(IReadOnlyList<HotkeyBinding> bindings)
    {
        var ok = true;
        foreach (var binding in bindings)
        {
            if (binding.Scope != HotkeyScope.Global || binding.Gesture.IsEmpty) continue;
            var id = HotkeyId(binding.Action);
            if (RegisterHotKey(_window.Handle, id, (uint)binding.Gesture.Modifiers | ModNoRepeat, (uint)binding.Gesture.VirtualKey))
            {
                _registered[id] = binding.Action;
            }
            else
            {
                ok = false;
            }
        }

        if (ok) _current = bindings.Where(b => b.Scope == HotkeyScope.Global && !b.Gesture.IsEmpty).ToList();
        return ok;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }

        _registered.Clear();
    }

    private void Dispatch(int id)
    {
        if (_suspended) return;
        if (!_registered.TryGetValue(id, out var action)) return;
        try
        {
            Pressed?.Invoke(this, action);
        }
        catch (Exception ex)
        {
            _log.Error("Hotkey handler failed", ex);
        }
    }

    private static int HotkeyId(HotkeyAction action) => 1000 + (int)action;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action<int> _callback;

        public HotkeyWindow(Action<int> callback) => _callback = callback;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                _callback((int)m.WParam);
                return;
            }

            base.WndProc(ref m);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        try { _window.DestroyHandle(); } catch { /* shutdown */ }
    }
}
