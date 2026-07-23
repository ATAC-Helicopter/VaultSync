# Backups overview

VaultSync creates snapshot-based backups with progress tracking, status history, and optional post-backup verification.

## Core concepts
- Project: a source folder you want to protect.
- Snapshot: a point-in-time capture of project metadata.
- Backup: the physical copy of project data in a destination.

## Backup types
- Trigger type:
  - On-demand backups: started manually per project.
  - Scheduled backups: started by automatic scheduler or rules.
- Data mode:
  - Full: complete backup payload.
  - Incremental: backup created using incremental copy mode.
  - Imported: history discovered or imported from metadata sync or destination scan.

## What happens during a backup
1. Prepare destination
   - Resolve network paths and validate access.
2. Copy data
   - Use optimized copy tools depending on the target.
3. Verify and hash
   - Hashing and verification can run after copy to keep the UI responsive.

## Progress and stages
The UI shows stages like Preparing, Hashing, Copying, Compressing, and Uploading (when applicable), along with file counts, speed, and ETA.

Archive-mode retries can resume from validated checkpoints when the destination allows it. If a preserved partial archive no longer matches the rebuilt local archive prefix, VaultSync discards the checkpoint and restarts safely instead of guessing.

For encrypted archive backups, VaultSync creates and encrypts the archive locally before uploading `data.vse`. The destination does not receive a completed plaintext `data.zip`. See [Backup encryption](Encryption.md) for setup and usage.

## Restore guidance
- Before restore starts, VaultSync shows a confirmation dialog with a `What happens next` block.
- The guidance is type-aware (`Full`, `Incremental`, `Imported`) and highlights password requirements for encrypted backups.

## Explore and compare
- Snapshot Explorer browses available folder and ZIP payloads without restoring the full backup.
- Snapshot Compare shows changed files between two restore points from the same project.
- Supported text files get line-by-line change groups; unavailable or unsupported content remains metadata-only.

## Recovery proof
- Run a drill from Recovery to verify record linkage, reachability, readable inventory, bounded stored bytes, and a read-only restore plan.
- A drill never writes to the live project.
- Encrypted backups are reported as limited unless their content is unlocked.
- A passing drill is useful evidence, but important data should still receive a real test restore periodically.

## Delete and permissions
- If a delete fails due to permissions, VaultSync can prompt for credentials to retry.
- Read-only destinations can be imported from, but writes and deletes require permission.
- If backup folders are manually removed outside the app, the Backups page prunes the stale local history entries only after the matching active destination is reachable.

## Where backups are stored
- Simple mode: under the single backup root path.
- Advanced mode: under each configured destination.

See `Destinations.md`, `Backup-Pipeline.md`, and `Recovery.md` for details.

## Cross-machine history sync
VaultSync can sync backup history (projects, snapshots, and backups) across machines by storing portable metadata alongside the backups.

How it works:
- A destination with `.vaultsync/meta/` is treated as a history source.
- On discovery, VaultSync imports metadata into the local DB and merges history.
- If a project was auto-imported, VaultSync prompts you to restore the latest backup before creating new snapshots or backups.

Notes:
- This sync is metadata-only; files are restored only when you choose to restore.
- Read-only destinations can be imported from, but VaultSync will not write updates.
- Destination scans can import untracked backups into history when enabled.
- Destination refreshes can also remove local history entries for recorded backup paths that no longer exist, but only for reachable active destinations.
- If a destination has partial history, enable `Force full history export` on that destination and run a backup to backfill the store.
- If imported settings conflict with local project settings, review them in Settings > Advanced > Doctor instead of silently overwriting one side.
