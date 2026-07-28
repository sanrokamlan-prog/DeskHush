# Changelog

All notable changes to DeskHush are documented in this file.

## [0.2.0] - 2026-07-29

### Added

- In-memory virtual-desktop capture with automatic top-level window outlines and click-to-select rule creation.
- Background window recording for newly shown top-level windows, including process, title, class, position, and size metadata.
- One-click popup-rule creation from recorded windows, with a bounded 500-record in-memory history and an explicit clear action.
- Optional daily GitHub Release check with manual checking, settings/tray notices, and browser-only handoff.
- Multi-resolution application icon shared by the executable, WPF window, and notification-area icon.

### Changed

- Expanded the popup workflow so targets can be selected from the current window list, the desktop picker, or recent window records.
- Reworked the recorder around a bounded fixed-consumer queue, absolute metadata retries, clear epochs, and serialized hook lifecycle.
- New rules default to the non-destructive Hide action; titleless recorded windows create disabled rules for review.
- Release archives and checksums are produced by the tag-triggered GitHub Actions workflow.

### Privacy

- Desktop captures are never written to disk, and recorded window metadata is neither persisted across application exits nor uploaded.
- Update checks read public release metadata without GitHub credentials and can be disabled.

## [0.1.0] - 2026-07-27

### Added

- Rule-based popup blocking with close and hide actions.
- Reversible Windows context-menu management.
- Reversible startup-entry management.
- Windows Task Manager `StartupApproved` status detection and exact state restoration.
- Tray background mode and per-user start-with-Windows support.
- Single-instance handoff for elevated restarts and stale self-start registration detection.
- Serialized settings persistence with exit-time flush and visible write failures.
- Unified system-process protection for popup rules and serialized context-menu operations.
- Self-contained Windows x64 release packaging with SHA-256 checksums.

[0.2.0]: https://github.com/sanrokamlan-prog/DeskHush/releases/tag/v0.2.0
[0.1.0]: https://github.com/sanrokamlan-prog/DeskHush/releases/tag/v0.1.0
