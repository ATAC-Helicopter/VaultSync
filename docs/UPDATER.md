# Patch-Based Updater

VaultSync uses GitHub Releases for update discovery and supports patch assets to avoid full-installer downloads on every update.

## Channels
- Stable: latest non-prerelease release.
- Beta/Dev: prerelease-capable flow for `Dev` branch builds that use a prerelease suffix (when enabled in app settings).

## Required Release Assets
- Canonical release manifest:
  - `vaultsync-release-manifest.json`
- Patch manifest:
  - `vaultsync-patch-<platform>.json`
- Patch archive:
  - `vaultsync-patch-<platform>.zip`
- Windows installer:
  - `VaultSyncInstaller.exe`
- macOS bundles:
  - architecture-specific DMGs
- Linux bundles:
  - `VaultSync-<version>-linux-x64.deb`
  - `VaultSync-<version>-linux-arm64.deb`
  - `VaultSync-<version>-linux-x64.AppImage`
  - `VaultSync-<version>-linux-x64.tar.gz`
  - `VaultSync-<version>-linux-arm64.tar.gz`

The Linux `.tar.gz` bundles include `install.sh` and `uninstall.sh` for a
per-user install that creates a desktop launcher, icon, and `vaultsync`
command under `~/.local` without requiring a distro-specific package manager.

macOS can use architecture-specific patch names:
- `vaultsync-patch-macos-apple-silicon.*`
- `vaultsync-patch-macos-intel.*`

Linux can use architecture-specific patch names:
- `vaultsync-patch-linux-x64.*`
- `vaultsync-patch-linux-arm64.*`

## Runtime Expectations
- Updater checks according to Settings policy.
- A newer release is offered only after its canonical manifest is downloaded
  from the official GitHub release, matched to the exact release tag and
  channel, and reconciled with GitHub's complete asset list.
- Asset selection uses the manifest's official URL, exact byte size, and
  SHA-256. A missing manifest, unsupported schema, duplicate or unexpected
  asset, unsafe URL, or metadata mismatch fails closed instead of presenting an
  unverified download.
- Canonical and platform patch manifests are immutable for a published release
  and are cached on disk by official URL, exact size, and GitHub-published
  SHA-256. Cache bytes are rehashed before every use; linked, truncated,
  oversized, or tampered entries are ignored and never trusted as release
  metadata. This keeps scheduled checks and restarts from repeatedly increasing
  GitHub asset download counters for the same release.
- Patch apply does not replace user config/data.
- A failed in-process replacement restores overwritten files and removes newly created files before reporting failure.
- Full power-loss atomicity requires a future directory-level installer transaction; until then, release qualification must exercise interrupted updates and retain full-installer recovery.
- Installer fallback is used when patch update is not viable.

## Patch Manifest Base-Version Rules
- Legacy manifests may declare one exact base version via `previousVersion`.
- Current release automation emits one qualified base in `baseVersions`.
- `baseVersions` is an exact allowlist, not a version range.
- Patch preflight and helper apply both require the installed version to match one listed base exactly.
- If the installed version is not listed, VaultSync must fall back to the installer.
- Prerelease labels are part of the exact version identity, so `1.7.4-Beta.1` and `1.7.4` are treated as different bases.

This is required because patch archives do not remove obsolete files. The automated release path therefore accepts exactly one explicitly tested predecessor. Older or additional base versions use the full installer.

## Release Validation
After publishing assets, verify:
- canonical release manifest resolves and passes schema v1 validation
- every GitHub asset name, URL, size, and digest matches that manifest exactly
- patch downloads succeed
- patch apply succeeds on target platform
- installer fallback remains functional
- the single base version listed in `baseVersions` was validated against that same patch payload

The updater and `scripts/release_readiness_gate.ps1 -Phase PostPublish` enforce
the same canonical release-manifest contract. Patch manifests remain a separate
payload-level contract describing the files inside one platform patch.

## Related Docs
- `docs/RELEASING.md`
- `docs/wiki/Updates.md`
- `CHANGELOG.md`
