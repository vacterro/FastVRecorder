using System.Text;

namespace FastVRecorder.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,     // MOD_ALT
    Control = 0x2, // MOD_CONTROL
    Shift = 0x4,   // MOD_SHIFT
    Win = 0x8,     // MOD_WIN
}

public enum HotkeyAction
{
    ToggleRecording,
    StartRecording,
    StopRecording,
    PauseResume,
    ShowHideWindow,
    OpenRecordingFolder,
    OpenLastRecording,
    ToggleAlwaysOnTop,
}

public enum HotkeyScope
{
    Global,
    Local,
}

/// <summary>A key combination: Win32 MOD_* flags + virtual key code.</summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    public const int VkMain0 = 0x30;
    public const int VkNumpad0 = 0x60;

    public static readonly HotkeyGesture DefaultToggle = new(HotkeyModifiers.Alt, VkMain0);

    public bool IsEmpty => VirtualKey == 0;

    public static string KeyName(int vk)
    {
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "Num " + (vk - 0x60);
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0x91 => "ScrollLock",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0xB0 => "MediaNext",
            0xB1 => "MediaPrev",
            0xB2 => "MediaStop",
            0xB3 => "MediaPlayPause",
            _ => $"VK{vk:X2}",
        };
    }

    private static readonly Lazy<Dictionary<string, int>> NameToVk = new(() =>
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var vk = 1; vk < 256; vk++)
        {
            var n = KeyName(vk);
            if (!n.StartsWith("VK", StringComparison.Ordinal)) d.TryAdd(n, vk);
        }
        d.TryAdd("Escape", 0x1B);
        d.TryAdd("Return", 0x0D);
        d.TryAdd("Numpad0", 0x60);
        for (var i = 0; i <= 9; i++) d.TryAdd("NumPad" + i, 0x60 + i);
        return d;
    });

    public override string ToString() => Format(" + ");

    public string Format(string separator)
    {
        if (IsEmpty) return "Unassigned";
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl").Append(separator);
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt").Append(separator);
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift").Append(separator);
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) sb.Append("Win").Append(separator);
        sb.Append(KeyName(VirtualKey));
        return sb.ToString();
    }

    /// <summary>Storage form, e.g. "Ctrl+Alt+Num 0". Empty string = unassigned.</summary>
    public string Serialize() => IsEmpty ? string.Empty : Format("+");

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return true; // unassigned
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        // Allow "Num +" style names containing '+': rejoin a trailing empty part.
        var tokens = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 && tokens.Count > 0 && i == parts.Length - 1)
            {
                tokens[^1] = tokens[^1] + "+";
                continue;
            }
            if (parts[i].Length == 0) continue;
            tokens.Add(parts[i]);
        }
        if (tokens.Count == 0) return false;
        var mods = HotkeyModifiers.None;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= HotkeyModifiers.Control; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win":
                case "windows": mods |= HotkeyModifiers.Win; break;
                default: return false;
            }
        }
        var key = tokens[^1];
        int vk;
        if (NameToVk.Value.TryGetValue(key, out var found)) vk = found;
        else if (key.StartsWith("VK", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) && hex is > 0 and < 256) vk = hex;
        else return false;
        gesture = new HotkeyGesture(mods, vk);
        return true;
    }

    public static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
}

public sealed record HotkeyBinding(HotkeyAction Action, HotkeyGesture Gesture, HotkeyScope Scope);

public static class HotkeyDefaults
{
    public static IReadOnlyList<HotkeyBinding> All { get; } = new[]
    {
        new HotkeyBinding(HotkeyAction.ToggleRecording, HotkeyGesture.DefaultToggle, HotkeyScope.Global),
        new HotkeyBinding(HotkeyAction.StartRecording, default, HotkeyScope.Global),
        new HotkeyBinding(HotkeyAction.StopRecording, default, HotkeyScope.Global),
        new HotkeyBinding(HotkeyAction.PauseResume, default, HotkeyScope.Global),
        new HotkeyBinding(HotkeyAction.ShowHideWindow, default, HotkeyScope.Global),
        new HotkeyBinding(HotkeyAction.OpenRecordingFolder, default, HotkeyScope.Global),
        new HotkeyBinding(HotkeyAction.OpenLastRecording, new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4C), HotkeyScope.Global),
        new HotkeyBinding(HotkeyAction.ToggleAlwaysOnTop, default, HotkeyScope.Local),
    };

    public static HotkeyBinding For(HotkeyAction action) => All.First(b => b.Action == action);

    public static string Title(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleRecording => "Toggle recording",
        HotkeyAction.StartRecording => "Start recording",
        HotkeyAction.StopRecording => "Stop recording",
        HotkeyAction.PauseResume => "Pause / resume",
        HotkeyAction.ShowHideWindow => "Show / hide window",
        HotkeyAction.OpenRecordingFolder => "Open recording folder",
        HotkeyAction.OpenLastRecording => "Open last recording",
        HotkeyAction.ToggleAlwaysOnTop => "Toggle always on top",
        _ => action.ToString(),
    };
}

public static class HotkeyValidator
{
    private static readonly HashSet<HotkeyGesture> SystemReserved = new()
    {
        new(HotkeyModifiers.Alt, 0x09),          // Alt+Tab
        new(HotkeyModifiers.Alt, 0x1B),          // Alt+Esc
        new(HotkeyModifiers.Alt, 0x73),          // Alt+F4
        new(HotkeyModifiers.Alt, 0x20),          // Alt+Space (window menu)
        new(HotkeyModifiers.Control, 0x1B),      // Ctrl+Esc
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x1B), // Ctrl+Shift+Esc
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x2E),   // Ctrl+Alt+Del
        new(HotkeyModifiers.Win, 0x4C),          // Win+L
        new(HotkeyModifiers.Win, 0x44),          // Win+D
        new(HotkeyModifiers.Win, 0x45),          // Win+E
        new(HotkeyModifiers.Win, 0x52),          // Win+R
        new(HotkeyModifiers.Win, 0x09),          // Win+Tab
        new(HotkeyModifiers.Win, 0x49),          // Win+I
        new(HotkeyModifiers.Win, 0x58),          // Win+X
        new(HotkeyModifiers.Win, 0x41),          // Win+A
        new(HotkeyModifiers.Win, 0x53),          // Win+S
        new(HotkeyModifiers.Win, 0x56),          // Win+V
        new(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x53), // Win+Shift+S
    };

    /// <summary>Returns null when acceptable, otherwise a user-facing reason.</summary>
    public static string? Validate(HotkeyGesture g, HotkeyScope scope)
    {
        if (g.IsEmpty) return null;
        if (HotkeyGesture.IsModifierKey(g.VirtualKey)) return "A shortcut needs a non-modifier key.";
        if (SystemReserved.Contains(g)) return $"{g} is reserved by Windows. Choose another shortcut.";
        if (g.VirtualKey == 0x7B && g.Modifiers == HotkeyModifiers.None) return "F12 alone is reserved for debuggers. Add a modifier.";
        if (scope == HotkeyScope.Global && g.Modifiers == HotkeyModifiers.None)
        {
            var isFunction = g.VirtualKey is >= 0x70 and <= 0x87;
            var isMedia = g.VirtualKey is >= 0xB0 and <= 0xB3 || g.VirtualKey == 0x13;
            if (!isFunction && !isMedia)
                return $"Global shortcut {g} would block normal typing. Add Ctrl, Alt, Shift or Win.";
        }
        if (g.Modifiers == HotkeyModifiers.Shift && scope == HotkeyScope.Global && g.VirtualKey is (>= 0x30 and <= 0x5A))
            return $"Global shortcut {g} would block typing capital letters/symbols. Add Ctrl or Alt.";
        return null;
    }

    /// <summary>
    /// Ctrl+Alt is AltGr on several international layouts; letters/digits may stop typing
    /// characters. This is a warning, not a hard rejection.
    /// </summary>
    public static bool IsAltGrRisk(HotkeyGesture gesture)
    {
        if (gesture.IsEmpty) return false;
        var required = HotkeyModifiers.Control | HotkeyModifiers.Alt;
        if ((gesture.Modifiers & required) != required) return false;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) return false;
        var vk = gesture.VirtualKey;
        return vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A;
    }

    /// <summary>Finds another action already using the gesture (any scope).</summary>
    public static HotkeyAction? FindDuplicate(IEnumerable<HotkeyBinding> bindings, HotkeyAction self, HotkeyGesture g)
    {
        if (g.IsEmpty) return null;
        foreach (var b in bindings)
            if (b.Action != self && b.Gesture == g) return b.Action;
        return null;
    }
}
