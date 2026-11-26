using System.Security.Cryptography;
using System.Text;
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
    public const int CurrentSchemaVersion = 2;

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

    /// <summary>
    /// Replaces this document's sections in place from another instance. Used when a settings
    /// reload must be adopted by the live object the UI already binds to.
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        SchemaVersion = other.SchemaVersion;
        Recording = other.Recording;
        Files = other.Files;
        Notifications = other.Notifications;
        Application = other.Application;
        Appearance = other.Appearance;
        Advanced = other.Advanced;
        Window = other.Window;
        Hotkeys = other.Hotkeys;
    }

    public AppSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this)) ?? new AppSettings();
}

public enum SettingsLoadStatus
{
    Loaded,
    CreatedDefaults,
    RecoveredFromBackup,
    PartiallyRecovered,
    ResetAfterCorruption,

    /// <summary>The file exists but could not be read; saving is refused to avoid overwriting it.</summary>
    Unreadable,
}

/// <summary>Explicit read state for one settings document.</summary>
public enum SettingsFileState
{
    Missing,
    Readable,
    AccessDenied,
    SharingViolation,
    IoError,
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

    /// <summary>False when Load could not read an existing document; Save must not overwrite it.</summary>
    public bool CanSave { get; private set; } = true;

    /// <summary>Mechanical read state of the primary document at the last Load.</summary>
    public SettingsFileState PrimaryState { get; private set; } = SettingsFileState.Missing;

    /// <summary>Mechanical read state of the backup document at the last Load.</summary>
    public SettingsFileState BackupState { get; private set; } = SettingsFileState.Missing;

    private bool _suppressBackupOnce;
    private bool _primaryWasMissingAtLoad;

    /// <summary>Hash of the primary document this store last read or wrote. Used to notice a newer
    /// settings document written by another process before a stale in-memory copy overwrites it.</summary>
    private string? _lastDiskTextHash;

    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

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
            var backupPath = FilePath + ".bak";
            var primary = TryReadState(FilePath);
            var backup = TryReadState(backupPath);
            PrimaryState = primary.State;
            BackupState = backup.State;
            _primaryWasMissingAtLoad = primary.State == SettingsFileState.Missing;
            _suppressBackupOnce = false;
            _lastDiskTextHash = primary.State == SettingsFileState.Readable && primary.Text is not null
                ? HashText(primary.Text)
                : null;

            if (primary.State == SettingsFileState.Missing && backup.State == SettingsFileState.Missing)
            {
                LoadStatus = SettingsLoadStatus.CreatedDefaults;
                LoadDetail = null;
                CanSave = true;
                return Defaults();
            }

            if (primary.State == SettingsFileState.Readable && primary.Text is not null)
            {
                var parsed = Deserialize(primary.Text);
                if (parsed is not null)
                {
                    Migrate(parsed);
                    parsed.Normalize();
                    LoadStatus = SettingsLoadStatus.Loaded;
                    LoadDetail = null;
                    CanSave = true;
                    return parsed;
                }

                var lenient = DeserializeLenient(primary.Text, out _);
                if (lenient is not null)
                {
                    Migrate(lenient);
                    lenient.Normalize();
                    LoadStatus = SettingsLoadStatus.PartiallyRecovered;
                    LoadDetail = "Some settings sections were malformed and were reset to defaults.";
                    CanSave = true;
                    _suppressBackupOnce = true;
                    PreserveCorrupt();
                    return lenient;
                }

                // Readable but malformed. Replacing it is authorized only when the backup's own
                // state is known: a temporarily unreadable backup may hold the last valid copy.
                if (backup.State is SettingsFileState.AccessDenied or SettingsFileState.SharingViolation or SettingsFileState.IoError)
                {
                    LoadStatus = SettingsLoadStatus.Unreadable;
                    LoadDetail = "settings.json is damaged AND its backup cannot be read; nothing will be overwritten until the backup is accessible.";
                    CanSave = false;
                    _suppressBackupOnce = true;
                    return Defaults();
                }

                if (backup.State == SettingsFileState.Readable && backup.Text is not null)
                {
                    var fromBackup = Deserialize(backup.Text) ?? DeserializeLenient(backup.Text, out _);
                    if (fromBackup is not null)
                    {
                        Migrate(fromBackup);
                        fromBackup.Normalize();
                        LoadStatus = SettingsLoadStatus.RecoveredFromBackup;
                        LoadDetail = "settings.json was damaged; restored the previous valid settings from settings.json.bak.";
                        CanSave = true;
                        _suppressBackupOnce = true;
                        PreserveCorrupt();
                        return fromBackup;
                    }
                }

                PreserveCorrupt();
                LoadStatus = SettingsLoadStatus.ResetAfterCorruption;
                LoadDetail = "settings.json and its backup were unreadable; defaults are in use. The damaged file was kept as settings.json.corrupt.";
                CanSave = true;
                _suppressBackupOnce = true;
                return Defaults();
            }

            // The primary is present (or inaccessible) but was not read. This is NEVER
            // corruption: another process may hold it briefly, or access may be restored later.
            // Use a readable backup in memory but refuse writes until the primary's state is known.
            if (primary.State != SettingsFileState.Missing)
            {
                LoadStatus = SettingsLoadStatus.Unreadable;
                LoadDetail = DescribeUnreadablePrimary(primary.State);
                CanSave = false;
                if (backup.State == SettingsFileState.Readable && backup.Text is not null)
                {
                    var fromBackup = Deserialize(backup.Text) ?? DeserializeLenient(backup.Text, out _);
                    if (fromBackup is not null)
                    {
                        Migrate(fromBackup);
                        fromBackup.Normalize();
                        return fromBackup;
                    }
                }

                return Defaults();
            }

            // The primary is missing. A readable backup is safe to use in memory, and writing a
            // fresh primary destroys nothing.
            if (backup.State == SettingsFileState.Readable && backup.Text is not null)
            {
                var fromBackup = Deserialize(backup.Text) ?? DeserializeLenient(backup.Text, out _);
                if (fromBackup is not null)
                {
                    Migrate(fromBackup);
                    fromBackup.Normalize();
                    LoadStatus = SettingsLoadStatus.RecoveredFromBackup;
                    LoadDetail = "settings.json was missing; restored the previous valid settings from settings.json.bak.";
                    CanSave = true;
                    return fromBackup;
                }
            }

            LoadStatus = SettingsLoadStatus.CreatedDefaults;
            LoadDetail = null;
            CanSave = true;
            return Defaults();
        }
    }

    /// <summary>
    /// Retry a load that failed only because the primary was unreadable. Returns the freshly
    /// read settings when access was restored inside the same window.
    /// </summary>
    public bool TryReloadAfterAccessRestored(out AppSettings? settings)
    {
        lock (_lock)
        {
            settings = null;
            if (LoadStatus != SettingsLoadStatus.Unreadable || CanSave) return false;
            var candidate = Load();
            if (!CanSave) return false;
            settings = candidate;
            return true;
        }
    }

    private static AppSettings Defaults()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    private static string DescribeUnreadablePrimary(SettingsFileState state) => state switch
    {
        SettingsFileState.AccessDenied =>
            "settings.json exists but access was denied. Defaults are in use and changes will not be saved until access is restored and FastVRecorder is restarted.",
        SettingsFileState.SharingViolation =>
            "settings.json is temporarily locked by another program. Defaults are in use and changes will not be saved; restart FastVRecorder once the lock clears.",
        _ =>
            "settings.json exists but could not be read. Defaults are in use and changes will not be saved until FastVRecorder is restarted.",
    };

    private sealed record FileReadResult(SettingsFileState State, string? Text);

    private static FileReadResult TryReadState(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return new FileReadResult(SettingsFileState.Readable, reader.ReadToEnd());
            }
            catch (FileNotFoundException)
            {
                return new FileReadResult(SettingsFileState.Missing, null);
            }
            catch (DirectoryNotFoundException)
            {
                // A missing parent directory means the document does not exist yet, not that
                // an existing document was denied (that arrives as UnauthorizedAccessException).
                return new FileReadResult(SettingsFileState.Missing, null);
            }
            catch (UnauthorizedAccessException)
            {
                return new FileReadResult(SettingsFileState.AccessDenied, null);
            }
            catch (IOException ex)
            {
                var code = ex.HResult & 0xFFFF;
                var sharing = code is 32 or 33;
                if (!sharing || attempt == 2)
                {
                    return new FileReadResult(sharing ? SettingsFileState.SharingViolation : SettingsFileState.IoError, null);
                }

                Thread.Sleep(50);
            }
        }

        return new FileReadResult(SettingsFileState.IoError, null);
    }

    private void PreserveCorrupt()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Schema migrations. v0/v1 -> v2 assigns the new default "open and play last recording"
    /// shortcut when the legacy document left it unassigned.
    /// </summary>
    private static void Migrate(AppSettings s)
    {
        if (s.SchemaVersion < 2 && s.Hotkeys is not null)
        {
            var entry = s.Hotkeys.FirstOrDefault(h => h is not null && h.Action == HotkeyAction.OpenLastRecording);
            if (entry is not null && string.IsNullOrWhiteSpace(entry.Gesture))
            {
                entry.Gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4C).Serialize();
            }
        }
    }

    public bool Save(AppSettings settings)
    {
        lock (_lock)
        {
            if (!CanSave)
            {
                LastSaveError = string.IsNullOrWhiteSpace(LoadDetail)
                    ? $"Settings were not read from {FilePath}; refusing to overwrite the existing file. Restart FastVRecorder after resolving the access problem."
                    : LoadDetail;
                return false;
            }

            // A primary that appeared after a defaults load belongs to another writer (another
            // instance or a restored file). Never overwrite it sight-unseen.
            if (_primaryWasMissingAtLoad && TryReadState(FilePath).State != SettingsFileState.Missing)
            {
                LoadStatus = SettingsLoadStatus.Unreadable;
                LoadDetail = "settings.json appeared after startup. Restart FastVRecorder so the newer on-disk settings are loaded instead of overwritten.";
                CanSave = false;
                LastSaveError = LoadDetail;
                return false;
            }

            // Another process may have written a newer valid document after this store read the
            // file. A stale in-memory copy must not silently replace it; the user has to restart
            // so the newest document (and its backup) are adopted instead.
            var keepBackup = !_suppressBackupOnce;
            if (_lastDiskTextHash is not null)
            {
                var current = TryReadState(FilePath);
                if (current.State == SettingsFileState.Readable && current.Text is not null)
                {
                    if (!string.Equals(HashText(current.Text), _lastDiskTextHash, StringComparison.Ordinal))
                    {
                        if (Deserialize(current.Text) is not null || DeserializeLenient(current.Text, out _) is not null)
                        {
                            LastSaveError =
                                "settings.json was changed by another process after FastVRecorder loaded it. The newer on-disk settings were kept; restart FastVRecorder to apply them.";
                            return false;
                        }

                        // An externally damaged primary is not allowed to rotate into the last
                        // valid backup: overwrite it directly and keep the existing .bak.
                        keepBackup = false;
                    }
                }
                else if (current.State != SettingsFileState.Missing && current.State != SettingsFileState.Readable)
                {
                    LastSaveError =
                        "settings.json cannot be read right now; refusing to overwrite it. Resolve the access problem and restart FastVRecorder.";
                    return false;
                }
            }

            var json = Serialize(settings);
            try
            {
                // After a damaged/partial load the next write must not rotate the damaged
                // primary into the backup slot: that would destroy the last valid document.
                AtomicFile.WriteAllText(FilePath, json, keepBackup: keepBackup);
                _suppressBackupOnce = false;
                _primaryWasMissingAtLoad = false;
                _lastDiskTextHash = HashText(json);
                PrimaryState = SettingsFileState.Readable;
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
