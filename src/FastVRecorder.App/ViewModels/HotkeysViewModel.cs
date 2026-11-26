using System.Collections.ObjectModel;
using System.Windows.Input;
using FastVRecorder.Core.Hotkeys;
using FastVRecorder.Core.Settings;
using FastVRecorder.Infrastructure.Hotkeys;

namespace FastVRecorder.App.ViewModels;

public sealed class HotkeyRow : ObservableObject
{
    private readonly Action<HotkeyRow, HotkeyScope> _scopeChanged;
    private readonly Loc _loc;
    private HotkeyScope _scope;
    private bool _isCapturing;
    private string _statusText = string.Empty;

    public HotkeyRow(
        HotkeyAction action,
        string title,
        HotkeyGesture gesture,
        HotkeyScope scope,
        Loc loc,
        Action<HotkeyRow, HotkeyScope> scopeChanged)
    {
        Action = action;
        Title = title;
        Gesture = gesture;
        _scope = scope;
        _loc = loc;
        _scopeChanged = scopeChanged;
    }

    public HotkeyAction Action { get; }
    public string Title { get; }
    public HotkeyGesture Gesture { get; private set; }
    public bool HasGesture => !Gesture.IsEmpty;

    public string GestureText => Gesture.IsEmpty ? _loc["Common.Unassigned"] : Gesture.ToString();

    public string DisplayText => IsCapturing ? _loc["Keys.PressKeys"] : GestureText;

    public HotkeyScope Scope
    {
        get => _scope;
        set
        {
            if (_scope == value || _isCapturing) return;
            _scopeChanged(this, value);
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (Set(ref _isCapturing, value)) OnPropertyChanged(nameof(DisplayText));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    internal void UpdateGesture(HotkeyGesture gesture)
    {
        Gesture = gesture;
        OnPropertyChanged(nameof(Gesture));
        OnPropertyChanged(nameof(GestureText));
        OnPropertyChanged(nameof(HasGesture));
        OnPropertyChanged(nameof(DisplayText));
    }

    internal void UpdateScopeSilent(HotkeyScope scope)
    {
        _scope = scope;
        OnPropertyChanged(nameof(Scope));
    }
}

/// <summary>
/// Hotkey editor. Every assignment is transactional: Windows registration is attempted first and
/// the previous working shortcuts are restored if the new combination is rejected.
/// </summary>
public sealed class HotkeysViewModel : ObservableObject
{
    private readonly AppSettings _s;
    private readonly GlobalHotkeyService _service;
    private readonly Loc _loc;
    private readonly Action _saveNow;
    private bool _rebuilding;
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;

    public HotkeysViewModel(AppSettings settings, GlobalHotkeyService service, Loc loc, Action saveNow)
    {
        _s = settings;
        _service = service;
        _loc = loc;
        _saveNow = saveNow;
        Rows = new ObservableCollection<HotkeyRow>();
        ScopeChoices = new ObservableCollection<Choice<HotkeyScope>>();
        ReloadFromSettings();
        BuildScopeChoices();

        BeginCaptureCommand = new RelayCommand(p => { if (p is HotkeyRow row) BeginCapture(row); });
        ClearCommand = new RelayCommand(p => { if (p is HotkeyRow row) ClearRow(row); });
        ResetCommand = new RelayCommand(p => { if (p is HotkeyRow row) ResetRow(row); });
        RestoreDefaultsCommand = new RelayCommand(RestoreAllDefaults);
    }

    public RelayCommand BeginCaptureCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }

    public event EventHandler? Changed;

    public ObservableCollection<HotkeyRow> Rows { get; }
    public ObservableCollection<Choice<HotkeyScope>> ScopeChoices { get; }

    public HotkeyRow? CaptureRow { get; private set; }

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

    public void ReloadFromSettings()
    {
        ReloadFromSettings(out _);
    }

    public bool ReloadFromSettings(out string? error)
    {
        _rebuilding = true;
        try
        {
            Rows.Clear();
            foreach (var action in Enum.GetValues<HotkeyAction>())
            {
                var entry = _s.Hotkeys.FirstOrDefault(h => h is not null && h.Action == action);
                if (entry is null)
                {
                    entry = new HotkeySetting { Action = action, Gesture = string.Empty, Scope = HotkeyScope.Global };
                    _s.Hotkeys.Add(entry);
                }

                HotkeyGesture.TryParse(entry.Gesture, out var gesture);
                Rows.Add(new HotkeyRow(action, Title(action), gesture, entry.Scope, _loc, OnRowScopeChanged));
            }
        }
        finally
        {
            _rebuilding = false;
        }

        return ApplyBindings(out error);
    }

    public bool ApplyBindings(out string? error)
    {
        var bindings = BuildBindings(null, default, null);
        var ok = _service.Apply(bindings, out error);
        if (!ok)
        {
            ErrorText = _loc["Msg.HotkeyConflict"] + error;
        }

        return ok;
    }

    public void RefreshLocalized()
    {
        CancelCapture();
        BuildScopeChoices();
        ReloadFromSettings();
        RaiseAllChanged();
        foreach (var row in Rows)
        {
            OnPropertyChanged(nameof(Rows));
        }
    }

    public void BeginCapture(HotkeyRow row)
    {
        if (CaptureRow is not null) CancelCapture();
        CaptureRow = row;
        foreach (var other in Rows) other.IsCapturing = ReferenceEquals(other, row);
        row.StatusText = _loc["Keys.PressKeys"] + " " + _loc["Keys.EscToCancel"];
        _service.Suspend(true);
        ErrorText = string.Empty;
        StatusText = _loc["Keys.AppliesNow"];
    }

    public void CancelCapture()
    {
        if (CaptureRow is not null) CaptureRow.IsCapturing = false;
        CaptureRow = null;
        foreach (var row in Rows) row.IsCapturing = false;
        _service.Suspend(false);
    }

    /// <summary>Returns true when the keystroke belonged to the capture UI.</summary>
    public bool HandleCapturedKey(Key key, ModifierKeys modifiers)
    {
        var row = CaptureRow;
        if (row is null || _rebuilding) return false;
        if (key == Key.Escape)
        {
            CancelCapture();
            StatusText = string.Empty;
            return true;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || HotkeyGesture.IsModifierKey(virtualKey)) return true;

        var gesture = new HotkeyGesture(ToModifiers(modifiers), virtualKey);
        var validation = HotkeyValidator.Validate(gesture, row.Scope);
        if (validation is null && row.Scope == HotkeyScope.Local && gesture.Modifiers == HotkeyModifiers.None &&
            gesture.VirtualKey is not (>= 0x70 and <= 0x87))
        {
            validation = _loc["Keys.LocalNeedsModifier"];
        }

        if (validation is not null)
        {
            row.StatusText = _loc["Keys.Invalid"] + validation;
            return true;
        }

        var duplicate = HotkeyValidator.FindDuplicate(CurrentBindings(), row.Action, gesture);
        if (duplicate is not null)
        {
            row.StatusText = _loc["Keys.Conflict"] + gesture + " — " + _loc["Keys.Duplicate"] + Title(duplicate.Value);
            return true;
        }

        return TryApplyBinding(row.Action, gesture, row.Scope, row);
    }

    public void ClearRow(HotkeyRow row)
    {
        TryApplyBinding(row.Action, default, row.Scope, row);
    }

    public void ResetRow(HotkeyRow row)
    {
        var defaults = HotkeyDefaults.For(row.Action);
        TryApplyBinding(row.Action, defaults.Gesture, defaults.Scope, row);
    }

    public void RestoreAllDefaults()
    {
        var bindings = BuildBindings(null, default, null, useDefaults: true);
        if (!_service.Apply(bindings, out var error))
        {
            ErrorText = _loc["Msg.HotkeyConflict"] + error;
            return;
        }

        _s.Hotkeys = HotkeyDefaults.All.Select(b => new HotkeySetting
        {
            Action = b.Action,
            Gesture = b.Gesture.IsEmpty ? string.Empty : b.Gesture.Serialize(),
            Scope = b.Scope,
        }).ToList();
        _saveNow();
        ReloadFromSettings();
        StatusText = _loc["Keys.Saved"];
        ErrorText = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRowScopeChanged(HotkeyRow row, HotkeyScope scope)
    {
        if (_rebuilding) return;
        TryApplyBinding(row.Action, row.Gesture, scope, row);
    }

    private bool TryApplyBinding(HotkeyAction action, HotkeyGesture gesture, HotkeyScope scope, HotkeyRow row)
    {
        var duplicate = HotkeyValidator.FindDuplicate(CurrentBindings(), action, gesture);
        if (duplicate is not null)
        {
            ErrorText = _loc["Keys.Conflict"] + gesture + " — " + _loc["Keys.Duplicate"] + Title(duplicate.Value);
            ReloadRow(row);
            return false;
        }

        var validation = HotkeyValidator.Validate(gesture, scope);
        if (validation is null && scope == HotkeyScope.Local && !gesture.IsEmpty && gesture.Modifiers == HotkeyModifiers.None &&
            gesture.VirtualKey is not (>= 0x70 and <= 0x87))
        {
            validation = _loc["Keys.LocalNeedsModifier"];
        }

        if (validation is not null)
        {
            ErrorText = _loc["Keys.Invalid"] + validation;
            ReloadRow(row);
            return false;
        }

        var candidate = BuildBindings(action, gesture, scope);
        if (!_service.Apply(candidate, out var error))
        {
            ErrorText = _loc["Msg.HotkeyConflict"] + error;
            if (CaptureRow is not null) _service.Suspend(true);
            ReloadRow(row);
            return false;
        }

        var entry = _s.Hotkeys.FirstOrDefault(h => h.Action == action);
        if (entry is null)
        {
            entry = new HotkeySetting { Action = action };
            _s.Hotkeys.Add(entry);
        }

        entry.Gesture = gesture.IsEmpty ? string.Empty : gesture.Serialize();
        entry.Scope = scope;
        _saveNow();
        CancelCapture();
        ErrorText = string.Empty;
        StatusText = _loc["Keys.Saved"];
        _rebuilding = true;
        try
        {
            row.UpdateGesture(gesture);
            row.UpdateScopeSilent(scope);
            row.StatusText = _loc["Keys.Saved"];
        }
        finally
        {
            _rebuilding = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ReloadRow(HotkeyRow row)
    {
        var entry = _s.Hotkeys.FirstOrDefault(h => h.Action == row.Action);
        _rebuilding = true;
        try
        {
            HotkeyGesture.TryParse(entry?.Gesture, out var gesture);
            row.UpdateGesture(gesture);
            row.UpdateScopeSilent(entry?.Scope ?? HotkeyScope.Global);
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private List<HotkeyBinding> CurrentBindings()
    {
        var list = new List<HotkeyBinding>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var entry = _s.Hotkeys.FirstOrDefault(h => h.Action == action);
            HotkeyGesture.TryParse(entry?.Gesture, out var gesture);
            list.Add(new HotkeyBinding(action, gesture, entry?.Scope ?? HotkeyScope.Global));
        }

        return list;
    }

    private List<HotkeyBinding> BuildBindings(
        HotkeyAction? overrideAction,
        HotkeyGesture overrideGesture,
        HotkeyScope? overrideScope,
        bool useDefaults = false)
    {
        var list = new List<HotkeyBinding>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (useDefaults)
            {
                var defaults = HotkeyDefaults.For(action);
                list.Add(new HotkeyBinding(action, defaults.Gesture, defaults.Scope));
                continue;
            }

            var entry = _s.Hotkeys.FirstOrDefault(h => h.Action == action);
            HotkeyGesture.TryParse(entry?.Gesture, out var gesture);
            var scope = entry?.Scope ?? HotkeyScope.Global;
            if (overrideAction == action)
            {
                gesture = overrideGesture;
                scope = overrideScope ?? scope;
            }

            list.Add(new HotkeyBinding(action, gesture, scope));
        }

        return list;
    }

    private void BuildScopeChoices()
    {
        ScopeChoices.Clear();
        ScopeChoices.Add(new Choice<HotkeyScope>(HotkeyScope.Global, _loc["Keys.Global"]));
        ScopeChoices.Add(new Choice<HotkeyScope>(HotkeyScope.Local, _loc["Keys.Local"]));
    }

    private static HotkeyModifiers ToModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;
        return result;
    }

    private string Title(HotkeyAction action) => _loc["Keys." + action];
}
