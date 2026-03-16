# FAQ

## Does VaultSync support multiple destinations?
Yes. Enable advanced destinations in Settings > Backups.

## Where are backups stored?
Under the configured backup root or destination path. See `Destinations.md`.

## Can I skip an update?
Yes, use the Skip version option in the update banner.

## Why was a patch update not offered?
Patch updates are exact-match only. Your installed version must be explicitly listed in the release manifest as an allowed base version. Otherwise VaultSync will use the full installer path.

## Can I sync backup history across machines?
Yes. VaultSync can import and merge history from destinations that contain `.vaultsync/meta/`.
This is metadata-only until you restore files.

## What is Doctor?
Doctor is the repair and conflict-review area under Settings > Advanced. It can run startup-integrity follow-up actions, show deterministic repair plans, and let you resolve imported-setting conflicts.

## How do I report a bug?
Use GitHub Issues and include steps, logs, and version info.
See `Reporting-Bugs.md`.
