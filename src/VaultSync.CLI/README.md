# VaultSync CLI

Snapshot, sync, and verify project folders with rsync-backed mirroring.

## Commands
- `vaultsync init`
- `vaultsync add-project <name> <path> --preset <unity|dotnet|custom>`
- `vaultsync snapshot <name>`
- `vaultsync sync <name> <destination> [--dry-run]`
- `vaultsync verify <name> <destination> [--full|--percent 10]`
- `vaultsync list-projects [--json]`
- `vaultsync doctor [--check-dest <PATH>]`
- `vaultsync version`

# VaultSync CLI

A cross‑platform command‑line tool for snapshotting, syncing, and verifying project folders with rsync‑ or robocopy‑backed mirroring.

VaultSync keeps your project directories in sync with a mirror destination while maintaining versioned snapshots in a lightweight SQLite database.

---

## ✨ Features

- **Snapshot → Sync → Verify** workflow
- Uses `rsync` (macOS/Linux) or `robocopy` (Windows) for efficient mirroring
- **Presets** for Unity, .NET, and custom projects
- **Watcher mode** for automatic snapshot + sync + verify on file changes
- Local SQLite database for metadata and file hashes
- Cross‑platform (.NET 8+)

---

## ⚙️ Installation

From a local build:

```bash
cd ~/Desktop/Dev/VaultSync
dotnet pack src/VaultSync.CLI -c Release
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet tool install --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli
```

Or, if already installed:

```bash
dotnet tool update --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli
```

Verify:

```bash
vaultsync version
```

---

## 🚀 Quick Start

```bash
vaultsync init --db ~/.vaultsync/vault.db
vaultsync add-project Demo ~/Projects/Demo --preset custom
vaultsync snapshot Demo
vaultsync sync Demo ~/Backup/Demo
vaultsync verify Demo ~/Backup/Demo --full
```

---

## 🧩 Presets

| Name   | Includes / Excludes |
|--------|----------------------|
| `unity` | Skips Library/, Temp/, Builds/, Logs/ |
| `dotnet` | Skips bin/, obj/, .vs/ |
| `custom` | No filters, everything included |

---

## 🖥️ Commands

### Core

| Command | Description |
|----------|-------------|
| `vaultsync init` | Initialize local configuration and database |
| `vaultsync add-project <name> <path> --preset <unity|dotnet|custom>` | Register a folder |
| `vaultsync remove-project <name>` | Remove a project and its data |
| `vaultsync list-projects [--json]` | List all tracked projects |
| `vaultsync set-path <name> <newPath>` | Update a project path |
| `vaultsync snapshot <name>` | Create a snapshot (scan + hash) |
| `vaultsync sync <name> <destination> [--dry-run]` | Mirror project to destination |
| `vaultsync verify <name> <destination> [--full|--percent N]` | Compare destination vs snapshot |
| `vaultsync history <name>` | Show snapshot history |
| `vaultsync diff <name>` | Compare two snapshots |
| `vaultsync prune <name> [--keep-last N | --before YYYY-MM-DD]` | Delete old snapshots |
| `vaultsync restore <name> <destination>` | Restore files from a snapshot |
| `vaultsync self-test` | Run a built‑in end‑to‑end smoke test |
| `vaultsync doctor [--check-dest <PATH>]` | Environment check (tools, permissions, DB) |
| `vaultsync version` | Show version and build info |

---

### Watch Mode

Automatically monitors a project for changes and triggers snapshot → sync → verify.

```bash
vaultsync watch <name> --dest <path> [--sync] [--verify] [--debounce-ms 500]
```

- `--debounce-ms` controls how long to wait after the last change before running a cycle.
- Each cycle is serialized — only one snapshot/sync/verify runs at a time.
- Perfect for long‑running Unity or .NET projects.

---

## 📂 Configuration

All data lives under `~/.vaultsync` by default:

```
~/.vaultsync/
 ├─ vault.db           # SQLite database
 ├─ config.json        # Global settings
 ├─ logs/              # Per-run logs
 ├─ selftest/          # Used by vaultsync self-test
 └─ e2e/               # Used for stress & integration tests
```

---

## 🧰 Example Workflow

```bash
# Initialize
vaultsync init --db ~/.vaultsync/vault.db

# Add project with preset
vaultsync add-project Game ~/Projects/MyGame --preset unity

# Snapshot
vaultsync snapshot Game

# Sync to external drive
vaultsync sync Game /Volumes/Backups/Game

# Verify 10% sample
vaultsync verify Game /Volumes/Backups/Game --percent 10

# Start automatic watch mode
vaultsync watch Game --dest /Volumes/Backups/Game --sync --verify --debounce-ms 2500
```

---

## 🧾 Changelog

See [CHANGELOG.md](../CHANGELOG.md) for version history and upcoming features.

---

© 2025 VaultSync Project. MIT Licensed.