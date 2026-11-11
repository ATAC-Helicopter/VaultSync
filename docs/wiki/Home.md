# VaultSync Wiki

Use this wiki for user-facing workflows and troubleshooting.

Current stable is **1.8.9**, published September 16, 2026. The **1.9.5** development CLI and
recovery architecture are in development; see [development status](Development-Status.md)
and the [development CLI guide](CLI-Development.md) before using preview commands.

![VaultSync dashboard showing protection, activity, storage, and recovery status](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/images/Dashboard.png)

## Getting Started
- [Quick start](Quick-Start.md)
- [Guided app tour](Guided-Tour.md)
- [Installation](Installation.md)
- [Configuration](Configuration.md)
- [Every setting and toggle](Settings-Reference.md)

## Backups and Storage
- [Backups overview](Backups.md)
- [Backup pipeline](Backup-Pipeline.md)
- [Backup encryption](Encryption.md)
- [Destinations](Destinations.md)
- [Metadata sync](Metadata-Sync.md)
- [Network shares](Network-Shares.md)
- [Snapshots](Snapshots.md)
- [Recovery](Recovery.md)
- [Tray menu](Tray.md)

## Updates
- [Updates](Updates.md)

## Recovery Horizon development (1.9)
- [Current development, release gates, and PR stack](Development-Status.md)
- [CLI backup, verification, selective restore, and watcher plans](CLI-Development.md)
- [Delivery Project](https://github.com/users/ATAC-Helicopter/projects/7)

## Chronicle (1.8) focus areas
- History, Snapshot Explorer, and comparisons explain what changed between restore points.
- Recovery drills prove that bounded stored bytes remain readable without touching live project data.
- The 3-2-1 advisor measures reachable copies, distinct storage media, and user-confirmed offsite coverage.
- Protected points and byte-proof retention floors preserve important recovery baselines.

## Support
- [Troubleshooting](Troubleshooting.md)
- [FAQ](FAQ.md)
- [Reporting bugs](Reporting-Bugs.md)

## Repository Docs
- [Documentation index](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/README.md)
- [Documentation hub](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/DOCUMENTATION.md)
- [Roadmap](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/ROADMAP.md)
- [Changelog](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/CHANGELOG.md)
- [Contributing](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/CONTRIBUTING.md)
- [Security](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/SECURITY.md)

- [Complete Recovery Horizon release plan](Release-Plan.md): CLI ownership and immutable BSC ID crosswalk.

## Release grouping and dependency audit — 2026-10-03

One open draft PR per release now includes its preparation and corrections;
see [Release Plan](Release-Plan.md). Dependabot #661/#720 merged as verified
maintenance; rendering/CLI compatibility fixes are assembled in 1.9.0 and
propagated to later implemented CLI variants. Unused Inter packaging is removed.
[Development status](Development-Status.md) and the linked daily ledger record
full changelog ownership, dependency consumers and remaining qualification gates.
