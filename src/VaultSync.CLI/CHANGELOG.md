# Changelog

## [0.8.1] - 2025-11-09
### Fixed
- Resolved SQLite **FOREIGN KEY constraint** errors during heavy file churn in watcher mode.
- Added concurrency protection in `WatchCommand` using `SemaphoreSlim` to serialize snapshot/sync/verify cycles.
- Added graceful handling and clear message for FK19 errors.
- Ensured watcher cancellation safely short-circuits change handlers.
- Verified stability under stress: rename storms, 400+ file bursts, large binary files, read-only destinations.

### Changed
- Watch cycles are now atomic — no overlap possible.
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

© 2025 VaultSync Project. MIT Licensed.