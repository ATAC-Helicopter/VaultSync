# VaultSync 1.9 — Recovery Horizon
## Full Family Roadmap and Audited Binary Source Control Integration

**Document type:** Consolidated planning reference  
**Family:** `1.9.x` — Recovery Horizon  
**Status:** Planned / staged delivery  
**Canonical implementation source:** `ROADMAP.md` in the VaultSync repository  
**BSC technical contract:** `docs/BINARY_SOURCE_CONTROL_STRATEGY.md`

> **1.8 proves and explains recovery.**  
> **1.9 expands what VaultSync can recover.**

---

# 1. Family Promise

> Recover from disk and machine-level failure with inspectable evidence and a
> bootable path that does not depend on the installed operating system.

Recovery Horizon expands VaultSync from project/file protection into a broader
recovery system capable of surviving installation, disk, and machine loss.

The family is deliberately split into staged releases. Disk recovery is not
blocked by later Binary Source Control work, and Binary Source Control is not
allowed to become a source-control rewrite that delays the core recovery path.

The family progression is:

| Order | Release | Primary outcome |
|---:|---|---|
| 1 | `1.9.0` | Disk and Bootable Recovery Foundation |
| 2 | `1.9.1` | Clone Explorer |
| 3 | `1.9.2` | Portable Recovery |
| 4 | `1.9.3` | Offsite Protection |
| 5 | `1.9.4` | Unified Recovery Experience |
| 6 | `1.9.5` | Continuous Recovery Assurance |
| 7 | `1.9.6` | Binary Source Control Foundation |
| 8 | `1.9.7` | Binary Collaboration and Scale |
| 9 | `1.9.8` | Stability and LTS Baseline |

The intended progression is:

```text
1.8.x
BACK UP
+ VERIFY
+ PROVE RECOVERY
        │
        ▼
1.9.0
RECOVER DISKS / BOOT INDEPENDENTLY
        │
        ▼
1.9.1
INSPECT AND EXTRACT FROM IMAGES
        │
        ▼
1.9.2
RECOVER WITHOUT THE ORIGINAL INSTALLATION
        │
        ▼
1.9.3
RECOVER FROM OFFSITE STORAGE
        │
        ▼
1.9.4
ONE RECOVERY EXPERIENCE
        │
        ▼
1.9.5
CONTINUOUSLY PROVE RECOVERY
        │
        ▼
1.9.6
VERSION LARGE / MERGE-HOSTILE BINARY PROJECT DATA
        │
        ▼
1.9.7
COLLABORATE AND SCALE BINARY REPOSITORIES
        │
        ▼
1.9.8
FREEZE, QUALIFY, AND SUPPORT THE 1.9 FORMAT FAMILY
```

---

# 2. Recovery Horizon Architecture Approval Gate

Feature implementation does not begin merely because `1.9` is the next release
family. Before destructive disk work or stable support claims, the following
contracts must be reviewed together:

- imaging-engine strategy and isolation boundary;
- versioned disk-image format;
- incomplete, interrupted, resumable, and corrupt-state behavior;
- operating-system, filesystem, partition-table, encryption, live-capture,
  Secure Boot, driver, and hardware support matrix;
- security model for compromised sources, credentials, keys, recovery media,
  immutable storage, and provider-account loss;
- portable recovery dependencies and the minimum independent restore path;
- recovery identities, dependencies, evidence provenance, and failure domains;
- Binary Source Control repository/object format;
- BSC workspace/ref model;
- BSC locking and conflict semantics;
- BSC encryption and portable recovery boundary;
- BSC GC, repair, and corruption invariants;
- local/NAS-to-offsite BSC backend contract.

Unsupported combinations must remain explicit.

A prototype, benchmark, or technology spike may inform an architecture decision.
It does not itself establish a stable support promise.

---

# 3. 1.9 UI Migration Program

The interface migration is staged rather than implemented as a one-shot visual
rewrite.

Target information architecture:

- **Protect**
- **History**
- **Recover**
- **Manage**

Principles:

- organize workflows around user intent rather than implementation details;
- separate routine state/actions from expert configuration;
- keep one authoritative route/state model;
- preserve deep links, selection, filters, and unfinished work;
- migrate one workflow family at a time;
- do not remove the legacy workflow until functional parity is demonstrated;
- retain keyboard accessibility, localization, themes, narrow-layout support,
  scaling support, and assistive-technology behavior;
- use explicit usability testing, opt-in feedback, and local diagnostics rather
  than hidden telemetry to judge the migration.

---

# 4. Existing 1.9 Foundation Work

- `VS-1902` `P1` — Add trusted application signing where operationally and
  financially feasible.
- `VS-1903` `P2` — Background integrity audits with alerts. This is already
  delivered foundation work; future recovery-assurance expansion receives new
  release-specific identifiers.

---

# 5. 1.9.0 — Disk and Bootable Recovery Foundation

**Tagline:** *Recover when the installed system cannot.*

This release establishes the complete recovery loop:

> **Capture → Validate → Boot independently → Restore → Validate again**

Disk cloning is not considered complete as an isolated copy feature. Stable
promotion requires image creation, validation, independent boot/recovery,
restore, and post-restore validation to work together on the supported matrix.

## Work items

### `VS-1910` `P0` — 1.9 information architecture

Define:

- route model;
- workflow boundaries;
- navigation invariants;
- migration map from the legacy shell.

### `VS-1917` `P0` — Versioned disk-image format

The image format must represent:

- stable source identity;
- partition layout;
- block/sparse maps;
- integrity records;
- compression descriptors;
- encryption descriptors;
- incomplete state;
- resumable/checkpoint state;
- application/build identity;
- reader/writer compatibility.

Acceptance:

- incomplete images cannot appear valid;
- missing or corrupt image regions are detectable;
- unsupported future formats are rejected without mutation.

### `VS-1918` `P0` — Imaging-engine and supported-system matrix

Decide:

- build versus third-party engine integration;
- privilege boundaries;
- process isolation;
- supported operating systems;
- supported filesystems;
- partition-table support;
- encrypted-volume behavior;
- live-capture behavior;
- Secure Boot behavior;
- recovery-media signing;
- driver requirements;
- representative hardware.

Every supported combination requires a qualification method. Every unsupported
combination requires explicit application and documentation behavior.

### `VS-1919` `P0` — Shared recovery identity and evidence model

Model recovery-critical concepts including:

- devices;
- sites;
- repositories;
- destinations;
- credentials;
- encryption-key references;
- recovery media;
- recovery tools;
- recovery points;
- verification;
- drills;
- dependency/failure-domain correlation.

Evidence must distinguish:

- recorded fact;
- measured evidence;
- simulation;
- inference;
- user confirmation;
- stale evidence;
- missing evidence;
- unsupported checks.

### `VS-1904` `P0` — Isolated cloning engine

Build disk/partition cloning with explicit operation states and checkpoint
contracts.

### `VS-1905` `P0` — Destructive-operation safety

Add:

- source/destination identity;
- exact overwrite preview;
- capacity checks;
- source = destination protection;
- interruption semantics;
- block validation.

### `VS-1906` `P0` — Bootable recovery media

Produce recovery media for supported UEFI systems with:

- offline device discovery;
- image discovery;
- image validation;
- recovery workflows independent of the installed OS.

### `VS-1907` `P0` — Image-to-disk / partition restore

Restore supported images from the bootable recovery environment.

### `VS-1908` `P1` — Recovery evidence

Record:

- clone evidence;
- verification evidence;
- boot evidence;
- restore evidence;
- post-restore validation;
- source-machine identity;
- recovery-tool version.

### `VS-1909` `P0` — End-to-end qualification

Qualify the complete clone-to-bootable-recovery path on representative physical
hardware and virtual machines.

## 1.9.0 stable gate

Stable promotion requires all of the following:

- source and destination cannot be confused silently;
- destructive operations display an exact wipe/overwrite preview;
- interrupted images are rejected or safely resumable;
- image verification detects incomplete or corrupt data;
- recovery media boots independently of the installed OS;
- supported images can be restored from that environment;
- restored media passes the documented validation procedure;
- filesystem, encryption, Secure Boot, live-capture, and hardware limitations
  are explicit;
- VaultSync does **not** claim universal cross-hardware bare-metal recovery.

If the complete disk-recovery loop cannot satisfy the stable gate, the imaging
feature remains clearly labelled Preview rather than weakening the gate.

---

# 6. 1.9.1 — Clone Explorer

Clone Explorer turns a disk image from an opaque recovery object into a
read-only, inspectable recovery source.

## Work items

- `VS-1911` `P1` — Browse supported image partitions and files read-only.
- `VS-1912` `P1` — Search and selectively extract files from images.
- `VS-1913` `P1` — Inspect image creation, verification, and compatibility.
- `VS-1914` `P2` — Compare supported clone images where safe and practical.
- `VS-1915` `P0` — Introduce the adaptive 1.9 shell and typed route/state
  infrastructure without removing existing workflow views.
- `VS-1916` `P1` — Migrate Dashboard and Recovery entry points into
  goal-oriented home and recovery workspaces.

## Product behavior

Clone Explorer should support, where the underlying image/filesystem permits:

- partition-map inspection;
- filesystem identification;
- directory browsing;
- search;
- metadata inspection;
- selective extraction;
- verification-state inspection;
- image compatibility information;
- comparison of supported images.

Images are treated as read-only recovery sources by default. Inspection must not
silently mutate the source image.

---

# 7. 1.9.2 — Portable Recovery

Portable Recovery removes the original VaultSync installation and local database
as mandatory recovery dependencies.

Provider expansion deliberately follows this release because remote storage is
not a complete recovery path until a fresh installation can discover, inspect,
unlock, restore, and verify supported data.

## Work items

- `VS-1931` `P0` — Ship a standalone desktop restore utility.
- `VS-1932` `P1` — Generate an emergency recovery kit.
- `VS-1933` `P0` — Publish the supported backup and encryption format
  specification.
- `VS-1934` `P0` — Define compatibility, migration, deprecation, and emergency
  read-only policies.
- `VS-1935` `P1` — Migrate History, Snapshot Explorer, and Settings into
  focused activity, inspection, and management workspaces.

## Target recovery scenario

A supported recovery path must work when:

1. the original computer is unavailable;
2. the original VaultSync installation is unavailable;
3. the original local SQLite database is unavailable;
4. a user has only the protected repository/data and authorized key material;
5. a fresh recovery tool discovers the available recovery points;
6. the user can inspect, verify, unlock, and restore them.

The recovery kit must not silently contain plaintext passwords or equivalent
secret material.

---

# 8. 1.9.3 — Offsite Protection

This release adds remote recovery targets without weakening the recovery-first
contract.

## Work items

- `VS-1921` `P0` — Resumable S3-compatible object-storage destinations.
- `VS-1922` `P1` — Backblaze B2 and SFTP destination profiles.
- `VS-1923` `P0` — Validate remote manifests and clean incomplete uploads.
- `VS-1924` `P1` — Explain immutability, object lock, retention, and cost.
- `VS-1925` `P1` — Prove clean-machine recovery from supported offsite data.
- `VS-1926` `P1` — Migrate Projects, Backups, and Schedule into one
  progressive-disclosure Protection workspace.
- `VS-1927` `P1` — Add multi-destination health scoring and safe, explainable
  automatic failover.

## Required properties

Remote protection must support:

- resumable upload;
- resumable download;
- incomplete-transfer detection;
- remote manifest validation;
- retry behavior;
- integrity verification;
- clean-machine restore;
- explicit credential failure;
- explainable provider limitations;
- cost/retrieval/egress awareness where relevant.

VaultSync must distinguish between:

- protection from VaultSync retention rules; and
- actual storage-side immutability/object lock.

They are not equivalent.

---

# 9. 1.9.4 — Unified Recovery Experience

By this point VaultSync may expose file backups, snapshots, archives, disk
images, portable recovery, and offsite recovery. They must not feel like
independent products.

## Work items

- `VS-1941` `P0` — Complete unified project, file, image, storage, and
  recovery navigation; remove the legacy shell only after parity.
- `VS-1942` `P1` — Add a shared Recovery Inspector across recovery types.
- `VS-1943` `P2` — Revisit project groups and high-density organization if the
  earlier project-group work remains outstanding.
- `VS-1944` `P0` — Qualify the entire 1.9 interface across supported widths,
  scaling, keyboard, screen reader, theme, and interrupted-work states.

## Recovery Inspector

The shared inspector should answer:

- What is this recovery source?
- When was it created?
- Where is it stored?
- Is it complete?
- Has it been verified?
- Is it encrypted?
- Is the key path available?
- Has it been restore-tested?
- Which recovery methods are supported?
- Which limitations apply?
- What is the next safe action?

---

# 10. 1.9.5 — Continuous Recovery Assurance

Recovery evidence must remain current. A successful backup months ago is not
automatically proof of present recoverability.

## Work items

- `VS-1951` `P1` — Schedule full verification and recovery drills.
- `VS-1952` `P1` — Add user-controlled stale, missed, offline, credential,
  and offsite alerts.
- `VS-1953` `P1` — Add CLI and structured headless recovery reporting.
- `VS-1954` `P2` — Add multi-machine summaries without hidden telemetry or
  mandatory hosted services.
- `VS-1955` `P2` — Add explicit local automation hooks for approved backup,
  verification, and restore events.
- `VS-1956` `P2` — Bring the CLI to documented parity with stable,
  automation-safe desktop workflows.

## Assurance model

VaultSync should be able to explain not only that protection once succeeded, but
also:

- when the destination was last reachable;
- when integrity was last verified;
- when a restore was last tested;
- what scope was tested;
- when evidence becomes stale;
- why current state is no longer sufficient;
- what action restores confidence.

---

# 11. Binary Source Control Program

Binary Source Control **is part of the 1.9 family**.

It is deliberately sequenced after the core recovery, portable recovery,
offsite, UI, and assurance foundations so that it cannot block disk/boot
recovery.

BSC is a separate repository model designed for projects containing large or
merge-hostile binary assets.

Typical targets include:

- game-development assets;
- 3D scenes and models;
- audio/video source material;
- design files;
- large generated-but-authoritative assets;
- other files where exact reconstruction and exclusive editing matter more than
  textual merge.

## BSC is deliberately not

- a replacement for Git source-code workflows;
- a Git server;
- a Git LFS protocol implementation in 1.9;
- a hosted SaaS control plane;
- ordinary VaultSync backup history with a different label;
- automatic binary merge;
- the same capability as full `.git` repository backup.

A project may use:

```text
Git
→ code, scripts, text configuration, small mergeable assets

VaultSync BSC
→ selected large or merge-hostile binary assets

VaultSync Backup
→ independent recovery protection for the working project and/or BSC repository
```

VaultSync may detect Git for coexistence guidance, but 1.9 BSC must not silently
modify `.git`, rewrite Git history, or change `.gitattributes` / `.gitignore`.

---

# 12. BSC Architecture Gate

Current canonical IDs:

- `VS-1970` `P0` — Architecture, format, immutable object/change graph,
  product boundary, backend primitives, threat model, migration rules, and
  Stable-versus-Preview gate.
- `VS-1971` `P0` — Authorship, workspace identity, ownership, path locks,
  short writer leases, guarded refs, stale-lock takeover, conflict review, and
  audit evidence.

`VS-1970` and `VS-1971` must be approved before later BSC items are treated as
stable-format commitments.

Prototypes and benchmarks may run before the gate is closed.

---

# 13. BSC Storage and Safety Contract

## 13.1 Immutable data first

The following are immutable after publication:

- chunks;
- packs;
- file objects;
- trees;
- changesets.

Mutable refs are the visibility boundary.

A changeset becomes visible only after every referenced immutable object is
durable and verified.

## 13.2 Separate repository model

BSC must not overload the existing backup/snapshot domain until its semantics
become ambiguous.

Source control requires:

- explicit check-in;
- canonical version graph;
- workspaces tracking refs;
- staged versus unstaged changes;
- path locks;
- guarded ref updates;
- stale-workspace detection;
- branches;
- conflict semantics;
- reachability-based garbage collection;
- exact workspace/authorship provenance.

## 13.3 Content-defined chunking

The preferred direction is content-defined chunking so insertions or deletions
do not necessarily shift every following block and force full restorage.

**FastCDC** is the leading candidate.

Initial benchmark candidates:

- minimum chunk: approximately `512 KiB`;
- target chunk: approximately `2 MiB`;
- maximum chunk: approximately `8 MiB`.

These are benchmark inputs, not frozen format values.

Qualification must include:

- multi-gigabyte files;
- edits near beginning/middle/end;
- insertion/deletion;
- already-compressed formats;
- globally rewritten formats;
- many small files;
- sparse files;
- identical assets repeated across paths/revisions.

## 13.4 No unbounded canonical binary-delta chains

Canonical history uses chunk/object references rather than long chains such as:

```text
v1
↓
delta 1
↓
delta 2
↓
delta 3
↓
...
```

A missing base or intermediate delta must not invalidate a large range of later
history.

Bounded binary deltas may be evaluated later as an optimization inside
independently verifiable objects, and transfer-level deltas may be used only
when both peers possess a verified base.

Transfer optimization must never change the reconstructed bytes or changeset
identity.

## 13.5 Object identity and deduplication

SHA-256 remains the baseline integrity primitive.

Encrypted repositories may require keyed object identity or a separate keyed
lookup identity so the storage namespace does not unnecessarily expose plaintext
equality.

Deduplication is repository-local in 1.9.

Cross-repository global deduplication is out of scope because it complicates:

- isolation;
- privacy;
- encryption;
- retention;
- deletion;
- recovery.

## 13.6 Pack files and rebuildable indexes

Small/medium chunks should be grouped into immutable packs rather than creating
millions of tiny filesystem objects.

Indexes contain enough information to find and decode chunks but are not the
only authoritative history.

Indexes must be rebuildable from authoritative repository data.

Repacking is maintenance, not history rewriting.

## 13.7 Compression

Compression is independently recoverable per chunk/object or equivalent unit.

VaultSync should avoid wasting CPU recompressing data that is already highly
compressed or encrypted.

Codec and decoding parameters are part of the versioned repository contract.

Unknown encoding is rejected rather than guessed.

---

# 14. BSC Domain Model

## Repository

Contains:

- stable repository identity;
- repository-format version;
- chunk/object-format version;
- chunking parameters;
- encryption descriptor/key identity when applicable;
- compatibility metadata;
- required feature flags.

## Workspace

Machine-local state associated with:

- repository identity;
- durable workspace identity;
- installation identity;
- local root;
- tracked ref/branch;
- base changeset;
- sparse rules;
- scan/index cache;
- local author display configuration.

Workspace identity is provenance, not an authentication credential.

## File object

Contains at least:

- logical length;
- ordered chunk references;
- whole-file content hash;
- executable bit where meaningful;
- symlink type/target where supported;
- format-versioned reconstruction metadata.

Path belongs to the owning tree rather than the content identity.

## Tree

Deterministic mapping of canonical repository names to:

- child trees;
- file objects;
- supported links.

## Changeset

Immutable object containing:

- root tree ID;
- one or more parent changeset IDs;
- repository identity/format;
- author label;
- installation/workspace provenance;
- UTC timestamp;
- check-in message;
- application/build identity;
- optional resolution/audit references.

Changeset identity is derived from canonical serialized content.

## Ref

Small mutable name pointing to a changeset.

Ref publication requires an expected old value and must fail if the head moved.

## Tag

Named recovery/milestone marker.

Stable tags should default to immutable behavior.

## Path lock

Long-lived cooperative editing lease separate from the short repository writer
lease.

It records:

- repository and branch/ref scope;
- canonical path;
- owner installation;
- owner workspace;
- display author;
- base changeset;
- random nonce;
- acquisition time;
- heartbeat;
- expiry;
- application version;
- takeover/release evidence.

---

# 15. BSC Commit Publication Protocol

A check-in is visible only after a guarded publish sequence.

Conceptually:

1. acquire the short repository writer lease;
2. re-read the tracked ref;
3. verify the expected workspace base;
4. verify required path-lock ownership;
5. scan/stage selected paths;
6. establish exact cryptographic identity of staged content;
7. write missing immutable chunks/packs;
8. durably publish pack indexes;
9. publish file/tree objects;
10. publish the immutable changeset;
11. compare-and-swap the ref from the expected head to the new changeset;
12. record audit/evidence;
13. release the writer lease.

If failure occurs before the ref update, the previous head remains valid and new
objects are unreachable.

If compare-and-swap fails because another writer moved the ref, VaultSync must
not force the update. It creates a stale-base/conflict plan.

There is no silent last-writer-wins behavior.

---

# 16. BSC Workspace, Checkout, and Path Safety

## Status model

Status should scale without hashing and rechunking the entire repository on
every refresh.

Layered status:

1. enumerate paths and cheap metadata;
2. reuse verified scan-cache entries when safe;
3. hash changed candidates;
4. chunk only paths needed for check-in or deep verification.

The cache is advisory. Final staged content always receives cryptographic
identity before publication.

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

Rename detection is a UI optimization. Correctness cannot depend on a heuristic
rename detector.

## Safe checkout/sync

Rules:

- never overwrite dirty local work silently;
- never silently delete an untracked path;
- stage replacement content into temporary storage first;
- verify complete content before final replacement;
- use atomic replace/rename where available;
- retain rollback state if atomic replacement is unavailable;
- re-check link/path safety immediately before replacement;
- cancellation leaves either the old verified file or the new verified file,
  never a partial replacement;
- force/discard requires an exact preview.

## Cross-platform path contract

Explicitly handle:

- Windows reserved names and characters;
- case-sensitive versus case-insensitive workspaces;
- Unicode normalization;
- path-length limits;
- symlink/junction/reparse-point boundaries;
- Unix executable permission;
- traversal/rooted-path attempts.

A repository may contain a path that a specific platform cannot materialize.
The client may inspect it, but checkout must explain the incompatibility rather
than silently rename it.

Case-colliding siblings are rejected when the target workspace cannot represent
them safely.

---

# 17. BSC Locking and Concurrency

Binary locking is a first-class workflow.

Lock rules are repository policy patterns rather than hard-coded extension
behavior. Presets may be offered for common game, 3D, audio, video, and design
formats.

Supported flows:

- checkout without lock where policy permits;
- lock and checkout;
- explicit unlock;
- revert unchanged and release;
- inspect lock owner;
- explicit stale takeover after expiry.

Making an unlocked file locally read-only may improve UX but is not the
authoritative safety boundary.

Commit-time repository checks remain authoritative.

## Distinct coordination concepts

```text
editing lock
= long-lived path record

check-in / ref publication
= short repository writer lease

maintenance
= exclusive maintenance lease
```

A BSC path lock must never hold the repository-wide writer lease for hours or
days.

## Stale workspaces

When the tracked head moved:

- independent changes may receive a deterministic update/rebase plan;
- byte-identical results may collapse a conflict;
- overlapping different binary content requires explicit resolution;
- timestamps never decide the winner.

---

# 18. 1.9.6 — Binary Source Control Foundation

The stable target is a complete **local/NAS default-line workflow**.

Branches, sparse workspaces, and offsite multi-writer repositories are not
required to call the 1.9.6 foundation complete.

## Work items

- `VS-1972` `P0` — Immutable CDC object store with repository-local
  deduplication, independently verifiable packs, rebuildable indexes, and
  bounded-memory streaming.
- `VS-1973` `P0` — Canonical file/tree objects, immutable changesets, tags, and
  compare-and-swap refs with atomic publication ordering.
- `VS-1974` `P0` — Workspace status, staging, check-in, sync, and safe checkout
  with dirty-workspace protection and cross-platform path checks.
- `VS-1975` `P0` — Binary lock rules, exclusive checkout, lock heartbeat,
  commit-time ownership checks, and explicit stale-lock takeover.
- `VS-1976` `P1` — Changeset/path history, read-only browse, exact restore,
  compare, tagging, and recovery evidence.
- `VS-1978` `P0` — Authenticated repository encryption, secure local credential
  use, and portable recovery-key path.
- `VS-1979` `P0` — Verification, index rebuild, reachability analysis,
  quarantine, safe GC/repack, and emergency read-only recovery.
- `VS-1981` `P1` — BSC desktop and CLI foundation with explicit Git
  coexistence and no automatic `.git` mutation.

## 1.9.6 stable gate

A stable release requires:

- repository creation and reopen without dependence on the original local app
  database;
- clean-machine repository discovery and recovery;
- second-machine workspace attach;
- sync;
- lock;
- modify;
- check-in;
- exact restore of earlier content;
- no incomplete changeset publication under interrupted object/pack/index/tree
  or changeset writes;
- stale ref updates rejected instead of overwriting newer work;
- dirty workspace paths never silently overwritten;
- exclusive locks never silently stolen;
- stale takeover retains evidence;
- index/cache loss recoverable from authoritative repository data;
- full verification detects injected corruption;
- encrypted clean-machine recovery works with documented authorized recovery
  material;
- GC/repack fault tests preserve every reachable changeset;
- supported Windows, macOS, Linux, NAS, and SMB workflows meet the declared
  qualification envelope.

If these gates are not met, BSC remains clearly labelled **Preview**.

That outcome does not block `1.9.0` through `1.9.5` from becoming Stable.

---

# 19. BSC Encryption and Independent Recovery

BSC encryption requires an object/chunk-level encryption contract. Existing
archive-level backup encryption must not simply be reused without proving that
its assumptions fit BSC.

Goals:

- independently authenticated chunks/objects;
- wrong-key detection;
- corruption detection before reconstructed bytes are trusted;
- repository-scoped key identity;
- no plaintext password or derived key in portable metadata;
- OS secure-store integration for routine use;
- explicit portable recovery material;
- interruption-safe key rotation;
- no accidental cross-repository deduplication.

The format must separate:

- KDF descriptor;
- encrypted repository key material/recovery envelope;
- object-encryption descriptor;
- non-secret algorithm/version metadata;
- machine-local credential reference.

A clean machine with the repository and authorized recovery material must be
able to:

- discover repository identity;
- inspect refs/history;
- verify stored data;
- restore a selected changeset.

---

# 20. BSC Verification, Repair, GC, and Repack

BSC inherits VaultSync's recovery-first philosophy.

## Verification levels

### Metadata / index audit

Checks:

- parseability;
- format versions;
- ref targets;
- pack/index relationships;
- lock format;
- obvious missing references.

### Reachability audit

Traverses protected roots and confirms that every referenced:

- tree;
- file object;
- chunk;
- pack

exists.

### Full content verification

Reads, decrypts/decompresses, hashes, and validates reachable stored data.

### Recovery drill

Materializes a selected changeset to an isolated target, re-hashes output, and
records evidence.

## Garbage collection

Committed history is retained by default. Backup retention rules must not
silently prune source-control history.

GC roots include:

- live refs;
- protected tags;
- in-progress recovery/maintenance roots;
- explicit future policy roots.

Safe GC sequence:

1. acquire maintenance exclusivity;
2. capture root set;
3. mark reachable immutable objects;
4. classify unreachable candidates;
5. quarantine/log candidates with a grace period where practical;
6. re-check roots before deletion;
7. delete only after policy and verification gates;
8. record maintenance evidence.

## Repack

Repack/compaction writes replacement packs and indexes first, verifies them,
switches authoritative indexes only after success, and retires old packs last.

Power loss must leave at least one valid reachable copy.

---

# 21. 1.9.7 — Binary Collaboration and Scale

This release adds collaboration semantics that are intentionally deferred from
the local/NAS foundation.

## Work items

- `VS-1977` `P1` — Branches and deterministic three-way path planning;
  automatically resolve only provably independent or byte-identical changes;
  require explicit resolution for overlapping binary edits.
- `VS-1980` `P1` — Sparse workspaces, verified local cache, resumable object
  transfer, and safe mirror/offsite replication where backend conditional-write
  semantics are sufficient.
- `VS-1982` `P0` — Performance, clean-machine recovery, multi-client NAS
  concurrency, large logical histories, corruption, interruption, maintenance,
  and fault-injection qualification.

## Branches and conflicts

Branch creation is a cheap ref operation.

Integration uses a three-way plan:

```text
merge base
+ ours
+ theirs
→ path-level integration plan
```

Automatic resolution is limited to cases that are provably safe:

- changed on only one side;
- an unambiguous delete/unchanged combination;
- both sides produce the same content identity;
- independent paths.

If both sides modify the same binary differently, the user chooses explicitly:

- ours;
- theirs;
- keep both under explicit names;
- replace with an externally resolved file.

Resolution creates a new changeset. Existing history is not silently rewritten.

## Sparse workspaces

Sparse rules affect local materialization, not changeset identity.

A verified local cache may retain objects across branch changes.

Cache eviction changes performance, not history.

Corrupt cache entries must be re-fetched/reverified rather than trusted.

## Transfer

Transfer planning is object-based:

1. enumerate objects required for the target tree;
2. subtract verified local/cache objects;
3. batch/range-read missing pack regions where possible;
4. resume incomplete transfers;
5. verify before materialization.

## Offsite replication

BSC does not require cloud storage to function.

When the offsite layer is available, repositories may be mirrored using the
same immutable identities.

A mirror is healthy for a changeset only after all objects reachable from that
changeset, plus required repository metadata, are durable and verifiable at the
destination.

A backend that cannot provide guarded mutable state for refs/locks is
read-only/replica-only rather than pretending to support safe multi-writer
source control.

## 1.9.7 stable gate

Stable promotion requires:

- branch/ref operations never silently discard overlapping binary work;
- sparse and full workspaces reconstruct identical bytes for included paths;
- corrupt cache entries trigger recovery rather than trusted output;
- resumable transfer never publishes incomplete history;
- replicated changesets are not called protected until all required remote data
  is durable and verifiable;
- large-history performance and memory budgets are enforced;
- repeated check-in/verify/GC/repack cycles remain recoverable under forced
  termination and network loss.

---

# 22. BSC Desktop and CLI Surface

BSC should live within the goal-oriented 1.9 shell without turning ordinary
backup pages into source-control pages.

## Protect / Version Control

Repository card should expose:

- repository/workspace name;
- tracked ref;
- sync state;
- changed/staged count;
- locks held;
- blocked paths;
- verification state;
- repository storage use;
- dedup reuse;
- destination health.

Primary actions:

- Refresh status
- Lock / Unlock
- Stage / Unstage
- Check in
- Sync
- History

Expert actions:

- Branches / Tags
- Verify
- Repository maintenance
- Sparse rules
- Recovery kit / encryption
- Mirror / replication

## History

Changeset timeline should expose:

- message;
- author/workspace;
- exact ID;
- parent(s);
- changed paths;
- physical bytes added versus logical bytes represented;
- verification/evidence state.

## Recover

Restore:

- individual file;
- subtree;
- complete changeset.

Use isolated-target defaults and exact overwrite previews.

## CLI

Functional surface:

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

Structured output must provide stable machine-readable result states for at
least:

- stale ref;
- dirty workspace;
- lock conflict;
- unsupported format;
- corruption;
- missing key;
- unavailable repository;
- maintenance contention.

---

# 23. BSC Qualification Envelope

Synthetic reproducible repositories should be used in CI alongside controlled
real-asset smoke tests.

## Required dataset dimensions

- `100k` paths;
- `500k` paths;
- `1M` paths where practical;
- `100 GB` logical history;
- `1 TB` logical history;
- multi-TB logical history where practical;
- `1 GB` individual files;
- `10 GB` individual files;
- larger generated files;
- highly deduplicable edits;
- globally rewritten / low-dedup compressed assets;
- rename-heavy trees;
- Unicode edge cases;
- case collisions;
- sparse files;
- repeated ref movement;
- thousands of changesets;
- repeated verify / GC / repack cycles.

## Measure

At minimum:

- open time;
- status/scan latency;
- p95 scan latency;
- CPU;
- allocations/memory;
- logical versus new stored bytes;
- chunk reuse ratio;
- check-in throughput;
- sync throughput;
- first checkout;
- warm-cache checkout;
- recovery throughput;
- verification throughput;
- GC/repack duration;
- temporary-space amplification;
- cancellation latency.

## Fault injection

Inject termination, I/O failure, or network loss during:

- chunk write;
- pack finalization;
- index publication;
- tree publication;
- changeset publication;
- ref update;
- lock acquire/renew/release;
- sync replacement;
- encryption-key rotation;
- verification;
- mirror upload;
- GC mark;
- quarantine;
- repack;
- old-pack retirement.

The invariant is:

> VaultSync can always explain which state is authoritative and preserve the
> last valid reachable history.

---

# 24. BSC Security Boundary

BSC is designed to defend against:

- accidental corruption;
- crashes;
- I/O faults;
- network faults;
- stale clients;
- path attacks;
- conflicting cooperating writers.

The 1.9 model does **not** claim that a user or administrator with unrestricted
write access to the repository storage cannot intentionally rewrite or delete
history.

The following are not implied unless implemented and separately qualified:

- hostile-admin tamper resistance;
- cryptographic commit signing;
- central hosted identity;
- enterprise ACL management;
- non-repudiation.

---

# 25. Audit Additions Applied to the BSC Plan

The BSC strategy is already technically substantial. The following
clarifications are worth carrying into implementation acceptance criteria. They
do not require new work IDs unless the scope grows enough to justify separate
execution items.

## 25.1 Offline / unreachable lock behavior

When the repository cannot be reached or a lock heartbeat cannot be validated:

- VaultSync must not claim that exclusive ownership is still verified;
- the UI should show the lock state as unavailable/unverified;
- local editing may continue only as an explicitly local-risk state;
- publication must revalidate ownership before a changeset can become visible.

This fits under `VS-1971` / `VS-1975`.

## 25.2 Lease and clock safety

Lock expiry and stale takeover must not trust a client wall clock as the sole
authority.

Takeover should re-read persistent lock state, compare the nonce/owner/base, and
preserve displaced-lock evidence before replacement.

This strengthens `VS-1971` without changing the repository model.

## 25.3 Temporary-space preflight

Operations capable of temporary storage amplification should report and check
their space requirements before destructive/large writes where practical.

This includes:

- checkout/sync;
- repack;
- quarantine;
- key rotation;
- large restore;
- mirror staging.

Low-space failure must remain recoverable and must not retire the last known
valid copy.

This fits primarily under `VS-1974`, `VS-1979`, and `VS-1982`.

## 25.4 Writer capability / format negotiation

Repository feature flags already fail closed. Make the writer rule explicit:

> A client that does not understand every required write-side repository feature
> may inspect supported data read-only, but must not participate as a writer.

This applies to:

- repository format;
- object format;
- encryption mode;
- lock protocol;
- ref publication protocol;
- required backend semantics.

This strengthens `VS-1970` and the long-term compatibility contract.

---

# 26. Audit Correction — BSC Work-ID Reconciliation

An older copy of the Binary Source Control Strategy used the range
`VS-1981`–`VS-1993`.

That numbering is stale.

The current canonical VaultSync roadmap and maintained BSC strategy use:

| Current ID | Scope |
|---|---|
| `VS-1970` | Architecture, format, product boundary |
| `VS-1971` | Identity, ownership, locking, conflict, audit |
| `VS-1972` | Immutable CDC object store |
| `VS-1973` | Trees, changesets, refs, atomic publication |
| `VS-1974` | Workspace engine |
| `VS-1975` | Exclusive binary locking |
| `VS-1976` | History, restore, compare, evidence |
| `VS-1977` | Branches and binary conflict resolution |
| `VS-1978` | Encryption and portable key recovery |
| `VS-1979` | Verification, rebuild, GC, disaster recovery |
| `VS-1980` | Sparse/cache/transfer/offsite replication |
| `VS-1981` | Desktop, CLI, Git coexistence |
| `VS-1982` | Scale, cross-platform, NAS, fault qualification |

No implementation should use the stale `VS-1981`–`VS-1993` mapping.

The issue association in the maintained plan is:

- `VS-1970` — issue `#703`
- `VS-1971` — issue `#704`
- `VS-1972` — issue `#705`
- `VS-1973` — issue `#706`
- `VS-1974` — issue `#707`
- `VS-1975` — issue `#708`
- `VS-1976` — issue `#709`
- `VS-1977` — issue `#710`
- `VS-1978` — issue `#711`
- `VS-1979` — issue `#712`
- `VS-1980` — issue `#713`
- `VS-1981` — issue `#714`
- `VS-1982` — issue `#715`

---

# 27. 1.9.8 — Stability and LTS Baseline

The previous final stability release moves to `1.9.8` because `1.9.6` and
`1.9.7` are now reserved for the BSC track.

Existing LTS IDs are not renumbered.

## Work items

- `VS-1961` `P0` — Stabilize disk-image, offsite, portable-recovery, and
  Binary Source Control formats.
- `VS-1962` `P0` — Complete long-duration, large-dataset, migration,
  filesystem, source-control, and fault-injection qualification.
- `VS-1963` `P1` — Publish the supported compatibility window and LTS policy.

## LTS intent

This release freezes and qualifies the support contract for the 1.9 family:

- disk-image formats;
- portable-recovery formats;
- offsite recovery formats;
- BSC repository formats;
- migration rules;
- read/write compatibility;
- emergency read-only behavior;
- long-duration reliability;
- supported compatibility window.

The long-term rule is:

> A future VaultSync version must not strand supported 1.9 recovery data without
> a documented recovery path.

---

# 28. Cross-Release Recovery Invariants

Across the complete 1.9 family:

1. **Unknown formats fail closed.**
2. **Incomplete data is never presented as complete.**
3. **Verification state is separate from creation state.**
4. **Restore-tested state is separate from verification state.**
5. **Unsupported configurations remain explicit.**
6. **Destructive operations require an exact preview.**
7. **Recovery must not depend on an opaque local database when the authoritative
   protected data is otherwise intact.**
8. **Recovery evidence records what was actually measured or tested.**
9. **User-confirmed, inferred, simulated, and measured evidence remain
   distinguishable.**
10. **No silent last-writer-wins behavior is permitted in BSC.**
11. **Maintenance never removes the last valid reachable copy before a verified
    replacement exists.**
12. **Earlier Recovery Horizon releases are not blocked by later BSC work.**

---

# 29. Post-1.9 Boundary

Compatible evolutionary work belongs in a future `1.10`, for example:

- additional providers;
- additional filesystems;
- additional image formats;
- additional automation;
- performance improvements;
- optional integrations;
- incremental interface improvements;
- BSC editor/tool integrations that do not change the authoritative product
  model.

A future `2.0` is justified only if VaultSync changes its authoritative user
model from individual backup/recovery operations to resilience outcomes and that
change creates a deliberate compatibility or migration boundary.

Candidate resilience concepts include:

- Protection Plans;
- Recovery Graph;
- Protection Map;
- failure-scenario evaluation;
- recovery orchestration;
- policy-controlled corrective actions.

These remain candidate concepts until their own architecture and migration gates
are satisfied.

---

# 30. Roadmap Governance

- `ROADMAP.md` remains the canonical planning document.
- `CHANGELOG.md` records shipped behavior, not future promises.
- GitHub milestones define release gates.
- Project tracking mirrors the roadmap rather than creating a second source of
  truth.
- Stable history is not rewritten to make a release branch appear cleaner.
- Scope changes must update the roadmap, owning issue, milestone, project
  fields, and release description together.
- Work IDs are not silently reused or renumbered after allocation.
- BSC remains Preview when its stable gates are not satisfied.
- Preview BSC status does not weaken or delay the stable gates of earlier
  Recovery Horizon releases.

---

# 31. Final 1.9 Family Definition

VaultSync 1.9 is therefore not simply a disk-cloning release.

It is the family in which VaultSync moves from:

> **“I have a backup.”**

through:

> **“I can prove this recovery path works.”**

to:

> **“I can recover after losing the original installation, disk, or machine,
> and I can version large binary project state without sacrificing the same
> recovery guarantees.”**

The release family is complete only when recovery remains explainable,
verifiable, portable, and maintainable across the formats that 1.9 introduces.
