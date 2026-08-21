# Metadata Sync

VaultSync can export a portable metadata store to backup destinations and later import that metadata on another machine.

## Where it lives
- Destination metadata is stored under `.vaultsync/meta/` at the destination root.
- The SQLite store file is `vaultsync.meta.db`.

## What it carries
- Project identity: external id, name, preset, root path hint, timestamps
- Portable project settings:
  - avatar color
  - encryption policy (never the machine-local key reference)
  - preferred destination id
  - restore mode
  - verification policy
  - auto-backup enabled state
  - tags
- Snapshot summaries:
  - file count
  - total bytes
  - diff summary fields
  - top changed paths summary
- Backup history fields:
  - backup type
  - backup mode
  - relative backup path
  - destination alias
  - source machine name
  - keep/protected flag
  - non-secret encryption descriptor metadata
- Tombstones for deleted projects, snapshots, and backups

## What it does not carry
- Backup payload contents
- Plaintext passwords or secret material
- Full local app configuration
- Full destination definitions from another machine
- Encryption key references or credential identifiers

## Preferred destination behavior
- Imported `preferredDestinationId` values are normalized against your current configured destinations.
- If the imported value matches a configured destination id, alias, or path, VaultSync resolves it to the local canonical destination id.
- If the imported value does not match a local destination, VaultSync clears the unusable remote choice rather than retaining a foreign path or identifier.

## Conflict behavior
- Some project settings do not silently overwrite differing local values on existing projects.
- Avatar color, encryption policy, preferred destination, restore mode,
  verification policy, auto-backup state, and tags share one conflict record.
- Review these conflicts from `Settings > Advanced > Doctor`.
- Keep local and Accept imported create bounded durable resolution records, so
  an unchanged rejected revision does not reappear after restart.
- Automatic imports never apply project, snapshot, backup, or inferred deletion
  changes. Manual refresh lists each destructive category and requires review.

### Compatibility and mixed-version limits

The 1.8.6 metadata store is a portable inventory and recovery aid, not a fully
synchronized multi-writer configuration database. VaultSync 1.8.7 adds
schema-version-3 provenance, durable merge bases, field-level three-way merge,
and a repository-scoped writer lease.

- It compares the current local value with the latest value in the destination
  store. It does not retain a common base revision, so it cannot prove which of
  two independent edits is newer or automatically perform a true three-way
  merge.
- Version-2 project records carry a per-record writer and revision. Legacy
  version-1 records remain readable but cannot provide trustworthy record-level
  provenance and are handled conservatively.
- Project fields now share one review path and durable decisions, while true
  three-way merge still requires the planned common base revision.
- The in-process metadata gate coordinates one running VaultSync process only.
  It is not a cross-machine writer lock.

On the active 1.8.7 release branch, cooperating metadata writers are serialized
by that lease. A second client can still preview and import read-only, but it
cannot write tombstones or exports while the repository is busy. This protection
is not shipped until 1.8.7 reaches Stable, and it can never constrain a
pre-1.8.7 client that does not understand the protocol. Do not let 1.8.6 and
1.8.7 write the same destination concurrently.

The maintained 1.8.7 implementation status is recorded in the
[1.8.7 release contract](../RELEASE_1.8.7.md). The current and planned on-disk
layouts, compatibility rules, and emergency inspection boundary are documented
in [Repository formats](../REPOSITORY_FORMATS.md).
The writer and merge threat model is in
[Cross-machine safety](../CROSS_MACHINE_SAFETY.md).

![Doctor, metadata-conflict, maintenance, and update controls](../images/Settings_Maintenance.png)

## Missing backup paths
- Import only materializes backups whose relative path still exists at the destination.
- If metadata references a backup path that no longer exists, VaultSync can record a tombstone instead of importing broken history.

## Related docs
- [Help](../HELP.md)
- [Documentation hub](../../DOCUMENTATION.md)
- [Backups overview](Backups.md)
- [Destinations](Destinations.md)
- [Troubleshooting](Troubleshooting.md)
