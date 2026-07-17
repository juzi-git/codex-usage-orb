# Windows

The Windows version is a lightweight WPF application that uses the .NET Framework components included with Windows. No third-party dependencies or installer are required.

## Requirements

- Windows 10 or 11
- Codex desktop app or Codex CLI installed and authenticated with a ChatGPT account
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
- Right-click → Appearance Settings: change the size and color in real time
- Appearance Settings → Language: switch between English and Chinese
- Right-click → Launch at Startup: toggle automatic startup
- Right-click → Quit: close the application

## Appearance

- Resize the orb from 50 to 300 px
- Choose a green, blue, purple, or orange preset
- Open the Windows color picker for a custom accent color
- Preview changes immediately; choose OK to save or Cancel to revert
- English is the default language; the selected language is restored on the next launch

The size, color, and position are saved to:

```text
%LOCALAPPDATA%\CodexUsageOrb\settings.ini
```

## Troubleshooting

If the orb displays `--`, complete at least one Codex conversation so Codex can write an up-to-date usage status. If the value remains unavailable after a Codex update, the local session format may have changed.
