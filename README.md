<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
  <img src="https://img.shields.io/badge/platform-macOS%20|%20Windows%20|%20Linux-green" />
  <img src="https://img.shields.io/badge/.NET-8.0-blueviolet" />
</p>

# VaultSync  
### Snapshot | Backup | Sync | Verify - for Projects & Workspaces

VaultSync is a cross-platform backup and snapshot manager built for developers, creators, and power-users working with large project folders.  
It provides fast snapshots, incremental backups, filtering via presets, and a modern desktop UI.

---

## Features

### CLI (Command Line Interface)
- Create snapshots of any project folder  
- Sync using **rsync** (macOS/Linux) or **robocopy** (Windows)  
- Hash-based file verification  
- Watch mode for automatic syncing  
- JSON output for scripting  
- Customizable preset rules per project  
- Works headless for servers or automation scripts  

### Desktop UI (Avalonia)
- Modern dashboard (projects, backups, storage usage)  
- One-click snapshots and backups (auto + manual)  
- Live progress overlays and per-project status cards  
- Backup history with "Keep" (protected) backups that bypass retention  
- Disk health (best-effort SMART) and backup retention controls  
- Cross-platform: macOS, Windows, Linux

### Smart Presets
Presets define what gets included/excluded (like `.gitignore`).  
Common presets included:
- Unity
- .NET / C#
- Game engines (Godot, Unreal, GameMaker)
- Common programming stacks (Node, Python, Rust, Java, Go)
- Creative tools (Blender, Video Editing, Music DAWs)
- General development presets (VSCode, JetBrains, Docker, etc.)

You can also create your own, or choose **No preset**.

### Snapshot System
- Fast directory scanner with filtering  
- Tracks added / modified / deleted / unchanged files  
- Stores snapshots in SQLite  
- View snapshot history per project  

### Backup System
- Backup any snapshot to local or external storage  
- Timestamped folders (e.g., 2025-11-16_20-41-43)  
- Per-project or "backup all"  
- Automatic backups (optional)  
- Progress, file count, and failure handling (NAS sleep detection, retries coming soon)  
- Retention: keep the newest N backups per project; protected ("Keep") backups are never pruned  
- Integrated snapshot creation: every backup captures a fresh snapshot; orphan snapshots are cleaned up when backups are pruned

---

## Installation (CLI)

```sh
cd ~/Desktop/Dev/VaultSync
dotnet pack src/VaultSync.CLI -c Release
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet tool install --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli
```

Update:
```sh
dotnet tool update --global vaultsync.cli
```

---

## Updates & installers

VaultSync’s updater polls the `stable` branch of the [ATAC-Helicopter/VaultSync](https://github.com/ATAC-Helicopter/VaultSync) repo each time the app starts (when “Check for updates on startup” is enabled). Every push to that branch is treated as an available update: the UI compares the metadata of the latest release with the running version, warns the user if a newer release exists, and lets the user decide when to download and install.

Desktop installers are published as assets on the repo’s [Releases](https://github.com/ATAC-Helicopter/VaultSync/releases) page, so you can grab the matching installer for your platform once you accept the update prompt. Windows installers are produced with the `installer/VaultSyncInstaller.iss` Inno Setup script (compile it with the Inno Setup compiler after publishing the `win-x64` output), while macOS/Linux patches are delivered via platform-specific delta archives (see `docs/UPDATER.md`). The CLI follows the same stable channel; run `dotnet tool update --global vaultsync.cli` after a release is published to stay in sync.

VaultSync now exposes a language selector (English + Italiano) under Settings → Advanced; translations are loaded from the `Localization/` folder and can be extended to other languages in future releases.

---

# 0.9.0 Beta

VaultSync **0.9.0-beta.1** introduces the new patch-based updater (see `docs/UPDATER.md`) and ships a localized experience (English + Italiano) for Settings, Dashboard, Backups, and Projects. Select Italiano in Settings → Advanced to see the UI translate instantly, and add more languages by dropping another `Localization/strings.<code>.json`.

---

## Quick Start

```sh
vaultsync init
vaultsync add-project Demo ~/Projects/Demo --preset unity
vaultsync snapshot Demo
vaultsync sync Demo ~/Backup/Demo
vaultsync verify Demo ~/Backup/Demo --full
```

---

## Useful CLI Commands

### Core
| Command | Description |
|--------|-------------|
| `vaultsync init` | Initialize config + database |
| `vaultsync add-project <name> <path>` | Register a new project |
| `vaultsync list-projects` | Show all tracked projects |
| `vaultsync snapshot <name>` | Create snapshot |
| `vaultsync sync <name> <dest>` | Mirror project to destination |
| `vaultsync verify <name> <dest>` | Hash-compare project vs backup |
| `vaultsync history <name>` | Show snapshots |
| `vaultsync diff <name>` | Compare two snapshots |
| `vaultsync prune <name>` | Remove old snapshots |
| `vaultsync restore <name> <dest>` | Restore a previous snapshot |
| `vaultsync doctor` | Check environment |

### Watch Mode
```sh
vaultsync watch Game --dest /Backups/Game --sync --verify --debounce-ms 2500
```

---

## License
Licensed under the MIT License.  
See the full license here: [LICENSE](LICENSE).

---

## Credits

Created by **Flavio Giacchetti**

Built with:
- .NET 8  
- Avalonia UI  
- SQLite  
- rsync / robocopy 
