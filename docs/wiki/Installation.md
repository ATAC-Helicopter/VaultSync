# Installation

VaultSync is distributed through two Windows channels plus the existing macOS/Linux direct-download path.

## Requirements
- macOS, Windows 10+, or a modern Linux distro.
- A backup destination with free space (local SSD/HDD, external drive, or network share).

## Install
1. Choose a channel:
   - Windows Direct: download from GitHub Releases.
   - Windows Store: install from Microsoft Store when that channel is published.
   - macOS/Linux: download from GitHub Releases.
2. Windows Direct: run the `.exe` installer. Linux x64: use the `.AppImage` for the most app-like portable launch, or extract the `.tar.gz` and run `./install.sh` for a per-user desktop/menu install. Linux arm64: extract the `.tar.gz` and run `./install.sh`. macOS: open the architecture-appropriate `.dmg` and drag `VaultSync.app` to `/Applications`.
3. Direct packages are intentionally unsigned. Download only from the official VaultSync GitHub release and compare its published SHA-256 digest before bypassing an operating-system warning. On macOS, right-click -> Open the first time (or run `xattr -dr com.apple.quarantine /Applications/VaultSync.app`).

When upgrading to 1.8.7 from an older architecture-named bundle, install
`/Applications/VaultSync.app`, launch it once to verify your existing settings,
then move `VaultSync-macos-arm64.app` or `VaultSync-macos-x64.app` to Trash. User
configuration and backup metadata live outside the application bundle and are
not removed with the old app.
4. Launch VaultSync.

## Updating
- Patch updates are only offered when the installed version exactly matches one of the allowed base versions in the release manifest.
- A release may allow more than one exact base version, but only versions explicitly listed in the manifest are eligible.
- If patch preflight is blocked, use the installer for that release instead.
- Treat long upgrade jumps as installer updates, not patch updates.
- Microsoft Store builds follow Microsoft Store update delivery instead of the GitHub patch/installer flow.

## Update channels
- Stable: recommended for production use.
- Beta: early access to new features.

Switch channels in Settings > Advanced. After switching, use `Check for updates now`.
Microsoft Store builds use the Store distribution channel and do not self-switch into the GitHub updater path.

## Optional tools
VaultSync can bundle helper tools such as `rsync` for faster network backups. The installer places them in the app `tools` folder; the uninstaller removes them.

## Uninstall
Use your platform's standard uninstall flow. This removes the app and bundled tools, but not your backups.
If you want to move from Microsoft Store to the Direct installer, uninstall the Store build first instead of mixing channels in place.
