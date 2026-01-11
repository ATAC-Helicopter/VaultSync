# Installation

VaultSync is distributed via platform installers and patch updates.

## Requirements
- macOS, Windows 10+, or a modern Linux distro.
- A backup destination with free space (local SSD/HDD, external drive, or network share).

## Install
1. Download the latest release from GitHub Releases.
2. Windows: run the `.exe` installer. Linux: use `.AppImage` or `.tar.gz`. macOS: open the `.dmg` and drag `VaultSync.app` to `/Applications`.
3. On macOS, the app is unsigned: right-click → Open the first time (or run `xattr -dr com.apple.quarantine /Applications/VaultSync.app`).
4. Launch VaultSync.


## Update channels
- Stable: recommended for production use.
- Beta: early access to new features.

Switch channels in Settings > Advanced. After switching, use "Check for updates now".

## Optional tools
VaultSync can bundle helper tools (like rsync) for faster network backups. The installer
places them in the app `tools` folder; the uninstaller removes them.

## Uninstall
Use your platform's standard uninstall flow. This removes the app and bundled tools, but not your backups.
