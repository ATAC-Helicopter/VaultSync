# Backup pipeline

This page explains the internal phases so you can interpret progress and logs.

## 1) Preparation
- Reads the active destination settings.
- Resolves network mounts (if configured).
- Validates the backup path and available space.

## 2) Copy phase
- Copies data to the destination using the best available method:
  - Local disk: optimized copy with multi-threaded file operations.
  - Network share: prefers rsync delta when available and tuned copy flags.
- Progress reports include file counts, speed, and ETA.

## 3) Verification and hashing
- Hashing ensures data integrity and supports snapshot comparisons.
- These steps may run after the copy phase to keep backups fast.

## 4) Post-backup actions
- Updates snapshot metadata and UI status.
- Logs results for troubleshooting.

## How to read logs
- Look for “Preparing destination”, “Copying”, and “Verification” entries.
- For network shares, additional lines identify mount resolution and path selection.

