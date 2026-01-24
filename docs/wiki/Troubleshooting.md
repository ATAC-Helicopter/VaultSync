# Troubleshooting

## Backup is slow
- Use a local disk or SSD when possible.
- Avoid running backups during heavy network use.
- For NAS/SMB, see `Network-Shares.md`.

## Destination unreachable
- Confirm the path is accessible in File Explorer.
- Check credentials and permissions.
- Use the destination test action in Settings.
- macOS NFS requires pre-mounting with `sudo mount_nfs`; auto-mount is not supported.

## Backup delete fails (permission denied)
- Make sure the destination credentials have delete access.
- VaultSync will prompt for credentials if a delete fails; enter a one-time admin/root user if needed.
- On Windows SMB, disconnect existing connections using different credentials before retrying.

## Tray says no destinations
- Confirm at least one destination is active.
- Reopen the tray menu after changing settings.

## Update banner stuck
- Click Close on the banner.
- Run "Check for updates now" in Settings > Advanced.

## Language switching looks wrong
- Switch back to English, then reselect your language.
- Restart the app after changing language if UI doesn't refresh.
