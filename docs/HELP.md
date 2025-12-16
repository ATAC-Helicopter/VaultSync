# VaultSync Help

## Overview
- Cross-platform backup/snapshot manager (macOS, Windows, Linux) with UI + CLI.
- Tracks projects, creates snapshots, runs backups (local, external, NAS), verifies, and retains history.
- UI built with Avalonia; data stored in SQLite; app config lives in `~/.vaultsync/appsettings.json`.

## Install & Run
- Build UI: `dotnet run --project src/VaultSync.UI --framework net8.0` (macOS/Linux) or use the `net8.0-windows10.0.19041.0` target on Windows.
- CLI tool: `dotnet pack src/VaultSync.CLI -c Release` then `dotnet tool install --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli`.
- Secrets are stored in keychain/DPAPI where possible (referenced via `KeyRef`), not in plaintext config.

## UI Primer
- **Dashboard**: quick stats, recent backups, disk health.
- **Projects**: add/edit projects, choose preset filters, trigger snapshots.
- **Backups**: per-project cards, history, start/stop backups, view/keep/delete snapshots.
- **Settings**: backups, destinations, credentials, notifications, appearance, and language.
- Tray/menu bar: quick snapshot/backup actions; respects "Show window on tray actions" in Settings.

- **Advanced updates**: the same Settings → Advanced card that lets you choose a language now contains a “Beta channel” toggle. It mirrors the normal update flow (“Check for updates on startup” still applies) but switches the updater to prefer `dev`-branch releases and accept prereleases so you can try work-in-progress builds before they are promoted to `stable`.
- **Beta warning**: the toggle is marked **BETA**—dev branch builds are prereleases and can break unexpectedly. Keep extra backups and switch back to the stable channel if you hit issues.

## Backups: Simple vs Advanced
VaultSync supports two destination modes (Settings -> Storage -> "Backup destinations mode"):
- **Simple (recommended to start)**: one backup folder path (the legacy fallback).
  - Set the path under Settings -> Backups -> Backup location.
  - VaultSync backs up to that single path.
- **Advanced**: multiple destinations (NAS/USB) with per-destination behavior and credentials.
  - Configure destinations under Settings -> Storage -> Destinations.
  - One destination writes metadata/history; additional destinations can mirror the same snapshot content.

## Destinations & Credentials (Advanced)
Each destination has:
- **Path**: local/external path or network share (Windows UNC like `\\host\share`).
- **Active**: included in runs.
- **Pre-mounted/guest**: use as-is; skip mount/credentials (share already connected).
- **Auto-mount**: try to mount/login if unreachable (uses the selected credential).
- **Auto-unmount**: disconnect after the run if VaultSync mounted it.
- **Credential**: optional profile used for Auto-mount.

Use the **Test** button to check reachability/writability and confirm mount/login behavior.

## Projects & Snapshots
- Add a project (Projects page) with name + root path, select a preset filter.
- Snapshots capture file state into SQLite; "Keep" prevents retention pruning.

## Auto-backup & Power
- Auto-backup interval is set in Settings; per-project opt-out is in Backups page.
- Backups can pause on battery if enabled.

## Troubleshooting
- Auto-mount fails: confirm credential username/password, correct share path, and that Auto-mount is enabled (unless Pre-mounted/guest).
- Windows error 1219 during Test/Auto-mount: Windows already has an existing connection to the same server with different credentials. Disconnect existing connections to that server (e.g. `net use \\host /delete`) and try again.
- Backups skipped: check low-disk warnings and that at least one destination is Active (Advanced) or that backup location is set (Simple).
- UI build errors: if the app is running under the debugger, `dotnet build` may fail due to locked binaries; stop the running app and rebuild.

## Where data lives
- Config: `~/.vaultsync/appsettings.json`.
- Database: `~/Library/Application Support/VaultSync/vaultsync.db` (macOS) or `%AppData%\\VaultSync\\vaultsync.db` (Windows).
- Local avatar/cache: `LocalApplicationData/VaultSync`.
