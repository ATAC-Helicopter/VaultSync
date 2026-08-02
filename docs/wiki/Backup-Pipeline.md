# Backup pipeline

This page explains the internal phases so you can interpret progress and logs.

![Backups page showing project controls, progress context, and restore points](../images/Backup_Page.png)

## 1) Preparation
- Reads the active destination settings.
- Resolves network mounts, if configured.
- Validates the backup path and available space.
- For archive-mode uploads, validates whether checkpoint retry can resume from a preserved partial payload.

## 2) Copy phase
- Copies data to the destination using the best available method:
  - Local disk: optimized copy with multi-threaded file operations.
  - Network share: prefers rsync delta when available and tuned copy flags.
- Progress reports include file counts, speed, and ETA.

For encrypted archive mode:

1. VaultSync compresses the project into a local temporary archive.
2. It encrypts that archive locally into `data.vse`.
3. It uploads only `data.vse` to the destination.
4. It copies the non-secret crypto descriptor and cleans the local temporary workspace.

See [Backup encryption](Encryption.md) for setup, password storage, opening, restore, and format details.

## 3) Verification and hashing
- Hashing ensures data integrity and supports snapshot comparisons.
- These steps may run after the copy phase to keep backups fast.
- A later Recovery drill can re-read bounded stored content and compare size and SHA-256 values with the recorded snapshot.

## 4) Post-backup actions
- Updates snapshot metadata and UI status.
- Logs results for troubleshooting.
- Exports metadata and history where configured and records retry or checkpoint diagnostics for support bundles.

## Integrity guardrails
- Startup consistency checks run separately from backup execution and persist a summary for diagnostics.
- Retention preflight preserves protected points and the newest recovery point with a passing byte-level proof.
- Repair flows are deterministic; VaultSync only applies exact remaps.

## How to read logs
- Look for `Preparing destination`, `Copying`, and `Verification` entries.
- For network shares, additional lines identify mount resolution and path selection.
