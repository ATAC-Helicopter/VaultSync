# Updates

VaultSync supports patch updates and full installer updates for macOS, Windows, and Linux.

## 1.4.1 highlights
- Destination status cards now appear in the sidebar and Backups page.
- Backup delete can prompt for credentials when NAS permissions block removal.
- Metadata import now cleans up missing backups and orphan snapshots.
- Snapshot trend labels collapse by day for cleaner timelines.

## Patch updates
- Smaller and faster when available.
- If a patch fails, the installer fallback is offered.
- Patch assets are named `vaultsync-patch-<platform>.json` and `vaultsync-patch-<platform>.zip`.
  - macOS checks for arch-specific assets first (`vaultsync-patch-macos-apple-silicon.*` or `vaultsync-patch-macos-intel.*`).

## Manual update check
- Settings > Advanced > Check for updates now.
- The update banner shows the channel and current status.

## Skipping a version
- Use the Skip version action in the update banner.
- You can re-enable the banner by clearing the skipped tag in Settings.

## Channels
- Stable: recommended for production use.
- Beta: early access to new features.

Switch channels in Settings > Advanced.

## Installers
- Windows: `.exe` installer (Inno Setup).
- macOS: unsigned `.dmg` of the `.app` bundle.
- Linux: `.AppImage` or `.tar.gz` assets.
