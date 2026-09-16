# Windows

The Windows version is a lightweight WPF application that uses the .NET Framework components included with Windows. No third-party dependencies or installer are required.

## Requirements

- Windows 10 or 11
- Codex desktop app or Codex CLI installed and authenticated with a ChatGPT account (the CLI is used to access the local app-server)
- .NET Framework 4.x system assemblies

## Build

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

The executable is written to:

```text
dist/windows/CodexUsageOrb.exe
```

## Run

Double-click the executable, or run:

```powershell
.\dist\windows\CodexUsageOrb.exe
```

## Controls

- Left-click and drag: move the orb
- Hover: view each rate-limit window and reset time
- Double-click: refresh immediately
- Right-click → Appearance Settings: change the layout, size, meter widths, and colors in real time
- Appearance Settings → Language: switch between English and Chinese
- Right-click → Launch at Startup: toggle automatic startup
- Right-click → Quit: close the application

## Appearance

- Resize the orb from 50 to 300 px
- Adjust the concentric-ring and weekly-arc widths independently from 2 to 14 px
- Switch between **Concentric rings** and **Main value + weekly arc**
- View the 5-hour allowance as the primary value and the weekly allowance as the secondary meter
- Choose separate colors for the 5-hour and weekly meters
- Use green, blue, purple, or orange presets, or open the Windows color picker for either meter
- Preview changes immediately; choose OK to save or Cancel to revert
- English is the default language; the selected language is restored on the next launch

The layout, size, both meter widths, both meter colors, language, and position are saved to:

```text
%LOCALAPPDATA%\CodexUsageOrb\settings.ini
```

## Troubleshooting

The orb first calls the local Codex app-server `account/rateLimits/read`, which is the account-level source used by the Codex desktop client. If that process cannot start or the request times out, the reader falls back to recent session-file snapshots and keeps a successful app-server value instead of replacing it with an older fallback.

If the orb displays `--`, verify that `codex.exe` is installed under `%LOCALAPPDATA%\OpenAI\Codex\bin` or set `CODEX_CLI_PATH` to its full path, then use **Refresh now**. The reader still separates multiple rate-limit scopes and supports the legacy `rate_limits` session format as a fallback.

The Windows build uses a named single-instance lock, so starting the executable repeatedly will not create additional orbs. If an older build left several processes running, close the old `CodexUsageOrb.exe` processes once in Task Manager (or run `Get-Process CodexUsageOrb | Stop-Process`) before starting the rebuilt version. Refresh and rendering exceptions are kept in:

```text
%LOCALAPPDATA%\CodexUsageOrb\logs\orb.log
```
