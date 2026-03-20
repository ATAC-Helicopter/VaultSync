# Patch-Based Updater

VaultSync uses GitHub Releases for update discovery and supports patch assets to avoid full-installer downloads on every update.

## Channels
- Stable: latest non-prerelease release.
- Beta/Dev: prerelease-capable flow for `Dev` branch builds that use a prerelease suffix (when enabled in app settings).

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

## Patch Manifest Base-Version Rules
- Legacy manifests may declare one exact base version via `previousVersion`.
- Multi-base manifests may additionally declare `baseVersions`.
- `baseVersions` is an exact allowlist, not a version range.
- Patch preflight and helper apply both require the installed version to match one listed base exactly.
- If the installed version is not listed, VaultSync must fall back to the installer.
- Prerelease labels are part of the exact version identity, so `1.7.x-beta.N` and `1.7.0` are treated as different bases.

This is required because patch archives are partial target payloads. Files omitted from the patch are assumed to already be correct on every listed base version.

## Release Validation
After publishing assets, verify:
- manifest resolves correctly
- patch downloads succeed
- patch apply succeeds on target platform
- installer fallback remains functional
- every base version listed in `baseVersions` was actually validated against that same patch payload

## Related Docs
- `docs/RELEASING.md`
- `docs/wiki/Updates.md`
- `CHANGELOG.md`
