# Patch-Based Updater

VaultSync uses GitHub Releases for update discovery and supports patch assets to avoid full-installer downloads on every update.

## Channels
- Stable: latest non-prerelease release.
- Beta/Dev: prerelease-capable flow for `dev` branch builds (when enabled in app settings).

## Required Release Assets
- Patch manifest:
  - `vaultsync-patch-<platform>.json`
- Patch archive:
  - `vaultsync-patch-<platform>.zip`
- Windows installer:
  - `VaultSyncInstaller.exe`
- macOS bundles:
  - architecture-specific DMGs

macOS can use architecture-specific patch names:
- `vaultsync-patch-macos-apple-silicon.*`
- `vaultsync-patch-macos-intel.*`

## Runtime Expectations
- Updater checks according to Settings policy.
- Patch apply does not replace user config/data.
- Installer fallback is used when patch update is not viable.

## Release Validation
After publishing assets, verify:
- manifest resolves correctly
- patch downloads succeed
- patch apply succeeds on target platform
- installer fallback remains functional

## Related Docs
- `docs/RELEASING.md`
- `docs/wiki/Updates.md`
- `CHANGELOG.md`
