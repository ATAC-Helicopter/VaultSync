# Destinations

VaultSync supports two modes:
- Simple mode: a single backup root path.
- Advanced mode: multiple destinations with per-destination settings.

## Simple mode
- Set the backup root in Settings > Backups.
- All backups are written under this path.
- Use this for a single disk or external drive.

## Advanced mode
- Enable advanced destinations in Settings > Backups.
- Add one or more destinations with aliases.
- Destinations can be enabled/disabled without removing them.

## Destination fields (advanced mode)
- Alias: short label shown in the UI and tray menu.
- Path: the destination folder.
- Active: if off, the destination is ignored for backups.
- Credentials: used for network shares.
- Pre-mounted: enable if the path is already mounted.
- Auto-mount/unmount: allow VaultSync to mount and clean up network paths.
- Sync history: enables portable metadata sync for this destination.

## Health checks
- The tray menu shows storage health and destination reachability.
- Use "Recheck now" to refresh the status.

## Tips
- Keep at least 10 percent free space on the target drive.
- Use a dedicated disk for large projects.
- Use clear aliases so the tray and logs are easy to read.


## History metadata
- VaultSync stores portable history in `.vaultsync/meta/` at the destination root.
- If the destination is read-only, history can be imported but not written back.
- Project roots can also be treated as history sources when backups + metadata are present.
