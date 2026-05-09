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
2. Windows Direct: run the `.exe` installer. Linux x64: use the `.AppImage` for the most app-like portable launch, or extract the `.tar.gz` and run `./install.sh` for a per-user desktop/menu install. Linux arm64: extract the `.tar.gz` and run `./install.sh`. macOS: open the `.dmg` and drag `VaultSync.app` to `/Applications`.
3. On macOS, the app is unsigned: right-click -> Open the first time (or run `xattr -dr com.apple.quarantine /Applications/VaultSync.app`).
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
