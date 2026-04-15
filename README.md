<p align="center">
  <img
    width="960"
    alt="VaultSync dashboard"
    src="docs/images/Dashboard.png"
  />
</p>

<p align="center">
  <strong>Snapshot | Backup | Sync | Verify</strong><br/>
  Backups you can actually understand — and actually trust.
</p>

<p align="center">
  <a href="#installation-cli-only">Install</a> |
  <a href="#features">Features</a> |
  <a href="DOCUMENTATION.md">Documentation</a> |
  <a href="docs/DOWNLOAD_STATS.md">Download Stats</a> |
  <a href="ROADMAP.md">Roadmap</a> |
  <a href="CHANGELOG.md">Changelog</a> |
  <a href="SECURITY.md">Security</a> |
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <strong>Website:</strong><br/>
  <a href="https://fglabs.dev/vaultsync">
    https://fglabs.dev/vaultsync
  </a>
</p>

---

> Most backup tools either hide everything… or expect you to script your life away.  
>  
> VaultSync sits in the middle:  
> **full visibility, real control, and backups that don’t fall apart when you need them.**

---

## Why VaultSync

Most backup tools fail in the same ways:

- You don’t really know what got backed up
- Restores feel risky or unclear
- NAS / external drives break silently
- History becomes messy or unusable

VaultSync focuses on fixing that:

- See exactly what changed (snapshot diffs)
- Know what’s safe to restore (integrity + readiness checks)
- Control where data goes (per-project destinations)
- Keep history clean and usable (retention + metadata sync)

---

> [!WARNING]
> VaultSync installers are currently unsigned (code signing is planned).
>
> This means:
> - Windows may show a SmartScreen warning
> - macOS may require manual confirmation
>
> The app itself is safe and open-source — these are standard OS security checks.

### Windows
- SmartScreen will flag the installer  
- Click **More info → Run anyway**

### macOS
1. Open the downloaded `.dmg`
2. Drag the app into **Applications**
3. Close the disk image
4. Open **Applications**
5. Right-click VaultSync → **Open**

If Gatekeeper still blocks it:

**Apple Silicon (ARM64)**
```sh
xattr -dr com.apple.quarantine /Applications/VaultSync-macos-arm64.app
```

**Intel (x64)**
```sh
xattr -dr com.apple.quarantine /Applications/VaultSync-macos-x64.app
```

---

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
  <img src="https://img.shields.io/badge/platform-macOS%20|%20Windows%20|%20Linux-green" />
  <img src="https://img.shields.io/badge/.NET-8.0-blueviolet" />
</p>

<p align="center" style="margin-top:14px;display:flex;justify-content:center;gap:8px;flex-wrap:wrap;">
  <a href="https://www.fglabs.dev/vaultsync">
    <img src="https://img.shields.io/badge/Website-FG%20Labs-111827?style=for-the-badge&logo=vercel" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases/latest">
    <img src="https://img.shields.io/github/v/release/ATAC-Helicopter/VaultSync?style=for-the-badge" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases">
    <img src="https://img.shields.io/github/v/tag/ATAC-Helicopter/VaultSync?include_prereleases&label=Beta&style=for-the-badge" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/releases">
    <img src="https://img.shields.io/github/downloads/ATAC-Helicopter/VaultSync/total?style=for-the-badge" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/tree/download-stats">
    <img src="https://img.shields.io/badge/Download%20Stats-Live%20History-1f6feb?style=for-the-badge" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/issues">
    <img src="https://img.shields.io/github/issues/ATAC-Helicopter/VaultSync?style=for-the-badge" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/pulls">
    <img src="https://img.shields.io/github/issues-pr/ATAC-Helicopter/VaultSync?style=for-the-badge" />
  </a>
  <a href="https://www.reddit.com/r/VaultSync/">
    <img src="https://img.shields.io/reddit/subreddit-subscribers/VaultSync?style=for-the-badge&logo=reddit&label=r%2FVaultSync&color=FF4500" />
  </a>
  <a href="https://github.com/ATAC-Helicopter/VaultSync/discussions">
    <img src="https://img.shields.io/badge/GitHub-Discussions-24292f?style=for-the-badge&logo=github" />
  </a>
</p>

---

## Project Activity

<p align="center">
  <img
    alt="VaultSync activity"
    src="https://repobeats.axiom.co/api/embed/2ff04847931404c1d0a47e6628fbc5cf1fc7f9f0.svg"
  />
</p>

---

# VaultSync

### Snapshot | Backup | Sync | Verify — for projects & real workflows

VaultSync is a cross-platform backup and snapshot manager built for developers, creators, and power-users working with real project folders.

Not system images.  
Not cloud lock-in.  
Just reliable backups you can inspect, understand, and restore.

---

## App Screenshots

**Dashboard — see activity, storage, and backup health at a glance**  
![Dashboard page](docs/images/Dashboard.png)

**Projects — manage what matters, not your whole system**  
![Projects page](docs/images/Projects_Page.png)

**Backups — history, restore points, and status in one place**  
![Backups page](docs/images/Backup_Page.png)

---

## Core Features

- Snapshot-based backups with full change tracking
- Reliable restores with integrity checks and safety prompts
- Designed for NAS and external storage workflows
- Per-project configuration and routing
- Desktop UI + CLI for automation

---

## Typical Use Cases

- Backing up development projects to a NAS
- Keeping versioned backups of creative work (Blender, video, audio)
- Syncing workspaces across multiple machines
- Maintaining clean restore points without full system images

---

## Features

### CLI

- Create snapshots of any project folder
- Sync using **rsync** (macOS/Linux) or **robocopy** (Windows)
- Hash-based file verification
- Watch mode for automatic syncing
- JSON output for scripting
- Customizable preset rules per project

### Desktop UI

- One-click snapshots and backups
- Per-project destination routing
- Encryption support (global + per-project)
- Backup history with clear context
- Snapshot diff summaries
- Retention policies with protected backups
- Integrity scan + Doctor repair tools
- Metadata sync across machines
- Support bundle export for debugging

---

## Snapshot System

- Fast directory scanning with filtering
- Tracks added / modified / deleted files
- SQLite-backed history
- Per-project snapshot timeline

---

## Backup System

- Backup any snapshot to local or external storage
- Timestamped backups
- Automatic or manual execution
- NAS-aware handling and retries
- Retention with protected backups
- Integrated snapshot + backup lifecycle

---

## Installation (CLI ONLY)

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

| Command | Description |
|--------|------------|
| `vaultsync init` | Initialize config |
| `vaultsync add-project` | Register project |
| `vaultsync snapshot` | Create snapshot |
| `vaultsync sync` | Backup project |
| `vaultsync verify` | Validate backup |
| `vaultsync restore` | Restore snapshot |
| `vaultsync doctor` | Run diagnostics |

---

## Updates & Installers

VaultSync uses GitHub Releases for updates.

- Stable → production releases  
- Beta → optional prerelease builds  

Installers:
- Windows → Inno Setup
- macOS → `.dmg` (unsigned)
- Linux → planned / in progress

---

## Get Started

- Download the latest release
- Try it on a real project
- See what actually changes

If something feels off, open an issue or drop feedback.  
That’s how VaultSync gets better.

---

## License

MIT License — see [LICENSE](LICENSE)

---

## Credits

Created by **Flavio Giacchetti**
