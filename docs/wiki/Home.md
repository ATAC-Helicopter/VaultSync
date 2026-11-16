# VaultSync Wiki

Welcome to the VaultSync documentation.

VaultSync is an open-source backup and recovery application for Windows, macOS, and Linux. This wiki explains how to configure protection, inspect backup history, verify stored data, and recover files safely.

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

- [Backups Overview](Backups.md)
- [Backup Pipeline](Backup-Pipeline.md)
- [Encryption](Encryption.md)
- [Destinations](Destinations.md)
- [Network Shares and NAS](Network-Shares.md)
- [Snapshots](Snapshots.md)
- [Metadata Synchronization](Metadata-Sync.md)
- [Tray Menu](Tray.md)

A snapshot, a stored backup, and a verified restore are different things. See the [Backup Pipeline](Backup-Pipeline.md) for an explanation of how the pieces fit together.

## Recovery Horizon development (1.9)
- [Current development, release gates, and PR stack](Development-Status.md)
- [CLI backup, verification, selective restore, and watcher plans](CLI-Development.md)
- [Delivery Project](https://github.com/users/ATAC-Helicopter/projects/7)

## Chronicle (1.8) focus areas
- History, Snapshot Explorer, and comparisons explain what changed between restore points.
- Recovery drills prove that bounded stored bytes remain readable without touching live project data.
- The 3-2-1 advisor measures reachable copies, distinct storage media, and user-confirmed offsite coverage.
- Protected points and byte-proof retention floors preserve important recovery baselines.

- [Recovery Guide](Recovery.md) — Run read-only drills and interpret recovery evidence.
- [Recovery Lab](Recovery-Lab.md) — Restore disposable test data to an independent destination.
- [Snapshots](Snapshots.md) — Inspect and compare recorded project states.

A completed backup does not guarantee that every file can be restored. A passing recovery drill validates the checks that ran, but important data should also receive periodic real test restores.

### Chronicle (1.8)

The Chronicle release family introduced and expanded workflows for understanding backup history and recovery readiness:

- History, Snapshot Explorer, and comparisons.
- Read-only recovery drills and bounded stored-data verification.
- Recovery readiness and the 3-2-1 protection advisor.
- Protected recovery points and retention safeguards.

## Installation and Updates

- [Installation Guide](Installation.md)
- [Updates and Release Channels](Updates.md)
- [Official Releases](https://github.com/ATAC-Helicopter/VaultSync/releases/latest)

Use official release assets and verify published checksums. Development branches and unpublished release plans are not substitutes for a qualified release.

## Community and Support

- [Community and Support Guide](../COMMUNITY.md)
- [Troubleshooting](Troubleshooting.md)
- [Frequently Asked Questions](FAQ.md)
- [Reporting Bugs](Reporting-Bugs.md)

Join the community through [Discord](https://discord.gg/sgP8UzeAQt), [r/VaultSync](https://www.reddit.com/r/VaultSync/), or [GitHub Discussions](https://github.com/ATAC-Helicopter/VaultSync/discussions).

Discord offers public discussion, optional private support tickets, ideas, and development participation. You do not need Discord to use VaultSync.

Reproducible software bugs belong on [GitHub Issues](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose). Suspected security vulnerabilities must follow the [private security reporting policy](../../SECURITY.md).

## Development and Roadmap

VaultSync continues to develop additional recovery capabilities, including planned offline system-disk recovery.

**Bootable system recovery is not a capability to assume is available in the latest stable release.** Platform and disk-profile support must be explicitly qualified and published.

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
