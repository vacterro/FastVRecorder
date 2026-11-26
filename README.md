<div align="center">
  <img src="media/favicon.png" width="104" alt="FastVRecorder logo">

  <h1>FastVRecorder</h1>

  <p><strong>One hotkey. Your voice. Nothing else.</strong></p>
  <p>A compact, portable microphone recorder for Windows.<br>
  Open → Configure → Forget → Record.</p>

  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-3e6e93?style=flat-square" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/Portable-x64-9b8049?style=flat-square" alt="Portable x64">
  <img src="https://img.shields.io/badge/Audio-WAV%20%2F%20MP3-626b52?style=flat-square" alt="WAV and MP3">
  <img src="https://img.shields.io/badge/UI-33%20languages-555555?style=flat-square" alt="33 UI languages">

  <p><a href="https://github.com/vacterro/FastVRecorder/releases"><strong>Releases</strong></a> · <a href="#quick-start">Quick start</a> · <a href="docs/USER_GUIDE.md">User guide</a> · <a href="CHANGELOG.md">Changelog</a></p>
</div>

## A look inside

<div align="center">
  <img src="media/screenshots/recorder.png" width="23%" alt="Recorder ready">
  <img src="media/screenshots/recording-live.png" width="23%" alt="Live microphone recording">
  <img src="media/screenshots/hotkey-assigned.png" width="23%" alt="Custom global and local shortcuts">
  <img src="media/screenshots/settings.png" width="23%" alt="Recording and file settings">
  <p><sub>Recorder · Live recording · Hotkeys · Settings — Golden Default theme</sub></p>
  <a href="media/screenshots">More screenshots</a>
</div>

## Features

- **Record from anywhere:** press `Alt + 0` to start or stop, even when the app is in the tray.
- **Hear it again:** `Ctrl + Alt + L` opens and plays the last recording in your default player.
- **Your audio, your files:** WAV or MP3 (when the Windows encoder is available), timestamp or incremental names, and a configurable output folder.
- **Built for interruptions:** recoverable partial files, periodic disk checkpoints and long-recording segmentation.
- **Make it yours:** configurable global and local hotkeys, sound cues, microphone selection and optional Windows autostart.
- **One compact window:** recording, history, playback, shortcuts and settings with Wintage themes and 33 UI languages.
- **Portable and private:** local settings, no installer, no required administrator rights, no cloud or telemetry.

## Quick start

1. Get a portable build from [Releases](https://github.com/vacterro/FastVRecorder/releases) when available, or build it from source below.
2. Extract the folder and run `FastVRecorder.exe`.
3. Choose your microphone and recording folder in **Settings**.
4. Press **Alt + 0** to record. Press it again to save. Close the window to keep recording controls available in the tray.

> Default: 48 kHz mono, 16-bit PCM WAV. Use **Settings → Recording → Test microphone** to verify your input signal.

## Build from source

Windows 10/11 x64 and the .NET 10 SDK are required for development.

```powershell
.\scripts\Publish.ps1
```

The self-contained portable build is generated in `dist\FastVRecorder`. No separate .NET runtime installation is required for the published build.

## Documentation

[User guide & troubleshooting](docs/USER_GUIDE.md) · [Architecture](docs/ARCHITECTURE.md) · [Acceptance & testing](docs/ACCEPTANCE.md) · [Roadmap](docs/ROADMAP.md) · [Changelog](CHANGELOG.md)

<div align="center"><sub>FastVRecorder · Simple by design.</sub></div>
