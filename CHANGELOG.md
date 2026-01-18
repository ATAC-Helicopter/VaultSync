# Changelog
## [1.3.2] - 18.01.2026
### Added
- Cross-machine metadata store (`.vaultsync/meta/`) with portable project/snapshot/backup records and external IDs.
- Metadata sync controls (global + per-destination), manual refresh, and review dialog.
- Metadata backfill options with per-destination force-export toggle.
- macOS rsync bundling (arch-specific) plus Settings hint when rsync is missing/too old.
- Archive upload auto-tuning per destination (small probe file).
- Toggle to enable/disable parallel archive uploads.
- "What's new" popup shown once per version on first launch after updating.
- Editable `docs/WHATS_NEW.md` content for the "What's new" popup.
### Changed
- Auto-imported projects now advise restore only when imported history is newer.
- Manual per-project backups can run concurrently (unless backup-all is active).
- Drive health probe deferred to reduce startup impact.
- Destination probe tracks effective path/read-only status.
- Backups page right panel now uses expandable project headers with clearer stats.
- Removed sample “default” projects when no real projects exist. (thanks to King_Hippo for repùrting)
- Scroll layout now scales more reliably at higher DPI. (thanks to King_Hippo for reporting)
- Docs updated to cover new features and macOS release flow.
- macOS NFS auto-mount is disabled; pre-mounted paths are required instead.
- Archive upload auto-tune now defaults to off, with a fixed buffer fallback.
- SMB archive uploads use a smaller buffer and avoid parallel writers by default.
### Fixed
- Fixed localization coverage across all languages (including backup progress/status keys).
- Arabic UI font fallback now uses bundled Noto Sans + Noto Sans Arabic to avoid missing glyphs.
- Metadata import handles locked/missing stores (temp copy with WAL/SHM, schema ensure).
- Manual/auto metadata refresh now updates UI lists immediately.
- Backup retention and cleanup now respect destination paths and skip unrelated directories; interrupted backups are
  cleaned safely.
- Backup status cards no longer duplicate speed/ETA, support cancelling/deleting states, and avoid auto-scroll 
  jumps.
- Dashboard storage totals, per-project segments, and donut tooltips now match actual stored data.
- Backups page right panel/history styling cleaned up with clearer hierarchy.
- Toast notifications no longer render a duplicated band.
- macOS mounts now use a user-writable root, redact SMB passwords, validate SMB/NFS mounts, and report permission
  errors instead of crashing.
- macOS/Linux free-space checks now use statvfs and avoid false readings on unmanaged mounts.
- Destination tests use unique probe files to avoid repeated "file exists" warnings.
- Archive upload auto-tune now times out quickly and can be disabled in Settings.
- Backup storage usage card now preserves the last known usage when the target is temporarily unavailable.
- Archive upload progress now stays responsive on slow links and uses longer stall timeouts.
- Upload status now shows "Finalizing" after 100% instead of "Waiting for network".
- Retention cleanup now normalizes cross-platform backup paths to avoid false "not found" logs.
- Backup cancellation now shows a cancelling state and avoids failed notifications after cleanup.
- macOS fullscreen now falls back to maximized to avoid a crash during the native fullscreen transition.
- macOS SMB auto-mount now respects subfolder paths (e.g., `//host/share/Dev`) for backups and metadata import.

## [1.2.3] - 2026-01-07
### Added
- Configurable update check interval in Settings -> Advanced.
- Manual "Check for updates now" action for on-demand update checks.
- Roadmap outline for upcoming features and priorities.
- Active backup cards now show explicit stages (preparing, hashing, backing up, compressing, uploading).
- Snapshot hashing now reports progress and ETA during backups.
- Active backup detail line now shows the current file name plus files moved/total and speed.
- Update banner actions to skip a version or close the banner.
- Persisted skipped update tag to suppress a specific release.
- Localized copy-stage strings and backup status keys across all languages.
- Localized "No snapshots yet" and time-since strings on the Projects page.
### Changed
- Backup compression now defaults to off for new installs.
- Update checks now expose richer diagnostic logging (candidates, decisions, errors).
- Settings and log console buttons now use unified action styles.
- Log console window now matches app card styling and layout.
- Active backup card layout refreshed with clearer status/ETA and staging.
- Active backup phases now reset the progress bar between hashing and copy phases.
- Copy phase now reports estimated file counts and copy speed in MB/s.
- Copy phase now derives progress from destination file sizes for steadier percentages.
- Copy progress sampling now batches file checks to reduce stalls on large backups.
- Backup snapshots now defer hashing until after data is copied to speed up the copy phase.
- Auto-backup runs now parallelize projects for faster completion.
- Robocopy thread count now scales with CPU cores for higher throughput.
- Active backup cards now show live elapsed time per phase.
- Backup/mount steps now emit detailed console logs for destinations and network mounts.
- Copy progress now logs periodic file/percent/speed updates in the console.
- Robocopy progress now feeds ETA/percent lines to the UI when file-size scanning is slow.
- Robocopy output now logs periodic progress/file hints to the console.
- Copy phase now surfaces "robocopy" activity even before file sizes start reporting.
- Backup ETA helper text now localizes across supported languages.
- App settings writes now retry with a temp file to avoid crashes during concurrent saves.
- Update checks now use ETag caching and a single release page to reduce rate-limit pressure.
- Network share backups now prefer rsync delta when available and tune robocopy for network paths.
### Fixed
- Update banner now clears when no newer release is available, preventing stale "update available" states.
- Patch installs now shut down cleanly without triggering the "still running" tray notification.
- Patch helper relaunch no longer fails due to an invalid app manifest XML header.
- Language switching now loads legacy-encoded localization files correctly.
- Log console filters noisy Avalonia trace spam for layout/input/render-loop glitches.
- Manual update checks now log their progress and outcomes for troubleshooting.
- Cleaned mojibake in localized strings so non-ASCII languages render correctly.
- Replaced broken localization glyphs (bullet, separator, dismiss) to avoid � placeholders.
- Fixed garbled update language strings across non-English translations.
- Settings view no longer jumps to the top when switching language.
- Settings descriptions now wrap cleanly instead of clipping in narrow windows.
- Active backup progress bars no longer jump to 100% prematurely.
- Backup verification now runs off the UI thread to prevent completion freezes.
- Missing update status label now added across all translations.
- Projects page snapshot and health labels now localize correctly after language changes.
- Update-available notification now calls out the active update channel (stable/beta).
- Windows uninstaller now removes bundled tools under `tools`.
- Post-backup verification and hashing now run asynchronously to avoid blocking the UI.
- Update banner layout now matches the app style and groups actions cleanly.
- Installer fallback button only appears after a patch install fails.
- Project health pills now refresh on language switch.
- Projects/Settings labels now wrap instead of truncating.

## [1.2.0] - 2026-01-01
### Added
- Incremental backup mode (rsync hardlinks) toggle to keep history while only copying changes.
- New "Delta sync for large files" backup setting, with persisted config and localized UI copy.
- Bundled cwRsync client + license bundle under tools/rsync, copied into Windows publish output for zero-install delta sync.
- Crash handler that writes a crash log and shows a crash dialog with copy/open-log actions.
- In-app log console with live capture, optional disk logging, and export support via Settings -> Advanced.
- Dedicated updater window that stays visible while a patch installs and the app restarts.
### Changed
- Updater now surfaces installer downloads when patches are incompatible, enabling version skipping and beta-to-stable moves.
- Incremental backups now disable the delta-sync toggle to avoid slow conflicting modes.
- Backups can use rsync delta transfers when enabled; Windows prefers bundled rsync and falls back to PATH/robocopy.
- rsync runner now supports custom executable paths and optional whole-file mode.
- Refined backup settings descriptions for clarity in the Settings UI.
- Updated backup and advanced settings translations across all supported languages.
### Fixed
- Backup settings text wrapping improved for delta/incremental descriptions.
- Log console auto-scroll no longer triggers layout loop warnings; noisy layout trace messages are filtered.
- Log console no longer blocks main window interaction.
- rsync on Windows now hides the console window and rewrites paths for bundled cwRsync compatibility.
- Suppress update banners and notifications while handling crashes.
- ViewLocator now ignores non-view-model data types to avoid mis-instantiating log items.
### Removed
- Removed legacy beta notes document.

## [1.1.0] - 2025-12-17
### Added
- Responsive layout scaffolds for Dashboard, Settings, Projects, and Backups so each view uses a centered, width-capped grid instead of `Viewbox` scaling, letting the UI naturally expand and contract on any resolution or DPI without misaligned cards.
- Added translations for the remaining UI text (advanced settings beta channel text, health badges, buttons, etc.) across all supported locales so switching languages no longer exposes English placeholders.
### Changed
- The sidebar header now honors the shared theme brushes (`ShellBrandStartBrush`, `BorderSoft`, etc.) so the VaultSync title/slogan block matches both light and dark palettes instead of hard-coded gradients.
- Reflowed the storage KPI text so the metric and hint stack vertically and align to the left, keeping the description legible on large screens.
- Updated the light-theme brand colors (`VsShellBrand*` values) so the shell banner text uses the same primary/foreground tokens as the rest of the app.
- Beta update checks now consider stable releases and will upgrade prerelease installs to the matching stable version when available.
### Fixed
- Build failures caused by `VaultSync.UI.exe` remaining open are prevented by closing the running app before rebuilding; the shell banner and layout changes now compile cleanly once the process is released.
- The "VaultSync is still running" notification now fires only when minimizing to the tray, so quitting the app never triggers the toast unexpectedly.
- Prevented multiple instances of the app from launching and activated the existing window when a second launch is attempted.
- Dashboard storage pie chart now keeps a consistent size without overlapping the chart card next to it, and the legend list wraps cleanly.
- Settings destinations/credentials layout now aligns controls and action buttons correctly on narrow and wide windows.

## [1.0.0] - 2025-12-07
### Added
- Advanced destination mode now shares the same Housekeeping block close to the fallback backup path, with localized descriptions, localized checklist, and a dedicated ?Test? flow that mounts/unmounts    using credential profiles.
- Auto backups now compare snapshots before running so they skip when nothing changed and report skips separately in the UI.
### Changed
- Dashboard storage/gradient branding, shell tagline localization, and the backup settings layout use theme-aware resources so every element adapts to light/dark variants.
### Fixed
- Windows SMB mounts handle error 1219 by disconnecting existing sessions and retrying, and clipboard/mount tooling runs hidden to avoid flickering consoles.
## [0.9.8] - 2025-12-05
### Fixed
- Language selection now persists across restarts/updates and settings clamp numeric fields (snapshots, intervals, free-space) to avoid crashes.
- Updated translations and storage labels for a cleaner UI.

## [0.9.7.3] - 2025-12-05
### Fixed
- Patch helper now copies the full install directory into its temp folder before elevating, so UAC launches keep dependencies and the app restarts after applying the update.

## [0.9.7.2] - 2025-12-04
### Fixed
- Patch manifest validation now treats missing revision as zero, allowing 0.9.7 ? 0.9.7.1/2 deltas to apply when the assembly reports 0.9.7.0.
- Rebuilt patch assets for 0.9.7.2.

## [0.9.7.1] - 2025-12-04
### Fixed
- Elevated patch helper launch for Program Files installs and resolved compile warnings; rebuilt patch assets for 0.9.7.1.

## [0.9.7] - 2025-12-04
### Changed
- Bumped Windows metadata to 0.9.7 (installer + assembly) to ship the built-in patch helper flow that self-applies and restarts.
- Regenerated the Windows patch assets to match the latest publish output.

## [0.9.4] - 2025-12-02
### Changed
- Bumped the Windows build (Net 8 + `net8.0-windows10.0.19041.0`) to 0.9.4 and republished the installer metadata so the update channel can pick up the hotfix.
- Added a reusable `VersionHelper` so release and patch manifest comparisons normalize strings like `v0.9.2.0`, letting 0.9.4 delta manifests validate cleanly against older installs.
### Fixed
- Created the `patches/v0.9.4/vaultsync-patch-windows.json` manifest/+zip (and published the delta assets) so 0.9.3 installations can download/apply just the changed `.exe`, `.dll`, and runtime config files.

## [0.9.0-beta.1] - 2025-11-16
### Added
- Patch-based updater downloads delta packages per platform from the `stable` GitHub release channel and stages them for the updater helper.
- Cross-platform localization pipeline with JSON resource dictionaries, `LocalizationService`, and Italian translations covering Settings, Dashboard, Backups, Projects, and notifications.
- Language selector in Settings -> Advanced plus docs showing how to add new languages.
### Changed
- Docs now describe the patch updater + localization workflow ahead of the public beta.

---

## [0.8.1] - 2025-11-09
### Fixed
- Resolved SQLite **FOREIGN KEY constraint** errors during heavy file churn in watcher mode.
- Added concurrency protection in `WatchCommand` using `SemaphoreSlim` to serialize snapshot/sync/verify cycles.
- Added graceful handling and clear message for FK19 errors.
- Ensured watcher cancellation safely short-circuits change handlers.
- Verified stability under stress: rename storms, 400+ file bursts, large binary files, read-only destinations.

### Changed
- Updater now surfaces installer downloads when patches are incompatible, enabling version skipping and beta-to-stable moves.
- Backup settings text wrapping improved for delta/incremental descriptions.
- Watch cycles are now atomic ? no overlap possible.
- Improved log readability during watch cycles (Spectre.Console markup formatting).
- Debounce logic refined for high-frequency file systems.

### Planned
- `--no-startup-snapshot` and `--idle-after-ms` flags for watch mode.
- Snapshot rollback on failure to ensure atomic DB writes.
- Configurable symlink policy (`skip` vs `dereference`).

---

## [0.8.0] - 2025-11-08
### Changed
- **Program.cs fully modularized**:
  - Separated each CLI command into its own file under `VaultSync.CLI.Commands`.
  - Added clean namespace structure and modern Spectre.Console.Cli setup.
  - Replaced obsolete `SetApplicationName()` with branch generics `AddBranch<CommandSettings>`.
- Verified compatibility with all existing commands.
- Logging now centralized through `VaultSync.CLI.Utils.Log`.

### Fixed
- Command discovery issues under .NET 8 due to non-generic branch registration.
- CLI startup logging consistency.

---

## [0.7.0] - 2025-11-03
### Added
- **Spectre.Console.Cli** integration replacing raw `System.CommandLine`.
- Implemented full project lifecycle commands:
  - `init`, `add-project`, `remove-project`, `list-projects`, `set-path`, `snapshot`, `sync`, `verify`, `restore`, `history`, `diff`, `prune`, `doctor`, `self-test`, `version`.
- Added `config` and `presets` branches with subcommands for configuration inspection.
- Integrated `Log` utility with color-coded console output.

### Fixed
- Early null reference handling when DB not initialized.
- Command help output rendering.

---

## [0.6.0] - 2025-10-31
### Added
- Core `SqliteRepository` with schema creation for `projects`, `snapshots`, and `files` tables.
- Services: `SnapshotService`, `SyncService`, `VerifyService`, `FilterService`, `ScannerService`, `HashService`, and `RobocopyRunner`/`RsyncRunner`.
- Implemented basic snapshot creation, hash storage, and diff comparison.
- Introduced `DoctorCommand` to validate environment (`rsync`, `robocopy`, DB path).

---

## [0.5.0] - 2025-10-27
### Added
- Initial CLI project structure: `VaultSync.CLI` under `src/`.
- Basic `Program.cs` entrypoint with hardcoded commands for testing.
- Local SQLite persistence prototype.
- Basic logging utilities.

---

## [0.4.0] - 2025-10-19
### Added
- Migration of OverSteer network management and RaceManager flow to separate baseline (predecessor to VaultSync CLI project).
- Established shared core for later reuse (config loader, command dispatcher).

---

## [0.3.0] - 2025-10-17
### Added
- Baseline logic for AI racing system (from related OverSteer project, used as testing ground for VaultSync command orchestration).

---

## [0.1.0] - 2025-10-15
### Added
- Project initialized, foundational scaffolding set up for CLI + SQLite architecture.

---

? 2025 VaultSync Project. MIT Licensed.
