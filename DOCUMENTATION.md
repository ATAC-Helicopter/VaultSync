# VaultSync Documentation

VaultSync is a cross-platform snapshot, backup, sync, and verification toolkit for developers, creators, and power users managing large project folders. This document mirrors the README content but presents it as a standalone reference page so you can link directly to a clean feature overview, CLI cheatsheet, and operational guidance.

## Key Concepts

- **Snapshot | Backup | Sync | Verify** – Each project snapshot captures file state with hashes, backups mirror those snapshots to destinations, sync pushes file changes, and verify compares hashes between source and backup.
- **Smart presets** – Presets act like `.gitignore` filters (Unity, .NET/C#, game engines, language stacks, creative tools, etc.) so VaultSync only tracks relevant artifacts. You can also opt for **No preset** to keep everything.
- **Cross-platform** – One-click snapshots/backups, progress overlays, per-project status cards, backup history with “Keep” protection, SMART-style disk health, and retention controls are all available on macOS, Windows, and Linux.
- **Channels** – Stable uses the latest non-prerelease GitHub release, Beta surfaces prereleases (or falls back to stable when there are none). Badge links in the README jump directly to each channel.

## Quick CLI Reference

| Command | Description |
| --- | --- |
| `vaultsync init` | Create config + SQLite database. |
| `vaultsync add-project <name> <path>` | Register a project path with optional preset. |
| `vaultsync list-projects` | Show tracked projects with destination summaries. |
| `vaultsync snapshot <name>` | Capture a new snapshot (hash storage + delta). |
| `vaultsync sync <name> <dest>` | Mirror files to `dest` using `rsync` (macOS/Linux) or `robocopy` (Windows). |
| `vaultsync verify <name> <dest>` | Hash-compare project vs backup folder; `--full` forces complete verification. |
| `vaultsync history <name>` | List snapshots/backups with timestamps. |
| `vaultsync diff <name>` | Compare two snapshots and show changes (added/modified/deleted). |
| `vaultsync prune <name>` | Remove old snapshots/backups per retention rules. |
| `vaultsync restore <name> <dest>` | Restore a previous snapshot to `dest`. |
| `vaultsync doctor` | Validate environment: binaries, DB path, permissions, etc. |

### Watch Mode

`vaultsync watch <project>` automates snapshot + sync cycles:

- Use `--dest` to supply a backup location, `--sync`/`--verify` to toggle operations.
- `--debounce-ms` (ex: `2500`) controls how long VaultSync waits after changes before syncing.
- Watch mode serializes operations via `SemaphoreSlim` so there are no overlapping runs.
- Cancellation tokens, graceful handling of high-frequency file churn, and NAS sleep detection keep long-running watches stable.

## Snapshot & Backup Details

- Snapshots track added / modified / deleted / unchanged files and store metadata in SQLite tables for projects, snapshots, and files.
- Backup folders are timestamped (`YYYY-MM-DD_HH-MM-SS`) and can be per-project or “backup all”.
- Automatic backups can be enabled from the desktop UI; VaultSync compares snapshots before running and skips when nothing has changed, reporting skips separately.
- Protected backups (“Keep”) bypass retention heuristics so you can always preserve critical states.
- Orphaned snapshots are removed when their associated backups are pruned.

## Desktop UI Highlights

- One-click snapshot + backup buttons with live progress overlays.
- Dashboard cards show each project’s latest snapshot, backup health, and destination status.
- Backup history includes retention controls and disk health hints (best-effort SMART data).
- Translations (Localization folder) power language selectors in Settings → Advanced.
- “Beta channel” toggle in Settings → Advanced opts into the `dev` branch (checks for prereleases using `target_commitish = dev`).
- Clipboard and mount tooling run hidden to avoid flickering consoles, and SMB mounts automatically handle error 1219 by disconnecting existing sessions before retrying.

## Installation & Updates

- Clone or download the repo, build installers via `dotnet pack` (CLI) or `installer/VaultSyncInstaller.iss` (Windows).
- Desktop updates poll the `stable` GitHub release channel on startup when “Check for updates” is enabled. The UI compares release metadata, warns when a newer version exists, and lets users choose to install.
- CLI updates happen via `dotnet tool update --global vaultsync.cli` after a release is published.
- macOS/Linux updates use delta patches produced by the updater; see `docs/UPDATER.md` for the step-by-step flow and patch packaging instructions.

## Release Channels

- **Stable** – Latest release with `prerelease = false`.
- **Beta/Dev** – Releases marked as prerelease (or the `dev` branch); the README badges fall back to the stable release if no prerelease exists.
- Badges on the README header link to `%github%/releases/tag/...` for direct download.
- Desktop “Beta channel” toggle listens to `target_commitish == dev` and includes prereleases.

## Troubleshooting & Notes

- **Unsigned builds** trigger Windows SmartScreen: click **More info** → **Run anyway** once you trust VaultSync.
- Check the `Localization/` folder for strings and help translate additional languages.
- `docs/UPDATER.md` documents delta patch generation, updater helpers, and platform-specific behaviors.
- Need help? Use GitHub Discussions (`/discussions`) or open an issue on the repo.

## Supporting Resources

- Screenshots and flow descriptions live in the README (search for “VaultSync_MM1”, etc.).
- Use this documentation as the canonical reference when sharing release news, onboarding teammates, or pointing customers to CLI cheatsheets.
- Keep the badges/links at the top of the README as the entry points to Stable/Beta, discussions, and documentation (this file).
