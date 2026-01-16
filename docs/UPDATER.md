# Patch-based Updater

VaultSync tracks the `stable` branch via GitHub Releases, but the UI no longer forces a full installer download on every push. Instead, we deliver _delta patches_ that touch only the binaries and assets that changed between releases, keep the user data/config untouched, and hand off the apply step to a built-in patch helper.  

When the new **Beta channel** toggle under Settings → Advanced is enabled, the updater prefers releases whose `target_commitish` is `dev` and no longer filters out prereleases, so you can test the latest dev builds while still reusing the same delta/installer workflow.  
This toggle is labeled **BETA** because dev-branch prereleases may be unstable—expect issues and keep backups handy while the channel is active.

## Release assets
- Patch manifest: `vaultsync-patch-<platform>.json`
- Patch archive: `vaultsync-patch-<platform>.zip`
  - macOS uses arch-specific assets first: `vaultsync-patch-macos-apple-silicon.*` or `vaultsync-patch-macos-intel.*` (falls back to `vaultsync-patch-macos.*`).
- Windows installer: `.exe` (Inno Setup)
- macOS release asset: unsigned `.dmg` containing the `.app` bundle (right-click → Open on first launch).
- Linux installer: `.AppImage` or `.tar.gz`
