# Safety and Recovery

DeskHush is designed to make selected changes reversible, but it is not a full system-backup product. This document explains what changes, what is retained, and how to remove the application without discarding recovery data.

## Before the first change

1. Download only from the project's GitHub Releases page and verify the SHA-256 listed in `SHA256SUMS.txt`.
2. Extract DeskHush to a stable directory. Moving the executable later makes its saved Start-with-Windows command stale until that setting is toggled off and on again.
3. Create a Windows restore point or export the relevant registry branches before changing business-critical shell or startup entries.
4. Test popup rules with **Hide** before **Close**, especially when the target window may contain unsaved work.
5. Start with current-user entries. Use the elevated restart only when a selected machine-wide item actually requires it.

Unsigned public builds may trigger SmartScreen. A matching checksum proves that the downloaded file matches the published artifact; it does not make an untrusted download safe or replace code signing.

## What DeskHush changes

| Action | Live change | Recovery data | Important caveat |
| --- | --- | --- | --- |
| Close popup | Posts `WM_CLOSE` to the matched window | Rule and hit metadata only | The target may close with unsaved data or ignore the message |
| Hide popup | Hides the matched window | Rule and hit metadata only | The target application may need to show it again or restart |
| Disable static context verb | Adds `LegacyDisable` | Original marker presence in `context-menu-state.json` | Explorer may cache the old menu |
| Disable shell extension | Adds the CLSID to the current-user Blocked list | Original marker presence in `context-menu-state.json` | One CLSID block may affect registrations visible in both scopes |
| Disable `Run`/`RunOnce` value | Removes an enabled active value; mapped `Run` items can restore a saved Task Manager-disabled state | Exact value kind/content and any applicable `StartupApproved` state in `startup-state.json` | A same-name value or changed mapped approval state blocks automatic restore |
| Disable Startup-folder item | Moves an enabled item under `disabled-startup`, or restores a saved Task Manager-disabled state | Original path, disabled path, item type, and `StartupApproved` state | A new item or changed approval state blocks automatic restore |
| Enable DeskHush at sign-in | Writes HKCU `Run\DeskHush` with `--background` | Current setting in `settings.json` | Moving/deleting the executable leaves a stale command |

DeskHush does not modify scheduled tasks or services, install a driver, inject into another process, or automatically restart Explorer.

`StartupApproved` is a Windows implementation detail rather than a stable public management API. DeskHush preserves the complete value, treats unknown or malformed states conservatively as disabled, and refuses to overwrite an externally changed value. A future Windows release may require this mapping to be updated.

## Preferred recovery path

Use DeskHush itself whenever possible:

1. Open DeskHush from the tray.
2. For each disabled right-click or startup entry that should be restored, switch it back on.
3. Read the status message and refresh the list. Do not assume a failed operation completed.
4. For machine-wide entries, use “以管理员身份重新启动” and repeat only the required restore.
5. Reopen Explorer windows or sign out if a restored context menu is not immediately visible.

Recovery stops rather than overwriting a conflicting value or file. If this happens, inspect both the new live item and DeskHush's saved copy before deciding which one to keep. Do not delete either side just to make the toggle succeed.

## Local recovery state

The default state directory is `%LOCALAPPDATA%\DeskHush`:

```text
settings.json
context-menu-state.json
startup-state.json
disabled-startup\<stable-id>\<original-name>
```

Use the Settings page's “打开目录” action to open the actual directory. `DESKHUSH_DATA_DIR` can override it for development and QA runs; do not mix production and test state.

Treat `startup-state.json` and `disabled-startup` as one recovery set. Copying one without the other can leave a record with no file, or a file with no record. Keep the entire directory backed up while any item is disabled.

The JSON files may contain executable paths, command lines, publisher names, and window rule text. They remain local in the current implementation, but should still be redacted before attaching them to a public issue.

## If DeskHush no longer starts

1. Do not delete `%LOCALAPPDATA%\DeskHush`.
2. Copy the whole directory to a separate backup location before troubleshooting.
3. Reinstall or extract a trusted DeskHush release and launch it against the same Windows user profile.
4. Restore current-user items first. Relaunch explicitly as administrator only for recovery records that reference machine scope or the common Startup folder.
5. If the state file is reported invalid, leave it untouched and open a support issue with sensitive paths redacted. Do not edit IDs or paths by guesswork.

`startup-state.json` is human-readable, and disabled Startup-folder items remain ordinary files or directories. Manual registry recovery can lose the original value kind or expansion semantics, so it should be a last resort performed from a verified backup by someone familiar with Registry Editor.

## Context-menu refresh

Explorer and applications can cache shell registrations. After a successful change:

1. close and reopen the affected Explorer window;
2. test a new Explorer window;
3. if still unchanged, save work and sign out/in.

DeskHush deliberately does not kill `explorer.exe`. Avoid terminating Explorer from an elevated shell unless you understand the session and unsaved shell state involved.

## Popup-rule recovery

Popup actions do not create a restorable snapshot of a target application's state.

- If a rule hides the wrong window, pause popup blocking or disable the rule, then use the target application's own reopen/show command. Restarting the target program is the fallback.
- If a rule closes the wrong window, DeskHush cannot restore unsaved content. Use the target application's recovery feature.
- Narrow the rule with an exact process path, window class, and title before enabling it again.

Protected-process checks reduce risk but cannot identify every important third-party window. User review remains required.

## Safe removal

Do not uninstall by deleting the program and state directories together. Use this order:

1. Restore every right-click menu and startup item you want to keep enabled.
2. Confirm the lists show the intended states after refresh.
3. Turn off DeskHush's own “开机启动” setting.
4. Exit DeskHush from the tray menu; closing the window alone may leave it running.
5. Delete the extracted program directory.
6. Reboot or sign in again and verify startup behavior and context menus.
7. Only after verification, archive or delete `%LOCALAPPDATA%\DeskHush`.

If disabled items are intentionally meant to remain disabled after DeskHush is removed, archive the entire state directory anyway. Future manual recovery depends on those records and files.

## Escalation and reports

For a normal recovery problem, open a GitHub issue with the DeskHush version, Windows version, entry scope/type, exact status message, and redacted state excerpt. For a vulnerability or a bug that can corrupt unrelated registry/file state, use the private process in [SECURITY.md](../SECURITY.md).
