# VaultSync Help

## Overview
VaultSync is a cross-platform snapshot and backup app (Windows, macOS, Linux) with UI and CLI workflows.

Core actions:
- Track projects
- Create snapshots
- Run backups to local, external, or network destinations
- Restore and verify backup integrity
- Sync metadata history across machines

## Install and Run
- UI:
  - macOS/Linux: `dotnet run -f net10.0 --project src/VaultSync.UI/VaultSync.UI.csproj`
  - Windows target: `dotnet run -f net10.0-windows10.0.19041.0 --project src/VaultSync.UI/VaultSync.UI.csproj`
- CLI tool build/install:
  - `dotnet pack src/VaultSync.CLI -c Release`
  - `dotnet tool install --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli`

Windows distribution channels:
- Direct build:
  - GitHub installer and GitHub-managed updater
- Microsoft Store build:
  - Store-managed updates
  - no GitHub self-update or installer fallback from inside the app

## UI Primer
- Dashboard: global status, restore readiness, storage, and recent activity.
- Projects: project list, snapshot controls, per-project details, and tag management.
- Backups: per-project backup controls, restore guidance, and backup history.
- Settings: destinations, encryption, Doctor tools, update diagnostics, maintenance, localization.

## Smart Presets
- Presets apply `.vaultsyncignore` rules to project backups.
- Consumer-friendly presets are available for:
  - Photos libraries
  - Documents libraries
  - Steam mods
  - Creative suite workspaces
- Projects show a short preset description and example hint under the preset selector.

## Destination Modes
VaultSync supports two destination modes:
- Simple mode: one backup location.
- Advanced mode: multiple destinations (NAS, USB, network) with per-destination options and credentials.

## Cross-Machine Metadata Sync
- Metadata is stored under `.vaultsync/meta/` on destinations.
- VaultSync can import and merge metadata from reachable destinations.
- Optional auto-import is available in Settings.
- Conflicting imported project settings can be reviewed and resolved from Settings > Advanced > Doctor.

Metadata sync carries:
- project identity and portable project settings such as encryption policy references, preferred destination, restore mode, verification policy, tags, and avatar color
- snapshot history summaries such as file counts, bytes, and diff summary data
- backup history details such as backup mode, destination alias, protection flag, source machine name, and non-secret encryption descriptor metadata

Metadata sync does not carry:
- backup file contents themselves
- plaintext secrets or encryption passwords
- your full app configuration or destination definitions
- arbitrary local machine state outside the exported metadata store

## Doctor and Integrity Checks
- Startup backup-index consistency checks run in the background and persist a summary for diagnostics and support bundles.
- Settings > Advanced includes Doctor actions for:
  - dry-run repair planning
  - exact repair apply
  - metadata conflict review
- Repair actions are deterministic only; VaultSync does not use fuzzy remaps.

## Restore Readiness
- Dashboard and Backups show restore-readiness summaries so you can see whether projects are ready, need attention, or are unavailable.
- The Dashboard review card links directly to Backups for drill-down.
- Retention cleanup protects the last metadata-valid restore point for a project.

## Backup Encryption
- Encrypted archive backups are encrypted locally before upload; destinations receive `data.vse`, not a completed plaintext `data.zip`.
- Passwords are stored through Windows DPAPI, macOS Keychain, or Linux Secret Service. Configuration and metadata sync carry only non-secret references.
- A plaintext archive temporarily exists on the source machine during creation, and decrypted content temporarily exists during open/restore.
- VaultSync cannot recover a forgotten encryption password.

Full security explanation: `docs/wiki/Encryption.md`.

## Updates Summary
- Patch updates use a strict manifest allowlist for compatible base versions.
- Multi-base patch manifests are still exact allowlists, not version ranges.
- If your installed version is not explicitly allowed, VaultSync falls back to the installer.
- Support bundles include updater and patch preflight diagnostics for troubleshooting.
- Microsoft Store builds do not use the GitHub updater path and instead open the Store listing for update management.

## Troubleshooting (Quick)
- Mount or auth failures: verify path, credentials, and destination options.
- Backups skipped: verify active destination and disk-space thresholds.
- Build errors with locked outputs: stop the running app and rebuild.

See full troubleshooting page: `docs/wiki/Troubleshooting.md`.

## Where Data Lives
- Config: `~/.vaultsync/appsettings.json`
- DB (all platforms, default): `~/.vaultsync/vaultsync.db`
- Logs: `~/.vaultsync/logs/`
- Metadata store on destinations: `<destination>/.vaultsync/meta/vaultsync.meta.db`

## More Docs
- Docs index: `docs/README.md`
- Documentation hub: `DOCUMENTATION.md`
- Wiki home: `docs/wiki/Home.md`
- Backup encryption: `docs/wiki/Encryption.md`
- Microsoft Store notes: `docs/MICROSOFT_STORE.md`
- Microsoft Store submission checklist: `docs/MICROSOFT_STORE_SUBMISSION_CHECKLIST.md`
- Roadmap: `ROADMAP.md`
- Changelog: `CHANGELOG.md`
