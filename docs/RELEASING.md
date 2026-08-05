# Releasing VaultSync (Windows, macOS, and Linux)

This document defines the current release packaging flow.

## Prerequisites
- .NET 10 SDK
- Inno Setup (Windows installer)
- Repo version/changelog already updated for the target release
- The prepared stable release target is `1.8.6`; prerelease builds for the active patch train use `1.8.6-Beta.N` until the stable release is cut.

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
   bash scripts/build_linux_release.sh 1.8.6 x64 src/VaultSync.UI/bin/Release/net10.0/linux-x64/publish
   bash scripts/build_linux_release.sh 1.8.6 arm64 src/VaultSync.UI/bin/Release/net10.0/linux-arm64/publish
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

For `VS-1724` multi-base patch support:
- use `previous_version` as the primary legacy base version
- optionally provide `previous_versions` as a comma/newline separated exact allowlist
- only include versions you have actually validated against the same patch payload
- do not use ranges or inferred compatibility

Stable example:
- branch: `Stable`
- release channel: `stable`
- `previous_version = 1.8.5`
- `target_version = 1.8.6`

Pre-merge release candidate example:
- branch: `release/v1.8.6`
- release channel: `stable`
- `release_candidate = true`
- `previous_version = 1.8.5`
- `target_version = 1.8.6`
- candidate artifacts remain GitHub Actions artifacts; do not attach them to a
  non-prerelease GitHub Release until the release PR is approved and merged
  into `Stable`

This mode builds the exact stable-version binaries from the release branch
without merging the release PR. The workflow rejects a candidate build unless
the branch name exactly matches `release/v<target_version>`.

Beta example for future prereleases:
- branch: `Dev` after the beta changes are merged there
- release channel: `beta`
- `release_candidate = false`
- `previous_version = 1.8.5`
- `target_version = <next-version>-Beta.1`
- `include_linux_patches = false` when the previous Linux build can be installed under `/opt/vaultsync`, so Linux users receive installer fallback instead of an unwritable patch apply.

The `release_candidate` switch is not used for beta builds. It exists only to
build unpublished, stable-version candidate assets from a matching release
branch before the final merge into `Stable`.

Patch builds require one qualified predecessor through `previous_version`. This produces a manifest with:
- `previousVersion` for backward compatibility
- `baseVersions` containing that same qualified predecessor

Do not broaden the allowlist to older releases without a separate qualification mechanism and test evidence for every platform. Older or unlisted installs must fall back to the full installer.

## 5) Release Checklist
- Run the release gate before publishing:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -TargetVersion 1.8.6 -ReleaseTrack 1.8.x -TargetMilestone 1.8.6
  ```
- Run the release gate again after GitHub Actions uploads assets:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -TargetVersion 1.8.6 -ReleaseTrack 1.8.x -TargetMilestone 1.8.6 -Phase PostPublish
  ```
- `CHANGELOG.md` updated
- `docs/WHATS_NEW.md` updated
- relevant wiki/help docs updated
- build/test validation captured
- release assets uploaded (installer/DMG/Linux archives/patch assets)
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
