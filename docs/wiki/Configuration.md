# Configuration

This page summarizes key settings and how they affect behavior.

## Appearance
- Theme: Light, Dark, or Follow system.
- Compact layout: reduces padding for dense views.

## Backups
- Backup root (simple mode): single destination path.
- Advanced destinations: multiple targets with per-destination options.
- Scheduled backups: automatic schedule and trigger-based runs.
- Sync backup history across devices: enables portable metadata in `.vaultsync/meta/`.
- Auto-import history on discovery: merge destination history into the local DB.
- Prompt to restore after import: ask to restore the latest backup before new snapshots or backups.
- Force full history export (per destination): backfill a project's entire history into the metadata store on the next backup.
- Retention simulation: preview what would be kept or deleted before changing cleanup settings.
- Maintenance window jobs: optional once-per-day background jobs for consistency scan, repair dry-run, and metadata refresh.
- Backup history labels in the app:
  - Full
  - Incremental
  - Imported

## Notifications
- Enable or disable notifications.
- Show only when inactive.
- Use OS notifications (Windows toast, macOS Notification Center).

## Tray
- Show tray icon.
- Show tray backup widget when starting backups from the tray.
- Open window on tray actions.

## Updates
- Update channel: Stable or Beta.
- Check interval and manual checks.
- Patch diagnostics: latest release-target and patch-preflight outcome are visible in Settings > Advanced.
- Patch manifests can allow multiple exact tested base versions; all other installs must use the installer.
