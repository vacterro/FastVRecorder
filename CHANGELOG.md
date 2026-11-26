# Changelog

All notable changes to FastVRecorder are recorded here.

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
