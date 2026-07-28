# Troubleshooting

Settings > Advanced groups update diagnostics, Doctor, maintenance, and support
actions in one place.

![Maintenance, repair planning, metadata conflicts, and update diagnostics](../images/Settings_Maintenance.png)

## Backup is slow
- Use a local disk or SSD when possible.
- Avoid running backups during heavy network use.
- For NAS or SMB, see `Network-Shares.md`.

## Destination unreachable
- Confirm the path is accessible in File Explorer.
- Check credentials and permissions.
- Use the destination test action in Settings.
- macOS NFS requires pre-mounting with `sudo mount_nfs`; auto-mount is not supported.

## Backup delete fails (permission denied)
- Make sure the destination credentials have delete access.
- VaultSync can prompt for credentials if a delete fails; use a one-time admin or root user only when required.
- On Windows SMB, disconnect existing connections using different credentials before retrying.

## Deleted backup still appears in history
- Open the Backups page while the destination is reachable.
- VaultSync removes stale local history entries only after it can resolve the matching active destination and confirm the recorded backup path is gone.
- If the destination is offline or no longer configured, the entry is preserved to avoid losing valid history for disconnected drives.

## Update banner stuck
- Click Close on the banner.
- Run `Check for updates now` in Settings > Advanced.
- If patch preflight is blocked, inspect updater diagnostics in Settings > Advanced and use the full installer if your current version is not in the manifest allowlist.

## Dashboard says storage is at risk
- `Backup root not configured`: set a backup root or active destination.
- `Backup target not available`: the destination is unreachable, so capacity and restore viability cannot be verified.
- `Backup target size unknown`: VaultSync reached the destination but could not read capacity details.
- Low free space warnings mean future backups may fail or trigger retention cleanup earlier.

## Projects page looks empty
- Confirm `Projects root` is set if you rely on discovery.
- Even when discovery finds nothing, tracked projects should still appear from the database.
- If the detail pane shows no selection, pick a project from the left list first.

## Language switching looks wrong
- Switch back to English, then reselect your language.
- Restart the app after changing language if UI does not refresh.

## Recovery drill needs attention or fails
- Expand the latest proof and start with the first warning or error action.
- Confirm the recorded destination is configured, reachable, and readable.
- If a stored file was changed or removed, create a new verified backup rather than dismissing the mismatch.
- Encrypted points are intentionally limited until opened with their password.
- If inventory was capped, treat the result as partial evidence and perform a real test restore for critical data.

![Recovery drill evidence and the checks to review](../images/Recovery_Drill.png)

## Crash email draft cannot include the report
- The report remains local and nothing has been sent.
- Use **Open folder** in the report preview and attach the displayed `.txt` file manually.
- On macOS, allow VaultSync to control Mail if you want automatic draft attachment.
- You can copy or delete the report without opening an email draft.
