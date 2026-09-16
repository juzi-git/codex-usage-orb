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
- Refreshes the Codex account rate-limit snapshot about every 15 seconds
- Animated liquid level and remaining percentage
- Shows the 5-hour and weekly remaining allowances at the same time
- Two switchable layouts: concentric rings or main value with a weekly arc
- Dynamically adjustable from 50 to 300 px
- Independently adjustable concentric-ring and weekly-arc widths from 2 to 14 px
- Independent 5-hour and weekly colors, each with presets and a custom color picker
- English by default, with an English/Chinese language switch in settings
- Persists the layout, size, meter widths, colors, language, and window position
- Uses the local Codex app-server for current account data; the orb itself never handles credentials or sends requests directly

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

The orb asks the locally installed Codex app-server for `account/rateLimits/read`, the same account-level source used by the Codex desktop client. If the CLI/app-server is unavailable, it falls back to parsing the tail of recent session files and extracts their `rate_limits` snapshots. The fallback can be older than the account panel, so the tooltip timestamp identifies when the displayed value was obtained.

The 5-hour window is the primary value and the weekly window is shown as a secondary ring or arc. Windows are identified by their reported duration, with shortest/longest-window fallback handling if the field order changes. When Codex writes more than one rate-limit scope (for example, a model-specific weekly scope), the reader keeps scopes separate, prefers a scope that contains a 5-hour window, and merges the latest values for each window within that scope.

### Privacy

- Does not read `auth.json`
- Does not collect conversation content
- Does not upload telemetry or usage data itself
- Does not read or transmit credentials; the local Codex app-server performs any authenticated network request

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
- Appearance settings: switch layouts, resize from 50 to 300 px, adjust meter widths from 2 to 14 px, and choose separate colors
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

- Usage updates depend on the locally installed Codex app-server being available. If it cannot be started, the display falls back to the latest local session snapshot.
- A future Codex update may change the local session format and require a parser update.
- The macOS implementation must still be compiled and tested on a Mac.

## Building and Contributing

Keep platform-specific code under `src/` and generated artifacts under `dist/`. Before submitting changes, build the affected platform and verify both usage windows, both layouts, resizing, dragging, appearance persistence, and unavailable-data states.

## License

This project is licensed under the [MIT License](LICENSE).
