# DeskHush

> A focused Windows desktop utility for popup rules, Explorer context-menu entries, and common logon startup locations.

[简体中文](README.md) · [Latest release](https://github.com/sanrokamlan-prog/DeskHush/releases/latest) · [Safety and recovery](docs/safety-and-recovery.md) · [Architecture](docs/architecture.md)

[![CI](https://github.com/sanrokamlan-prog/DeskHush/actions/workflows/ci.yml/badge.svg)](https://github.com/sanrokamlan-prog/DeskHush/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/sanrokamlan-prog/DeskHush?display_name=tag)](https://github.com/sanrokamlan-prog/DeskHush/releases/latest)
[![License](https://img.shields.io/github/license/sanrokamlan-prog/DeskHush)](LICENSE)

DeskHush is an open-source Windows utility. It can locate difficult popup targets through a desktop picker or background window recording, while also managing Explorer context-menu entries and common logon startup locations. It installs no driver, injects no code into other processes, and does not restart Explorer automatically. Registry and Startup-folder changes retain recovery state, and restore operations refuse to overwrite conflicts.

![DeskHush popup management](docs/images/popup-management.png)

## Download and run

The current release target is **Windows 10/11 x64**.

1. Open [Releases](https://github.com/sanrokamlan-prog/DeskHush/releases/latest) and download `DeskHush-v*-win-x64.zip` plus `SHA256SUMS.txt`.
2. Verify the archive's SHA-256, then extract it to a stable writable directory such as `D:\Apps\DeskHush`. Do not run it inside the archive.
3. Start `DeskHush.exe`. The release is self-contained and does not require a separate .NET runtime installation.
4. Keep “Minimize to tray when closing” enabled for continuous popup handling. Enable “Start with Windows” if DeskHush should start after sign-in.

> Public builds are currently unsigned, so Windows SmartScreen may report an unknown publisher. Only proceed with a file downloaded from this repository after verifying its checksum.

Closing the window keeps DeskHush in the notification area by default. Double-click the tray icon to restore it; use **Exit** from the tray menu to stop it completely.

## Current capabilities

| Area | Implemented behavior | Mechanism |
| --- | --- | --- |
| Popup rules | Current-window list, desktop picker, and background window recording; match process name/path, class, and title; close or hide action | Automatically outline detectable windows in a desktop capture; observe `EVENT_OBJECT_SHOW`, then apply `WM_CLOSE` or hide |
| Context menus | File, folder, folder background, drive, desktop, and all-filesystem-object locations; static verbs and shell extensions; HKCU/HKLM and 32/64-bit views | `LegacyDisable` for static verbs; per-user `Shell Extensions\Blocked` for handlers |
| Startup entries | Current-user/all-users `Run` and `RunOnce`, user/common Startup folders, and applicable Task Manager approval state | Preserve registry values and `StartupApproved`; move Startup-folder items into recovery storage |
| Background mode | Single instance, tray lifecycle, close/minimize to tray | `DeskHush.exe --background` |
| Start with Windows | Start minimized to the tray after the current user signs in | A DeskHush-owned HKCU `Run` value |
| Update notice | Quiet daily release check, with disable and manual-check controls | Read public GitHub Release metadata; open the release page only after a click |

The startup manager does **not** currently enumerate or modify scheduled tasks, Windows services, drivers, or other auto-start extension points. A reserved model enum is not an implemented feature.

## Popup rules

Targets can be selected from the current-window list, by freezing the virtual desktop and clicking an automatically outlined window, or from a background record of newly shown windows. The latter two paths are useful for windows that disappear before a manual list refresh.

The desktop picker hides the DeskHush window and creates one in-memory capture of the complete virtual desktop. The image is never written to disk and is released when the picker closes. Window recording retains process, title, class, position, and size metadata for up to 500 newly shown windows. Records can be cleared explicitly, remain in process memory only, are discarded on exit, and are never uploaded.

A rule must identify a specific process by name or full path and also specify a window title or class. Rules that target protected system processes are rejected. Matching is case-insensitive; window classes use wildcards, while titles support contains, exact, wildcard, and timeout-bounded regular expressions.

The **Close** action posts a normal close message and can be rejected by the target application. **Hide** leaves the process running; the target application may need to show the window again, or be restarted, to recover it. New rules default to Hide. If a recorded window has not produced a title yet, its rule remains disabled until the matching scope is reviewed.

DeskHush stores hit counts and the most recent hit time locally. It does not upload window metadata.

## Context-menu manager

DeskHush enumerates current-user and machine registrations in both 32-bit and 64-bit registry views.

- Static verbs are toggled with the `LegacyDisable` marker.
- Shell-extension handlers are toggled by CLSID through the per-user `Shell Extensions\Blocked` list, which can override both per-user and machine registrations.
- DeskHush records the marker state that existed before its first change and restores that baseline.
- Explorer may cache context menus. DeskHush never terminates `explorer.exe`; reopen Explorer windows or sign out if a change is not immediately visible.

Machine-wide static verbs generally require elevation. DeskHush runs as the current user by default and offers an explicit elevated restart from Settings.

## Startup manager

DeskHush also reads the `StartupApproved` state used by Windows Task Manager for persistent `Run` entries and Startup-folder items. Disabling an originally enabled registry entry captures its value type, content, and any applicable approval state before removing the live value. Enabling an entry that Task Manager had disabled preserves the complete approval value and restores it when the entry is switched off again. Startup-folder items use the same approval-state protection, while their files and directories are moved into DeskHush's recovery directory. `RunOnce` has no reliable one-to-one approval mapping, so it is managed only through its source value and never borrows a same-name `Run` state.

Restore verifies the result and refuses to overwrite a registry value or file that appeared at the original location.

Machine-wide `Run`/`RunOnce` entries and the common Startup folder require elevation. The startup-manager page controls other applications; the Start with Windows setting only controls DeskHush itself.

## Update notice

DeskHush checks public GitHub Release metadata at most once per day by default. A newer version is shown in Settings and as a notification-area notice; the release page opens only after a click. DeskHush never downloads, installs, or replaces itself. Automatic checks can be disabled while manual checks remain available.

The request carries no GitHub credentials, rules, or window data, but a normal HTTPS request exposes the public IP address and installed DeskHush version to GitHub. A failed check does not affect popup handling, recording, or background startup.

## Safety model

- Out-of-context WinEvent observation: no process injection and no third-party module loading.
- In-memory desktop captures and window records: no screenshot files, persistent history, or uploads.
- Scoped popup rules and a protected-system-process denylist.
- Recovery state persisted before reversible registry or file changes.
- Conflict and concurrent-change checks instead of silent overwrite.
- Current-user execution by default, with elevation requested only for machine scope.
- Local configuration only; no telemetry, cloud sync, or rule download feature. Optional update checks read public Release metadata only.

These controls are not a replacement for a restore point, registry export, or system backup. Read [Safety and recovery](docs/safety-and-recovery.md) before changing critical software entries.

## Local data and removal

State is stored under `%LOCALAPPDATA%\DeskHush` by default:

```text
settings.json
context-menu-state.json
startup-state.json
disabled-startup\
```

Desktop captures and window records are transient and therefore do not appear in this directory.

Do not delete this directory while entries remain disabled. Restore the entries you want to keep, disable DeskHush's own Start with Windows setting, exit from the tray, remove the program directory, and only then remove the data directory after verifying recovery.

## Build and test

Requirements: Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet restore DeskHush.sln
dotnet build DeskHush.sln -c Release --no-restore
dotnet run --project tests/DeskHush.Tests/DeskHush.Tests.csproj -c Release
```

Official archives are not packaged on a developer workstation. Updating the project version and pushing the matching tag (for example, `v0.2.0`) runs the [Release workflow](.github/workflows/release.yml) on GitHub Actions. It performs strict build and tests, creates the self-contained x64 ZIP plus `SHA256SUMS.txt`, uploads both to the matching Release, and retains the same workflow artifact for 14 days.

To reproduce the same pipeline locally when needed:

```powershell
./scripts/build-release.ps1 -Version 0.2.0
```

The console test runner has no third-party test-framework dependency. It covers the bounded recording queue, retry schedule, capacity/deduplication, release-version comparison, rule logic, and persistence. Its Windows enumeration smoke tests are read-only.

## Architecture and contribution

`DeskHush.App` owns WPF UI, tray, single-instance behavior, and lifecycle; `DeskHush.Core` contains models, interfaces, matching, and settings; `DeskHush.Windows` adapts Win32 events, the registry, and Startup folders. See [Architecture](docs/architecture.md) for the dependency and recovery flows.

Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting code. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Disclaimer

DeskHush modifies registry values and Startup-folder content selected by the user. Review each target and keep backups. The software is provided under the MIT License “as is,” without warranty.

## License

[MIT License](LICENSE) © 2026 DeskHush contributors.
