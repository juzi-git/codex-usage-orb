# macOS

The macOS version is a native AppKit implementation with no third-party dependencies.

## Current Status

The source and application bundle structure were generated in a Windows environment. Windows does not include the macOS SDK, so the Swift source, generated `.app`, and Apple code signing have not yet been validated on macOS hardware.

## Requirements

- macOS 11 or later
- Xcode Command Line Tools
- Codex desktop app or Codex CLI installed and authenticated

Install the compiler tools with:

```bash
xcode-select --install
```

## Build

From the repository root:

```bash
chmod +x scripts/build-macos.sh
./scripts/build-macos.sh
```

The application bundle is written to:

```text
dist/macos/CodexUsageOrb.app
```

Run it with:

```bash
open dist/macos/CodexUsageOrb.app
```

## First Launch

The build script creates a locally built, unsigned application. If macOS blocks it, right-click the app in Finder and select **Open**, or approve it under **System Settings → Privacy & Security**. Do not bypass system security checks or use an untrusted signature.

## Controls

- Left-click and drag: move the orb
- Hover: view each rate-limit window and reset time
- Right-click → Refresh Now
- Right-click → Appearance Settings: change the layout, size, meter widths, and colors in real time
- Appearance Settings → Language: switch between English and Chinese
- Right-click → Quit

## Appearance

The macOS version shows the 5-hour and weekly allowances together and can switch between **Concentric rings** and **Main value + weekly arc**. It supports sizes from 50 to 300 px, independent concentric-ring and weekly-arc widths from 2 to 14 px, independent colors for both meters, four presets plus the system color picker for each meter, and persistent layout, size, widths, colors, language, and window position. The reader first calls the local Codex app-server `account/rateLimits/read` and falls back to session snapshots if the CLI is unavailable. English and concentric rings are used by default.
