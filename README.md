
⸻

VaultSync

Snapshot • Sync • Verify — for Projects & Workspaces

VaultSync is a cross-platform backup, sync, and project snapshot manager with:
	•	Modern dashboard UI built with Avalonia
	•	High-performance CLI powered by rsync / robocopy
	•	Versioned snapshots stored in a fast local SQLite database
	•	Smart presets for Unity, .NET, custom projects
	•	Watch mode for automatic incremental syncing

Designed for developers with large codebases, Unity projects, and multi-machine workflows.

⸻

 Features


🔧 CLI (Powerful Command Line Tool)
	•	snapshot → sync → verify pipeline
	•	Uses rsync (macOS/Linux) or robocopy (Windows)
	•	Hash-based verification
	•	Watch mode for auto-syncing
	•	JSON output modes
	•	Unity/.NET/custom presets

⸻

 Installation

cd ~/Desktop/Dev/VaultSync
dotnet pack src/VaultSync.CLI -c Release
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet tool install --global --add-source src/VaultSync.CLI/bin/ToolPackages vaultsync.cli

Update:

dotnet tool update --global vaultsync.cli


⸻

 Quick Start

vaultsync init --db ~/.vaultsync/vault.db
vaultsync add-project Demo ~/Projects/Demo --preset custom
vaultsync snapshot Demo
vaultsync sync Demo ~/Backup/Demo
vaultsync verify Demo ~/Backup/Demo --full


⸻

 Presets

Preset	Rules
unity	Skips Library/, Temp/, Builds/, Logs/
dotnet	Skips bin/, obj/, .vs/
custom	No exclusions


⸻

 Useful Commands

Core

Command	Description
vaultsync init	Initialize config + DB
vaultsync add-project <name> <path>	Register project folder
vaultsync list-projects	List all tracked projects
vaultsync snapshot <name>	Create snapshot
vaultsync sync <name> <dest>	Mirror project folder
vaultsync verify <name> <dest>	Compare against snapshot
vaultsync history <name>	Snapshot history
vaultsync diff <name>	Compare two snapshots
vaultsync prune <name>	Remove old snapshots
vaultsync restore <name> <dest>	Restore files
vaultsync doctor	Environment check

Watch Mode

vaultsync watch Game --dest /Backups/Game --sync --verify --debounce-ms 2500


⸻

 Directory Structure

VaultSync/
 ├─ src/
 │   ├─ VaultSync.Core/        # Core engine (DB, hashing, sync logic)
 │   ├─ VaultSync.CLI/         # Command line interface
 │   └─ VaultSync.UI/          # Avalonia dashboard UI
 ├─ README.md
 ├─ LICENSE
 └─ build scripts...


⸻

 Roadmap
	•	Multi-destination sync profiles
	•	Incremental diff viewer
	•	Version compare UI inside dashboard
	•	Automated cloud sync (S3, Backblaze, OneDrive)
	•	Encrypted snapshot packs

⸻

 License

This project is licensed under the MIT License.
See LICENSE￼ for details.

⸻

 Credits

Created by Flavio Giacchetti
Built with:
	•	.NET 8
	•	Avalonia UI
	•	LiveCharts
	•	SQLite
	•	rsync / robocopy


