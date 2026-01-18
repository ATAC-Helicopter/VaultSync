# Backups overview

VaultSync creates snapshot-based backups with progress tracking, status history, and
optional post-backup verification.

## Core concepts
- Project: a source folder you want to protect.
- Snapshot: a point-in-time capture of project metadata.
- Backup: the physical copy of project data in a destination.

## Backup types
- Manual backups: run on demand per project.
- Auto backups: scheduled or triggered by configured rules.

## What happens during a backup
1. Prepare destination
   - Resolves network paths and validates access.
2. Copy data
   - Uses optimized copy tools depending on the target.
3. Verify and hash
   - Hashing and verification can run after copy to keep the UI responsive.

## Progress and stages
The UI shows stages like Preparing, Hashing, Copying, Compressing, and Uploading
(when applicable), along with file counts, speed, and ETA.

## Where backups are stored
- Simple mode: under the single backup root path.
- Advanced mode: under each configured destination.

See `Destinations.md` and `Backup-Pipeline.md` for details.

## Cross-machine history sync
VaultSync can sync backup history (projects, snapshots, and backups) across machines
by storing portable metadata alongside the backups.

How it works:
- A destination with `.vaultsync/meta/` is treated as a history source.
- On discovery, VaultSync imports metadata into the local DB and merges history.
- If a project was auto-imported, VaultSync prompts you to restore the latest backup
  before creating new snapshots or backups.

Notes:
- This sync is metadata-only; files are restored only when you choose to restore.
- Read-only destinations can be imported from, but VaultSync will not write updates.
- If a destination has partial history, enable "Force full history export" on that destination and run a backup to backfill the store.
