# What's New

### Performance and polish
- Faster startup with deferred dashboard and backups refresh until views are shown.
- Backups view now reuses cached data and skips rebuilds when nothing changed.
- Filters and snapshot/verify sampling are lighter on allocations.

### Backup and destination reliability
- New destination status cards show reachability in the sidebar and Backups page.
- Backup delete can prompt for credentials when NAS permissions block removal.
- Backups no longer write completion markers on network shares to avoid locks.

### Metadata sync
- Import preview streams store entries and cleans orphan snapshots for missing backups.
- Missing backups now tombstone cleanly so they do not reappear after manual deletes.

### UI refresh
- Backups per day chart moved up for better visibility.
- Snapshot trend labels collapse by day for cleaner timelines.
- New tray menu groups quick actions, status, and recent backups in a compact panel.
- Status pills, avatars, and destination cards are tighter and more consistent.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
