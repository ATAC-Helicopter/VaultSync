# VaultSync 1.9.0 — Disk and Bootable Recovery Foundation

Planning release; current stable is 1.8.9. Stable target: 2027-03-26.
Working branch: `release/1.9.0`; PRs into `Dev`, promotion to `Stable` through
merge commits. No disk support or predecessor upgrade is qualified yet.

## Delivery order

1. Approve VS-1910 typed routes and adaptive UI architecture, VS-1917 versioned
   image format, VS-1918 engine/isolation and supported-system matrix, and
   VS-1919 recovery identities, dependencies, evidence and failure domains.
2. Implement VS-1904 capture and VS-1905 exact source/destination and wipe
   previews; incomplete/corrupt captures fail explicitly.
3. Implement VS-1906 independent boot media and VS-1907 image-to-disk restore.
4. Record VS-1908 evidence and qualify VS-1909 Capture → Validate → Boot →
   Restore → Validate on the declared hardware/VM/filesystem/encryption matrix.
5. Qualify exact 1.8.9 upgrades, distribution integrity and user-data preservation.

Unsupported Secure Boot, live capture, encryption and filesystem combinations
must be explicit. If the complete loop fails, imaging remains Preview.
There is no universal bare-metal recovery claim. BSC prerequisites do not
block this release. The approved route draft is still awaiting review.

## Scope realignment on 2026-10-03

The future CLI rework is preserved in the 1.9.5 branch; discovery and completion
are adapted to the supported flat CLI in 1.9.1. BUG-19001 remains an independent
safety fix. VS-1980 retains kickoff ownership. Updater organization-transfer
compatibility and AppImage packaging fixes remain maintenance prerequisites.
Windows notification and xUnit modernization are separately tracked maintenance
work, not evidence that disk recovery is ready.

See [family ownership, gates and ID crosswalk](RELEASE_FAMILY_1.9.md),
[canonical roadmap](../ROADMAP.md) and [route draft](ROUTE_ARCHITECTURE_1.9.md).

Legacy watcher safety is retained: finite read-only text dry runs, explicit `--db`,
shutdown cancellation/drain and explicit failure. Resource/JSON commands stay in 1.9.5.

Raw command arguments are not logged, preserving secret input safety.
