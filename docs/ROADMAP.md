# Roadmap

0.0.4 is functionally complete for the captured handoff and the follow-up reliability audit
(SAIHANDOFF-3). Everything below is deliberately deferred, not accidentally missing.

## Candidate next steps

1. **Optional history polish** — search, date filter, sorting presets, pinning, a larger inline
   editor. The persistent index already supports these without schema changes.
2. **Waveform / trim preview** — would require a decoder and a rendering control; kept out of the
   compact recorder scope.
3. **FLAC / Opus** — only if a codec that ships with Windows (or an approved bundled codec) is
   confirmed. The UI must keep hiding formats whose probe fails.
4. **Toast notifications** — replace the tray balloon with a real Windows toast if the project
   acquires a packaged identity; the current balloon is dependency-free.
5. **RF64** — only if 2 GiB segments prove inconvenient; segmentation already keeps RIFF safe.
6. **Per-event volume sliders** — the model already stores per-event volume; expose it in the
   collapsed notification details.
7. **Compact mode** — a denser layout for small screens; the current 380 × 460 layout is already
   compact.
8. **Additional languages** — the embedded JSON packs and `languages/` overrides are isolated in `LanguagePacks`; no engine changes are needed.
9. **Installer** — optional MSIX/Inno Setup wrapper; the portable folder remains the primary
   distribution.
10. **Manual hardware matrix** — capture testing on USB/Bluetooth microphones, network shares,
    removable drives, sleep/resume, and secure-desktop boundaries.

## Non-goals

- Desktop/system audio capture, streaming, cloud sync, accounts, telemetry.
- Audio editing, effects, normalization, silence removal, VAD.
- Multiple simultaneous recordings or multiple visible windows.
- A background service or a database.
