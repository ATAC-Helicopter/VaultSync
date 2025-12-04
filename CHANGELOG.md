# Changelog
## [0.9.8] - 2025-12-05
### Fixed
- Language selection now persists across restarts/updates and settings clamp numeric fields (snapshots, intervals, free-space) to avoid crashes.
- Updated translations and storage labels for a cleaner UI.


## [0.9.7.3] - 2025-12-05
### Fixed
- Patch helper now copies the full install directory into its temp folder before elevating, so UAC launches keep dependencies and the app restarts after applying the update.

## [0.9.7.2] - 2025-12-04
### Fixed
- Patch manifest validation now treats missing revision as zero, allowing 0.9.7 â†’ 0.9.7.1/2 deltas to apply when the assembly reports 0.9.7.0.
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
- Watch cycles are now atomic â€” no overlap possible.
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

Â© 2025 VaultSync Project. MIT Licensed.
