using FastVRecorder.Core.Files;
using FastVRecorder.Core.Hotkeys;
using FastVRecorder.Core.Settings;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class SettingsStoreTests : TempDirectoryTest
{
    private string SettingsPath => Path.Combine(Root, "config", "settings.json");

    [Fact]
    public void LegacySettings_GainTheOpenLastRecordingHotkey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            {
              "schemaVersion": 1,
              "hotkeys": [
                { "action": "OpenLastRecording", "gesture": "", "scope": "Global" }
              ]
            }
            """);

        var loaded = new SettingsStore(SettingsPath).Load();
        var entry = loaded.Hotkeys.Single(h => h.Action == HotkeyAction.OpenLastRecording);

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("Ctrl+Alt+L", entry.Gesture);
    }

    [Fact]
    public void UnreadablePrimaryWithValidBackup_StaysReadOnlyAndUsesTheBackupInMemory()
    {
        var store = new SettingsStore(SettingsPath);
        var first = new AppSettings();
        first.Files.Prefix = "BackupValue";
        Assert.True(store.Save(first));
        var second = new AppSettings();
        second.Files.Prefix = "PrimaryValue";
        Assert.True(store.Save(second));
        Assert.True(File.Exists(SettingsPath + ".bak"));

        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedStore = new SettingsStore(SettingsPath);
            var loaded = lockedStore.Load();

            Assert.Equal(SettingsLoadStatus.Unreadable, lockedStore.LoadStatus);
            Assert.Equal(SettingsFileState.SharingViolation, lockedStore.PrimaryState);
            Assert.False(lockedStore.CanSave);
            Assert.Equal("BackupValue", loaded.Files.Prefix);
            Assert.False(lockedStore.Save(loaded));
        }
    }

    [Fact]
    public void AccessDeniedPrimary_IsExplicitlyClassifiedAndNeverOverwritten()
    {
        Directory.CreateDirectory(SettingsPath);
        var store = new SettingsStore(SettingsPath);
        var loaded = store.Load();

        Assert.Equal(SettingsLoadStatus.Unreadable, store.LoadStatus);
        Assert.Equal(SettingsFileState.AccessDenied, store.PrimaryState);
        Assert.False(store.CanSave);
        Assert.False(store.Save(loaded));
        Assert.True(Directory.Exists(SettingsPath));
    }

    [Fact]
    public void LockClearingAfterLoad_ReloadsThePrimaryInsteadOfOverwritingIt()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings();
        settings.Files.Prefix = "NewerPrimary";
        Assert.True(store.Save(settings));

        SettingsStore lockedStore;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            lockedStore = new SettingsStore(SettingsPath);
            lockedStore.Load();
            Assert.False(lockedStore.CanSave);
        }

        Assert.True(lockedStore.TryReloadAfterAccessRestored(out var reloaded));
        Assert.NotNull(reloaded);
        Assert.Equal(SettingsLoadStatus.Loaded, lockedStore.LoadStatus);
        Assert.Equal("NewerPrimary", reloaded!.Files.Prefix);
        Assert.True(lockedStore.CanSave);
    }

    [Fact]
    public void PrimaryAppearingAfterDefaults_IsNotOverwritten()
    {
        var store = new SettingsStore(SettingsPath);
        store.Load();
        Assert.Equal(SettingsLoadStatus.CreatedDefaults, store.LoadStatus);

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var appeared = """{"schemaVersion":2,"files":{"prefix":"AppearedLater"}}""";
        File.WriteAllText(SettingsPath, appeared);

        var replacement = new AppSettings();
        replacement.Files.Prefix = "Overwrite";
        Assert.False(store.Save(replacement));
        Assert.Equal(appeared, File.ReadAllText(SettingsPath));
        Assert.Equal(SettingsLoadStatus.Unreadable, store.LoadStatus);
    }

    [Fact]
    public void SaveAfterBackupRecovery_DoesNotRotateTheDamagedPrimaryIntoTheBackup()
    {
        var store = new SettingsStore(SettingsPath);
        var first = new AppSettings();
        first.Files.Prefix = "FirstValid";
        Assert.True(store.Save(first));
        var second = new AppSettings();
        second.Files.Prefix = "SecondValid";
        Assert.True(store.Save(second));

        File.WriteAllText(SettingsPath, "{ this is not json ");
        var reopened = new SettingsStore(SettingsPath);
        var loaded = reopened.Load();
        Assert.Equal(SettingsLoadStatus.RecoveredFromBackup, reopened.LoadStatus);
        loaded.Files.Prefix = "EditedFromBackup";
        Assert.True(reopened.Save(loaded));

        var backupText = File.ReadAllText(SettingsPath + ".bak");
        var backup = SettingsStore.Deserialize(backupText);
        Assert.NotNull(backup);
        Assert.Equal("FirstValid", backup!.Files.Prefix);
        Assert.DoesNotContain("this is not json", backupText);
        Assert.Equal("EditedFromBackup", SettingsStore.Deserialize(File.ReadAllText(SettingsPath))!.Files.Prefix);
    }

    [Fact]
    public void MalformedPrimaryWithUnreadableBackup_RefusesToGuessAndKeepsBothFiles()
    {
        var store = new SettingsStore(SettingsPath);
        var first = new AppSettings();
        Assert.True(store.Save(first));
        var second = new AppSettings();
        Assert.True(store.Save(second));
        File.WriteAllText(SettingsPath, "{ broken ");

        using (new FileStream(SettingsPath + ".bak", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var reopened = new SettingsStore(SettingsPath);
            reopened.Load();

            Assert.Equal(SettingsLoadStatus.Unreadable, reopened.LoadStatus);
            Assert.False(reopened.CanSave);
            Assert.Equal("{ broken ", File.ReadAllText(SettingsPath));
        }
    }

    [Fact]
    public void CopyFrom_AdoptsEveryReloadedSectionIntoTheLiveInstance()
    {
        var live = new AppSettings();
        live.Files.Prefix = "Stale";
        var reloaded = new AppSettings { SchemaVersion = 7 };
        reloaded.Files.Prefix = "Newest";
        reloaded.Recording.SampleRate = 16000;
        reloaded.Appearance.Theme = "vintagedark";
        reloaded.Hotkeys = new List<HotkeySetting>();

        live.CopyFrom(reloaded);

        Assert.Same(reloaded.Files, live.Files);
        Assert.Same(reloaded.Recording, live.Recording);
        Assert.Same(reloaded.Notifications, live.Notifications);
        Assert.Same(reloaded.Application, live.Application);
        Assert.Same(reloaded.Appearance, live.Appearance);
        Assert.Same(reloaded.Advanced, live.Advanced);
        Assert.Same(reloaded.Window, live.Window);
        Assert.Same(reloaded.Hotkeys, live.Hotkeys);
        Assert.Equal(7, live.SchemaVersion);
        Assert.Equal("Newest", live.Files.Prefix);
    }

    [Fact]
    public void ReloadAdoptThenSave_PersistsTheNewestDocumentAcrossLaterSaves()
    {
        var store = new SettingsStore(SettingsPath);
        var newest = new AppSettings();
        newest.Files.Prefix = "NewestOnDisk";
        Assert.True(store.Save(newest));

        SettingsStore locked;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            locked = new SettingsStore(SettingsPath);
            locked.Load();
            Assert.False(locked.CanSave);
        }

        Assert.True(locked.TryReloadAfterAccessRestored(out var reloaded));
        Assert.NotNull(reloaded);
        // The fixed caller adopts the reloaded document into the live object before saving.
        var live = new AppSettings();
        live.Files.Prefix = "StaleInMemory";
        live.CopyFrom(reloaded!);
        Assert.True(locked.Save(live));
        // A later save (auto-save or shutdown) of the same live object must not undo the reload.
        Assert.True(locked.Save(live));

        var persisted = SettingsStore.Deserialize(File.ReadAllText(SettingsPath));
        Assert.NotNull(persisted);
        Assert.Equal("NewestOnDisk", persisted!.Files.Prefix);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsValues()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings();
        settings.Recording.SampleRate = 24000;
        settings.Files.Prefix = "Voice";
        settings.Notifications.Volume = 42;
        settings.Appearance.Theme = "vintagedark";

        Assert.True(store.Save(settings));
        var loaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal(24000, loaded.Recording.SampleRate);
        Assert.Equal("Voice", loaded.Files.Prefix);
        Assert.Equal(42, loaded.Notifications.Volume);
        Assert.Equal("vintagedark", loaded.Appearance.Theme);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAssignedHotkeysAndFolder()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings();
        var showHide = settings.Hotkeys.Single(h => h.Action == HotkeyAction.ShowHideWindow);
        showHide.Gesture = "Shift+Alt+0";
        showHide.Scope = HotkeyScope.Global;
        var openFolder = settings.Hotkeys.Single(h => h.Action == HotkeyAction.OpenRecordingFolder);
        openFolder.Gesture = "Ctrl+Alt+O";
        openFolder.Scope = HotkeyScope.Local;
        settings.Files.RecordingFolder = Path.Combine("Recordings", "Voice Notes");

        Assert.True(store.Save(settings));
        var reopened = new SettingsStore(SettingsPath);
        var loaded = reopened.Load();

        Assert.Equal(SettingsLoadStatus.Loaded, reopened.LoadStatus);
        Assert.True(HotkeyGesture.TryParse("Shift+Alt+0", out var expectedShowHide));
        Assert.True(HotkeyGesture.TryParse(
            loaded.Hotkeys.Single(h => h.Action == HotkeyAction.ShowHideWindow).Gesture,
            out var loadedShowHide));
        Assert.Equal(expectedShowHide, loadedShowHide);
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+O", out var expectedOpenFolder));
        Assert.True(HotkeyGesture.TryParse(
            loaded.Hotkeys.Single(h => h.Action == HotkeyAction.OpenRecordingFolder).Gesture,
            out var loadedOpenFolder));
        Assert.Equal(expectedOpenFolder, loadedOpenFolder);
        Assert.Equal(HotkeyScope.Local, loaded.Hotkeys.Single(h => h.Action == HotkeyAction.OpenRecordingFolder).Scope);
        Assert.Equal(Path.Combine("Recordings", "Voice Notes"), loaded.Files.RecordingFolder);
    }

    [Fact]
    public void LockedExistingSettings_AreNotResetOrOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var original = """{"schemaVersion":2,"files":{"prefix":"Kept"}}""";
        File.WriteAllText(SettingsPath, original);

        using var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = new SettingsStore(SettingsPath);
        var loaded = store.Load();

        Assert.Equal(SettingsLoadStatus.Unreadable, store.LoadStatus);
        Assert.False(store.CanSave);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        var replacement = new AppSettings();
        replacement.Files.Prefix = "Overwrite";
        Assert.False(store.Save(replacement));

        locked.Dispose();
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = store.Load();

        Assert.Equal(SettingsLoadStatus.CreatedDefaults, store.LoadStatus);
        Assert.NotNull(settings);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Contains(settings.Hotkeys, h => h.Action == HotkeyAction.ToggleRecording);
    }

    [Fact]
    public void CorruptPrimary_RecoversPreviousBackup()
    {
        var store = new SettingsStore(SettingsPath);
        var first = new AppSettings();
        first.Files.Prefix = "First";
        Assert.True(store.Save(first));

        var second = new AppSettings();
        second.Files.Prefix = "Second";
        Assert.True(store.Save(second));
        Assert.True(File.Exists(SettingsPath + ".bak"));

        File.WriteAllText(SettingsPath, "{ this is not json ");
        var reopened = new SettingsStore(SettingsPath);
        var loaded = reopened.Load();

        Assert.Equal(SettingsLoadStatus.RecoveredFromBackup, reopened.LoadStatus);
        Assert.Equal("First", loaded.Files.Prefix);
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void MissingSection_KeepsOtherValidValues()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            {
              "schemaVersion": 3,
              "recording": null,
              "files": { "prefix": "Kept", "namingPreset": "Counter" }
            }
            """);

        var store = new SettingsStore(SettingsPath);
        var loaded = store.Load();

        Assert.Equal("Kept", loaded.Files.Prefix);
        Assert.Equal(NamingPreset.Counter, loaded.Files.NamingPreset);
        Assert.NotNull(loaded.Recording);
        Assert.True(loaded.Recording.EffectiveFormat().IsValid(out _));
    }

    [Fact]
    public void BothFilesCorrupt_ResetsToDefaultsAndKeepsCorruptCopy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "garbage");
        File.WriteAllText(SettingsPath + ".bak", "garbage too");

        var store = new SettingsStore(SettingsPath);
        var loaded = store.Load();

        Assert.Equal(SettingsLoadStatus.ResetAfterCorruption, store.LoadStatus);
        Assert.Equal("Recording", loaded.Files.Prefix);
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Normalize_ClampsValuesAndRepairsHotkeys()
    {
        var settings = new AppSettings
        {
            Recording = new RecordingSettings { Format = OutputFormat.Flac, SampleRate = 1, Channels = 9 },
            Files = new FileSettings { RecordingFolder = "  ", NamingPreset = NamingPreset.Counter, Prefix = "  " },
            Notifications = new NotificationSettings { Volume = 500 },
            Appearance = new AppearanceSettings { Theme = "", Language = "klingon" },
            Advanced = new AdvancedSettings { RecoveryFlushSeconds = 99, LowDiskSpaceMb = 0 },
            Hotkeys = new List<HotkeySetting>
            {
                new() { Action = HotkeyAction.ToggleRecording, Gesture = "Alt+0", Scope = HotkeyScope.Global },
                new() { Action = HotkeyAction.StartRecording, Gesture = "Alt+0", Scope = HotkeyScope.Global },
            },
        };

        settings.Normalize();

        Assert.Equal(OutputFormat.Wav, settings.Recording.Format);
        Assert.Equal(48000, settings.Recording.SampleRate);
        Assert.Equal(1, settings.Recording.Channels);
        Assert.Equal("", settings.Files.RecordingFolder);
        Assert.Equal(100, settings.Notifications.Volume);
        Assert.Equal("goldendefault", settings.Appearance.Theme);
        Assert.Equal("auto", settings.Appearance.Language);
        Assert.Equal(10, settings.Advanced.RecoveryFlushSeconds);
        Assert.Equal(50, settings.Advanced.LowDiskSpaceMb);
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, settings.Hotkeys.Count);
        Assert.Equal(2, settings.Hotkeys.Count(h => !string.IsNullOrEmpty(h.Gesture)));
        Assert.Contains(settings.Hotkeys, h => h.Gesture == "Alt+0");
        Assert.Contains(settings.Hotkeys, h => h.Gesture == "Ctrl+Alt+L");
    }

    [Fact]
    public void Save_LeavesBackupOfPreviousValidDocument()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings();
        Assert.True(store.Save(settings));
        settings.Files.Prefix = "Changed";
        Assert.True(store.Save(settings));

        Assert.True(File.Exists(SettingsPath + ".bak"));
        var backup = SettingsStore.Deserialize(File.ReadAllText(SettingsPath + ".bak"));
        Assert.NotNull(backup);
        Assert.Equal("Recording", backup!.Files.Prefix);
    }

    [Fact]
    public void ExternalValidChangeAfterLoad_IsNotOverwrittenByAStaleSave()
    {
        var writer = new SettingsStore(SettingsPath);
        var original = new AppSettings();
        original.Files.Prefix = "Original";
        Assert.True(writer.Save(original));

        var liveStore = new SettingsStore(SettingsPath);
        var live = liveStore.Load();
        Assert.Equal("Original", live.Files.Prefix);

        var external = new AppSettings();
        external.Files.Prefix = "ExternalNewer";
        File.WriteAllText(SettingsPath, SettingsStore.Serialize(external));

        live.Files.Prefix = "StaleInMemory";
        Assert.False(liveStore.Save(live));
        Assert.Contains("changed by another process", liveStore.LastSaveError, StringComparison.OrdinalIgnoreCase);

        var persisted = SettingsStore.Deserialize(File.ReadAllText(SettingsPath));
        Assert.NotNull(persisted);
        Assert.Equal("ExternalNewer", persisted!.Files.Prefix);
    }

    [Fact]
    public void ConsecutiveSavesFromTheSameStoreAreAllowed()
    {
        var store = new SettingsStore(SettingsPath);
        store.Load();
        var settings = new AppSettings();
        settings.Files.Prefix = "One";
        Assert.True(store.Save(settings));
        Assert.True(store.Save(settings));

        settings.Files.Prefix = "Two";
        Assert.True(store.Save(settings));
        var persisted = SettingsStore.Deserialize(File.ReadAllText(SettingsPath));
        Assert.Equal("Two", persisted!.Files.Prefix);
    }

    [Fact]
    public void ExternalMalformedChangeKeepsTheLastValidBackup()
    {
        var store = new SettingsStore(SettingsPath);
        var first = new AppSettings();
        first.Files.Prefix = "First";
        Assert.True(store.Save(first));
        var second = new AppSettings();
        second.Files.Prefix = "Second";
        Assert.True(store.Save(second));
        var third = new AppSettings();
        third.Files.Prefix = "Third";
        Assert.True(store.Save(third));

        var liveStore = new SettingsStore(SettingsPath);
        var live = liveStore.Load();
        Assert.Equal("Third", live.Files.Prefix);
        File.WriteAllText(SettingsPath, "this is not json");

        live.Files.Prefix = "Recovered";
        Assert.True(liveStore.Save(live));

        var backup = SettingsStore.Deserialize(File.ReadAllText(SettingsPath + ".bak"));
        Assert.NotNull(backup);
        Assert.Equal("Second", backup!.Files.Prefix);
        var primary = SettingsStore.Deserialize(File.ReadAllText(SettingsPath));
        Assert.Equal("Recovered", primary!.Files.Prefix);
    }
}
