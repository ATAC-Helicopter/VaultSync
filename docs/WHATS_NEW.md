# What's New

## [1.5.0]

### Security and encryption
- Added full backup encryption support with password-protected archives.
- Added global and per-project encryption policies.
- Added encrypted restore/open flows with secure password handling and session unlock timeout.
- Added encrypted backup key rotation tools.

### Backup controls
- Added bandwidth limiting for backup transfer paths.
- Added quiet-hours scheduling for automatic backups.
- Added policy visibility (`Throttled`, `Quiet hours`) in cards, tray, and logs.

### Backup UX improvements
- Standardized backup type labels to `Full`, `Incremental`, and `Imported`.
- Added retention outcome messaging in backup history.
- Added restore confirmation guidance ("what happens next") by backup type.

### Snapshot insights
- Added snapshot diff summaries (added, modified, deleted, net size delta).
- Added top changed paths in history details.
- Added diff preview and export actions (`Text` and `JSON`).

### Reliability and compatibility
- Strengthened mixed `1.4`/`1.5` metadata compatibility for encrypted/plain history.
- Improved destination and metadata-sync resilience across import/export flows.

### UI and onboarding
- Refreshed dashboard cards and weekly activity visuals.
- Expanded onboarding tour to cover encryption, bandwidth/quiet-hours, and diff-summary workflows.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
