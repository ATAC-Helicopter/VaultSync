# Releasing VaultSync (Windows + macOS)

This is the lightweight GitHub release flow used today. The app is unsigned, so macOS users will need to bypass Gatekeeper the first time.

## Windows (Installer)
1. Publish the app:
   ```bash
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net8.0-windows10.0.19041.0 -r win-x64 --self-contained true
   ```
2. Build the installer with Inno Setup:
   - Open `installer/VaultSyncInstaller.iss` in Inno Setup.
   - Point it at the `win-x64` publish output.
   - Compile to produce `VaultSyncInstaller.exe`.
3. Upload `VaultSyncInstaller.exe` to the GitHub Release.

## macOS (DMG)
1. Publish for each architecture:
   ```bash
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net8.0 -r osx-arm64 --self-contained true
   dotnet publish src/VaultSync.UI/VaultSync.UI.csproj -c Release -f net8.0 -r osx-x64 --self-contained true
   ```
2. Build the `.app` bundle + `.dmg` (already scripted in the repo steps used today):
   - `.app` bundle path: `dist/macos/VaultSync-macos-<arch>.app`
   - DMG output: `dist/macos/VaultSync-<version>-macos-apple-silicon.dmg` or `dist/macos/VaultSync-<version>-macos-intel.dmg`
3. Upload both `.dmg` files to the GitHub Release.

Gatekeeper note (unsigned builds):
- First launch requires right-click → Open.
- Or clear quarantine:
  ```bash
  xattr -dr com.apple.quarantine /Applications/VaultSync.app
  ```

## Update Flow (Patch + Manifest)
VaultSync uses GitHub Releases for update detection. Patch manifests and delta archives are documented in `docs/UPDATER.md`. Use that doc when generating the update artifacts.
