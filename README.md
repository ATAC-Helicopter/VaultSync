<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
  <img src="https://img.shields.io/badge/platform-macOS%20|%20Windows%20|%20Linux-green" />
  <img src="https://img.shields.io/badge/.NET-8.0-blueviolet" />
</p>

<p align="center" style="margin-top:16px;display:flex;justify-content:center;gap:8px;flex-wrap:wrap;">
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases/latest">
    <img src="https://img.shields.io/github/v/release/ATAC-Helicopter/VaultSync?style=for-the-badge" alt="Latest release" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases/latest">
    <img src="https://img.shields.io/github/v/release/ATAC-Helicopter/VaultSync?label=Stable&style=for-the-badge" alt="Stable release" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases">
    <img src="https://img.shields.io/github/v/tag/ATAC-Helicopter/VaultSync?include_prereleases&label=Beta&style=for-the-badge" alt="Beta release" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases">
    <img src="https://img.shields.io/github/downloads/ATAC-Helicopter/VaultSync/total?style=for-the-badge" alt="Total downloads" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/issues">
    <img src="https://img.shields.io/github/issues/ATAC-Helicopter/VaultSync?style=for-the-badge" alt="Open issues" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/pulls">
    <img src="https://img.shields.io/github/issues-pr/ATAC-Helicopter/VaultSync?style=for-the-badge" alt="Open pull requests" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/discussions">
    <img src="https://img.shields.io/badge/Join%20the%20Discussion-24292f?style=for-the-badge&logo=github&logoColor=white" alt="Join the Discussion" />
  </a>
  <a href="https://www.reddit.com/r/VaultSync/">
    <img src="https://img.shields.io/badge/Reddit-Join%20the%20subreddit-FF4500?style=for-the-badge&logo=reddit&logoColor=white" alt="VaultSync subreddit" />
  </a>
  <a href="DOCUMENTATION.md">
    <img src="https://img.shields.io/badge/Documentation-%20-%23686868?style=for-the-badge&logo=bookstack" alt="Documentation" />
  </a>
</p>

> [!WARNING]
> VaultSync is currently **unsigned**.
>
> **Windows**
>
> - SmartScreen will flag the installer
> - Click **More info -> Run anyway**
>
> **macOS**
>
> 1. Open the downloaded `.dmg`
> 2. **Drag the VaultSync app into the Applications folder**
> 3. Close the disk image
> 4. Open **Applications**
> 5. Right-click VaultSync -> **Open**
>
> If Gatekeeper still blocks it, clear quarantine manually:
>
> **Apple Silicon (ARM64):**
>
> ```sh
> xattr -dr com.apple.quarantine /Applications/VaultSync-macos-arm64.app
> ```
>
> **Intel (x64):**
>
> ```sh
> xattr -dr com.apple.quarantine /Applications/VaultSync-macos-x64.app
> ```

> [!NOTE]
> **Stable** shows the latest non-prerelease version.  
> **Beta** shows the newest prerelease and falls back to Stable when none exist.

<p align="center">

![Repobeats analytics image](https://repobeats.axiom.co/api/embed/97908bbb972d09cdcaa6193062073b21dc8c55fa.svg)

</p>

# VaultSync

### Snapshot | Backup | Sync | Verify - for Projects & Workspaces

VaultSync is a cross-platform backup and snapshot manager built for developers, creators, and power-users working with large project folders.  
It provides fast snapshots, filtering via presets, and a modern desktop UI.

---

## Docs & Links

- Documentation overview: [DOCUMENTATION.md](DOCUMENTATION.md)
- Wiki (how-to guides): [docs/wiki/Home.md](docs/wiki/Home.md)
- Roadmap: [ROADMAP.md](ROADMAP.md)
- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Updater details: [docs/UPDATER.md](docs/UPDATER.md)
- Contributing: [CONTRIBUTING.md](CONTRIBUTING.md)
- Security: [SECURITY.md](SECURITY.md)
- Code of Conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## App screenshots

![Dashboard page](docs\images\Dashboard.png)
![Projects page](docs\images\Projects_Page.png)
![Backups page](docs\images\Backup_Page.png)

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

- One-click snapshots and backups (manual + scheduled)
- Per-project destination routing (`Auto`, specific destination, multi-destination support)
- Global and per-project encryption policies with secure credential-store integration
- Password-gated encrypted backup open/restore flow with auto-lock timeout and manual `Lock now`
- Backup history with type and encryption context (`Full`, `Incremental`, `Imported`, `Encrypted/Plain`)
- Snapshot diff summaries (`added`, `modified`, `deleted`, net size, top changed paths) with export (`text` / `JSON`)
- Backup policy controls (bandwidth limit + quiet hours) with policy state shown in cards/tray/logs
- Metadata sync across machines (`.vaultsync/meta`) with source-machine tracking on imported backups
- Retention with protected (`Keep`) backups and integrated cleanup behavior
- Cross-platform desktop support: macOS, Windows, Linux

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
- Adaptive archive compression policy for better speed/ratio balance by file type
- Retention: keep the newest N backups per project; protected ("Keep") backups are never pruned
- Integrated snapshot creation: every backup captures a fresh snapshot; orphan snapshots are cleaned up when backups are pruned
- Backup history terminology:
  - `Full`: complete backup payload
  - `Incremental`: backup created using incremental copy mode
  - `Imported`: history discovered/imported from metadata sync or destination scan
- Restore flow now shows a "What happens next" confirmation block before running restore.

### Network shares (SMB/NFS)

- SMB auto-mount is supported on Windows and macOS using credential profiles.
- NFS auto-mount is **not** supported on macOS (requires admin privileges). Pre-mount the share and set the destination
  to the local mount path with **Pre-mounted** enabled and **Auto-mount** disabled.
- Example NFS destination on macOS:
  - Mount: `sudo /sbin/mount_nfs -o rw,resvport 192.168.1.138:/export/Flavio_Share "/Users/flavio/Library/Application Support/VaultSync/mounts/nfs-share"`
  - Destination path: `/Users/flavio/Library/Application Support/VaultSync/mounts/nfs-share/Dev`

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

Desktop installers are published as assets on the repo's [Releases](https://github.com/ATAC-Helicopter/VaultSync/releases) page, so you can grab the matching installer for your platform once you accept the update prompt. Windows installers are produced with the installer/VaultSyncInstaller.iss Inno Setup script (compile it with the Inno Setup compiler after publishing the win-x64 output). macOS builds are shipped as unsigned .dmg images containing the .app bundle; users may need to right-click -> Open or clear quarantine (xattr -dr com.apple.quarantine /Applications/VaultSync.app). macOS/Linux patches are delivered via platform-specific delta archives (see docs/UPDATER.md). The CLI follows the same stable channel; run dotnet tool update --global vaultsync.cli after a release is published to stay in sync.

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

| Command                               | Description                    |
| ------------------------------------- | ------------------------------ |
| `vaultsync init`                      | Initialize config + database   |
| `vaultsync add-project <name> <path>` | Register a new project         |
| `vaultsync list-projects`             | Show all tracked projects      |
| `vaultsync snapshot <name>`           | Create snapshot                |
| `vaultsync sync <name> <dest>`        | Mirror project to destination  |
| `vaultsync verify <name> <dest>`      | Hash-compare project vs backup |
| `vaultsync history <name>`            | Show snapshots                 |
| `vaultsync diff <name>`               | Compare two snapshots          |
| `vaultsync prune <name>`              | Remove old snapshots           |
| `vaultsync restore <name> <dest>`     | Restore a previous snapshot    |
| `vaultsync doctor`                    | Check environment              |

### Watch Mode

```sh
vaultsync watch Game --dest /Backups/Game --sync --verify --debounce-ms 2500
```

---

## License

Licensed under the MIT License.  
See the full license here: [LICENSE](LICENSE).

## Credits

Created by **Flavio Giacchetti**


