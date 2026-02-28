# Updates

VaultSync supports patch updates and full installer updates for macOS, Windows, and Linux.

## 1.5.1 highlights
- Startup selector persistence on the Backups page was fixed so project cards reopen with the saved destination and encryption policy.
- Backup action-state and deletion flows were hardened to reduce gray-button stalls and exception noise.
- Consumer-friendly presets (`Photos`, `Documents`, `Steam mods`, `Creative suites`) were added with in-app guidance.
- Localization coverage, release notes, and update-facing copy were refreshed for the release.

## 1.5.0 highlights
- Backup encryption with global and per-project policy controls is now available.
- Backup bandwidth limits and quiet-hours scheduling were added.
- Backup history now shows `Full` / `Incremental` / `Imported` labels and retention outcomes.
- Snapshot diff summaries now include preview and export actions.

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
