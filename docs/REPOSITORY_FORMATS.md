# VaultSync Repository Formats and Recovery Boundary

This document records the on-disk contracts implemented for VaultSync 1.8.7.
These contracts remain unreleased until 1.8.7 reaches `Stable`; the shipped
1.8.6 client does not understand repository leases or schema-version-3 merge
provenance.

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

The SQLite metadata store uses schema version `3`. Its logical tables are:

- `meta_info`: schema version, creation/write timestamps, writer app version,
  and the most recent store-level writer machine value;
- `projects`: external identity, name, preset, root-path hint, timestamps,
  per-record writer/revision/base identity, field-level provenance, the latest
  safe resolution evidence, and JSON-encoded project settings;
- `snapshots`: external/project identities, creation time, counts, sizes, and
  diff summaries;
- `backups`: external/project/snapshot identities, creation time, backup type
  and mode, relative path, destination alias, source-machine display name,
  protection state, encryption flag, and non-secret descriptor JSON;
- `tombstones`: entity type, external identity, deletion time, and origin
  machine value.

`settings_json` includes avatar color, encryption policy, preferred destination,
restore mode, verification policy, auto-backup state, and tags. Encryption key
references are deliberately excluded because they identify credentials that
exist only on one installation. Imported destination choices resolve only to a
destination configured locally.

### Current compatibility behavior

- Unknown future schema versions are rejected rather than guessed.
- Older stores are extended with known additive columns when opened for write.
- Rooted paths from another machine are normalized or treated as hints; they are
  not authoritative local paths.
- Plaintext credentials and backup payload contents are not stored in the
  metadata database.
- Version-3 project rows record their writer, monotonically advancing revision,
  exact base revision, per-field writer/revision/timestamp provenance, and safe
  resolution evidence. Version-1 rows remain readable but have no trustworthy
  per-record writer; version-2 rows lack a portable base and field provenance.
Both therefore use conservative conflict review until imported.
- Process-local semaphores serialize one VaultSync process only and do not
  protect a repository from another machine.

### Portability boundary by field

| Record | Portable | Machine-local or advisory |
|---|---|---|
| Project | external id, name, preset, creation/update times, portable settings, revision/base/provenance/resolution | root path is a hint and must resolve locally; encryption key reference is never exported |
| Snapshot | external/project ids, time, counts, sizes, diff summary | no source file contents or local database id |
| Backup | external/project/snapshot ids, time, type/mode, size, relative payload path, protection flag, encryption descriptor | destination alias and source-machine name are diagnostic hints, not authority |
| Tombstone | entity type/id, deletion time, origin installation id | deletion remains previewable and cannot authorize removal of unrelated payloads |

`kdf_params_json` is a non-secret format descriptor. It may identify the
algorithm and parameters needed to recognize encrypted backup content; it must
not contain a password, derived key, operating-system credential reference, or
recovery secret.

## Repository coordination — Implemented for 1.8.7

The coordination database, durable installation identity, protection of all
existing metadata writers, durable local merge bases, and the field-level
three-way planner are implemented on the 1.8.7 release branch. Schema version 3
adds guarded compare-and-swap project writes, explicit base revisions,
per-field writer/revision/timestamp provenance, and the latest safe resolution
record. Conflict review exposes Base/local/remote values with revision, writer,
and timestamp context. Resolution records retain the previous local state and
remain undoable until the next portable repository write marks them superseded.
Final two-machine qualification remains a release gate.

The separate coordination database currently records one active lease with
owner, diagnostic host label, process, operation, nonce, application version,
acquisition, heartbeat, and expiry. Normal release clears the active row without
growing history. Explicit stale takeover preserves the displaced record as
diagnostic evidence.

Offline metadata is queued in an app-created, destination-specific temporary
store. It is installed and retired once only when the returning destination has
no metadata database. If destination metadata already exists, VaultSync preserves
both stores and stops; it does not replay a whole queued database over potentially
divergent cross-machine changes. That case remains blocked until the versioned
merge/review workflow can reconcile it.

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

### Clean-machine identification

On a clean machine, treat a destination as a VaultSync repository only when its
root contains `.vaultsync/meta/vaultsync.meta.db`. Preserve the entire
`.vaultsync/meta/` directory, including SQLite sidecars, before inspection.
Open the copy read-only and verify:

1. `meta_info` contains exactly one supported `schema_version` (`1`, `2`, or
   `3`; 1.8.7 writes `3`). A higher value must fail closed.
2. Every backup has a relative `path_rel`; reject rooted paths and traversal
   outside the copied destination.
3. Referenced payloads exist before presenting them as usable recovery points.
4. An encrypted record has a non-secret descriptor, but assume its key is
   unavailable until the receiving machine supplies an authorized local
   credential.
5. A valid `writer.lease.db` record means inspection only. Do not edit, delete,
   or replace it. An expired record still requires explicit takeover in
   VaultSync and is retained as evidence.

If metadata cannot be read, inventory the backup payloads from the preserved
copy and restore to a new directory. Never restore over the only source and
never invent repository rows merely to make the UI accept a payload.

### Interrupted writes and rollback

- Keep `vaultsync.meta.db`, `writer.lease.db`, and their `-wal`/`-shm` files
  together. Removing a sidecar can discard committed or recoverable state.
- A transaction that did not commit is rolled back by SQLite. Reopen a copied
  repository through VaultSync; do not replay individual SQL statements.
- If a writer disappears, wait for conservative lease expiry and use the
  explicit stale-takeover review. Never clear a lease while the old client may
  still be running.
- If queued offline metadata meets a destination that already has metadata,
  preserve both stores and use merge review. VaultSync deliberately refuses a
  whole-database overwrite.
- Keep local / Accept imported resolutions can be undone only until the next
  portable write supersedes their recorded base. After that boundary, recover
  from the preserved pre-change copy rather than forcing revisions backward.

## Executable contract references

The maintained behavior is exercised by:

- `tests/VaultSync.Core.Tests/MetadataSyncTests.cs` — schemas 1–3, migration,
  preview, tombstones, guarded writes, merge bases, conflict resolution, and
  unknown-schema rejection;
- `tests/VaultSync.Core.Tests/RepositoryLeaseServiceTests.cs` — acquisition,
  heartbeat, read-only contention, expiry, takeover, nonce safety, and evidence;
- `tests/VaultSync.Core.Tests/RecoveryReportExporterTests.cs` — portable,
  redacted, checksummed recovery evidence;
- `tests/VaultSync.Core.Tests/ReleaseManifestVerifierTests.cs` and
  `tests/scripts/test_release_manifest.py` — release-manifest schema and exact
  artifact verification;
- `tests/scripts/test_release_sbom.py` — package/SBOM binding and offline
  validation.

The authoritative implementations are `MetadataStore.CurrentSchemaVersion`,
`RepositoryLeaseService`, the release-manifest v1 JSON schema, and the evidence
package schema. Documentation must change in the same PR as any of those
contracts.

## Official release validation on a clean machine

Download a package and `vaultsync-release-manifest.json` from the matching tag
on the official GitHub Releases page. Validate the manifest and colocated
assets with `scripts/release_manifest.py validate`, then verify the package's
SHA-256. When provenance is required, use `gh attestation verify`; an offline
machine additionally needs the downloaded attestation bundle and a recently
captured trusted-root snapshot. Exact commands and limitations are in
[Releasing](RELEASING.md#offline-checksum-verification).

Direct packages are currently unsigned and macOS builds are not notarized.
Checksums, SBOMs, and GitHub provenance establish byte identity and workflow
origin; they do not replace platform code signing, prove the software harmless,
or reveal trust-root revocations that happened after an offline snapshot.

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
