# Cross-Machine Metadata Safety

This is the design and threat-model contract for the 1.8.7 writer lease and
versioned metadata merge. It does not claim that planned behavior is available;
implementation status is maintained in [the 1.8.7 release page](RELEASE_1.8.7.md).

## Problem statement

A destination can be reachable from two installations through a local mount,
NAS, SMB share, dual-boot system, or synchronized directory. Host names,
process-local locks, and last-write timestamps are not enough to decide who may
write or whose setting is authoritative. A safe design must prevent cooperating
clients from writing concurrently and must never resolve divergent edits by
silently selecting the last value observed.

## Protected assets

- readable backup payloads and their mapping to projects and snapshots;
- portable project settings and deletion history;
- encryption descriptors without secrets;
- durable conflict decisions and record provenance;
- evidence needed to explain which installation performed a write.

## Threat and failure cases

The protocol must handle:

- two 1.8.7 clients starting a write at nearly the same time;
- a crash, power loss, forced termination, or network loss during a write;
- delayed, cached, or reordered NAS/SMB observations;
- wall-clock skew between installations;
- host rename, operating-system reinstall, cloned config, and dual boot;
- a valid writer performing a long operation;
- a stale lease whose former owner later reconnects;
- a pre-1.8.7 client that ignores the protocol;
- independent edits to different fields and to the same field;
- repeated imports after Keep local or Accept remote;
- tombstones and machine-local encryption-key references.

The protocol is a reliability and coordination boundary between cooperating
clients, not a defense against a malicious administrator who can rewrite the
repository.

## Installation identity

- A cryptographically random identifier is created once in the private local
  application-data directory.
- The canonical serialized form is a lowercase 32-character GUID without
  punctuation.
- The file is owner-private where the platform supports Unix permissions.
- A missing identity may be created atomically. A malformed existing identity is
  reported as corruption and must not be silently replaced.
- Identity is independent of telemetry, opt-in state, account name, and mutable
  host name.
- Diagnostic UI may show a short prefix and the host name as a friendly label;
  the full durable identifier remains the authority.

Copying an application-data directory clones its identity. Before repository
lease rollout is complete, the implementation must detect a lease claiming the
same identity with a different active nonce and treat it as a conflict rather
than assuming it is the same process.

## Writer lease

The repository stores one coordination record in
`.vaultsync/meta/writer.lease.db`, separate from the portable metadata schema.
SQLite immediate transactions provide compare-and-swap ownership for cooperating
clients. The active record contains:

| Field | Meaning |
|---|---|
| protocol version | Parser and compatibility boundary |
| installation id | Durable owner identity |
| host label | Diagnostic display only |
| process id | Local diagnostic hint only |
| operation | Export, tombstone, migration, repair, or other write class |
| nonce | Random acquisition identity; prevents an old owner releasing a new lease |
| app version | Writer compatibility evidence |
| acquired UTC | Diagnostic timestamp |
| heartbeat UTC | Most recently renewed writer timestamp |
| expires UTC | Conservative stale threshold |

Acquisition uses create-if-absent semantics. If a valid unexpired record exists,
the second client receives a busy result and may continue read-only. A lease
holder renews before one third of the lease duration elapses. Release succeeds
only when installation id and nonce still match the on-disk record.

The lease primitive, read-only inspection, automatic heartbeat, nonce-bound
release, conservative expiry, explicit stale takeover, and takeover evidence
were implemented on the 1.8.7 release branch on 2026-08-12. Backup/history,
project-settings, project/snapshot/backup tombstone, deferred, and deferred-flush
writers now require lease ownership and verify their nonce again immediately
before changing metadata. Import and preview remain readable while a writer is
active; optional source-side tombstone repair is suppressed in that state.

Expiry is evidence that a lease may be stale, not permission for invisible
takeover. The user must explicitly confirm takeover; the old record is preserved
as diagnostic evidence before a new lease is acquired. A former owner whose
nonce no longer matches must abort before committing another write.

Settings exposes that decision per destination. Inspection is read-only and
shows the diagnostic host label, a short durable-identity prefix, operation,
application version, heartbeat, and expiry. Takeover is offered only for a stale
lease, requires a separate confirmation, remounts and rechecks the same resolved
repository and nonce, and records the displaced lease before clearing it. The
interface also states that clients older than 1.8.7 do not honor this protocol.

Clock-skew qualification includes clients offset in both directions. Expiry
decisions use conservative tolerance and observable record age where available;
they never use a future timestamp as proof that takeover is safe.

## Metadata merge

Each portable record needs:

- stable record identity;
- monotonically advancing revision scoped to that record;
- base revision from which an edit was made;
- durable writer identity and write timestamp;
- per-field value and portability classification;
- durable resolution record when a conflict is decided.

Given base `B`, local `L`, and remote `R`:

- if only one side differs from `B`, select that changed side;
- if both sides change different portable fields, combine them in preview;
- if both sides change the same field to the same value, accept the value once;
- if both sides change the same field differently, require explicit review;
- if the base is unknown, do not infer causality from timestamps alone.

Machine-local fields, including an encryption key reference that names a local
credential, are never auto-applied on another installation. Tombstones remain
preview-only until their affected entities and payload implications are shown.

Keep local publishes or records a resolution tied to the remote revision.
Accept remote records the inverse decision. The same unchanged pair must not
reappear. An undo record is valid only until a later write advances the affected
revision.

## Safe rollout order

1. Land and test durable installation identity without changing repository data.
   **Implemented on the 1.8.7 release branch on 2026-08-12.**
2. Add lease parsing and read-only busy diagnostics.
   **Implemented on the 1.8.7 release branch on 2026-08-12.**
3. Protect every metadata writer, including tombstones and repair/migration.
   **Implemented for the existing version-1 writers on 2026-08-12; every future
   migration or repair writer must enter through the same boundary.**
4. Add the versioned schema and forward migration fixtures.
   **Version-2 project writer/revision columns and version-1 compatibility were
   implemented on 2026-08-16. Durable per-source base revisions and field-level
   three-way planning were implemented on 2026-08-16; guarded repository writes,
   exported provenance, and bounded undo remain in the VS-1879 merge work.**
5. Produce merge plans without applying them.
6. Add explicit apply, durable resolution, and bounded undo.
   **Durable Keep local and Accept imported decisions are implemented and
   bounded; revision-aware undo remains in VS-1879.**
7. Expose status, takeover, and conflict review in the UI.
   **Writer status and explicit stale takeover were implemented on 2026-08-16;
   complete portable-field conflict review was implemented on 2026-08-16.**
8. Qualify local disk, SMB/NAS, disconnection, skew, crash, and mixed-version
   scenarios before enabling multi-machine writes by default.

## Non-negotiable tests

- concurrent identity creation returns one durable value;
- malformed identity fails closed;
- simultaneous lease acquisition produces exactly one writer;
- a mismatched nonce cannot renew, release, or commit;
- a reader remains available while a writer holds the lease;
- crash and expiry require explicit takeover and preserve evidence;
- version-1 repository migration preserves all records;
- independent edits merge and overlapping edits never silently overwrite;
- Keep local, Accept remote, and undo remain durable across restart;
- local key references and destructive tombstones never bypass preview;
- mixed 1.8.6/1.8.7 guidance is visible and tested where the UI exposes it.
