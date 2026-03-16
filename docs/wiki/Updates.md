# Updates

VaultSync supports patch updates and full installer updates for macOS, Windows, and Linux.

## 1.7 highlights
- Patch preflight persists clearer diagnostics about why patching is allowed or blocked.
- Patch manifests can declare multiple exact allowed base versions for one target release.
- Release tooling distinguishes pre-publish warnings from post-publish hard failures.
- Support bundles include updater and patch preflight diagnostics for troubleshooting.

## Patch updates
- Smaller and faster when available.
- If a patch fails, the installer fallback is offered.
- Patch assets are named `vaultsync-patch-<platform>.json` and `vaultsync-patch-<platform>.zip`.
  - macOS checks for arch-specific assets first (`vaultsync-patch-macos-apple-silicon.*` or `vaultsync-patch-macos-intel.*`).
- Patch eligibility is exact:
  - the manifest must explicitly list the installed version as an allowed base version
  - unlisted or older installs fall back to the full installer
- Multi-base patch manifests are strict allowlists, not version ranges.

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

## Safe update expectation
- Use patch updates only for versions explicitly supported by the release manifest.
- Use the installer for:
  - major version jumps
  - very old versions
  - blocked or incompatible patch preflight results
