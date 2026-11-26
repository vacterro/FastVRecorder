# Acceptance matrix — SRC-001 (FastVRecorder handoff)

Status legend:

- **Done** — implemented and verified by an automated test and/or a live UI check.
- **Done (manual)** — implemented; verification requires a physical microphone or Windows UI.
- **Optional** — allowed by the handoff as an optional improvement; deliberately not included.
- **Environment** — the build environment lacks the hardware/OS feature needed to exercise it.
- **Out of receipt** — the supplied source receipt ends inside section 16, so later sections were
  never part of the captured request.

The source body (`SRC-001.md`) is 1,043 lines and ends mid-sentence at the tray-menu block of
section 16. All requirements in the captured body are covered below.

## SAIHANDOFF-4 stabilization evidence (0.0.5)

| Area | Status | Evidence |
|---|---|---|
| Live/paused/stalled writer is never recovered | Done | OS `FileShare.None` ownership probe; `LiveWriterWithoutRecentGrowthIsNotRecovered`, held-writer test |
| Recovery coordinators cannot publish duplicates | Done | exclusive per-folder lease; `AnotherCoordinatorHoldingTheFolderLeaseBlocksRecovery`, concurrent `RecoverAll` test |
| Ownership failure is never silent | Done | lease failure yields `RecoveryFailure` and no output |
| Journal paths/aliases | Done | `.wav.part` suffix required, full-path normalization, case-insensitive load; alias test |
| Published audio survives History failure and is indexed later | Done | `HeldHistoryFileKeepsJournalAndIndexesOnRetry`, live reload test |
| Interrupted/numbered recovered output is adopted without duplicates | Done | `NumberedRecoveredOutputFromInterruptedPublicationIsAdopted` |
| Receipts are identity-bound and re-evaluated | Done | malformed/legacy/repaired-header/forced-retry tests; matching receipt avoids repeat work |
| External newer settings are not overwritten | Done | `ExternalValidChangeAfterLoad_IsNotOverwrittenByAStaleSave`; live edit + close preserved SHA-256 |
| Restored settings are applied live | Done | live lock/reload run: `settings: Unreadable` → re-read/saved; hotkeys, theme, language and tray applied |
| Reproducible packaging | Done | `scripts/Package.ps1`: manifest-only, 343 entries, identical SHA-256 on PowerShell 7 and 5.1 |
| Real hardware acceptance | Environment | No default capture endpoint: `Core Audio error 0x80070490`; only the system-default placeholder is enumerated |

## 0. Mission

| Requirement | Status | Evidence |
|---|---|---|
| Compact, fast, fully portable microphone recorder | Done | `src/`, `scripts/Publish.ps1`, published self-contained folder |
| Open → Configure → Forget → Record | Done | default folder + `Alt+0`, tray, auto-save settings |
| Not a DAW/OBS/editor | Done | intentionally small feature set, no desktop capture |

## 1. Autonomous execution contract

| Requirement | Status | Evidence |
|---|---|---|
| Repository root, tree, Git, media, Wintage components inspected | Done | build log `E-3`, `themes/` copied from the local Wintage collection |
| Baseline, roadmap, acceptance matrix | Done | `docs/ROADMAP.md`, this file |
| Full structure created from a media-only repo | Done | `src/`, `tests/`, `scripts/`, `docs/`, solution |
| Build, test, diagnose, continue to a finished product | Done | `dotnet build`, 127 passing tests, published app, UI smoke test |
| Checkpoint for continuation | Done | SAIPEN log/state and this matrix |

## 2. Technology stack

| Requirement | Status | Evidence |
|---|---|---|
| C#, .NET 10 LTS, WPF, NAudio/WASAPI, RegisterHotKey, NotifyIcon, System.Text.Json, self-contained portable win-x64, xUnit | Done | project files; `dotnet publish -r win-x64 --self-contained true` |
| Stable pinned dependencies (no previews) | Done | NAudio 3.3.0, xUnit 2.9.2, test SDK 17.12.0 |
| Prefer `WasapiRecorder` when stable | Done | `WasapiCaptureSource` uses `WasapiRecorderBuilder`/`WasapiRecorder` |
| No Electron/Chromium/backend/Docker/heavy DB/FFmpeg | Done | dependency graph |
| Fully offline | Done | no HTTP client, no update check |

## 3. Project structure

| Requirement | Status | Evidence |
|---|---|---|
| `src/App`, `Core`, `Infrastructure`, `tests`, `media`, `themes`, `scripts`, `docs`, solution, README, CHANGELOG, .gitignore | Done | repository tree |
| Simple separation: UI, engine, devices, persistence, settings, hotkeys, tray, theme | Done | layer/namespace layout in `docs/ARCHITECTURE.md` |
| Engine independent of the visible window | Done | engine only raises events; survives window hide/close-to-tray |

## 4. Recording engine

### 4.1 State machine

| Requirement | Status | Evidence |
|---|---|---|
| Explicit `IDLE → STARTING → RECORDING → STOPPING → FINALIZING → IDLE` | Done | `RecordingState`, `RecordingEngine.Transition` |
| `RECOVERING`, `ERROR`, conditional `PAUSED` | Done | `BeginRecoveryScan`/`EndRecoveryScan`; pause implemented by detaching capture |
| One authoritative state used by UI/tray/hotkeys/writer | Done | `RecordingEngine.State` + events; `MainViewModel` derives everything from it |
| No parallel recording, no start over unfinished finalization | Done | command gate; `ConcurrentStarts_ProduceOnlyOneRecording` |
| Atomic serialized transitions, repeated stop safe, no double dispose | Done | gate + idempotent cleanup; `ConcurrentStops_AreSafeAndFinalizeOnce` |
| Automatic return to IDLE after a recoverable error | Done | `StopCoreLocked` → `Error` → `Idle` |
| Toggle independent of focus | Done | `RegisterHotKey` message-only window |

### 4.2 Capture

| Requirement | Status | Evidence |
|---|---|---|
| WASAPI shared mode, default device, WAV 16-bit mono 48 kHz | Done | settings defaults; `WasapiCaptureSource` |
| No normalization/AGC/noise suppression/silence removal; raw mode preferred | Done | `.WithRawMode()` with shared fallback; `WithCommunicationsMode` never used |
| Convert unsupported device rates/formats correctly; never reinterpret float as integer | Done | `SampleConverter`, `FloatStereo_ConvertsToMonoPcm16…` |
| Respect channels/rate/block align | Done | `SourceFormat`/`TargetFormat`, tests |
| Stream to disk through a bounded buffer, never accumulate in RAM | Done | `WavStreamWriter` + 8 s `BoundedAudioQueue` |
| Audio callback free of file/UI/encoding work | Done | callback only enqueues; conversion/writes on writer thread |
| Bounded queue; overflow reported, not silently lost | Done | `BoundedAudioQueue` ring buffer with a dedicated write cursor (silent-recording bug fixed); `DroppedBytes`, incomplete note, error cue/banner; live signal test |
| Multi-hour recordings; RIFF limit handled | Done | 2 GiB segment rollover + frame-aligned header limit |
| No silent corruption | Done | header checkpointing, `WavVerifier`, tests |

### 4.3 Lifecycle

| Requirement | Status | Evidence |
|---|---|---|
| START: validate settings, folder, free space, device, reserve name, temp file, capture, RECORDING, start cue, UI/tray | Done | `RecordingEngine.StartCoreLocked`, `MainViewModel.OnRecordingStarted` |
| STOP: STOPPING, stop data, wait callback, write remaining, finalize header, flush, close, verify, non-overwriting rename, History, done cue, IDLE | Done | `StopCoreLocked`, `CommitSegment`, `WavVerifier` |
| `done.wav` only after confirmed save; never report false success | Done | Completed raised only for verified files; error cue/banner otherwise |

## 5. Audio formats

| Requirement | Status | Evidence |
|---|---|---|
| WAV PCM 16-bit default | Done | settings + WAV writer |
| WAV 24-bit, Float32 | Done | Custom encoding; header tests for all three |
| MP3 with 64/96/128 presets (plus 192) | Done | Media Foundation probe, post-stop encode, WAV kept on failure |
| FLAC / Opus | Optional / not offered | would require external codecs; hidden from production UI as required |
| Record to recoverable intermediate, encode after stop, keep source until success | Done | WAV intermediate is the only recording path; MP3 deletes WAV only after verification |
| No user-installed codecs; no unsupported formats in UI | Done | probe hides MP3 when unavailable; FLAC normalized away |
| Quality presets Voice/Standard/High/Custom, default Standard | Done | `RecordingSettings.EffectiveFormat`, Settings UI |
| Quality not changed during recording | Done | Recording section disabled while active; “Applies to the next recording.” |

## 6. File naming and storage

| Requirement | Status | Evidence |
|---|---|---|
| Default `<app>\Recordings`; no first-run wizard | Done | `PortableRoot.DefaultRecordingsDir` |
| Folder remembered across sessions; manually typed paths accepted | Done | settings; editable folder field with `%ENV%`/relative resolution, create-on-apply, inline invalid-path feedback |
| Choose/open folder, open/reveal/copy last, free-space indicator | Done | Settings, History, tray, Recorder free-space line |
| Unicode, spaces, long paths, removable/network paths, errors handled | Done | .NET paths, `longPathAware` manifest, folder/space probes |
| Never silently switch an unavailable folder | Done | start error names the folder; settings keeps the path |
| OBS timestamp names and `_001` collisions | Done | `FileNamer`; `TimestampNaming_MatchesObsStyleAndSuffixesCollisions` |
| Counter and prefix+date presets | Done | `CounterNaming_UsesPersistentCounter`, date-folder test |
| Naming preset/prefix/incremental/date folders/preview | Done | Settings page + `Preview` |
| No overwrite, no traversal, invalid/reserved names, no collisions after restart/delete/clock change | Done | `PathSafety`, `CreateNew` reservation, persistent counters, tests |
| Atomic name reservation, not only `File.Exists` | Done | `.part` created with `FileMode.CreateNew` |
| Counter persisted and not reused after a failure | Done | `JsonCounterStore`; number allocated before file creation |

## 7. Crash recovery (P0)

| Requirement | Status | Evidence |
|---|---|---|
| `.wav.part` + `.recovery.json` with all listed fields | Done | `RecoveryMetadata`, engine metadata writer |
| Self-describing audio; metadata not a single point of failure | Done | real WAV header in the part; recovery works without JSON (`RecoversFromHeaderAlone…`) |
| Periodic flush ~1–2 s | Done | default 1.5 s, configurable |
| Next launch: find parts, validate, compute valid frames, drop only incomplete frame, rebuild header, create `_RECOVERED.wav`, verify, index | Done | `RecoveryService`; 6 recovery tests |
| Keep damaged originals until verification; never delete user recordings | Done | part preserved; markers written after verification |
| Do not claim full recovery when data was lost | Done | note records dropped bytes; UI marks Recovered/Incomplete |
| Handle process kill, system crash, reboot, OOM, disk full, mic/folder disconnect, finalization/encode/rename failure | Done / Environment | code paths for stop/write/capture failures; parts survive process kills; hardware disconnects cannot be exercised here |
| Keep unrecoverable files for diagnosis | Done | `.unrecoverable.json` sidecar, no deletion |
| Never promise bytes the OS never flushed | Done | documented in README/architecture |

## 8. Global and local hotkeys

| Requirement | Status | Evidence |
|---|---|---|
| `Alt + 0` toggles recording; main-row 0, Numpad 0 separate | Done | `HotkeyDefaults`, parse/serialize tests |
| Native `RegisterHotKey`, `MOD_NOREPEAT`, no polling/hook/admin | Done | `GlobalHotkeyService` |
| Works in other apps, minimized/hidden, fullscreen where Windows allows | Done (manual) | message-only window registration; log `Global hotkeys applied`; toggled a real recording from another foreground application and while hidden |
| Eight actions; Toggle `Alt+0`, Open Last `Ctrl+Alt+L`, others unassigned | Done | `HotkeyDefaults.All`, schema v2 migration test |
| Editor: title, combination, scope, click-assign, clear, reset, conflict | Done | Keys page |
| Ctrl/Alt/Shift/Win/F-keys/letters/digits/Numpad/specials; Esc cancels | Done | `HotkeyGesture`, `PreviewKeyDown` capture |
| Reject reserved combos, accidental recording while editing, duplicates, double local/global handling, losing the old working hotkey | Done | validator + transactional `Apply` + `Suspend` release; tested rules |
| Clear conflict message | Done | Win32 error surfaced in the Keys page/banner |
| Transactional change | Done | previous set restored on failure |
| Global Show/Hide action | Done (manual) | default gesture is unassigned; the operator assigned `Shift+Alt+0`, registration logged, and hide/show was toggled live (missing event wiring was fixed) |

## 9. Sound notifications

| Requirement | Status | Evidence |
|---|---|---|
| Original `media/start.wav`, `done.wav`, `error.wav` | Done | assets shipped untouched |
| Semantics start/save/failure | Done | MainViewModel event wiring |
| Enable, master volume 0–100 (default 35), per-event toggles, preview | Done | Settings Notifications; UI smoke shows 35 % |
| Audible output on the selected device | Done | notification output selector; WASAPI shared playback with WaveOut fallback; `WasapiCuePath…` test |
| Optional per-event volume | Partial/optional | model fields exist (100 %); no extra sliders in 0.0.2 |
| Volume independent of mic gain and recorded audio; no system-level changes | Done | separate `WaveOut` playback path |
| Asynchronous, non-blocking, no infinite queues, no capture interruption | Done | `CuePlayer` replaces/stops a single playback |
| Missing/corrupt asset never blocks recording; no recursive error cue | Done | `CuePlayer` catches and logs; tests |
| Never mix cues into the recording programmatically | Done | separate output device path |
| Warn that speakers may be picked up; allow disabling the start cue | Done | Recorder note + tooltip; per-event toggle |

## 10. User interface

| Requirement | Status | Evidence |
|---|---|---|
| One window with Recorder/History/Hotkeys/Settings; no extra app windows | Done | `MainWindow` + DataTemplates; only OS folder picker/Explorer |
| Inline errors/warnings/confirmations | Done | banner, inline rename/delete/reset confirmations |
| ~380 × 440 DIP default, resizable, readable minimum, DPI 100–200 %, remembered/clamped geometry | Done | 380 × 460 default, manifest PerMonitorV2, clamp logic; UI smoke |
| Recorder page elements (icon, status, timer, meter, mic, button, hotkey, folder, filename, navigation) | Done | screenshots/UI smoke |
| Compact but not cramped | Done | Golden Default layout |
| Status list Ready/Starting/Recording/Paused/Saving/Saved/Recovering/Error | Done | `Loc` status strings + engine states |
| Red indicator, timer, meter, file size “if it fits”, recording tray icon | Done / optional | indicator/timer/meter/tray icon done; file size omitted (optional) |
| Monotonic/byte-based timer | Done | `Elapsed` from written bytes |
| Do not open the microphone while idle | Done | capture created only on Start/resume |
| Microphone meter only during recording; separate test optional | Done | meter active only while recording; explicit **Test microphone** button with live level and no-signal text |

## 11. Wintage theme system

| Requirement | Status | Evidence |
|---|---|---|
| Golden Default mandatory default | Done | built-in tokens copied from `goldendefault.json`; screenshot |
| Use existing local Wintage definitions/contract | Done | all `themes/*.json` from the local collection shipped |
| Character: warm dark, gold/brass, muted surfaces, contrast, borders, compact controls, recording/error semantics | Done | semantic palette + screenshots |
| Semantic tokens listed | Done | brushes `Background`, `Surface`, `SurfaceElevated`, `Border`, `Accent`, `AccentHover`, `TextPrimary`, `TextSecondary`, `TextMuted`, `Recording`, `Success`, `Warning`, `Error`, `Selection`, `Focus` |
| Dynamic switching, remembered, other themes, validation, fallback, no hardcoded colours, no flicker/restart | Done | `ThemeCatalog`/`ThemeService`; live switch verified (Golden ↔ Vintage Dark) |
| Temporary palette clearly labelled if originals unavailable | Not needed | originals were available |

## 12. Icon and assets

| Requirement | Status | Evidence |
|---|---|---|
| ICO from `media/favicon.png` at 16/20/24/32/48/64/128/256 | Done | `media/favicon.ico` (verified sizes) |
| Use for EXE/window/taskbar/tray/Explorer/startup shortcut | Done | `ApplicationIcon`, window icon, `NotifyIcon`, shortcut `IconLocation` |
| Tray variants idle/recording/paused/error with red overlay while recording | Done | `media/tray-*.ico`, `TrayService` |
| Preserve original design/transparency; no downloaded icons | Done | derived only from the provided PNG |

## 13. Recording history

| Requirement | Status | Evidence |
|---|---|---|
| Compact Name/Date/Duration/Size/Format table | Done | History page |
| Play/pause, open with default player, reveal, copy path, rename, delete with confirmation | Done | History actions + tests of the store |
| Built-in playback without a second window, only one player | Done | single `WaveOut` in `HistoryViewModel` |
| Stop playback when a new recording starts | Done | `OnRecordingStarted` |
| No synchronous giant-directory scan on the UI thread | Done | persistent JSON index, refresh from saved entries |
| Survives restart; missing files shown or index updated | Done | `HistoryStore`, `Missing` detection |
| Recycle Bin deletion when possible | Done | `SHFileOperation` with undo |
| No age-based auto deletion | Done | no cleanup code |
| Optional search/filter/sort/pin/inline edit/recovered display | Partial/optional | recovered display and inline rename included; search/filter/pin omitted |

## 14. Settings

| Requirement | Status | Evidence |
|---|---|---|
| One Settings page with compact sections | Done | expanders |
| Recording/Files/Notifications/Application/Appearance/Advanced controls as listed | Done (with noted optional omissions) | Settings UI smoke |
| Auto-save, no Apply | Done | debounce + `SettingsStore.Save`; “Saved.” shown |
| “Applies to the next recording.” for capture-time options | Done | Recording section note + disabled while recording |
| Never interrupt a recording for theme/volume/visual changes | Done | independent settings; capture untouched |
| Windows notification default OFF | Done | tray balloon option, default false |

## 15. Portable settings

| Requirement | Status | Evidence |
|---|---|---|
| Canonical `FastVRecorder.exe`, `config/`, `media/`, `themes/`, `Recordings/` | Done | published layout |
| Settings relative to application root, not CWD | Done | `PortableRoot.FromExecutable` |
| Atomic writes, schema version, migration, backup, validation, safe defaults, malformed recovery, preserve valid fields | Done | `AtomicFile`, `SettingsStore`, `Normalize`, 6 settings tests |
| No absolute paths for app resources | Done | root-relative resolution |
| Relative path for folders inside the portable root; absolute for external | Done | `ToStoredFolder`/`ResolveRecordingFolder` |
| After moving the folder: themes, sounds, default folder, settings, hotkeys work | Done | all resolve from root; UI smoke from a moved-style output folder |
| Read-only portable folder handled visibly, no hidden AppData switch | Done | `CanWriteConfig`, warning banner, save error |

## 16. Windows tray

| Requirement | Status | Evidence |
|---|---|---|
| Keep running in tray when hidden/minimized | Done | `NotifyIcon`, close/minimize-to-tray |
| Tray menu: title, start/stop, … | Done (receipt ends here) | menu has show/hide, start/stop, open folder, open last, settings, exit |
| Recording/paused/error indicators in the tray | Done | state icons + tooltip |

## 17. Data-safety hardening and performance (0.0.2)

| Requirement | Status | Evidence |
|---|---|---|
| Publishing cannot delete user data | Verified | `Test-PublishSafety.ps1`: 42 assertions comparing SHA-256 before/after across recordings, WAV/MP3, settings, history, counters, subfolders, partials, recovery metadata, unknown files, custom themes, language overrides and cue assets; refusal cases for repo root, traversal, source tree, non-runtime and reparse-point targets |
| Clean/build scripts leave the portable runtime alone | Verified | `Clean-Build.ps1` touches only `bin/`/`obj/` and owned staging; publish installs through an isolated staging folder and a manifest-diff of owned files |
| Writer ownership and truthful stop | Verified | writer-timeout test leaves the session blocked, then finalizes under the engine gate; no concurrent finalization |
| Recovery is transactional and idempotent | Verified | owned `*.tmp-*` output, adopt verified `_RECOVERED.wav`, retry after a locked partial, zero-byte sentinel untouched, journal kept for a disconnected volume |
| Outcomes drive cues | Verified | `Success` → done cue; `PartialSaved`/`Failed*` → error cue; `CancelledNoAudio` → info; stale session events ignored |
| MP3 validation | Verified | 10.1 s recording → 162,095-byte MP3, `ffprobe` duration 10.104 s, stop-to-save 197 ms; failed encode keeps the WAV and does not overwrite |
| Performance (operator machine) | Measured | startup 2.5–3.1 s warm / 5.7 s cold from a freshly extracted zip; hotkey start 73–201 ms; stop-to-save 93–131 ms for a 3 s WAV; tray idle 30 s used 0.063 s CPU (0.01 % of a 16-core host) and 170.3 MB working set; crash recovery rebuilt 4.49 s of audio 3.35 s after relaunch |
| Queue overflow is truthful | Verified | `RecordingEngineTests.QueueOverflow_IsPartialSavedWithHistoryNote`: dropped bytes produce `PartialSaved`, a History note and the error path instead of `done.wav` |
| Locked settings are never overwritten | Verified | `SettingsStoreTests.LockedExistingSettings_AreNotResetOrOverwritten`: a held `settings.json` loads as `Unreadable`, saving is refused and the original bytes survive |
| Stale recovery markers do not hide parts | Verified | `RecoveryHardeningTests.StaleRecoveredMarkerWithoutOutput_RebuildsThePart`: missing output is rebuilt and the part is preserved |
| Release notices are reproducible | Verified | `Publish.ps1` stages `THIRD-PARTY-NOTICES.md` from the repo and `LICENSE.txt`/`ThirdPartyNotices.txt` from the SDK |
| Release package hygiene | Verified | `release/FastVRecorder-0.0.2-win-x64.zip` contains no `config/`, `Recordings/`, logs, settings, history or recovery artifacts; clean extraction recorded a 3.11 s `Success` WAV |

## Cross-cutting verification

| Item | Evidence |
|---|---|
| Solution builds in Release | `dotnet build FastVRecorder.sln -c Release` — success |
| Automated tests | 101 core + 58 integration = **159 passed**, 0 failed |
| Portable publish | `scripts/Publish.ps1` → `dist/FastVRecorder` with self-contained `FastVRecorder.exe` |
| UI smoke | live windows captured: Recorder, History, Hotkeys, Settings (all sections), Golden Default, Vintage Dark, English, Russian, System Default microphone, 35 % notification volume |
| UI languages | 33 language packs (`LanguageTests`), native-name selector, Arabic RTL, portable overrides; 31 are machine-translated with English fallback |
| Version | 0.0.5 (`VERSION`, `Directory.Build.props`, UI footer, CHANGELOG) |
| Real hardware capture | The published build recorded real multi-second WAV files from the ATR2100x-USB endpoint (48 kHz mono 16-bit, verified headers); after the queue fix a live recording contained peak 13925 / RMS 1537, not silence. |
| Notification output | `Notification 'start' -> WASAPI output 'Realtek Digital Output'` logged from the Preview button with the selector set; WaveOut fallback tested. |
| Manual folder entry | Editable field + Save/Enter/LostFocus; `RecordingFolderResolver` tests cover empty, relative, absolute and environment-variable paths. |
| 33 languages | `LanguageTests` verifies all 33 embedded packs, native names, Arabic RTL, English fallback, portable overrides; German/Chinese/Arabic UI captured. |
| Crash recovery on real audio | Process killed mid-recording; next launch rebuilt `2026-11-26_06-54-49_RECOVERED.wav` from the 353,580-byte part and indexed it as Recovered. |
| Input signal diagnosis (historical) | Before the queue fix the ATR2100x endpoint appeared near-silent because the ring buffer wrote at the read cursor. After the fix the same endpoint produced peak 13925 / RMS 1537; the in-app test now distinguishes real silence from an engine failure. |

## 18. Final reliability audit (0.0.4 / SAIHANDOFF-3)

Scope: publication trust, interrupted-update recovery, deferred writer completion, recovery
identity, settings load/backup safety, MP3/History reconciliation and honest performance numbers.
Historical hardware rows above describe earlier builds; they are not re-verified here.

| Requirement | Status | Evidence |
|---|---|---|
| Manifest paths cannot escape the publication root | Verified | `Test-PublishSafety.ps1` rejects parent traversal, rooted/UNC paths, alternate data streams, reserved device names, ambiguous segments, case-insensitive duplicates and non-SHA-256 digests before any mutation; canonical containment is proven per destination |
| Damaged manifests fail closed | Verified | corrupted, traversal, rooted and bad-digest manifests refuse the publication; user bytes unchanged; sentinel outside the target untouched |
| Reparse points cannot redirect writes or deletes | Verified | destination junction, existing destination file/ancestor checks; `destination under a junction refused`, sentinel untouched; file/manifest symlink cases covered when the host permits symlink creation (2 skipped otherwise) |
| Unknown user files are never silently replaced | Verified | unowned collision and user-modified owned collision both refuse; bytes preserved; obsolete artifacts deleted only when their hash matches the previous manifest |
| Interrupted publication is recoverable | Verified | injected faults at first copy, mid-copy, manifest replacement and deletion all roll back to the previous generation; journal/backup cleaned; retry succeeds; direct journal repair restores bytes, removes unowned output and restores the manifest |
| Concurrent publication is refused | Verified | exclusive target lock: a held lock refuses the second publish, first-run target unchanged, publish succeeds after release |
| Staging ownership is proven, not named | Verified | marker + nonce + repository identity + exclusive lease: active and foreign staging are preserved, released owned staging is removed; an unmarked `publish-*` folder is never deleted |
| Uninspectable running process blocks publish | Implemented | `Assert-RuntimeNotRunning` refuses when a `FastVRecorder` process path cannot be read (not exercisable without such a process) |
| Deferred finalization cannot strand a session | Verified | `WriterTimeout_...` test: timeout reports once, writer exit finalizes through the shared completion path, History gets one WAV entry, state returns to Idle, no duplicate terminal event; gate retries and crash reconciliation are code-reviewed |
| MP3/History reconciliation | Verified | MP3 test asserts one MP3 history entry and no WAV; missing segment, post-encode metadata failure and failed WAV deletion are handled without losing discoverability; existence refresh is lock-safe |
| Recovery verifies output identity | Verified | marker/adoption tests: unrelated same-length PCM, arbitrary external path and advisory metadata are refused and rebuilt from the part; matching PCM identity adopts without a duplicate; junction folders are never scanned; concurrent `RecoverAll` produces exactly one output |
| Settings locks cannot replace newer settings | Verified | unreadable primary + valid backup stays readonly with backup values in memory; primary appearing after defaults is not overwritten; damaged primary never rotates into the backup; access restored in-window re-reads the primary |
| A post-backup user edit survives a refused publication | Verified | `late-edit`, `late-edit-other-entry` and the two direct-repair tests: rollback is conditioned on journaled pre-image/new hashes, so an edit on the failing entry, on another entry, or before the new hash was journaled is preserved; only this transaction's output or its verified pre-image is restored/deleted |
| Recovery does not block recording | Verified | `RecoveryScan_DoesNotBlockRecording`: `StartAsync` succeeds during the scan, `EndRecoveryScan` does not clobber the active session, and the scan skips the engine's active part |
| Fast feedback preserved | Verified | 141 tests in ≈ 5 s (97 Core ≈ 0.9 s, 44 Integration ≈ 4 s); publish safety 139 assertions ≈ 3 s |
| Performance measured honestly | Measured | visible window 1.5–2.6 s warm (phase log: settings ≈ 20 ms, language/theme ≈ 65 ms); managed heap ≈ 12.5 MB; tray working set ≈ 158 MB (0.0.2 baseline 170.3 MB); tray idle CPU 0.00–0.05 s / 30 s; MP3 probe deferred (39–47 ms logged after show); language packs lazy (all-34 parse ≈ 23 ms vs en-only ≈ 0.7 ms) |
| Real hardware acceptance on 0.0.4 | NOT VERIFIED | this host exposes no capture endpoint (`No default capture endpoint: Core Audio error 0x80070490`, microphone list = 1 default placeholder); no physical recording was claimed |
| Release artifact (0.0.4) | Built and verified | `release/FastVRecorder-0.0.4-win-x64.zip`, 71,205,871 bytes, SHA-256 `5C54590B68FC4CDB08C3547368ECA03FFE856F06BD4E5C2D1C04D2DD60210BEA`, 343 entries (342 manifest files + manifest); packaged strictly from manifest-owned files with hash verification; no `config/`, `Recordings/`, logs or `.fvr-update`; clean extraction launched in 1.45 s with a bogus `DOTNET_ROOT` (self-contained); app exe SHA-256 `D79917C6FAFB851995DAF5CEC420F109C32C7BC223C7338A3393E15C6CC032EA` |
| Publication state | Pending by design | origin/main and tag publication are reconciled through the SAIPEN cohort workflow; no cohort release batch exists and no public GitHub Release was created |

## 19. Final stabilization (0.0.5 / SAIHANDOFF-4)

Scope: recovery ownership/concurrency, durable History reconciliation, unrecoverable receipt
identity, runtime settings reconciliation, real-Windows acceptance, performance and reproducible
packaging.

| Requirement | Status | Evidence |
|---|---|---|
| A live writer is never recovered | Verified | `LiveWriterWithoutRecentGrowthIsNotRecovered`: a paused writer handle with an hour-old timestamp and no growth is skipped; `RecoverAll` proves ownership with `FileShare.None` and never falls back to the length heuristic |
| Multiple coordinators cannot duplicate output | Verified | `AnotherCoordinatorHoldingTheFolderLeaseBlocksRecovery` reports the lease contention and produces no WAV; after release the scan recovers once; concurrent `RecoverAll` still yields one output |
| Journal path validation/aliases | Verified | `JournalEntryWithoutPartSuffixIsIgnored`, `RelativeJournalAliasIsRemovedByTheRecoveryPath`, `JournalAcceptsCaseVariedPropertyNames`; external configured journal paths remain supported by the existing test |
| Publication survives History failure | Verified | `HeldHistoryFileKeepsJournalAndIndexesOnRetry`: audio is published and reported, the journal stays, the in-memory index shows the entry, and the next scan persists it without a second WAV |
| Interrupted/numbered output is adopted | Verified | `NumberedRecoveredOutputFromInterruptedPublicationIsAdopted` adopts `_RECOVERED_001.wav` by PCM identity and creates no base duplicate |
| Receipt identity and retry | Verified | `MalformedUnrecoverableMarkerDoesNotHideRecoverableAudio`, `IdentityLessUnrecoverableMarkerIsReevaluatedOnce`, `RepairedHeaderAtTheSameLengthIsReevaluated`, `ExplicitRetryReevaluatesAMatchingUnrecoverableReceipt`, `MatchingReceiptAvoidsRepeatedPermanentWork` |
| External settings are preserved | Verified | `ExternalValidChangeAfterLoad_IsNotOverwrittenByAStaleSave`; live run: externally edited prefix survived a close/save with identical SHA-256 |
| Restored settings apply live | Verified | live locked-start run (`settings: Unreadable`) → unlock → close: log records re-read/save and the applied theme/language/diagnostics/startup/hotkeys/tray; stored `de`/`vintagedark` and `Alt+Shift+0` were loaded |
| Reproducible package | Verified | `scripts/Package.ps1` on Windows PowerShell 5.1 and PowerShell 7 produced the same 343-entry ZIP and SHA-256; each file is checked against `publish-manifest.json`; runtime data/journals/logs/staging excluded |
| Full suite | Verified | 159/159 in ≈ 9 s wall (101 Core ≈ 1 s, 58 Integration ≈ 6 s) |
| Publish safety | Verified | 139/139 assertions on PowerShell 7 and Windows PowerShell 5.1; symlink cases skipped only when host privileges are absent and accounted for |
| Performance | Measured | warm startup to visible window 1.33–1.99 s; WS ≈ 160 MB; private ≈ 105 MB; managed ≈ 13 MB; 23–25 threads; 860–880 handles; idle CPU 0.031 s / 30 s |
| Real hardware acceptance on 0.0.5 | NOT VERIFIED — ENVIRONMENT LIMITATION | no default capture endpoint (`Core Audio error 0x80070490`); microphone enumeration = 1 system-default placeholder; MP3 available=True; pause/resume, hotkey recording and unplug/reconnect could not be exercised without a capture endpoint |
| Release artifact (0.0.5) | Built and verified | `release/FastVRecorder-0.0.5-win-x64.zip`, 343 entries (342 manifest files + manifest); the final size and SHA-256 are recorded in `release/SHA256SUMS.txt`; packaged solely from `publish-manifest.json` with per-file hash verification; no `config/`, `Recordings/`, logs, journals, partial/recovered files or `.fvr-update`; clean extraction launched in 1.8–2.3 s (window-shown) with a bogus `DOTNET_ROOT`; artifact recovery rebuilt a real WAV and indexed it; externally edited settings survived a close/save with identical SHA-256 |
| Publication state | Pending by design | source/tag push only through SHIP; no public GitHub Release or asset upload without explicit operator authorization |
