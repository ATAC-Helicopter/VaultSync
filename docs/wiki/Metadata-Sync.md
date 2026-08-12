# Metadata Sync

VaultSync can export a portable metadata store to backup destinations and later import that metadata on another machine.

## Where it lives
- Destination metadata is stored under `.vaultsync/meta/` at the destination root.
- The SQLite store file is `vaultsync.meta.db`.

## What it carries
- Project identity: external id, name, preset, root path hint, timestamps
- Portable project settings:
  - avatar color
  - encryption policy and key reference
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

## Preferred destination behavior
- Imported `preferredDestinationId` values are normalized against your current configured destinations.
- If the imported value matches a configured destination id, alias, or path, VaultSync resolves it to the local canonical destination id.
- If the imported value does not match a local destination, it may remain unresolved or be ignored depending on the import path.

## Conflict behavior
- Some project settings do not silently overwrite differing local values on existing projects.
- In particular, preferred destination, restore mode, verification policy, and tags can create a metadata conflict record instead.
- Review these conflicts from `Settings > Advanced > Doctor`.

### Current 1.8.6 limitations

The 1.8.6 metadata store is a portable inventory and recovery aid, not a fully
synchronized multi-writer configuration database.

- It compares the current local value with the latest value in the destination
  store. It does not retain a common base revision, so it cannot prove which of
  two independent edits is newer or automatically perform a true three-way
  merge.
- The store-level writer machine is not record-level provenance. A conflict can
  therefore identify the most recent store writer rather than the machine that
  originally changed that specific project field.
- `Keep local` dismisses the current conflict record but does not publish a
  durable resolution to the destination. The same unchanged remote value can be
  discovered again by a later import.
- Encryption policy and key-reference metadata, auto-backup state, avatar color,
  and tombstones do not all use the same review path as the four visible
  conflict fields. Treat cross-machine imports as a review operation and avoid
  editing the same project from multiple machines concurrently.
- The in-process metadata gate coordinates one running VaultSync process only.
  It is not a cross-machine writer lock.

VaultSync 1.8.7 tracks a versioned three-way merge contract, durable conflict
resolution, per-record writer provenance, and a repository-scoped writer lease.
Until that ships, use one machine as the writer for a destination and use other
machines for recovery inspection or deliberate imports.

On the active 1.8.7 development branch, cooperating metadata writers are now
serialized by a durable repository lease. A second client can still preview and
import read-only, but it cannot write tombstones or exports while the repository
is busy. This protection is not considered shipped, and it cannot constrain a
pre-1.8.7 client that does not understand the protocol.

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
