# VaultSync Beta Notes

## Current state
- Backups always create a fresh snapshot first.
- Retention keeps the newest N unprotected backups per project; “Keep” protects a backup from pruning. Orphan snapshots are removed when their backup is deleted.
- Backup history supports protect/restore/delete; manual delete also removes orphan snapshots.
- SMART/health shown in UI (best effort; uses `smartctl` when available).
- Settings for interval, retention, compression, hashing, verification, and battery pause are persisted; browse buttons use cross‑platform folder pickers.

## Known gaps before GA
- NAS credentials for UNC paths (Windows): add connector + “Test connection”; safe no‑op on macOS/Linux.
- Advanced toggles: wire verbose logging, update checks, and anonymous usage stats (opt‑in).
- Error surfacing: richer banners/toasts for backup failures, low disk, and health issues.
- Packaging/CI: platform installers and basic CI to catch retention/config regressions.
