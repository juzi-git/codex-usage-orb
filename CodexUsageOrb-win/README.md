# Codex Usage Orb

Double-click `CodexUsageOrb.exe` to run it. No installation is required.

- Transparent, always-on-top desktop orb
- Freely draggable with the mouse
- Reads the latest local Codex rate-limit status every 5 seconds
- Animated liquid level and remaining percentage
- Dynamically adjustable from 50 to 300 px
- Green, blue, purple, and orange themes, plus a custom color picker
- Support for multiple desktop spaces and full-screen auxiliary display

## Dynamic Appearance

Right-click the orb and select **Appearance Settings...**:

- Use the slider to resize the orb from 50 to 300 px; all elements scale proportionally
- Choose a green, blue, purple, or orange preset
- Click **Custom...** to open the Windows color picker
- Changes are previewed immediately; click **OK** to save or **Cancel** to revert

The size, color, and window position are saved to `%LOCALAPPDATA%\CodexUsageOrb\settings.ini` and restored automatically the next time the app starts.

The main percentage shows the lowest remaining value among all active Codex limit windows. This prevents a short-term limit from being overlooked when only the weekly limit is considered. The app reads the latest rate-limit status from local Codex sessions every 5 seconds. It does not read `auth.json`, upload data, or make network requests.

If the orb displays `--`, complete at least one Codex conversation so Codex can write an up-to-date usage status. If a future Codex update changes the local state format, the parser may also need to be updated.

## System Requirements

Windows 10 or 11, with the Codex desktop app or Codex CLI installed and authenticated with a ChatGPT account.

## Source Code

`Program.cs` contains the complete source code. It uses the .NET Framework WPF components included with Windows and has no third-party dependencies.

After modifying the source, exit any running orb instance, right-click `build.ps1`, and select **Run with PowerShell** to regenerate `CodexUsageOrb.exe`.
