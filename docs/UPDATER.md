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
  - architecture-specific DMGs, each containing the canonical `VaultSync.app`
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

VaultSync 1.8.8 uses 1.8.7 as its primary patch predecessor on every direct-
download platform. Windows, macOS, and Linux additionally consider 1.8.2,
1.8.3, 1.8.5, and 1.8.6, but include each base only when its published
managed-file inventory is overlay-safe for the final target payload. Version
1.8.4 is incompatible, and every omitted or unlisted base uses the full
installer fallback.

All 1.8.7 macOS installations already use the canonical `VaultSync.app`
layout, so the 1.8.8 patch can update and verify the complete application
bundle relative to that root when macOS patch assets are explicitly enabled.
Earlier macOS versions are only included when their published patch inventories
remain overlay-safe or have an explicit bridge path.

VaultSync 1.8.7 includes one architecture-aware bridge patch for the exact
1.8.6 predecessor. The 1.8.6 helper updates its legacy `Contents/MacOS`
payload; on restart, 1.8.7 copies that complete bundle into a staged canonical
`VaultSync.app`, writes and verifies the canonical `Info.plist`, applies the
stable ad-hoc identity, launches the canonical app, and then moves the legacy
architecture-named bundle to Trash. If migration cannot be completed, the
legacy bundle is retained and the full DMG remains the recovery path. Later
macOS patches update and verify the complete `.app` bundle relative to its root.

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
- Current release automation always emits the primary qualified predecessor and
  may add platform-specific older bases proven overlay-safe from their published
  patch-manifest file inventories.
- `baseVersions` is an exact allowlist, not a version range.
- Patch preflight and helper apply both require the installed version to match one listed base exactly.
- If the installed version is not listed, VaultSync must fall back to the installer.
- Prerelease labels are part of the exact version identity, so `1.7.4-Beta.1` and `1.7.4` are treated as different bases.

Patch archives do not remove obsolete files. An additional base is therefore
eligible only when every file managed by its published patch manifest also
exists in the target payload. The release build checks that condition per
platform, requires the reference manifest target to match the exact candidate,
rejects duplicate managed paths, and omits incompatible candidates
automatically. Omitted, unknown, or unlisted versions use the full installer.
Maintainers should update `previousVersion`, `compatiblePredecessors`, and the
per-platform `patchBaseCandidates` in `release/release-metadata.json` for each
new release; see `docs/RELEASING.md` for the future-release maintenance recipe.

Patch eligibility also depends on the install layout, not only the version.
Package-owned installs should use installer fallback unless the elevated patch
handoff has been re-qualified on that OS. macOS DMG update media is manual, so
opening a DMG must not close the running app as though installation completed.

## Release Validation
After publishing assets, verify:
- canonical release manifest resolves and passes schema v1 validation
- every GitHub asset name, URL, size, and digest matches that manifest exactly
- patch downloads succeed
- patch apply succeeds on target platform
- installer fallback remains functional
- every base listed in `baseVersions` was validated against that same patch
  payload; additional bases have no managed files absent from the target

The updater and `scripts/release_readiness_gate.ps1 -Phase PostPublish` enforce
the same canonical release-manifest contract. Patch manifests remain a separate
payload-level contract describing the files inside one platform patch.

## Related Docs
- `docs/RELEASING.md`
- `docs/wiki/Updates.md`
- `CHANGELOG.md`
