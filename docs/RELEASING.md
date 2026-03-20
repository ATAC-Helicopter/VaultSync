# Releasing VaultSync (Windows and macOS)

This document defines the current release packaging flow.

## Prerequisites
- .NET 8 SDK
- Inno Setup (Windows installer)
- Repo version/changelog already updated for the target release
- Beta builds must use prerelease versions such as `1.7.x-beta.N`; the final Stable cut remains `1.7.0`

## 1) Windows Installer
1. Publish:
   ```bash
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net8.0-windows10.0.19041.0 -r win-x64 --self-contained true
   ```
2. Build installer using `installer/VaultSyncInstaller.iss`.
3. Upload generated `VaultSyncInstaller.exe` to GitHub Release assets.

## 2) macOS DMG
1. Publish both architectures:
   ```bash
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net8.0 -r osx-arm64 --self-contained true
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net8.0 -r osx-x64 --self-contained true
   ```
2. Build `.app` and `.dmg` using repository scripts.
3. Upload both DMGs to release assets.

## 3) Patch/Updater Assets
Create patch manifest and patch archives as described in `docs/UPDATER.md`.

For `VS-1724` multi-base patch support:
- use `previous_version` as the primary legacy base version
- optionally provide `previous_versions` as a comma/newline separated exact allowlist
- only include versions you have actually validated against the same patch payload
- do not use ranges or inferred compatibility

Beta example:
- branch: `Dev`
- release channel: `beta`
- `previous_version = 1.6.0`
- `target_version = 1.7.x-beta.N`

Stable example:
- branch: `Stable`
- release channel: `stable`
- `previous_version = 1.6.0`
- `target_version = 1.7.0`

Example multi-base input:
- `previous_version = 1.6.2`
- `previous_versions =`
  - `1.6.0`
  - `1.6.1`
  - `1.6.2`

This produces one manifest with:
- `previousVersion` for backward compatibility
- `baseVersions` as the exact allowed base-version list

Older or unlisted installs must fall back to the full installer.

## 4) Release Checklist
- Run the release gate before publishing:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1
  ```
- Run the release gate again after GitHub Actions uploads assets:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -Phase PostPublish
  ```
- `CHANGELOG.md` updated
- `docs/WHATS_NEW.md` updated
- relevant wiki/help docs updated
- build/test validation captured
- release assets uploaded (installer/DMG/patch assets)

## 5) Unsigned Build Note
VaultSync builds are currently unsigned.

macOS users may need to run:
```bash
xattr -dr com.apple.quarantine /Applications/VaultSync.app
```

## Related Docs
- `docs/UPDATER.md`
- `docs/WHATS_NEW.md`
- `CHANGELOG.md`
- `ROADMAP.md`
