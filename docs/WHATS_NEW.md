# What's New

## [1.4.0]

### Destination-aware backups
- Pick a destination per project (including an "All destinations" option).
- Backups/snapshots/verification now resolve destinations per project.
- Projects list shows the resolved destination label.

### Faster scans and smarter preflight
- Snapshot scan cache (optional aggressive mode) to speed up large projects.
- Preflight size/time estimates and capacity warnings.
- ETA calibration tracks archive/copy throughput and reuses recent snapshot stats.

### UI density + consistency
- Compact mode now tightens padding/typography across core pages.
- Sidebar and Backups page destination cards show active destinations consistently.
- Imported backup tags include the source machine name when available.

### Reliability fixes
- Restore/delete/open now resolve backups across inactive destinations.
- Metadata import cleans missing backups and orphan snapshots.
- Drive health status respects localization and refreshes with Backups data.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
