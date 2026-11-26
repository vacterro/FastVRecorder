using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FastVRecorder.Core.Audio;
using FastVRecorder.Core.Files;
using FastVRecorder.Core.Hotkeys;
using FastVRecorder.Core.Localization;

namespace FastVRecorder.Core.Settings;

public enum OutputFormat { Wav, Mp3, Flac }

public enum QualityPreset { Voice, Standard, HighQuality, Custom }

public enum UiPage { Recorder, History, Hotkeys, Settings }

public sealed class RecordingSettings
{
    /// <summary>Empty = follow the Windows default recording device at the moment recording starts.</summary>
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public OutputFormat Format { get; set; } = OutputFormat.Wav;
    public QualityPreset Preset { get; set; } = QualityPreset.Standard;
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 1;
    public SampleEncoding Encoding { get; set; } = SampleEncoding.Pcm16;
    public int Mp3BitrateKbps { get; set; } = 96;
    public bool StopPlaybackOnRecord { get; set; } = true;

    /// <summary>The on-disk capture format (intermediate WAV for compressed outputs).</summary>
    public TargetFormat EffectiveFormat()
    {
        var f = Preset switch
        {
            QualityPreset.Voice => new TargetFormat(24000, 1, SampleEncoding.Pcm16),
            QualityPreset.Standard => new TargetFormat(48000, 1, SampleEncoding.Pcm16),
            QualityPreset.HighQuality => new TargetFormat(48000, 1, SampleEncoding.Pcm24),
            _ => new TargetFormat(SampleRate, Channels, Encoding),
        };
        if (Format == OutputFormat.Mp3) f = f with { Encoding = SampleEncoding.Pcm16 };
        if (Format == OutputFormat.Flac && f.Encoding == SampleEncoding.Float32) f = f with { Encoding = SampleEncoding.Pcm24 };
        return f.IsValid(out _) ? f : TargetFormat.Standard;
    }

    public string DescribeOutput()
    {
        var f = EffectiveFormat();
        return Format switch
        {
            OutputFormat.Mp3 => $"MP3 {Mp3BitrateKbps} kbps · {f.SampleRate / 1000.0:0.#} kHz · {(f.Channels == 1 ? "Mono" : "Stereo")}",
            OutputFormat.Flac => $"FLAC · {f.Describe()}",
            _ => $"WAV · {f.Describe()}",
        };
    }

    public string OutputExtension => Format switch
    {
        OutputFormat.Mp3 => ".mp3",
        OutputFormat.Flac => ".flac",
        _ => ".wav",
    };
}

public sealed class FileSettings
{
    /// <summary>Relative (to the portable root) or absolute. Empty = &lt;root&gt;\Recordings.</summary>
    public string RecordingFolder { get; set; } = "";
    public NamingPreset NamingPreset { get; set; } = NamingPreset.DateTime;
    public string Prefix { get; set; } = "Recording";
    public bool DateFolders { get; set; }
    public bool OpenFolderAfterSave { get; set; }

    /// <summary>True keeps the persisted counter (never reuses numbers); false restarts from the stored value and resolves collisions with a suffix.</summary>
    public bool IncrementalNumbering { get; set; } = true;

    public NamingOptions ToNamingOptions() => new(NamingPreset, Prefix, DateFolders, IncrementalNumbering);
}

public sealed class NotificationSettings
{
    public bool SoundsEnabled { get; set; } = true;
    public int Volume { get; set; } = 35;
    public bool PlayStart { get; set; } = true;
    public bool PlayDone { get; set; } = true;
    public bool PlayError { get; set; } = true;
    public int StartVolume { get; set; } = 100;
    public int DoneVolume { get; set; } = 100;
    public int ErrorVolume { get; set; } = 100;
    public bool WindowsNotification { get; set; }

    /// <summary>Render endpoint ID for cue playback; empty = Windows default output.</summary>
    public string OutputDevice { get; set; } = string.Empty;
}

public sealed class ApplicationSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool RememberWindowPosition { get; set; } = true;
    public bool ShowTaskbarIcon { get; set; } = true;
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "goldendefault";

    /// <summary>auto, en or ru.</summary>
    public string Language { get; set; } = "auto";

    public bool CompactMode { get; set; }
}

public sealed class AdvancedSettings
{
    public double RecoveryFlushSeconds { get; set; } = 1.5;
    public int LowDiskSpaceMb { get; set; } = 500;
    public bool DiagnosticsLog { get; set; } = true;
}

public sealed class WindowSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 460;
    public bool IsMaximized { get; set; }
    public UiPage Page { get; set; } = UiPage.Recorder;
    public string ExpandedSections { get; set; } = "Recording";
}

public sealed class HotkeySetting
{
    public HotkeyAction Action { get; set; }
    public string Gesture { get; set; } = "";
    public HotkeyScope Scope { get; set; } = HotkeyScope.Global;
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public RecordingSettings Recording { get; set; } = new();
    public FileSettings Files { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public ApplicationSettings Application { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public List<HotkeySetting> Hotkeys { get; set; } = DefaultHotkeys();

    public static List<HotkeySetting> DefaultHotkeys() =>
        HotkeyDefaults.All.Select(b => new HotkeySetting { Action = b.Action, Gesture = b.Gesture.Serialize(), Scope = b.Scope }).ToList();

    /// <summary>Clamps and repairs individual values so one bad field never resets the rest.</summary>
    public void Normalize()
    {
        Recording ??= new();
        Files ??= new();
        Notifications ??= new();
        Application ??= new();
        Appearance ??= new();
        Advanced ??= new();
        Window ??= new();
        Hotkeys ??= DefaultHotkeys();

        Recording.DeviceId ??= "";
        Recording.DeviceName ??= "";
        if (!Enum.IsDefined(Recording.Format)) Recording.Format = OutputFormat.Wav;
        // FLAC needs an external codec; it is never advertised or recorded by this build.
        if (Recording.Format == OutputFormat.Flac) Recording.Format = OutputFormat.Wav;
        if (!Enum.IsDefined(Recording.Preset)) Recording.Preset = QualityPreset.Standard;
        if (!Enum.IsDefined(Recording.Encoding)) Recording.Encoding = SampleEncoding.Pcm16;
        Recording.SampleRate = Recording.SampleRate is 8000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 96000 ? Recording.SampleRate : 48000;
        Recording.Channels = Recording.Channels is 1 or 2 ? Recording.Channels : 1;
        Recording.Mp3BitrateKbps = Recording.Mp3BitrateKbps is 64 or 96 or 128 or 160 or 192 ? Recording.Mp3BitrateKbps : 96;

        Files.RecordingFolder = string.IsNullOrWhiteSpace(Files.RecordingFolder) ? "" : Files.RecordingFolder.Trim();
        Files.Prefix = FileNamer.SanitizePrefix(Files.Prefix);
        if (!Enum.IsDefined(Files.NamingPreset)) Files.NamingPreset = NamingPreset.DateTime;

        Notifications.Volume = Math.Clamp(Notifications.Volume, 0, 100);
        Notifications.StartVolume = Math.Clamp(Notifications.StartVolume, 0, 100);
        Notifications.DoneVolume = Math.Clamp(Notifications.DoneVolume, 0, 100);
        Notifications.ErrorVolume = Math.Clamp(Notifications.ErrorVolume, 0, 100);
        Notifications.OutputDevice = string.IsNullOrWhiteSpace(Notifications.OutputDevice) ? "" : Notifications.OutputDevice.Trim();

        Appearance.Theme = string.IsNullOrWhiteSpace(Appearance.Theme) ? "goldendefault" : Appearance.Theme.Trim().ToLowerInvariant();
        var language = (Appearance.Language ?? "auto").Trim();
        if (language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Appearance.Language = "auto";
        }
        else if (LanguageCatalog.TryNormalize(language, out var normalizedLanguage))
        {
            Appearance.Language = normalizedLanguage;
        }
        else
        {
            Appearance.Language = "auto";
        }

        if (!double.IsFinite(Advanced.RecoveryFlushSeconds)) Advanced.RecoveryFlushSeconds = 1.0;
        Advanced.RecoveryFlushSeconds = Math.Clamp(Advanced.RecoveryFlushSeconds, 0.5, 10.0);
        Advanced.LowDiskSpaceMb = Math.Clamp(Advanced.LowDiskSpaceMb, 50, 100_000);

        if (!double.IsFinite(Window.Width) || Window.Width < 300 || Window.Width > 4000) Window.Width = 380;
        if (!double.IsFinite(Window.Height) || Window.Height < 360 || Window.Height > 4000) Window.Height = 460;
        if (!Enum.IsDefined(Window.Page)) Window.Page = UiPage.Recorder;
        Window.ExpandedSections ??= "Recording";

        // Hotkeys: one entry per action, valid gestures, no duplicates (first wins).
        var result = new List<HotkeySetting>();
        var used = new HashSet<HotkeyGesture>();
        foreach (var def in HotkeyDefaults.All)
        {
            var hs = Hotkeys.FirstOrDefault(h => h is not null && h.Action == def.Action);
            var scope = hs is not null && Enum.IsDefined(hs.Scope) ? hs.Scope : def.Scope;
            HotkeyGesture g;
            if (hs is null) g = def.Gesture;
            else if (!HotkeyGesture.TryParse(hs.Gesture, out g)) g = def.Gesture;
            if (HotkeyValidator.Validate(g, scope) is not null) g = default;
            if (!g.IsEmpty && !used.Add(g)) g = default;
            result.Add(new HotkeySetting { Action = def.Action, Gesture = g.Serialize(), Scope = scope });
        }
        Hotkeys = result;
        SchemaVersion = CurrentSchemaVersion;
    }

    public IReadOnlyList<HotkeyBinding> HotkeyBindings() =>
        Hotkeys.Select(h => new HotkeyBinding(h.Action, HotkeyGesture.TryParse(h.Gesture, out var g) ? g : default, h.Scope)).ToList();

    public AppSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this)) ?? new AppSettings();
}

public enum SettingsLoadStatus
{
    Loaded,
    CreatedDefaults,
    RecoveredFromBackup,
    PartiallyRecovered,
    ResetAfterCorruption,
}

/// <summary>Loads/saves settings.json atomically with a .bak of the previous valid file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly object _lock = new();

    public SettingsStore(string path) => FilePath = path;

    public string FilePath { get; }
    public string? LastSaveError { get; private set; }
    public SettingsLoadStatus LoadStatus { get; private set; }
    public string? LoadDetail { get; private set; }

    public static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, Options);

    public static AppSettings? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(json, Options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// Tolerant section-by-section parse: a malformed section falls back to defaults while valid
    /// sections are preserved.
    /// </summary>
    internal static AppSettings? DeserializeLenient(string json, out bool partial)
    {
        partial = false;
        var partialFlag = false;
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return null; }
        if (root is not JsonObject obj) return null;
        var s = new AppSettings();
        T? Section<T>(string name) where T : class
        {
            JsonNode? node = null;
            foreach (var property in obj)
            {
                if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    node = property.Value;
                    break;
                }
            }

            if (node is null) return null;
            try { return node.Deserialize<T>(Options); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { partialFlag = true; return null; }
        }
        s.Recording = Section<RecordingSettings>(nameof(AppSettings.Recording)) ?? s.Recording;
        s.Files = Section<FileSettings>(nameof(AppSettings.Files)) ?? s.Files;
        s.Notifications = Section<NotificationSettings>(nameof(AppSettings.Notifications)) ?? s.Notifications;
        s.Application = Section<ApplicationSettings>(nameof(AppSettings.Application)) ?? s.Application;
        s.Appearance = Section<AppearanceSettings>(nameof(AppSettings.Appearance)) ?? s.Appearance;
        s.Advanced = Section<AdvancedSettings>(nameof(AppSettings.Advanced)) ?? s.Advanced;
        s.Window = Section<WindowSettings>(nameof(AppSettings.Window)) ?? s.Window;
        s.Hotkeys = Section<List<HotkeySetting>>(nameof(AppSettings.Hotkeys)) ?? s.Hotkeys;
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, nameof(AppSettings.SchemaVersion), StringComparison.OrdinalIgnoreCase) &&
                property.Value is JsonValue jv && jv.TryGetValue<int>(out var ver))
            {
                s.SchemaVersion = ver;
                break;
            }
        }

        partial = partialFlag;
        return s;
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            var primary = AtomicFile.TryReadAllText(FilePath);
            if (primary is null && !File.Exists(FilePath) && !File.Exists(FilePath + ".bak"))
            {
                LoadStatus = SettingsLoadStatus.CreatedDefaults;
                var d = new AppSettings();
                d.Normalize();
                return d;
            }
            if (primary is not null)
            {
                var s = Deserialize(primary);
                if (s is not null)
                {
                    Migrate(s);
                    s.Normalize();
                    LoadStatus = SettingsLoadStatus.Loaded;
                    return s;
                }
                var lenient = DeserializeLenient(primary, out var partial);
                if (lenient is not null)
                {
                    Migrate(lenient);
                    lenient.Normalize();
                    LoadStatus = SettingsLoadStatus.PartiallyRecovered;
                    LoadDetail = "Some settings sections were malformed and were reset to defaults.";
                    PreserveCorrupt();
                    return lenient;
                }
            }
            var backup = AtomicFile.TryReadAllText(FilePath + ".bak");
            if (backup is not null)
            {
                var b = Deserialize(backup) ?? DeserializeLenient(backup, out _);
                if (b is not null)
                {
                    Migrate(b);
                    b.Normalize();
                    LoadStatus = SettingsLoadStatus.RecoveredFromBackup;
                    LoadDetail = "settings.json was unreadable; restored the previous valid settings from settings.json.bak.";
                    PreserveCorrupt();
                    return b;
                }
            }
            PreserveCorrupt();
            LoadStatus = SettingsLoadStatus.ResetAfterCorruption;
            LoadDetail = "settings.json and its backup were unreadable; defaults are in use. The damaged file was kept as settings.json.corrupt.";
            var def = new AppSettings();
            def.Normalize();
            return def;
        }
    }

    private void PreserveCorrupt()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Schema migrations. v0 (missing) -> v1: nothing to transform, defaults fill gaps.</summary>
    private static void Migrate(AppSettings s)
    {
        if (s.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            // Newer file: keep known fields, do not downgrade data we do not understand.
        }
    }

    public bool Save(AppSettings settings)
    {
        lock (_lock)
        {
            try
            {
                AtomicFile.WriteAllText(FilePath, Serialize(settings));
                LastSaveError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastSaveError = $"Settings cannot be saved to {FilePath}: {ex.Message}";
                return false;
            }
        }
    }
}
