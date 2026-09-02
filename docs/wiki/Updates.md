# Updates

VaultSync supports two Windows update models:
- Direct builds use GitHub patch and installer updates.
- Microsoft Store builds use Store-managed updates.

## Updater safeguards
- Patch preflight persists clearer diagnostics about why patching is allowed or blocked.
- Patch manifests can declare multiple exact allowed base versions for one target release.
- Release tooling distinguishes pre-publish warnings from post-publish hard failures.
- Support bundles include updater and patch preflight diagnostics for troubleshooting.

## Patch updates
- Smaller and faster when available.
- If a patch fails, the installer fallback is offered.
- Patch assets are named `vaultsync-patch-<platform>.json` and `vaultsync-patch-<platform>.zip`.
  - macOS checks for arch-specific assets first (`vaultsync-patch-macos-apple-silicon.*` or `vaultsync-patch-macos-intel.*`).
  - Linux checks for arch-specific assets first (`vaultsync-patch-linux-x64.*` or `vaultsync-patch-linux-arm64.*`).
- Patch eligibility is exact:
  - the manifest must explicitly list the installed version as an allowed base version
  - unlisted or older installs fall back to the full installer
- Multi-base patch manifests are strict allowlists, not version ranges.
- Additional bases are included only when their published managed-file
  inventory is a subset of the target payload. The release build checks each
  platform independently and leaves incompatible versions on installer fallback.
- Patch + installer fallback applies to Direct builds only.
- VaultSync 1.8.7 provides a one-time architecture-aware bridge patch from the
  exact 1.8.6 predecessor to canonical `/Applications/VaultSync.app`. It stages,
  signs, verifies, and launches the canonical bundle before moving the legacy
  app to Trash. Older versions use the appropriate full DMG. Later patches
  update and verify the complete app bundle,
  including its version metadata.

## Manual update check
- Settings > Advanced > Check for updates now.
- The update banner shows the channel and current status.
- Microsoft Store builds replace GitHub update actions with `Open Microsoft Store`.

![Maintenance and update controls in Settings](../images/Settings_Maintenance.png)

## Skipping a version
- Use the Skip version action in the update banner.
- You can re-enable the banner by clearing the skipped tag in Settings.

## Channels
- Stable: recommended for production use.
- Beta: early access to new features.

Switch channels in Settings > Advanced.

## Installers
- Windows: `.exe` installer (Inno Setup).
- macOS: architecture-specific, intentionally unsigned `.dmg` containing the
  canonical `VaultSync.app` and an Applications shortcut.
- Linux: `.AppImage`, `.deb`, or `.tar.gz` assets, depending on architecture and distribution.
- Debian-package installation waits for administrator authentication and the
  package result; cancelling the password prompt keeps VaultSync open. After a
  successful package install, VaultSync schedules the installed app to start
  only after the old process exits, so the single-instance guard cannot swallow
  the relaunch.
- AppImage updates are launched through the same deferred handoff instead of
  starting the downloaded AppImage while the old app instance is still running.
- Windows Store: packaged Microsoft Store build when published.

## Safe update expectation
- Use only assets from the official `ATAC-Helicopter/VaultSync` release page and compare their published SHA-256 digests before bypassing SmartScreen or Gatekeeper warnings.
- VaultSync rejects direct-update payloads whose trusted GitHub digest or exact size is missing or mismatched.
- Use patch updates only for versions explicitly supported by the release manifest.
- Use the installer for:
  - major version jumps
  - very old versions
  - blocked or incompatible patch preflight results
- For Microsoft Store builds, use the Store listing and Store app update flow instead of the GitHub installer.
