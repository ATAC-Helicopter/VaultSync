<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
  <img src="https://img.shields.io/badge/platform-macOS%20|%20Windows%20|%20Linux-green" />
  <img src="https://img.shields.io/badge/.NET-8.0-blueviolet" />
</p>
<p align="center" style="margin-top:16px;display:flex;justify-content:center;gap:8px;flex-wrap:wrap;">
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.1.0">
    <img src="https://img.shields.io/badge/Latest%20release-v1.1.0-1f6feb?style=for-the-badge" alt="Latest release" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.1.0">
    <img src="https://img.shields.io/badge/Stable-v1.1.0-2165ff?style=for-the-badge" alt="Stable release" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.1.0">
    <img src="https://img.shields.io/badge/Beta-v1.1.0-f25c7b?style=for-the-badge" alt="Beta release" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/discussions">
    <img src="https://img.shields.io/badge/Join%20the%20Discussion-24292f?style=for-the-badge&logo=github&logoColor=white" alt="Join the Discussion" />
  </a>
<a href="docs/DOCUMENTATION.md">
    <img src="https://img.shields.io/badge/Documentation-%20-%23686868?style=for-the-badge&logo=bookstack" alt="Documentation" />
  </a>
</p>
<p style="text-align:center;margin:4px 0 12px;font-size:12px;color:#94a3b8;">
  Stable track shows the latest non-prerelease, Beta track shows the newest prerelease (falls back to stable when none exist).
</p>

<div style="background:#070b14;border-radius:10px;padding:14px 18px;margin:20px 0;border-left:4px solid #f2c94c;color:#f1f5f9;box-shadow:0 6px 16px rgba(0,0,0,0.4);font-size:15px;line-height:1.5;border:1px solid rgba(255,255,255,0.06);">
  <div style="display:flex;align-items:flex-start;gap:8px;margin-bottom:6px;">
    <span style="font-size:18px;color:#f2c94c;margin-top:2px;">⚠️</span>
    <strong style="color:#f2c94c;font-size:16px;">Warning</strong>
  </div>
  <p style="margin:0;font-size:14px;color:#dfe8ff;max-width:720px;">
    VaultSync is currently unsigned, so Windows SmartScreen will flag the installer/program during the first run. To continue, open the SmartScreen dialog, click <strong>More info</strong>, and choose <strong>Run anyway</strong>; the app is safe to install once you trust the publisher.
  </p>
</div>

# VaultSync  
### Snapshot | Backup | Sync | Verify - for Projects & Workspaces

VaultSync is a cross-platform backup and snapshot manager built for developers, creators, and power-users working with large project folders.  
It provides fast snapshots, filtering via presets, and a modern desktop UI.

---

## Features

### CLI 
- Create snapshots of any project folder  
- Sync using **rsync** (macOS/Linux) or **robocopy** (Windows)  
- Hash-based file verification  
- Watch mode for automatic syncing  
- JSON output for scripting  
- Customizable preset rules per project  
- Works headless for servers or automation scripts  

### Desktop UI   
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

or choose **No preset** if no presets apply or you want no file exclusion.

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
- Progress, file count, and failure handling (NAS sleep detection)  
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

VaultSync's updater polls the `stable` branch of the [ATAC-Helicopter/VaultSync](https://github.com/ATAC-Helicopter/VaultSync) repo each time the app starts (when "Check for updates on startup" is enabled). Every push to that branch is treated as an available update: the UI compares the metadata of the latest release with the running version, warns the user if a newer release exists, and lets the user decide when to download and install.

Desktop installers are published as assets on the repo's [Releases](https://github.com/ATAC-Helicopter/VaultSync/releases) page, so you can grab the matching installer for your platform once you accept the update prompt. Windows installers are produced with the `installer/VaultSyncInstaller.iss` Inno Setup script (compile it with the Inno Setup compiler after publishing the `win-x64` output), while macOS/Linux patches are delivered via platform-specific delta archives (see `docs/UPDATER.md`). The CLI follows the same stable channel; run `dotnet tool update --global vaultsync.cli` after a release is published to stay in sync.

VaultSync now exposes a language selector under Settings -> Advanced; translations are loaded from the `Localization/` folder and can be extended to other languages in future releases. A new "Beta channel" toggle in the same section lets you opt into the `dev` branch: it still honors "Check for updates on startup", but selects releases where `target_commitish` equals `dev` and includes prerelease builds so you can try the latest dev work before it lands on `stable`.

## Quick Start (CLI ONLY)

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

<img width="1280" height="709" alt="VaultSync_MM1" src="https://github.com/user-attachments/assets/57368d4d-6cd5-4743-ba15-054de5034f7c" />
<img width="1280" height="709" alt="VaultSync_MM2" src="https://github.com/user-attachments/assets/32e4d684-9a46-4e9d-a90f-d13dfb644c21" />
<img width="1280" height="709" alt="VaultSync_MM3" src="https://github.com/user-attachments/assets/dca44d74-62f1-4ba4-a334-4b9166630756" />


## Credits

Created by **Flavio Giacchetti**

