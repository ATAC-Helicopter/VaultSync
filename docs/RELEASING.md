# Releasing VaultSync (Windows and macOS)

This document defines the current release packaging flow.

## Prerequisites
- .NET 8 SDK
- Inno Setup (Windows installer)
- Repo version/changelog already updated for the target release

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

## 4) Release Checklist
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
