# VaultSync Help

## Overview
VaultSync is a cross-platform snapshot and backup app (Windows, macOS, Linux) with UI and CLI workflows.

Core actions:
- Track projects
- Create snapshots
- Run backups to local/external/network destinations
- Restore and verify backup integrity
- Sync metadata history across machines

## Install and Run
- UI (Windows target):
  - `dotnet run -f net8.0-windows10.0.19041.0 --project src/VaultSync.UI/VaultSync.UI.csproj`
- CLI tool build/install:
  - `dotnet pack src/VaultSync.CLI -c Release`
  - `dotnet tool install --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli`

## UI Primer
- Dashboard: global status, storage, recent activity.
- Projects: project list, snapshot controls, per-project details.
- Backups: per-project backup controls and backup history.
- Settings: destinations, encryption, retention, update options, localization.

## Smart Presets
- Presets apply `.vaultsyncignore` rules to project backups.
- Consumer-friendly presets are available for:
  - Photos libraries
  - Documents libraries
  - Steam mods
  - Creative suite workspaces
- Projects now show a short preset description and an example usage hint under the preset selector.

## Destination Modes
VaultSync supports two destination modes:
- Simple mode: one backup location.
- Advanced mode: multiple destinations (NAS/USB/network) with per-destination options and credentials.

## Destination Options (Advanced)
Per destination:
- Active
- Pre-mounted/guest
- Auto-mount
- Auto-unmount
- Credential profile

Use `Test` to verify reachability and write access.

## Cross-Machine Metadata Sync
- Metadata is stored under `.vaultsync/meta/` on destinations.
- VaultSync can import and merge metadata from reachable destinations.
- Optional auto-import is available in Settings.

## Encryption Summary
- Supports global and per-project encryption policy.
- Secrets are stored via OS secure storage when available.
- Encrypted open sessions can be auto-locked by timeout and manually locked from Settings.

## Troubleshooting (Quick)
- Mount/auth failures: verify path, credentials, and destination options.
- Backups skipped: verify active destination and disk-space thresholds.
- Build errors with locked outputs: stop running app and rebuild.

See full troubleshooting page: `docs/wiki/Troubleshooting.md`.

## Where Data Lives
- Config: `~/.vaultsync/appsettings.json`
- DB (Windows): `%AppData%/VaultSync/vaultsync.db`
- DB (macOS): `~/Library/Application Support/VaultSync/vaultsync.db`

## More Docs
- Docs index: `docs/README.md`
- Documentation hub: `DOCUMENTATION.md`
- Wiki home: `docs/wiki/Home.md`
- Roadmap: `ROADMAP.md`
- Changelog: `CHANGELOG.md`
