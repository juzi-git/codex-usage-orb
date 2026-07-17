<p align="center">
  <img src="assets/screenshots/orb.png" width="140" alt="Codex Usage Orb showing the remaining usage percentage">
</p>

<p align="center">
  <strong>English</strong> | <a href="README_CN.md">简体中文</a>
</p>

# Codex Usage Orb

A lightweight, draggable desktop orb that shows your remaining Codex usage at a glance.

> [!IMPORTANT]
> This is an unofficial community project. It is not affiliated with or endorsed by OpenAI.

## Features

- Transparent, always-on-top desktop orb
- Freely draggable with the mouse
- Refreshes local Codex rate-limit data every 5 seconds
- Animated liquid level and remaining percentage
- Dynamically adjustable from 50 to 300 px
- Green, blue, purple, and orange themes, plus a custom color picker
- English by default, with an English/Chinese language switch in settings
- Persists the size, color, and window position
- Reads local data only and makes no network requests

## Screenshots

| Compact orb | Usage details |
| --- | --- |
| <img src="assets/screenshots/orb.png" width="130" alt="Compact usage orb"> | <img src="assets/screenshots/usage-details.png" width="420" alt="Usage details tooltip"> |

### Context menu

<p align="center">
  <img src="assets/screenshots/context-menu.png" width="303" alt="English context menu with refresh, details, appearance, startup, and quit actions">
</p>

### Appearance settings

![Appearance settings with size, color, and language controls](assets/screenshots/appearance-settings.png)

The context-menu screenshot shows the default English interface. The other screenshots show the optional Chinese interface.

## Platform Support

| Platform | Status | Notes |
| --- | --- | --- |
| Windows 10/11 | Tested | A source build script is available. Publish prebuilt binaries through GitHub Releases. |
| macOS 11+ | Source available | Requires compilation on macOS with Xcode Command Line Tools. The current source has not yet been validated on macOS hardware. |

## How It Works

Codex writes structured rate-limit information to recent session files under its local data directory. The orb reads only the tail of the most recently updated session files and extracts the `rate_limits` field.

When multiple limit windows are present, the main percentage shows the lowest remaining value. This prevents a short-term limit from being overlooked when the weekly limit still has capacity.

### Privacy

- Does not read `auth.json`
- Does not collect conversation content
- Does not upload telemetry or usage data
- Does not make network requests

## Quick Start

### Windows

The current local build is generated at `dist/windows/CodexUsageOrb.exe`.

To build it from source:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Then run:

```powershell
.\dist\windows\CodexUsageOrb.exe
```

See [Windows documentation](docs/windows.md) for requirements and controls.

### macOS

Install Xcode Command Line Tools, then run:

```bash
xcode-select --install
chmod +x scripts/build-macos.sh
./scripts/build-macos.sh
open dist/macos/CodexUsageOrb.app
```

See [macOS documentation](docs/macos.md) for build requirements and first-launch notes.

## Controls

- Left-click and drag: move the orb
- Hover: view limit windows and reset times
- Double-click: refresh immediately (Windows)
- Right-click: refresh, change appearance, or quit
- Appearance settings: resize from 50 to 300 px and choose a theme or custom color
- Language setting: switch the orb, tooltip, menus, and settings between English and Chinese

## Repository Structure

```text
codex-usage-orb/
├── assets/screenshots/    # README images
├── docs/                  # Platform-specific documentation
├── src/windows/           # Windows WPF source
├── src/macos/             # macOS AppKit source and Info.plist
├── scripts/               # Platform build scripts
├── dist/                  # Local build output (ignored by Git)
├── README.md              # English documentation
└── README_CN.md           # Simplified Chinese documentation
```

## Known Limitations

- Usage updates depend on Codex writing a new local rate-limit status. The display is near-real-time rather than a direct account API feed.
- A future Codex update may change the local session format and require a parser update.
- The macOS implementation must still be compiled and tested on a Mac.

## Building and Contributing

Keep platform-specific code under `src/` and generated artifacts under `dist/`. Before submitting changes, build the affected platform and verify usage parsing, resizing, dragging, appearance persistence, and the unavailable-data state.

## License

This project is licensed under the [MIT License](LICENSE).
