# Releasing VaultSync (Windows, macOS, and Linux)

This document defines the current release packaging flow.

## Release cadence

- Minor releases target seven days and have a fourteen-day maximum from the
  preceding Stable release.
- P0 safety or data-integrity work blocks the active minor. Unfinished
  non-blocking work moves to the next minor instead of extending the train.
- Minor releases do not require a beta. An unpublished stable candidate still
  runs the complete release matrix before promotion.
- Major releases begin after their planned minor train is complete and use one
  or more explicit betas when the combined feature set is stable enough for
  broader qualification.
- `1.8.7` follows this policy with a Stable deadline of 2026-08-24.

## Prerequisites
- .NET 10 SDK
- Inno Setup (Windows installer)
- Repo version/changelog already updated for the target release
- The current stable release is `1.8.6`.
- The active development target is `1.8.7` on `release/1.8.7`, integrating
  through `Dev` and promoted to `Stable` only after its release gates pass.
- Do not create a beta or prerelease implicitly. A prerelease requires an
  explicit release decision, a version suffix, and the beta workflow inputs.

## 1) Windows Installer
1. Publish:
   ```bash
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true
   ```
2. Build installer using `installer/VaultSyncInstaller.iss`.
3. Upload generated `VaultSyncInstaller.exe` to GitHub Release assets.

## 2) macOS DMG
1. Publish both architectures:
   ```bash
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net10.0 -r osx-arm64 --self-contained true
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net10.0 -r osx-x64 --self-contained true
   ```
2. Build `.app` and `.dmg` using repository scripts.
3. Upload both DMGs to release assets.

## 3) Linux Direct Assets
1. Publish both Linux architectures:
   ```bash
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net10.0 -r linux-x64 --self-contained true
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net10.0 -r linux-arm64 --self-contained true
   ```
2. Build Linux archives:
   ```bash
   bash scripts/build_linux_release.sh 1.8.7 x64 src/VaultSync.UI/bin/Release/net10.0/linux-x64/publish
   bash scripts/build_linux_release.sh 1.8.7 arm64 src/VaultSync.UI/bin/Release/net10.0/linux-arm64/publish
   ```
3. Upload the generated `.tar.gz`, `.deb`, and `linux-x64` `.AppImage` artifacts.
   The `.tar.gz` archives include `install.sh` and `uninstall.sh` for a
   per-user Linux install that works across distro families:
   ```bash
   tar -xzf VaultSync-<version>-linux-<arch>.tar.gz
   ./install.sh
   ```

## 4) Patch/Updater Assets
Create patch manifest and patch archives as described in `docs/UPDATER.md`.

Patch automation accepts one qualified predecessor:
- set `previous_version` to the exact version validated against the patch
- do not use ranges or infer compatibility with older releases
- unlisted versions must use the full installer fallback

Stable example:
- branch: `Stable`
- release channel: `stable`
- `previous_version = 1.8.6`
- `target_version = 1.8.7`

Pre-merge release candidate example:
- branch: `release/1.8.7`
- release channel: `stable`
- `release_candidate = true`
- `previous_version = 1.8.6`
- `target_version = 1.8.7`
- candidate artifacts remain GitHub Actions artifacts; do not attach them to a
  non-prerelease GitHub Release until the release PR is approved and merged
  into `Stable`

This mode builds the exact stable-version binaries from the release branch
without merging the release PR. The workflow rejects a candidate build unless
the branch name exactly matches `release/<target_version>`.

Optional prerelease example (only after an explicit release decision):
- branch: `Dev` after the beta changes are merged there
- release channel: `beta`
- `release_candidate = false`
- `previous_version = 1.8.6`
- `target_version = <next-version>-Beta.1`
- `include_linux_patches = false` when the previous Linux build can be installed under `/opt/vaultsync`, so Linux users receive installer fallback instead of an unwritable patch apply.

The `release_candidate` switch is not used for beta builds. It exists only to
build unpublished, stable-version candidate assets from a matching release
branch before the final merge into `Stable`.

Patch builds require one qualified predecessor through `previous_version`. This produces a manifest with:
- `previousVersion` for backward compatibility
- `baseVersions` containing that same qualified predecessor

Do not broaden the allowlist to older releases without a separate qualification mechanism and test evidence for every platform. Older or unlisted installs must fall back to the full installer.

After all platform jobs complete, the workflow downloads the artifacts they
actually produced and generates `vaultsync-release-manifest.json`. Its v1 schema
is [`docs/schemas/release-manifest-v1.schema.json`](schemas/release-manifest-v1.schema.json).
The manifest records the release identity, qualified predecessor, source
commit, and each direct-download asset's platform, architecture, package kind,
exact byte size, SHA-256 digest, and official GitHub download URL. The generator
fails on missing, duplicate, unexpected, empty, or altered assets; the manifest
is deliberately excluded from its own digest set.

To validate a downloaded manifest and its colocated assets offline:

```bash
python3 scripts/release_manifest.py validate \
  --manifest release-assets/vaultsync-release-manifest.json \
  --asset-root release-assets
```

The post-publish readiness gate downloads the manifest and compares it with
GitHub's live asset metadata. Any missing or unexpected name, byte-size change,
digest change, unsafe URL, or schema mismatch blocks the release.

### Offline checksum verification

Download `vaultsync-release-manifest.json` and the package to verify into the
same directory. This macOS command reads the expected SHA-256 and checks the
local bytes without trusting the package filename supplied by a different
source (`sha256sum -c -` is the equivalent final command on Linux):

```bash
asset="VaultSync-1.8.7-linux-x64.tar.gz"
expected="$(jq -er --arg name "$asset" '.assets[] | select(.name == $name) | .sha256' vaultsync-release-manifest.json)"
printf '%s  %s\n' "$expected" "$asset" | shasum -a 256 -c -
```

On Windows PowerShell:

```powershell
$asset = "VaultSync-Setup-1.8.7.exe"
$manifest = Get-Content .\vaultsync-release-manifest.json -Raw | ConvertFrom-Json
$expected = ($manifest.assets | Where-Object name -eq $asset).sha256
$actual = (Get-FileHash ".\$asset" -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $expected -or $actual -cne $expected) { throw "SHA-256 verification failed for $asset" }
```

Before using a checksum, confirm the manifest itself came from the matching tag
on the official `ATAC-Helicopter/VaultSync` GitHub Releases page.

### SBOM and provenance verification

The `release-supply-chain-proof` workflow artifact contains one SPDX 2.3 JSON
document per self-contained direct package, an exact artifact-to-SBOM index,
and the package checksum list. Validate the complete set against the canonical
manifest without network access:

```bash
python3 scripts/release_sbom.py validate \
  --manifest release-assets/vaultsync-release-manifest.json \
  --sbom-root release-supply-chain-proof/sboms
```

GitHub attestations bind the final installer, DMG, tarball, Debian package, or
AppImage bytes—not an intermediate publish directory—to this repository,
workflow run, triggering event, and commit. Verify a downloaded package online:

```bash
gh attestation verify VaultSync-Setup-1.8.7.exe \
  --repo ATAC-Helicopter/VaultSync
```

For later offline verification, prepare the bundle and current public trusted
roots while connected:

```bash
gh attestation download VaultSync-Setup-1.8.7.exe \
  --repo ATAC-Helicopter/VaultSync
gh attestation trusted-root > trusted_root.jsonl
```

Move the package, downloaded `sha256:*.jsonl` bundle, trusted root, and GitHub
CLI to the offline machine, then run:

```bash
gh attestation verify VaultSync-Setup-1.8.7.exe \
  --repo ATAC-Helicopter/VaultSync \
  --bundle 'sha256:DIGEST.jsonl' \
  --custom-trusted-root trusted_root.jsonl
```

Refresh trusted roots before each archival transfer when possible; an offline
copy cannot reveal trust-root revocations that occurred after it was captured.

## 5) Release Checklist
- Run the release gate before publishing:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -TargetVersion 1.8.7 -ReleaseTrack 1.8.x -TargetMilestone 1.8.7
  ```
- Run the release gate again after GitHub Actions uploads assets:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -TargetVersion 1.8.7 -ReleaseTrack 1.8.x -TargetMilestone 1.8.7 -Phase PostPublish
  ```
- `CHANGELOG.md` updated
- `docs/WHATS_NEW.md` updated
- relevant wiki/help docs updated
- build/test validation captured
- release assets uploaded (installer/DMG/Linux archives/patch assets)
- canonical release manifest generated from the final direct-download assets
  and validated against the same bytes before upload
- one validated SPDX 2.3 SBOM per self-contained package, with final-byte
  provenance and SBOM attestations plus candidate online/offline verification
- every direct-download asset exposes a GitHub SHA-256 digest and the updater
  rejects missing, mismatched, or non-official integrity metadata
- Windows SmartScreen and macOS Gatekeeper instructions remain current

## 6) Unsigned Distribution Policy
VaultSync direct-download builds are intentionally unsigned because paid
platform signing programs are outside the supported release budget. Signing
and notarization are not release gates. Integrity verification and clear user
disclosure are mandatory compensating controls:

- publish only through the official `ATAC-Helicopter/VaultSync` release page;
- keep GitHub-provided SHA-256 asset digests available;
- verify installer, manifest, and patch digests and exact sizes before use;
- fail closed to the official release page when verification is unavailable;
- document the expected SmartScreen and Gatekeeper prompts.

Never describe an unsigned package as signed, notarized, or publisher-verified.

macOS users may need to run:
```bash
xattr -dr com.apple.quarantine /Applications/VaultSync-macos-<arch>.app
```

Linux AppImage users may need to run:
```bash
chmod +x VaultSync-<version>-linux-x64.AppImage
./VaultSync-<version>-linux-x64.AppImage
```

Linux tarball users can install VaultSync into their user app menu without root:
```bash
tar -xzf VaultSync-<version>-linux-<arch>.tar.gz
./install.sh
```

Linux `.deb` users on Debian/Ubuntu/Zorin can install through the graphical package installer, or with:
```bash
sudo apt install ./VaultSync-<version>-linux-<arch>.deb
```

## Related Docs
- `docs/UPDATER.md`
- `docs/WHATS_NEW.md`
- `CHANGELOG.md`
- `ROADMAP.md`
