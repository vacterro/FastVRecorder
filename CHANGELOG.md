# Changelog

All notable changes to FastVRecorder are recorded here.

## 0.0.4 — 2026-11-26

Final reliability audit (SAIHANDOFF-3): publication trust, recovery identity and settings safety.

### Security and publication
- The publish manifest now has a strict relative-path contract: parent traversal, rooted/UNC
  paths, alternate data streams, reserved device names, ambiguous segments, invalid digests and
  case-insensitive duplicates are rejected before any mutation. Every destination is proven to
  stay inside the authorized root.
- A damaged or missing manifest can no longer authorize a destructive legacy update; unowned or
  user-modified destination files are refused instead of overwritten.
- Reparse points are refused on every existing destination file and ancestor, so a junction or
  symlink cannot redirect a write or a deletion.
- Updates are transaction journaled: complete preflight, per-file atomic replacement, preserved
  previous generation and automatic rollback on failure or interruption. A concurrent publish is
  refused by an exclusive target lock, and staging ownership is proven by a marker plus a live
  lease instead of a folder-name prefix.
- Publishing refuses to proceed when a running FastVRecorder process cannot be inspected.
- Rollback is hash-conditioned: the journal records each published file's new hash, and a
  rollback restores or deletes only bytes this transaction wrote (or its verified pre-image).
  A file edited after its backup is preserved as a newer local edit instead of being overwritten.

### Recording completion
- Deferred writer finalization now shares the single completion path with the normal stop route:
  it retries gate acquisition, reconciles History and state to Idle, never emits a duplicate
  terminal event or a false success cue, and always clears the pending flag even after a crash.
- Application exit while deferred finalization is pending waits for it and records a precise
  recovery message when the partial file must be recovered on the next launch.
- MP3 conversion indexes the intermediate WAV before encoding, records a missing segment instead
  of silently skipping it, tolerates post-encode metadata I/O errors, and keeps/indexes the WAV
  when it cannot be removed after a successful conversion.
- `HistoryStore.RefreshExistence` updates entries under the same lock that serializes saves.

### Recovery
- Recovery markers and metadata are advisory. A surviving output is trusted only when its PCM
  SHA-256 payload matches the partial recording's payload and its path is the canonical sibling
  `_RECOVERED.wav`; arbitrary, external, damaged or unrelated same-length files are rebuilt from
  the original audio.
- Unrecoverable markers are bound to the partial file's length, folder scans never follow
  junctions, and `RecoverAll` is serialized (process and cross-session) so a startup scan and
  Retry cannot duplicate output. A crash-recovery scan no longer blocks starting a recording.

### Settings
- Explicit Missing/Readable/AccessDenied/SharingViolation/IoError states distinguish a missing
  document from an inaccessible one. An unreadable primary with a valid backup keeps the backup
  in memory but stays read-only, so the newer primary can never be overwritten by older state.
- A damaged primary is never rotated into the last valid backup, a settings file that appears
  after a defaults load is not overwritten, and access restored inside the same window is
  re-read instead of discarded.

### Performance
- Language packs load lazily (English plus the active language); Media Foundation probing runs
  after the window is visible. Startup now writes a phase-timing diagnostic line.
- Measured on this host: window visible in 1.5–2.6 s (settings ≈ 20 ms, language/theme ≈ 65 ms),
  managed heap ≈ 12.5 MB, tray working set ≈ 158 MB (0.0.2 baseline 170.3 MB), tray idle CPU
  0.00–0.05 s per 30 s. Real microphone acceptance is NOT VERIFIED: this host exposes no capture
  endpoint (`Core Audio error 0x80070490`).

### Tests
- 141 xUnit tests (97 Core + 44 Integration), including reparse refusal, interrupted-publication
  rollback, publish locking, recovery identity/adoption, junction scans, unreadable-primary
  settings and recovery-race coverage.
- Publish-safety suite covers 139 assertions on PowerShell 7 and Windows PowerShell 5.1.

## 0.0.3 — 2026-11-26

Review follow-up for trusted recordings.

### Fixed
- Queue overflow is no longer reported as success. Dropped audio bytes force
  `PartialSaved`, persist the dropped-byte note in History, show a warning and play the error cue
  instead of `done.wav`.
- An existing `settings.json` that cannot be read (for example a transient antivirus/backup lock)
  is no longer treated as corruption. The app keeps defaults in memory, refuses to overwrite the
  file, and tells the user to restart after fixing access; hotkeys and settings cannot be silently
  replaced.
- MP3 finalization wraps post-encode I/O in the engine error path, so an exception cannot strand
  the state machine in `Finalizing` without a completion event.
- A recovery marker whose output file is missing no longer hides the `.part`; recovery rebuilds
  the verified `_RECOVERED.wav`.
- Assets installed before manifests existed are adopted into the manifest only when byte-identical
  to the shipped file, so later updates replace them while user-modified themes, languages and cues
  remain protected.
- `Publish.ps1` now stages `THIRD-PARTY-NOTICES.md`, `LICENSE.txt` and `ThirdPartyNotices.txt`
  from the SDK, making the release package reproducible instead of relying on a manual step.

### Tests
- 127 xUnit tests (89 Core + 38 Integration), including queue-overflow outcome, locked settings
  preservation and stale recovery markers.
- Publish-safety suite covers 45 assertions, including adoption of pre-manifest assets.

## 0.0.2 — 2026-11-26

Data-safety and reliability hardening.

### Fixed
- Publishing and clean scripts no longer remove the portable runtime directory. Publishing
  builds in an isolated staging folder, validates the package, replaces only owned application
  artifacts, and preserves `config/`, recordings, journals, partial files, custom themes,
  language overrides, cues and unknown user files. Unsafe, reparse-point, source-tree and
  non-runtime targets are refused; a failed build leaves the last package untouched.
- Writer ownership: a stop that times out no longer finalizes concurrently. The session stays
  blocked with a truthful error until the writer exits, then finalizes under the engine gate.
- Recovery no longer deletes zero-byte files referenced by metadata, writes through an owned
  temporary path, adopts verified output after a crash, tolerates short stream reads, keeps
  journal entries for disconnected volumes, distinguishes transient from permanent failures,
  and exposes a retry action.
- Recording outcomes are an explicit contract (`Success`, `PartialSaved`, `FailedRecoverable`,
  `FailedNoOutput`, `CancelledNoAudio`); `done.wav` is played only for complete success,
  `error.wav` for partial/failed, and stale session events are ignored.
- MP3 is encoded through a temporary file, validated with Media Foundation for duration and
  decodability, published without overwrite, and the WAV is kept on any failure.
- History saves are serialized with their write; latest-existing selection skips missing files
  and uses file time; a bounded folder rescan indexes existing recordings without deleting data.
- Hotkey editor warns when Ctrl+Alt is AltGr on international layouts.

### Tests
- Publish-safety script covers 42 protected-file/target scenarios with SHA-256 checks.
- 123 xUnit tests (87 Core + 36 Integration), including writer timeout, partial outcomes,
  recovery transaction/idempotency, MP3 validation and audio-signal fidelity.

### Release
- Self-contained win-x64 package with documentation and screenshots, no `config/`, recordings,
  logs or recovery artifacts; SHA-256 checksums and a clean-extraction recording test.

## 0.0.1 — 2026-11-26

Initial autonomous build.

### Added
- Portable .NET 10 WPF application with a single window: Recorder, History, Keys, Settings.
- WASAPI shared-mode capture, raw mode preferred; float/PCM conversion, channel mapping and
  resampling on the writer thread.
- WAV PCM 16-bit / 24-bit / Float32 output and Media Foundation MP3 (probed; hidden when
  unavailable).
- Explicit recording state machine with serialized commands; concurrent start/stop protection;
  pause/resume.
- Bounded audio queue with overflow reporting; 1.5 s fsync checkpoints; 2 GiB segment rollover.
- Crash-safe `.wav.part` + `.recovery.json` pipeline; startup scan builds verified
  `_RECOVERED.wav` files and indexes them in History; damaged parts are kept for diagnosis.
- Atomic, portable settings with schema version, backup, per-section tolerant load, import,
  export, reset, and read-only detection.
- OBS-like filename presets, persistent counters, collision suffixes, date folders, safe names,
  and atomic reservation.
- Microphone test with a live level meter and an automatic no-signal warning during recording.
- 33 UI languages (English, Russian, 29 European languages, Simplified Chinese, Japanese, Korean,
  Arabic with right-to-left layout), switchable at runtime, embedded packs with `languages/`
  overrides.
- Editable recording-folder path in Settings (absolute, relative, or `%ENV%` variables), with
  create-on-apply and inline invalid/unavailable feedback.
- Notification output-device selector using WASAPI shared playback, with a WaveOut fallback.
- Persistent History with playback, open/reveal/copy, inline rename, and Recycle Bin deletion.
- Native `RegisterHotKey` + `MOD_NOREPEAT` hotkeys with a transactional editor for eight actions,
  global/local scopes, reserved-combination checks, and capture-safe suspension.
- Default `Ctrl+Alt+L` opens and plays the last recording in the Windows default player
  (schema v2 migration assigns it to existing settings).
- Tray icon with idle/recording/paused/error variants and a full context menu.
- Original start/done/error cues with 35 % default volume, per-event toggles and previews;
  missing assets never block recording.
- Wintage Golden Default theme and all original Wintage theme files; live theme switching and
  English/Russian runtime language switching.
- Multi-resolution application icon and tray derivatives from the original `media/favicon.png`.
- 103 xUnit tests covering core behaviour and end-to-end recording/recovery with a fake device.
- Build, test, publish, and clean scripts; README, architecture, acceptance matrix, roadmap.

### Fixed
- Global **Show / hide window** shortcut did nothing: the view-model event was never
  connected to the window. Wired it, and an unfocused visible window is now brought forward
  instead of being hidden.
- Silent recordings: `BoundedAudioQueue` wrote at the read index instead of a separate write index,
  corrupting queued audio under load. A dedicated write cursor and regression tests fixed the
  end-to-end signal path; a live microphone recording now contains non-zero samples.

### Notes
- FLAC and Opus are deliberately not offered: no codec is bundled and unsupported formats must
  not appear in the production UI.
- Version requested by the operator: 0.0.1.
