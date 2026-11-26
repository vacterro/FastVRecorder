# Architecture

## Goals

- Never lose a recording that reached the file system.
- One authoritative recording state; fast repeated input must be harmless.
- Keep the audio callback free of file I/O, UI work, and heavy encoding.
- Remain portable: all writable state and assets live next to the executable.
- Keep the stack small: C#, .NET 10, WPF, NAudio/WASAPI, System.Text.Json, xUnit.

## Layers

```
FastVRecorder.App (WPF)
    MainWindow, Views, ViewModels, Tray, ThemeService, Localization
        │
FastVRecorder.Infrastructure (Windows)
    RecordingEngine, FileNamer wiring, WASAPI adapter, hotkeys, cue player,
    recovery service, settings/history stores wiring, startup, shell
        │
FastVRecorder.Core (portable, no OS UI)
    Settings model + store, WAV header/writer verifier, SampleConverter,
    naming/counters/atomic file, history, journal, themes, hotkeys, logging
```

`Core` has no WPF or Windows Forms reference. `Infrastructure` owns Windows APIs and NAudio.
`App` is the only project with UI. Tests reference `Core` and, for end-to-end checks,
`Infrastructure`.

## Recording lifecycle

The engine exposes one serialized command gate (`SemaphoreSlim`). Start, stop, pause, and the
startup recovery transition acquire it; the engine state itself is guarded by a separate state
lock so readers always see a complete state/event pair.

START:

1. Snapshot settings and validate the target format.
2. Resolve the recording folder; create/probe it; check free space against the threshold.
3. Resolve the selected microphone through `IAudioCaptureFactory` (default endpoint or stored id).
4. Reserve a unique name (`FileNamer`) by creating the `.part` file with `FileMode.CreateNew`.
5. Open a `WavStreamWriter` on that stream; write a real WAV header immediately.
6. Write `.recovery.json` and add the part to `config/active-recordings.json`.
7. Create the capture source, the bounded queue, and the `SampleConverter`.
8. Start the writer thread, then the capture device.
9. Transition to `Recording`; raise `Started` (the app plays `start.wav`).

STOP:

1. Transition to `Stopping`; stop and dispose the capture source (waits, bounded, for the WASAPI
   callback acknowledgement).
2. Complete the queue and join the writer thread, which drains converted frames.
3. `WavStreamWriter.Complete()` patches the header sizes, flushes to disk, and closes the stream.
4. `WavVerifier` re-parses the file and checks format, declared data size, and physical length.
5. The `.part` is moved to the final name without overwriting anything; if the name was taken in
   the meantime, the next free suffixed name is used.
6. Recovery metadata and the journal entry are removed; a History entry is added.
7. For MP3: transition to `Finalizing`, encode each WAV segment with Media Foundation, verify the
   output, delete the intermediate WAV on success (keep it and report on failure).
8. Transition to `Idle`; raise `Completed` (the app plays `done.wav` only if no failure occurred).

On a recoverable failure the engine tries to finalize what exists, reports the error, transitions
`Error → Idle`, and keeps the app ready for the next attempt. A failed finalization keeps the
`.part` and its metadata for the next launch's recovery.

PAUSE detaches and disposes the capture device while keeping the writer/file open; resume creates
a fresh source, rebuilding the converter if the device format changed. Elapsed time counts only
bytes that were actually written, so paused time never appears in the timer.

## Threads

- **WASAPI callback**: copies the packet into `BoundedAudioQueue`; it never waits. If the queue is
  full the packet is dropped and the byte count is recorded (`DroppedBytes`).
- **Writer thread**: pulls raw device frames, runs `SampleConverter` (decode → channel map →
  resample → encode), writes whole frames to `WavStreamWriter`, publishes the peak level, and
  performs `Flush(flushToDisk: true)` on the configured checkpoint interval.
- **Capture stop**: `WasapiCaptureSource.Stop()` requests the stop and waits up to 3 s for the
  `RecordingStopped` callback; the writer drains afterwards.
- **UI thread**: polls the peak/timer, handles engine events marshalled through the WPF dispatcher.
- **Engine gate**: serializes commands; no state transition is ever executed concurrently.

`BoundedAudioQueue` is a single-producer/single-consumer ring buffer with `Monitor` waits. Its
capacity is eight seconds of the device format (minimum 1 MiB). Overflow is explicit: the saved
recording is marked incomplete, the History note records the dropped byte count, and the error cue
is used.

## Formats

`SourceFormat` describes the device packet; `TargetFormat` describes the file. `SampleConverter`
handles any supported source width and channel count:

- float32/float64 or PCM 8/16/24/32 decoded to float,
- multichannel → mono (average) or stereo (first two channels / duplicate mono),
- `WdlResampler` when rates differ,
- encoded as PCM16, PCM24, or Float32.

The WAV header has two fixed shapes:

- PCM: RIFF + fmt (16 bytes) + data, data at 44;
- IEEE float: RIFF + fmt (18 bytes) + fact + data, data at 58.

The header remains valid from the first byte and is re-patched on every checkpoint, so a crash
leaves a file whose header is at most one checkpoint stale. `WavVerifier` checks the format, the
declared data size, and the physical size.

Segment rollover occurs at 2 GiB of data. `WavStreamWriter` refuses a write that would exceed the
segment limit; the engine finalizes the segment, registers it, reserves the next name, and
continues in a new file. This keeps the 32-bit RIFF size valid without relying on RF64 support.

MP3 is not streamed: the recoverable intermediate is a WAV. After a successful stop the WAV is
encoded through Windows Media Foundation (`MediaFoundationEncoder.EncodeToMp3`) and only deleted
when the MP3 exists and is non-trivial. A failed encode keeps the WAV and records a History note.

## Crash recovery

Every recording creates:

- `name.wav.part` — a real WAV stream with a header;
- `name.wav.recovery.json` — `RecoveryMetadata` (schema 1);
- an entry in `config/active-recordings.json`.

At the next start the engine enters `Recovering` and `RecoveryService` scans:

1. every journal entry whose part still exists;
2. the configured recording folder and the default `Recordings` folder (depth-limited, skipping
   config/themes/media trees).

For each part it parses the WAV header, computes the physically present whole frames, copies them
into a new `<name>_RECOVERED.wav`, verifies the new header and reads frames back with
`WaveFileReader`, then writes a `.recovered` marker, updates the recovery JSON, adds a History
entry marked **Recovered**, removes the journal entry, and deletes only an empty reserved
placeholder. The `.part` itself is preserved.

If a part has no metadata, the header inside the part is enough. If the header is unusable or has
no complete frames, the part is left alone and a `.unrecoverable.json` sidecar is written; later
launches skip it. Recovery is idempotent: the marker prevents a second recovered copy.

## Naming and reservation

`FileNamer` composes:

- `Timestamp`: `yyyy-MM-dd_HH-mm-ss` (`HH-mm-ss` inside date folders);
- `Counter`: `<prefix>_0001`, advancing through `JsonCounterStore` (atomic JSON);
- `PrefixDateCounter`: `<prefix>_yyyy-MM-dd_0001` with a daily counter.

The prefix is reduced to one safe Windows component (`PathSafety`): separators and invalid
characters become `_`, reserved device names are prefixed, trailing dots/spaces are removed.

Reservation creates the `.part` with `CreateNew` and checks both the `.wav` and the optional
`.mp3` final names; a lost race retries. Collision suffixes are `_001…`. The final move never
overwrites: if a file appeared at the final name, `NextFreePath` picks a suffixed alternative.
Counters advance before the file is created, so a failed attempt never reuses a number.

## Settings and history

`SettingsStore` writes `config/settings.json` through a temporary file with
`File.Replace(..., backup: settings.json.bak)`. Load order:

1. full deserialize with camelCase/PascalCase tolerance;
2. tolerant section-by-section parse when the whole document fails;
3. backup;
4. defaults, keeping the invalid primary as `settings.json.corrupt`.

`AppSettings.Normalize` repairs individual fields without resetting valid neighbours: clamps
volume/interval/threshold, rejects unknown formats/themes/languages, removes duplicate or reserved
hotkeys, and fills every missing hotkey action with its default.

`HistoryStore` is a small JSON list (max 5000, pinned entries kept) with atomic writes. Paths are
absolute in 0.0.1; missing files are detected on refresh and shown as **Missing**. Recovery and
engine notes drive the Recovered/Incomplete labels.

## Hotkeys

`GlobalHotkeyService` owns a message-only `NativeWindow` and registers each global action with
`RegisterHotKey(id, MOD_* | MOD_NOREPEAT, vk)`. `WM_HOTKEY` maps the id back to a `HotkeyAction`
and raises it on the UI thread. Local combinations are matched in the main window's
`PreviewKeyDown`.

`Apply` is a whole-set transaction: unregister all, attempt the new set, and on any failure
unregister the partial set and restore the previous one. While the editor captures a combination,
`Suspend(true)` also releases registrations so the editor can observe the keys; `Suspend(false)`
restores the last accepted set. Validation rejects reserved Windows combinations, modifier-only
keys, global bare keys, and Shift+letter typing collisions.

## Themes

`ThemeCatalog` loads the built-in Golden Default plus every valid `themes/*.json`. A theme is
accepted only when all 21 Wintage tokens exist and are `#RRGGBB`. Broken files are logged and
skipped. `ThemeService` maps the tokens to semantic WPF brushes (Background, Surface, Accent,
Recording, Error, …) and replaces the application resources. XAML uses `DynamicResource` only, so
a theme change repaints live and without touching control code.

## UI and localization

`MainViewModel` composes `RecorderViewModel`, `HistoryViewModel`, `HotkeysViewModel`, and
`SettingsViewModel`, owns the navigation, the banner, the 120 ms timer, and the settings auto-save
debounce. Views bind to the view models; the only code-behind is window geometry, close-to-tray,
local hotkey dispatch, and hotkey capture routing.

`LanguagePacks` embeds 33 JSON packs in `Core` and exposes `Translate(code, key)` with English as
the fallback for every missing key. Optional `languages/<code>.json` files beside the executable
replace an embedded pack, so translations can be corrected without rebuilding. `Loc` resolves the
stored setting (or the OS culture) through `LanguageCatalog`, exposes the string table through an
indexer, and raises `Item[]`, `FlowDirection` and language properties on a switch. Arabic selects
right-to-left window flow; every view model is reused and no restart is required. The 31 added
packs are machine-translated from English and documented as such.

## Portability

`PortableRoot` is built from `Environment.ProcessPath`, never from the current directory. Settings,
history, counters, journal, logs, themes, media, and the default recording folder all resolve
under that root. Stored folder values inside the root are relative; external folders remain
absolute. The config folder is probed for writability at startup; a read-only folder produces a
visible warning and a failed save, never a hidden AppData fallback.

## Testing

Core tests exercise pure logic: naming, reservation, WAV header/writer/verifier, converter and
resampler, settings (atomicity, backup, corruption, normalization), hotkeys, themes, history,
journal.

Integration tests run the real `RecordingEngine` against a fake capture source that emits fixed
frames: start/stop, concurrent start/stop, pause/resume, missing device, unavailable folder, MP3
(when Media Foundation is present), and `RecoveryService` against hand-built partial files
(with/without metadata, idempotency, damaged parts, journal discovery). The environment used for
this build had no physical capture endpoint, so the WASAPI adapter boundary is covered by the
interface and the deterministic source; hardware capture remains an operator check.
