# VaultSync Help

## Overview
- Cross‑platform backup/snapshot manager (macOS, Windows, Linux) with UI + CLI.
- Tracks projects, creates snapshots, runs backups (local, external, NAS), verifies, and retains history.
- UI built with Avalonia; data stored in SQLite under `~/.vaultsync`.

## Install & Run
- Build UI: `dotnet run --project src/VaultSync.UI --framework net8.0` (macOS/Linux) or add `-windows` TFM on Windows.
- CLI tool: `dotnet pack src/VaultSync.CLI -c Release` then `dotnet tool install --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli`.
- Config lives at `~/.vaultsync/appsettings.json` (secrets are kept in keychain/DPAPI via KeyRef).

## Updates & installers
- The UI update check tracks the `stable` branch of `ATAC-Helicopter/VaultSync` on GitHub and tells you about new releases posted under the repo’s [Releases](https://github.com/ATAC-Helicopter/VaultSync/releases) page.
- Desktop installers are published as assets on that Releases page so the updater can download installers from the repo itself.
- Keep the CLI aligned with the same channel by rerunning `dotnet tool update --global vaultsync.cli` after a release is published.

## UI Primer
- **Dashboard**: quick stats, recent backups, disk health.
- **Projects**: add/edit projects, choose preset filters, trigger snapshots.
- **Backups**: per‑project cards, history, start/stop backups, view/keep/delete snapshots.
- **Settings**: backup destinations, credentials, auto‑backup interval, notifications, appearance.
- Tray/menu bar: quick snapshot/backup actions; respects “Show window on tray actions” in Settings.

## Projects & Snapshots
- Add a project (Projects page) with name + root path, select a preset (like gitignore for backups).
- Snapshots capture file state into SQLite; “Keep” prevents retention pruning.
- Diff snapshots via CLI (`vaultsync diff <project> <A> <B>`).

## Backups
- A backup creates a fresh snapshot then copies to the configured destination:
  - Local/external paths or network shares (UNC/smb://).
  - Multiple destinations supported; one writes metadata, others mirror with the same snapshot content.
- Compression toggle uses archive mode when enabled.
- Verification (Settings → Backups) re-hashes after backup; failures surface in Backups view + toast.
- Retention: keep latest N per project; protected backups are never pruned.

## Destinations & Credentials
- Configure destinations in Settings → Backup destinations.
- Flags:
  - **Active**: included in runs.
  - **Pre‑mounted/guest**: use as-is; no mount/creds.
  - **Auto‑mount**: try `mount_smbfs` (macOS) or `net use` (Windows) with the selected credential.
  - **Auto‑unmount**: unmount after a run if we mounted it.
- Credentials:
  - Store username + password; secrets saved to keychain/DPAPI under a generated **KeyRef**.
  - Passwords are not written to `appsettings.json`.
  - Choose a credential per destination. Auto‑mount requires one unless the share is guest.
- “Test” button on a destination checks reachability/writability and shows a toast.

## Auto‑backup & Power
- Auto‑backup interval set in Settings; per‑project opt‑out on Backups page.
- Backups pause on battery if configured. Drive health and low‑space warnings surface in Backups view and notifications.

## Notifications
- In‑app banners plus optional OS toasts (respect “Only when inactive”).
- Backup success/failure, verification issues, low disk, and destination problems are surfaced.

## CLI Highlights
- `vaultsync add-project <name> <path> --preset <preset>`
- `vaultsync snapshot <name>` / `vaultsync history <name>`
- `vaultsync sync <name> <dest>` (uses rsync/robocopy)
- `vaultsync verify <name> <dest> --full`
- `vaultsync watch <name> --dest <path> --sync --verify`

## Troubleshooting
- Auto‑mount fails: confirm credential username/password, path format (`smb://host/share` or `\\host\\share`), and that “Auto-mount” is enabled.
- Backups skipped: check low‑disk warnings and that at least one destination is Active.
- Verification failures: rerun backup, then verify again; inspect logs in the console output.
- UI not starting: ensure `.NET 8 SDK` installed; rebuild with `dotnet build VaultSync.sln`.

## Where data lives
- Config: `~/.vaultsync/appsettings.json`.
- Database: `~/Library/Application Support/VaultSync/vaultsync.db` (macOS) or `%AppData%\\VaultSync\\vaultsync.db` (Windows).
- Local avatar/cache: `LocalApplicationData/VaultSync`.
