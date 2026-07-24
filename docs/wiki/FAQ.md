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

## Does a successful backup prove that every file can be recovered?
Not by itself. Run a recovery drill to check reachability, readable inventory, bounded stored bytes, and a read-only restore plan. A passing drill is evidence for the checks that ran, but important data should still receive a real test restore periodically.

## Why is an encrypted recovery drill limited?
VaultSync confirms that the encrypted payload and descriptor exist, but it does not request or retain a password merely to improve a readiness score. Open the encrypted point with its password when you need to inspect or restore its contents.

## Does VaultSync send crash reports automatically?
No. When crash-report assistance is enabled, VaultSync creates an allowlisted report locally and shows the complete contents. It can prepare a visible email draft with the file attached, but only the user can press **Send**. The feature can be disabled in Settings > Advanced.

## How do I report a bug?
Use GitHub Issues and include steps, logs, and version info.
See `Reporting-Bugs.md`.
