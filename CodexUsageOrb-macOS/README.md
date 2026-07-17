# Codex Usage Orb (macOS)

This native macOS/AppKit implementation supports:

- A transparent, always-on-top desktop orb
- Free mouse dragging
- Reading the latest local Codex rate-limit status every 5 seconds
- An animated liquid level and remaining percentage
- Dynamic sizing from 50 to 300 px
- Green, blue, purple, and orange themes, plus the macOS system color picker
- Persistent position, size, and color settings
- Multiple desktop spaces and full-screen auxiliary display

The app reads only the `rate_limits` field near the end of recent session files under `~/.codex/sessions`. It does not read `auth.json`, upload data, or make network requests.

## Build Requirements

- macOS 11 or later
- Xcode Command Line Tools (install them by running `xcode-select --install` in Terminal)
- An installed and authenticated Codex desktop app or Codex CLI

## Build the `.app`

Open this directory in a macOS terminal and run:

```bash
chmod +x build-macos.sh
./build-macos.sh
open build/CodexUsageOrb.app
```

The generated application will be located at:

```text
build/CodexUsageOrb.app
```

## First Launch

The build script creates a locally built, unsigned application. If macOS blocks it on first launch, right-click the app in Finder and select **Open**, or approve it under **System Settings → Privacy & Security**. Do not use untrusted signatures or bypass system security checks.

## Controls

- Left-click and drag: move the orb
- Hover: view each limit window and its reset time
- Right-click → **Refresh Now**
- Right-click → **Appearance Settings**: change the size and color in real time
- Right-click → **Quit**

## Current Verification Scope

The source code and application bundle structure were generated in a Windows environment. Windows does not include the macOS SDK, so `swiftc`, the generated `.app`, and Apple code signing could not be tested here. Run `build-macos.sh` on a Mac to perform the final compilation.
