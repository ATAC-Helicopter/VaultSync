# VaultSync Repository Formats and Recovery Boundary

This document records the current on-disk contracts and the compatibility work
planned for VaultSync 1.8.7. Sections labeled **Current** describe implemented
1.8.6-compatible behavior. Sections labeled **Planned for 1.8.7** are design
contracts and must not be treated as available until their implementation and
tests land.

## Storage map

| Location | Purpose | Portability |
|---|---|---|
| Application database | Local projects, snapshots, backups, and application state | Machine-local |
| Application configuration | UI, destinations, schedules, and operational preferences | Machine-local |
| `<destination>/.vaultsync/meta/vaultsync.meta.db` | Portable project and backup-history metadata | Cross-machine |
| `<destination>/.vaultsync/meta/writer.lease.db` | 1.8.7 cooperating-writer coordination and exceptional takeover evidence | Repository-local coordination |
| Backup payload folders/archives | Recoverable project bytes | Cross-machine when the destination is reachable |
| Recovery evidence reports | Readable proof and drill summaries | Exportable, redacted |

The application database and configuration are not a shared multi-writer
database. Copying them between live installations is not a supported sync
mechanism.

## Portable metadata store — Current

The SQLite metadata store uses schema version `1`. Its logical tables are:

- `meta_info`: schema version, creation/write timestamps, writer app version,
  and the most recent store-level writer machine value;
- `projects`: external identity, name, preset, root-path hint, timestamps, and
  JSON-encoded project settings;
- `snapshots`: external/project identities, creation time, counts, sizes, and
  diff summaries;
- `backups`: external/project/snapshot identities, creation time, backup type
  and mode, relative path, destination alias, source-machine display name,
  protection state, encryption flag, and non-secret descriptor JSON;
- `tombstones`: entity type, external identity, deletion time, and origin
  machine value.

`settings_json` currently includes portable-looking values such as avatar color,
encryption policy and key reference, preferred destination, restore mode,
verification policy, auto-backup state, and tags. Not every one of these fields
is safe to apply automatically on another machine; the current limitation and
single-writer guidance are documented in
[Metadata Sync](wiki/Metadata-Sync.md#current-186-limitations).

### Current compatibility behavior

- Unknown future schema versions are rejected rather than guessed.
- Older stores are extended with known additive columns when opened for write.
- Rooted paths from another machine are normalized or treated as hints; they are
  not authoritative local paths.
- Plaintext credentials and backup payload contents are not stored in the
  metadata database.
- The store-wide writer value identifies the latest store writer, not the author
  of every individual field.
- Process-local semaphores serialize one VaultSync process only and do not
  protect a repository from another machine.

## Repository coordination — Planned for 1.8.7

The coordination database and durable installation identity are implemented on
the 1.8.7 release branch. Protection of every metadata writer is the next rollout
slice. The later portable-metadata schema revision will add, with explicit
migrations and fixtures:

- durable installation identity distinct from host name;
- per-record revision, base revision, writer identity, and timestamp;
- field-level portability and provenance for project settings;
- durable conflict-resolution records;
- per-record linkage to the repository-scoped writer identity where required.

The separate coordination database currently records one active lease with
owner, diagnostic host label, process, operation, nonce, application version,
acquisition, heartbeat, and expiry. Normal release clears the active row without
growing history. Explicit stale takeover preserves the displaced record as
diagnostic evidence.

The migration must preserve every readable version-1 record. A 1.8.7 client may
inspect a repository read-only while another valid lease exists, but it must not
silently steal or overwrite that lease. Pre-1.8.7 clients cannot participate in
the lease protocol and must not be used as concurrent writers.

## Encryption boundary

- Backup encryption descriptors may describe the non-secret format needed to
  recognize encrypted content.
- Plaintext passwords, derived keys, operating-system credential blobs, tokens,
  and recovery secrets never belong in portable metadata, support bundles, or
  evidence packages.
- A key reference from one installation is machine-local unless an explicit
  future portable-key mechanism says otherwise. Importing the reference must not
  imply that the secret exists on the receiving machine.

See [Encryption](wiki/Encryption.md) for the supported backup format and secret
storage behavior.

## Emergency read-only inspection

When VaultSync cannot open a destination normally:

1. Stop automatic backup activity on every machine that can reach the
   destination.
2. Preserve the destination as-is. Do not rename, delete, compact, or directly
   edit the SQLite database or its `-wal`/`-shm` sidecars.
3. Copy the complete `.vaultsync/meta/` directory and the relevant backup
   payload to separate storage before diagnosis.
4. Record the VaultSync version, platform, destination path/alias, error, and
   whether another client may have been writing.
5. Use VaultSync preview or read-only recovery surfaces against a copy. Do not
   make the only remaining repository copy the repair target.
6. If manual SQLite inspection is unavoidable, open the copied database
   read-only and do not claim the result is a supported repair.

The portable metadata store is an inventory and recovery aid; backup payloads
remain the source of recoverable bytes. A lost or corrupt metadata database must
not be “repaired” by deleting backup payloads.

## Change discipline

Any repository-format change requires all of the following in one PR:

- a schema-version decision and forward migration;
- upgrade fixtures from every supported predecessor;
- interrupted-write and corrupt/unknown-version tests;
- portable-versus-local field classification;
- updated `DOCUMENTATION.md`, Metadata Sync guidance, and release notes;
- a clean-machine recovery exercise before release.

The active delivery status and acceptance gates are maintained in the
[1.8.7 release contract](RELEASE_1.8.7.md). Identity, lease, and merge protocol
invariants are maintained in [Cross-machine safety](CROSS_MACHINE_SAFETY.md).
