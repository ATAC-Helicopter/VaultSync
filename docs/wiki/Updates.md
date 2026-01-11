# Updates

VaultSync supports patch updates and full installer updates for macOS, Windows, and Linux.

## Patch updates
- Smaller and faster when available.
- If a patch fails, the installer fallback is offered.
- Patch assets are named `vaultsync-patch-<platform>.json` and `vaultsync-patch-<platform>.zip`.

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
