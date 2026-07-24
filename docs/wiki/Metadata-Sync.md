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

## Missing backup paths
- Import only materializes backups whose relative path still exists at the destination.
- If metadata references a backup path that no longer exists, VaultSync can record a tombstone instead of importing broken history.

## Related docs
- [Help](../HELP.md)
- [Documentation hub](../../DOCUMENTATION.md)
- [Backups overview](Backups.md)
- [Destinations](Destinations.md)
- [Troubleshooting](Troubleshooting.md)
