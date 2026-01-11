# Progress

Tracking cross-machine history sync work.

## Feature: Cross-machine history sync
- [x] Roadmap updated with flow, merge rules, version guard, rollback.
- [x] Docs/wiki updated with history sync behavior and settings.
- [x] Step 1: Map current data model + hooks (projects/snapshots/backups/restore).
- [x] Step 2: Add metadata sync layer + storage layout (`.vaultsync/meta/`).
  - [x] Add external IDs to local DB schema (projects/snapshots/backups).
  - [x] Add metadata store schema + access layer.
  - [x] Add metadata merge logic and version checks.
- [x] Step 3: Wire sync triggers (startup, destination reachability, post-backup, manual refresh).
  - [x] Startup + destination reachability import.
  - [x] Post-backup metadata export.
  - [x] Manual refresh hook.
- [x] Step 4: Config + UI toggles (global + per-destination, restore prompt, version guard).
  - [x] Restore prompt enforcement for snapshots/backups.
- [x] Step 5: Tests (merge rules, version checks, tombstones, import flow).

## Notes
- Metadata sync is history-only; file restore is explicit.
- Read-only destinations allow import but block writes.

## Feature: macOS + Windows parity 
- [x] Add rsync capability probe (version + supported flags) and adjust arguments (macOS 2.6.x vs 3.x, Windows bundled).
- [x] macOS updater: detect non-writable install dir and fall back to installer/release flow.
- [x] Destination path validation per OS (UNC on Windows, smb:// or /Volumes on macOS).
- [x] Add doctor check + Settings hint for rsync version/availability on macOS.
