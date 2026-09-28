# Binary Source Control Strategy

This is the architecture and delivery contract for VaultSync Binary Source Control
(BSC) in the 1.9 Recovery Horizon family.

`ROADMAP.md` remains the canonical source for work IDs, priority, release
assignment, and delivery state. This document explains the product boundary,
storage model, safety invariants, sequencing, and qualification gates behind
`VS-1981` through `VS-1993`.

## Product definition

Binary Source Control is a binary-first version-control mode for projects whose
large or merge-hostile assets are a poor fit for ordinary source-code workflows.

The target use cases include game-development assets, 3D scenes and models,
audio/video source material, design files, large generated-but-authoritative
project assets, and similar data where exact historical reconstruction,
exclusive editing, and predictable storage matter more than textual merge.

BSC is deliberately not:

- a replacement for Git source-code workflows;
- a Git server or a Git LFS protocol implementation in 1.9;
- a hosted SaaS control plane;
- ordinary VaultSync backup history with a different label;
- the same feature as `VS-1801`, which remains the separate candidate for
  protecting complete Git repository state including `.git`;
- a generic automatic binary merge engine.

A project may use Git for source code and VaultSync BSC for selected binary
assets. VaultSync may detect Git only to explain coexistence. It must not modify
`.git`, rewrite Git history, or edit Git attributes automatically.

## Why BSC is a separate repository model

VaultSync snapshots and backups are recovery-oriented observations of a source
tree. Source control adds different semantics:

- explicit check-in rather than only scheduled capture;
- a canonical version graph;
- workspaces tracking a repository ref;
- staged versus unstaged changes;
- long-lived path locks for merge-hostile assets;
- guarded ref updates and stale-workspace detection;
- branch and conflict semantics;
- repository garbage collection based on reachability rather than backup
  retention alone;
- exact authorship/workspace provenance and audit events.

These semantics must not be bolted onto `Snapshot`, `Backup`, or the current
portable metadata schema until they become ambiguous. BSC receives a separate,
versioned repository format and domain model while reusing proven VaultSync
primitives where their contracts actually match.

Reusable foundations include durable installation identity, repository writer
leases, evidence/provenance conventions, destination identity, secure local
secret storage, recovery reporting, release-format discipline, and the 1.9
portable/offsite recovery work.

## Design references and lessons

The design is informed by several established approaches without copying one
system wholesale:

- Git LFS keeps large file contents outside ordinary Git blobs and uses pointer
  objects; its locking model demonstrates why large binary assets need explicit
  lock semantics.
- Perforce treats exclusive locking as a normal workflow for unmergeable binary
  assets and distinguishes full-file storage, compressed storage, and delta
  transfer. It also documents that already-compressed formats are often poor
  candidates for binary delta transfer.
- Unity Version Control exposes lock rules and checkout workflows for files that
  cannot be merged safely.
- restic and Borg demonstrate content-defined chunking (CDC), content-addressed
  immutable data, repository-level deduplication, rebuildable indexes, and
  reachability-based maintenance.
- FastCDC is the leading CDC candidate because it was designed to reduce the CPU
  overhead of byte-by-byte rolling-hash chunking while retaining useful
  deduplication behavior.
- VCDIFF remains a useful reference for a future transfer-level binary delta,
  not the canonical history representation.

The important conclusion for VaultSync is that canonical history should not be
a long chain of binary deltas. A damaged base or missing intermediate delta
must not invalidate many later revisions.

## Non-negotiable storage principles

1. **Immutable data first.** Chunks, packs, trees, and changesets are immutable
   after publication.
2. **Refs are the visibility boundary.** A new changeset becomes visible only
   after every referenced immutable object is durable and verified.
3. **No long canonical delta chains.** Storage efficiency comes primarily from
   chunk reuse and compression. Binary delta encoding may later optimize
   network transfer or cache population, but a repository must not need an
   unbounded revision chain to reconstruct a file.
4. **Repository data is authoritative.** The local application database and
   workspace cache accelerate operations; they are not required to reconstruct
   history.
5. **Indexes are rebuildable.** Losing an index may be expensive, but it must
   not mean losing history if the immutable objects remain.
6. **Unknown formats fail closed.** A client never writes through a repository
   version it does not understand.
7. **Exact bytes are preserved.** BSC does not normalize line endings or
   reinterpret binary formats.
8. **Recovery precedes cleanup.** Maintenance writes replacements before
   removing old data and uses quarantine/grace periods for unreachable data.

## Proposed domain model

The exact serialization is decided by `VS-1981`, but the logical model is:

### Repository

Stable repository identity plus:

- repository-format version;
- object/chunking format version and parameters;
- crypto descriptor and key identity when encrypted;
- creation and compatibility metadata;
- feature flags that readers must understand before writing.

### Workspace

A machine-local checkout associated with:

- repository identity;
- durable workspace identity;
- installation identity;
- local root;
- tracked ref/branch;
- base changeset;
- sparse/path rules;
- local scan/index cache;
- local author display configuration.

The workspace identity is not an authentication credential.

### File object

A canonical record containing at least:

- repository-relative canonical path only in the owning tree, not inside the
  content object identity;
- logical file length;
- ordered chunk references;
- whole-file content hash;
- executable bit where meaningful;
- symbolic-link type and link target when supported;
- format-versioned metadata required for exact reconstruction.

Modification time is useful workspace metadata but is not content identity.

### Tree

A deterministic directory/tree object mapping canonical names to child trees,
file objects, or supported links.

### Changeset

An immutable object containing:

- root tree identity;
- one or more parent changeset identities;
- repository identity and format;
- author label plus installation/workspace provenance;
- UTC timestamp;
- check-in message;
- application/build identity;
- optional resolution/audit references.

The changeset ID is derived from canonical serialized content.

### Ref

A small mutable name pointing to a changeset. Ref updates require an expected
old value and must fail rather than overwrite a head that moved unexpectedly.

### Tag

A named recovery/milestone marker with explicit immutability or controlled-move
semantics. Stable tags should default to immutable.

### Path lock

A long-lived cooperative editing lease distinct from the short repository
writer lease. It records:

- repository and ref/branch scope;
- canonical path;
- owner installation and workspace identities;
- display author;
- base changeset;
- random nonce;
- acquisition, heartbeat, expiry, and application version;
- takeover and release audit evidence.

## Repository layout and backend abstraction

The physical layout is versioned and may use packs rather than one file per
chunk. The logical backend must support:

- immutable put-if-absent objects;
- immutable object reads/range reads;
- object listing for rebuild and repair;
- guarded mutable ref/lock state;
- durable rename/replace or an equivalent conditional-write primitive;
- repository-level maintenance coordination.

A filesystem/NAS backend is first. A future object-storage backend must map the
same logical repository to conditional requests/versioned objects rather than
creating a second source-control format.

SQLite may be used for efficient coordination, indexes, or local caches on the
filesystem backend, but clean-machine recovery cannot require an opaque local
SQLite database that is the only copy of the history graph.

A possible version-1 physical namespace is:

```text
<destination>/.vaultsync/bsc/<repository-id>/
  format/
  packs/
  indexes/
  objects/
  refs/
  locks/
  audit/
  quarantine/
```

This is illustrative. `VS-1981` owns the final format.

## Chunking and deduplication

### Preferred direction

Use content-defined chunking so an insertion or deletion near the beginning of
a large, otherwise-similar binary does not shift every following fixed-size
block and force full restorage.

FastCDC is the leading implementation candidate. The algorithm identifier and
parameters are repository-format data and cannot change silently.

The first benchmark profile should test approximately:

- minimum chunk: 512 KiB;
- target chunk: 2 MiB;
- maximum chunk: 8 MiB;

alongside smaller and larger targets. These values are test candidates, not a
format promise.

Qualification must include:

- multi-gigabyte files;
- small edits near beginning/middle/end;
- insertions and deletions;
- already-compressed formats;
- files that rewrite globally on save;
- many small files;
- sparse files;
- repeated identical assets across paths and revisions.

### Object identity

SHA-256 is the current VaultSync integrity primitive and is the baseline
candidate for object verification. Encryption may require a keyed repository
object identifier or separate keyed lookup identity so object names do not
unnecessarily expose plaintext equality to someone who only sees the storage
namespace.

The repository may deduplicate within itself. Cross-repository global
deduplication is not a 1.9 goal because it complicates isolation, retention,
encryption, deletion, and privacy.

### Packing

Small/medium chunks should be grouped into immutable pack files so NAS and
object-storage backends do not suffer from millions of tiny filesystem objects.

Pack indexes contain chunk identity, offset, encoded length, logical length,
encoding, and integrity metadata. Indexes must be reconstructable from pack
headers/trailers or another authoritative self-describing structure.

The exact pack target size is benchmarked. Repacking is maintenance, not a
history rewrite.

### Compression

Compression is per object/chunk or otherwise independently recoverable. The
engine should avoid wasting CPU recompressing formats that are already
compressed or encrypted.

The codec and level are repository-format descriptors. A reader must know
exactly how to decode an object or reject it.

## Why canonical binary deltas are deferred

Classic binary delta compression can be useful when a large uncompressed file
changes in a small region, but it introduces choices about the base revision,
delta-chain depth, random access, corruption propagation, GC dependencies, and
repair.

For VaultSync, these costs conflict with recovery independence.

Therefore:

- canonical version-1 history uses chunk references, not unbounded patch chains;
- optional bounded deltas may be evaluated later inside independently
  verifiable objects;
- transport may eventually send a VCDIFF-like delta when both peers already
  possess a verified base and the measured changed ratio makes it worthwhile;
- compressed formats such as JPEG/PNG/video/ZIP-style documents may simply be
  transferred as new chunks when edits rewrite most bytes.

Transfer optimization must never change changeset identity or reconstruct a
different byte stream.

## Commit publication protocol

A check-in is visible only after a guarded publish sequence.

Conceptually:

1. Acquire the short repository writer lease needed for mutable repository
   metadata/ref publication.
2. Re-read the tracked ref and verify the workspace expected base.
3. Verify required path-lock ownership.
4. Scan/stage selected paths and build exact file objects.
5. Write any missing immutable chunks/packs.
6. Durably publish pack indexes.
7. Publish file/tree objects.
8. Publish the immutable changeset.
9. Compare-and-swap the ref from expected head to the new changeset.
10. Record audit/evidence.
11. Release the repository writer lease.

If cancellation or failure occurs before step 9, the old head remains valid and
new data is unreachable. Later maintenance may quarantine and collect it.

If step 9 fails because another writer advanced the ref, VaultSync must not
force the update. It computes a stale-base/conflict plan.

## Workspace status and staging

Status must scale beyond a naive full rehash of every file on every refresh.

Use a layered approach:

1. Enumerate paths and cheap metadata.
2. Reuse verified local scan-cache entries where size/mtime/file identity rules
   permit.
3. Hash changed candidates.
4. Chunk only paths needed for check-in or deep verification.

The cache is advisory. A check-in always establishes the final cryptographic
identity of staged content.

Status classes include:

- untracked;
- added;
- modified;
- deleted;
- type changed;
- rename hint;
- locked by me;
- locked by another workspace;
- stale against repository head;
- conflicted;
- ignored/excluded.

Rename detection may use identical whole-file IDs or high chunk overlap as a UI
hint. Repository correctness must not depend on a heuristic rename detector.

## Checkout and sync safety

Checkout/sync must preserve user work above convenience.

Rules:

- never overwrite a dirty path silently;
- never delete an untracked path silently because a target changeset does not
  contain it;
- stage replacement bytes into a temporary sibling/location;
- verify complete content before final replacement;
- use atomic replace/rename where supported;
- if atomic replacement is unavailable, record the operation state and retain a
  rollback copy until success;
- re-check path type/link safety immediately before replacement;
- cancellation returns the old verified file or the new verified file, not a
  partial file;
- a force/discard operation requires an exact preview of affected paths.

## Cross-platform path contract

Repository paths use one canonical separator and normalized UTF-8 representation
defined by the format.

The engine must explicitly handle:

- Windows reserved names and characters;
- case-insensitive versus case-sensitive workspaces;
- Unicode normalization differences;
- path-length limits;
- symbolic links and junction/reparse-point boundaries;
- executable permission on Unix-like systems;
- path traversal and rooted paths.

A repository may contain names that a specific target platform cannot
materialize. The reader can still inspect them, but checkout must explain the
incompatibility instead of silently renaming data.

Case-colliding sibling paths must be rejected for a workspace that cannot
represent them.

## Locking model

Binary assets are frequently not meaningfully mergeable, so locking is a
first-class workflow.

Lock rules are repository policy patterns, not hard-coded extension behavior.
VaultSync may offer presets for common game/3D/audio/video formats.

The system supports:

- checkout without lock for paths whose policy permits it;
- lock and checkout;
- explicit unlock;
- revert unchanged and release;
- inspect lock owner;
- stale takeover after expiry and explicit confirmation.

Making a local file read-only when it is not locked is helpful UX, but it is not
the security boundary. Commit-time repository checks are authoritative.

An expired lock is evidence that the owner may be gone, not automatic
permission for invisible takeover. The existing RepositoryLeaseService
takeover philosophy applies: re-read, compare nonce, show owner/base/time,
confirm, preserve displaced evidence, then replace.

A client that does not understand the lock protocol cannot safely participate
as a writer.

## Concurrency and stale workspaces

The current repository writer lease serializes short mutations. BSC file locks
must not hold that global writer lease for hours or days.

Instead:

- editing lock = persistent path record;
- check-in/ref update = short repository write lease;
- maintenance = exclusive maintenance lease.

Ref publication is compare-and-swap against the expected base. If the head has
moved:

- if the staged paths do not overlap remote changes, VaultSync may offer a
  deterministic rebase/update plan;
- if the same path changed to byte-identical content, it may collapse that
  conflict;
- if both sides changed a binary differently, explicit resolution is required;
- timestamps never decide the winner.

## Branches and binary conflicts

Branch creation is a cheap ref operation.

Branch integration uses a three-way tree/path plan from merge base, ours, and
theirs.

Automatic resolution is limited to cases that are provably safe:

- changed on one side only;
- deleted/unchanged combinations with unambiguous policy;
- both sides produce the same content identity;
- independent paths.

An overlapping binary edit is not automatically byte-merged. The user chooses:

- ours;
- theirs;
- keep both under explicit names;
- replace with an externally resolved file.

Resolution creates a new changeset and retains provenance. Existing history is
never rewritten silently.

## Encryption model

BSC encryption requires a new chunk/object-level contract; archive-level backup
encryption must not be reused blindly.

Goals:

- independent authenticated objects/chunks;
- exact corruption/wrong-key detection before bytes are trusted;
- repository-scoped key identity;
- no plaintext password or derived key in portable metadata;
- OS secure-store use for routine credentials;
- an explicit portable recovery-kit path so loss of local application data does
  not inherently destroy access;
- interruption-safe key rotation;
- no accidental cross-repository deduplication.

The format must separate:

- KDF descriptor;
- encrypted repository key material/recovery envelope;
- object encryption descriptor;
- non-secret algorithm/version metadata;
- machine-local credential reference.

The exact AEAD, nonce, keyed-ID, and rotation design is a `VS-1981` /
`VS-1989` security decision and requires dedicated fixtures.

## Verification and recovery

BSC inherits VaultSync's recovery-first philosophy.

Verification levels:

### Metadata/index audit

Checks parseability, versions, ref targets, pack/index relationships, lock
format, and obvious missing references without reading all payload bytes.

### Reachability audit

Traverses every protected ref/tag and proves every referenced tree, file object,
chunk, and pack exists.

### Full content verification

Reads, decrypts/decompresses, hashes, and validates reachable stored data.

### Recovery drill

Materializes a selected changeset to an isolated target, re-hashes the output,
and records evidence.

A clean machine with the repository and authorized key material must be able to
discover repository identity, inspect refs/history, verify data, and restore a
changeset without the original app database.

## Garbage collection, quarantine, and repack

Committed history is retained by default. Backup-retention rules do not silently
prune source-control history.

GC roots include:

- all live refs;
- protected tags;
- maintenance/recovery roots required by in-progress safe operations;
- explicit retention roots created by future policy.

GC sequence:

1. acquire maintenance exclusivity;
2. capture the root set;
3. mark all reachable immutable objects;
4. classify unreachable objects as candidates;
5. move/log candidates into a grace-period quarantine model where practical;
6. re-check roots before destructive deletion;
7. delete only after the policy window and verification gates;
8. record maintenance evidence.

Repack/compaction writes replacement packs and indexes first, switches indexes
only after verification, then retires old packs. Power loss must leave at least
one valid reachable copy.

## Sparse workspaces and transfer

Large repositories need partial materialization.

Sparse rules operate on repository paths and are part of workspace state. They
do not change changeset identity.

A local verified cache may retain chunks/packs across branch changes. Cache
entries are untrusted until their IDs/hashes validate. Eviction affects
performance, not history.

Transfer planning is object-based:

- enumerate required objects for the target tree;
- subtract verified local/cache objects;
- batch/range-read missing pack regions where possible;
- resume incomplete transfers;
- verify before materialization.

Future delta transfer is permitted only between verified base/target objects and
must fall back to full object transfer whenever unsuitable.

## Offsite and replication

BSC does not require cloud storage to function.

When 1.9 Offsite Protection is ready, a BSC repository may be mirrored to
supported object storage using the same immutable identities.

A mirror is reported healthy for a changeset only after all objects reachable
from that changeset and its required repository metadata are durable and
verifiable at the destination.

Object-store mutable state requires conditional writes/versioning semantics
equivalent to guarded refs and locks. A backend that cannot provide a safe
coordination contract is read-only/replica-only rather than pretending to be a
multi-writer source-control server.

## Git coexistence

A common workflow is expected to be:

- Git: code, text configuration, scripts, small mergeable assets;
- VaultSync BSC: selected large or merge-hostile assets;
- VaultSync Backup: independent recovery copy of the working project and/or BSC
  repository.

The products are separate layers.

In 1.9 VaultSync may:

- detect a Git worktree;
- warn when the same large binary appears to be controlled by both systems;
- provide suggested ignore/track guidance;
- show the current Git branch only as optional local context if it can do so
  without modifying Git.

It does not:

- become a Git remote;
- generate Git pointer files automatically;
- alter `.gitattributes` or `.gitignore` without an explicit future feature;
- assume VaultSync branches and Git branches are identical;
- claim that BSC replaces Git LFS for every workflow.

## UI information architecture

BSC should live under the 1.9 goal-oriented shell without turning ordinary
backup pages into source-control pages.

Suggested workspace:

### Protect / Version Control

Repository card:

- repository and workspace name;
- tracked ref;
- sync state;
- changed/staged count;
- locks held / blocked paths;
- verification state;
- repository storage use and dedup reuse;
- destination health.

Primary actions:

- Refresh status;
- Lock / Unlock;
- Stage / Unstage;
- Check in;
- Sync;
- History.

Expert actions:

- Branches/tags;
- Verify;
- Repository maintenance;
- Sparse rules;
- Recovery kit / encryption;
- Mirror/replication.

### History

Changeset timeline with:

- message;
- author/workspace;
- exact ID;
- parent(s);
- changed paths;
- size added versus logical bytes;
- verification/evidence state.

### Recover

Recover a file/subtree/changeset through the shared Recovery experience with
isolated-target defaults and exact overwrite previews.

## CLI surface

Names are subject to the 1.9 CLI contract, but the functional surface must
cover:

```text
vaultsync source init
vaultsync source open
vaultsync source status
vaultsync source stage
vaultsync source unstage
vaultsync source lock
vaultsync source unlock
vaultsync source commit
vaultsync source sync
vaultsync source log
vaultsync source show
vaultsync source restore
vaultsync source branch
vaultsync source tag
vaultsync source verify
vaultsync source gc
```

Automation-safe structured output should include stable machine-readable result
codes for stale ref, dirty workspace, lock conflict, unsupported format,
corruption, missing key, unavailable repository, and maintenance contention.

## Release sequencing

### Architecture gate — starts before BSC implementation

`VS-1981` and `VS-1982` must be approved before `VS-1983` onward are treated
as stable implementation commitments.

Prototypes and benchmarks are allowed to inform the decision.

### 1.9.6 — Binary Source Control Foundation

Stable target:

- immutable chunk/object store;
- canonical trees/changesets/default ref;
- workspace status/staging/check-in;
- safe sync/checkout;
- exclusive path locks;
- history/browse/exact restore;
- encryption/recovery-key path;
- verify/rebuild/GC;
- desktop + CLI foundation.

1.9.6 does not require branches, sparse workspaces, or offsite multi-writer
repositories to call the foundation complete.

### 1.9.7 — Binary Collaboration and Scale

Adds:

- branches and explicit binary conflict handling;
- sparse workspaces and verified local cache;
- resumable transfer;
- offsite mirror/replication integration where backend semantics are safe;
- final multi-machine/NAS/large-repository qualification.

### 1.9.8 — Stability and LTS Baseline

The existing 1.9 stability/LTS work moves here without renumbering its IDs.

This release freezes supported BSC format compatibility alongside disk-image,
portable-recovery, and offsite formats, then runs long-duration migration and
fault-injection qualification.

## Stable gates

BSC may be labelled Stable only when all of the following are true:

- no published changeset can reference missing data under tested interruption
  points;
- concurrent writers never silently overwrite one another;
- path-lock ownership is enforced at commit and stale takeover retains evidence;
- checkout/sync never silently destroys dirty local work;
- every supported changeset is reconstructable on a clean machine;
- index/cache loss is recoverable from authoritative repository data;
- full verification detects injected corruption;
- GC/repack fault tests preserve at least one valid copy of reachable data;
- encrypted recovery works without the original installation when the user has
  the documented recovery material;
- Windows, macOS, Linux, and representative NAS/SMB qualification pass;
- performance budgets cover repository open, status scan, hashing/chunking,
  check-in, sync, history, verification, and GC;
- unsupported path/platform/backend states are explicit.

If these gates are not met, BSC remains Preview and does not block earlier
Recovery Horizon releases.

## Qualification datasets

The test program should include synthetic reproducible repositories for CI plus
controlled real-asset smoke tests.

Required dimensions:

- 100k, 500k, and where practical 1M paths;
- 100 GB, 1 TB, and multi-TB logical history without requiring that every CI
  worker physically stores the full logical size;
- 1 GB, 10 GB, and larger individual generated files;
- highly deduplicable edits;
- low-dedup globally rewritten/compressed assets;
- rename-heavy trees;
- case collisions and Unicode edge cases;
- sparse files;
- repeated branch/ref movement;
- thousands of changesets;
- repeated verify/GC/repack cycles.

Measure at minimum:

- scan/status latency and p95;
- CPU and allocations;
- logical bytes versus new stored bytes;
- chunk-reuse ratio;
- check-in and sync throughput;
- first checkout versus warm-cache checkout;
- recovery throughput;
- verification throughput;
- GC/repack duration and temporary-space amplification;
- cancellation time.

## Fault-injection matrix

Inject termination or I/O/network failure during:

- chunk write;
- pack finalization;
- index publication;
- tree publication;
- changeset publication;
- ref update;
- lock acquisition/renew/release;
- sync replacement;
- encryption-key rotation;
- verification;
- mirror upload;
- GC mark;
- quarantine;
- repack;
- old-pack retirement.

The invariant is always that VaultSync can explain which state is authoritative
and preserve the last valid reachable history.

## Security boundary

BSC inherits the cooperating-client boundary of current repository coordination.
It must defend against accidental corruption, crashes, network faults, stale
clients, path attacks, and conflicting cooperating writers.

It does not claim that a user with unrestricted write access to the repository
storage cannot maliciously rewrite or delete history. Cryptographic commit
signing, hostile-admin tamper evidence, hosted identity, and ACL management may
be evaluated later, but 1.9 must not imply those guarantees without implementing
and qualifying them.

## Relationship to other roadmap items

- `VS-1910`: the new 1.9 shell must leave room for Version Control without
  duplicating navigation state.
- `VS-1919`: BSC repository/workspace/key/mirror identities participate in the
  common recovery dependency and evidence model.
- `VS-1931`–`VS-1934`: portable recovery establishes the independent-reader
  discipline reused by BSC.
- `VS-1921`–`VS-1925`: offsite backends may mirror BSC after the conditional
  write and recovery contracts are proven.
- `VS-1953` / `VS-1956`: BSC CLI behavior follows the same structured,
  automation-safe contract.
- `VS-1961`–`VS-1963`: the final LTS baseline includes BSC once its formats
  are stable.
- `VS-1801`: full Git-repository backup remains a separate candidate and is not
  silently absorbed by BSC.

## Work IDs

- `VS-1981` — architecture, format, and product boundary — issue #703.
- `VS-1982` — identity, ownership, locking, conflict, audit — issue #704.
- `VS-1983` — immutable CDC object store — issue #705.
- `VS-1984` — trees, changesets, refs, atomic publication — issue #706.
- `VS-1985` — workspace engine — issue #707.
- `VS-1986` — exclusive binary locking — issue #708.
- `VS-1987` — history, restore, compare, evidence — issue #709.
- `VS-1988` — branches and binary conflict resolution — issue #710.
- `VS-1989` — encryption and portable key recovery — issue #711.
- `VS-1990` — verification, rebuild, GC, disaster recovery — issue #712.
- `VS-1991` — sparse/cache/transfer/offsite replication — issue #713.
- `VS-1992` — desktop, CLI, Git coexistence — issue #714.
- `VS-1993` — scale, cross-platform, NAS, and fault qualification — issue #715.
