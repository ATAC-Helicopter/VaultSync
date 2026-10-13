# Disk-image format and compatibility — VS-1917 review draft

Status: proposed contract for [VS-1917 / #574](https://github.com/ATAC-Helicopter/VaultSync/issues/574),
prepared on 2026-10-04 for the sole 1.9.0 review, #736. A draft read-only
recognition envelope and manifest/completion-bound raw payload verifier exist;
no complete-image reader, writer, extension or stable format is established by this draft.
Review this together with VS-1918 (engine/support) and VS-1919 (identity/evidence).
Existing file backups and portable SQLite metadata keep their current contracts
in [Repository formats](REPOSITORY_FORMATS.md).

## Recovery boundary

An image must describe the bytes needed to reconstruct its declared disk or
partition scope without the original application database. An independent
recovery environment must understand the format, obtain required payload parts
and keys, and validate them before destination authorization. Inspecting an
image, validating its bytes, booting media and validating a restored system
produce different evidence; none substitutes for the others.

The proposed first profile is a full, offline, self-contained block image.
Incremental parent chains, live consistency, filesystem-aware omission, codecs,
encryption and foreign-engine import remain decisions for joint review. No
platform or filesystem becomes supported by selecting this logical model.
An engine-specific opaque payload needs a declared adapter/version and the same
coverage, integrity and independent-restore guarantees before it can qualify.

## Logical records

These are required semantics, not a committed serialized schema. Lengths and
offsets use bounded unsigned byte counts; partition geometry also records the
logical/physical sector sizes. Reject arithmetic overflow and impossible ranges.

| Record | Required meaning and boundary |
| --- | --- |
| Recognition envelope | Format family, major/minor version, bounded manifest length, required features and integrity suite; sufficient to reject unsupported input before interpreting payload |
| Image identity | Immutable random image ID, capture operation ID, generation and creation time; copying an image preserves its image ID, recapture creates a new one |
| Source descriptor | Disk/partition scope, captured capacity, geometry and identity observations; names and paths are hints, never destination authorization |
| Layout | Partition-table kind, exact table/boot metadata regions, partition starts/lengths and identifiers, and the complete address space covered by the capture |
| Extent map | Ordered, non-overlapping logical ranges, each mapped to stored bytes or a supported explicit reconstruction rule |
| Payload part | Relative part ID, encoded length, content digest and mapping to logical extents; no absolute path, URL, traversal, symlink escape or device access from image content |
| Integrity | Manifest/map binding, payload digests and decoded extent digests; suite/version and coverage are explicit |
| Transform descriptor | Ordered compression/encryption transforms, their versions, bounded decode parameters and non-secret key-acquisition descriptors |
| Capture/checkpoint | Capture state, durable acknowledged extents, checkpoint generation and source-consistency observations; never a substitute for final validation |
| Completion record | Image/generation binding, complete map and payload-root binding, written only after all referenced parts are durable |
| Tool provenance | App, engine, adapter and recovery-tool versions, build/source identity when available, capture mode and consistency method |
| Evidence reference | Separate validation scope/result, verifier build, time and image root binding; details align with VS-1919 and never contain secrets |

Physical serials, source machine names and partition labels can identify a user.
Keep observations to the recovery purpose, mark unknown/ambiguous identity
explicitly, and redact them from public diagnostics. A matching label or cloned
partition GUID does not prove a device is the intended restore destination.

## Coverage and sparse behavior

For a full image, the extent map covers exactly `[0, capturedCapacity)` including
partition tables, boot areas, gaps and the final sector. A partition-only image
covers its explicit partition scope; it cannot claim to reconstruct disk boot
metadata or other partitions. Reject overlap, gaps, duplicate part identities,
misaligned unsupported ranges and stored regions outside declared part bounds.

Every omitted region needs a supported, explicit reconstruction meaning. A
proposed `zero` extent means actual zero bytes at restore, not unchanged bytes
on the destination. An unreadable source range is an error, never sparse space.
Filesystem free-space omission requires an engine-specific reviewed consistency
contract; physical file holes alone do not prove source content was zero.

The first full-image profile has no external parent dependency. A future delta
profile must bind every ancestor by immutable content identity, detect missing
parents/cycles, and verify reconstructed logical bytes before restore. Readers
without that feature reject it rather than restoring only the available parts.

## Integrity, hostile input and keys

Bind source scope, layout, extent map, transform descriptors and ordered part
identities into the manifest root. The completion record binds that same root.
Validate encoded lengths/digests, then authenticated decryption/decompression
where applicable, then the decoded logical extents. Missing parts, truncation,
unexpected output lengths or a mismatched digest fail explicitly.

A checksum demonstrates consistency against the recorded digest; it cannot
prove trusted origin against an attacker able to replace both bytes and digests.
Authenticity/signature requirements and trust roots need a separate decision.
Encrypted profiles require a reviewed authenticated suite, nonce uniqueness and
metadata binding; no custom cryptography or unauthenticated encryption is allowed.

Never embed passwords, raw/derived keys, OS credential blobs or tokens. A local
credential reference is insufficient for independent recovery: the descriptor
must explain the supported offline key-acquisition method without storing its
secret. A locked image is not corrupt, and successful key lookup is not payload
validation. Key loss remains an explicit unrecoverable dependency.

Treat all input as untrusted before verification. Bound counts, nesting, manifest
size, encoded/decoded sizes, codec/KDF cost and allocation before processing;
derive sensible limits from the supported matrix. Reject unrecognized required
features and path escapes. Never execute programs, mount filesystems, follow
network links, initialize a store or write the source during inspection.

## Capture and reader state

Capture lifecycle: `capturing → finalizing → complete`, with `interrupted` or
`failed` outcomes before completion. `complete` describes finalized capture
structure; it does not itself mean the payload was independently verified.
Inspection derives a result from observed structure and bytes; it never trusts
a persisted `valid=true` or source-machine success flag.

| Observed result | Required behavior |
| --- | --- |
| Unsupported | Recognized family with unsupported version/required feature; bounded diagnostic only, no source mutation or restore |
| Incomplete | Missing completion record or unfinished coverage; diagnose/checkpoint inspection only, no ordinary restore |
| Invalid/corrupt | Malformed structure, impossible ranges or integrity failure; report the failing scope, no ordinary restore |
| Locked | Supported encrypted structure but missing usable key; no decoded-payload validation or restore claim |
| Complete, unverified | Supported finalized structure; permit validation, never present as verified or restore-ready |
| Verified for declared scope | All required bytes/map/transforms validated for this exact root; destination safety and platform qualification still apply |

Inspection and validation open image content read-only. Verification
reports are separate artifacts and must bind image ID, generation, manifest
root, declared coverage, tool/version and result. Changing any bound bytes
invalidates reuse. An old report cannot authorize restore of newly changed media.

## Checkpoints, completion and interruption

Use an exclusive capture workspace and immutable acknowledged payload parts.
Persist a part before its checkpoint; persist all parts/map before publishing
completion. The storage protocol must specify flushing, atomic publication and
crash behavior on each supported local/removable/network filesystem. Rename
alone is not proof of durability; unsupported atomicity must fail explicitly.

Inject crashes at each boundary, including during completion publication. Readers
must see a complete consistent generation or an incomplete image. Recovery must
not infer success from a filename, exit code or last-progress percentage.

Resume only after revalidating checkpoint parts, map, compatible writer and source
identity/consistency. A reconnect, matching size, device path or partition GUID
does not prove the source stayed unchanged. An offline source that changed since
capture requires a new capture unless a reviewed mechanism proves consistency.
Resume never rewrites a completed image, silently drops a bad range or upgrades
an unknown format. Unsupported resume preserves the partial image for diagnosis.

## Compatibility and independent inspection

Major changes alter interpretation and require explicit reader support. Minor
changes may be additive only where unknown optional fields have a specified safe
skip rule; unknown required features always reject. Product version, format
version, engine payload version and evidence version are independent.

Readers open recognized older supported profiles without in-place migration.
Conversion, if offered, creates a separate new image, retains the original,
validates reconstructed bytes and records the source root and conversion tool.
Deprecation requires a published read-support window, retained fixtures and an
independent recovery reader before write support is retired. Exact windows are
still an approval decision; this draft does not create them.

Legacy file backups are handled by their existing readers; they are never guessed
to be disk images. Foreign formats need explicit adapters. Emergency inspection
against a preserved copy can report bounded structure and missing dependencies
without auto-repair, key export or mutation. Recovery media must bundle compatible
reader/engine dependencies; package installation from the lost OS or internet
access cannot be an undeclared prerequisite for the qualified offline path.

## Permanent fixture and fault plan

Fixtures are required before the reader/writer is accepted. No fixture has been
produced or executed by this document. Use synthetic data with expected logical
bytes and geometry; fixtures contain no captured user data or real credentials.

| Fixture/fault | Required independent observation |
| --- | --- |
| Complete disk and partition images | Exact declared byte reconstruction; partition scope never implies whole-disk boot recovery |
| Explicit zero extent on a nonzero target | Reconstructed bytes are zero; no stale destination bytes survive |
| Interrupted capture at every publication boundary | No incomplete generation is classified verified or restore-ready |
| Missing/truncated/bit-flipped payload or edited map | Failure names the affected scope; no normal restore proceeds |
| Overlap, gap, overflow and decompression/resource limit | Bounded rejection before allocation or destination write |
| Unsupported major, minor required feature, codec or engine payload | Explicit unsupported result; source/sidecars unchanged |
| Known legacy and permitted optional-field variants | Only documented compatibility paths succeed without migration |
| Encrypted locked, wrong-key and tampered inputs | Locked/authentication-failure behavior is distinct; no plaintext/key leakage |
| Altered source or corrupted checkpoint during resume | Explicit refusal or new capture; no silently mixed source generation |
| Replaced bytes after a successful validation report | Old evidence is rejected; selected image must be revalidated |
| Offline environment with no original database | Compatible reader resolves declared parts/keys and validates synthetic image |

Record fixture hashes, reader/writer/engine builds and expected results. A
fixture reader round trip is necessary but not sufficient: VS-1909 still requires
Capture → Validate → Boot → Restore → Validate on its supported matrix.

## Decisions required before approval

1. VS-1918: select native container versus engine envelope, initial full/offline
   profile and supported geometry/filesystem/storage constraints.
2. VS-1917: fix recognition bytes, serialization/canonicalization, integer and
   size limits, part layout/extension, digest suite and completion protocol.
3. VS-1918/security review: select permitted codecs, authenticated encryption/KDF
   profiles, authenticity policy and independent media/key acquisition.
4. VS-1919: align identity/privacy and verification provenance/freshness; choose
   content binding without equating validation to trusted origin or boot success.
5. VS-1917: publish compatibility windows, implement bounded read-only inspection,
   and preserve the permanent fixtures with independently checked expected bytes.

Issue #574 stays open and In progress. The architecture gate remains unapproved;
no destructive device implementation or stable support claim follows from this draft.

## Logical coverage prototype — 2026-10-12

`DiskImageCoverageService` checks supplied in-memory raw/explicit-zero map records.
It supports only the proposed 512/4096-byte logical-sector profiles, positive
sector-aligned capacity and ordered contiguous extents. Part identities are unique;
raw ranges must fit the declared stored length. BUG-19026 also requires stored
offsets to align to the logical sector; an in-bounds byte shift cannot be accepted
as a raw sector map. Zero extents have no part reference or storage offset.
Subtraction-based bounds prevent unsigned arithmetic overflow.
Provisional review limits are 4,096 parts and 65,536 extents.

Gaps, overlap, impossible/misaligned ranges and invalid zero/part records reject.
Unknown geometry/extent rules are Unsupported. Missing tail coverage or completion
is Incomplete. Exact finalized coverage is only CompleteUnverified: the service
never reads payloads, validates digests, resolves paths, checks manifest/completion
roots, acquires keys, implements durability or authorizes restore.

These are synthetic logical fixtures, not complete encoded-image fixtures or a
reader/writer. Serialization, image identity/manifest binding, transforms,
independent byte inspection and joint architecture approval remain open. See
[the multi-contract validation batch](release-evidence/1.9-contract-batch-2026-10-12.md).

## Draft recognition envelope — 2026-10-13

A read-only recognizer consumes exactly 24 bytes from a supplied stream. Its
draft little-endian header is eight bytes `VSIMAGE\0`, then unsigned 16-bit
major and minor versions, unsigned 32-bit required feature flags, unsigned
32-bit manifest length and unsigned 32-bit reserved field. Version `1.0`
with zero required flags/reserved bits and a manifest length of 1–16 MiB is
recognized as `SupportedEnvelope`. Future version or required flags are
`Unsupported`; missing bytes are `Incomplete`; impossible length or nonzero
reserved data is `Invalid`. An unrelated magic is `NotImage`.

`SupportedEnvelope` means only that the header is recognizable. The service
does not read or trust manifest bytes, payload, completion or source identity;
it never opens a path or writes a destination. This provisional encoding may
change in joint review before a writer or compatible reader is accepted.
Permanent serialized header/refusal fixtures and their limits are documented
in [the envelope evidence](release-evidence/1.9-image-envelope-2026-10-13.md).

## Raw payload byte verification — 2026-10-13

`DiskImagePayloadVerificationService` checks supplied raw-part streams against
lowercase SHA-256 expectations after logical coverage is complete. It requires
one digest and one readable stream for each declared part, rejects raw part
lengths beyond the image capacity or not aligned to the logical sector,
reads in 128 KiB chunks, rejects missing/truncated/extra bytes and mismatched digests, and
honors cancellation. The caller owns the streams; this service opens no path,
allocates no capacity-sized buffer and writes no destination. A malformed or
unfinished coverage plan fails before any part is read.

`PayloadsVerified` means only that the supplied streams matched the supplied
digests. Those digests are not yet bound to a serialized manifest root or
trusted producer. No decoded extent, completion record, source identity,
independent image or restore destination is certified by this result. See
[the bounded byte-verification evidence](release-evidence/1.9-payload-verification-2026-10-13.md).

## Provisional manifest binding — 2026-10-13

`DiskImageManifestVerificationService` extends read-only verification with a
fixed-record binary review profile. These provisional bytes can change before
joint approval. Integers are little-endian; UUIDs are 16 network-order bytes.
Metadata contains the existing 24-byte 1.0 envelope, exactly the declared
manifest bytes, then exactly one 64-byte completion record and EOF.

The manifest header is 64 bytes: schema `uint32=1`, image UUID, capture UUID,
generation `uint64`, capacity `uint64`, logical sector `uint32`, part count
`uint32`, extent count `uint32`. Identities must be nonempty and generation
positive. Each 56-byte part is UUID, length `uint64`, raw 32-byte SHA-256.
Each 44-byte extent is offset/length `uint64`, kind `uint32` (`0=stored`,
`1=zero`), part UUID (empty only for zero), part offset `uint64`.
Counts and exact encoded length are checked before record allocation.
Existing envelope, coverage and raw-part limits apply.

Completion is `VSCOMP\0\0`, image UUID, generation `uint64`, and SHA-256
of the exact manifest bytes. Identity, generation, manifest root and metadata
EOF must match before payload access. Coverage and payload verification then
check the bound part lengths, digests and map. `ContentVerified` returns image,
capture, generation and root for this observed draft content only. Checksums
provide no trusted-origin guarantee; no persisted report authorizes later
reads of potentially changed bytes or a restore destination.

Permanent independently encoded raw, zero and mixed fixtures accompany
mutation, missing completion, generation substitution, count/resource-limit,
invalid-map, payload damage, short-read and cancellation tests. This closes
the narrow manifest binding gap, while source/layout/provenance records,
storage durability, writer, independent boot environment and joint approval
remain open. See [current evidence](release-evidence/1.9-manifest-binding-2026-10-13.md).

## Synthetic file capture — 2026-10-13

VS-1904's `DiskImageFileCaptureService` writes the provisional single-raw-part
profile from a supplied synthetic stream at its current position. It opens no
source path or device and creates only new regular files under an exclusive
temporary workspace; callers cannot select an overwrite destination. Capture
identities/generation, geometry and capacity are checked before workspace creation.
Supported test geometry is 512/4096 bytes, with positive aligned capacity bounded
by the regular-file length domain. Reads and writes use a 128 KiB buffer.

Publication order: create files exclusively, write/flush recognition header,
copy exactly declared source capacity while hashing, reject source truncation or
trailing bytes, flush payload to disk, write/flush manifest, then write/flush
completion. Cancellation is observed before completion publication. Once final
publication begins, it finishes without treating cancellation as rollback.
The caller retains its source handle; partial workspaces remain available for
diagnosis and require explicit cleanup. No checkpoint/resume is implemented.

`FileFixtureCaptured` records successful file writes/flushes only. Source read
errors, wrong lengths and cancellation before publication produce explicit
Failed/Invalid/Incomplete/Interrupted results without a completion record.
Storage failures during final publication may leave incomplete or structurally
consistent metadata; they never return successful capture, and content inspection
must still verify all observed bytes. File flushes do not prove directory-entry
durability, power-loss semantics or source consistency. Atomic/crash behavior on
supported filesystems remains unqualified. No worker, source lock, disk access,
user capture workflow or restoration claim follows from these fixtures.
See [file capture evidence](release-evidence/1.9-file-capture-2026-10-13.md).

## Synthetic file reconstruction — 2026-10-13

VS-1907's `DiskImageFileReconstructionService` stages each supplied raw part into
a new exclusive temporary regular file while the manifest verifier reads/hashes
it. After all metadata, coverage and payload checks pass, reconstruction uses
only these snapshots. Changed original streams cannot substitute unverified bytes
after validation. Output is a newly created regular file; no existing target path,
device or overwrite authorization is accepted. Parser-owned verified coverage
is internal to this file-only path and is not public device authorization.

The prototype bounds simultaneous part handles to 32, staged bytes to 1 GiB
total and declared output capacity to 1 GiB; these fixture limits are separate
from the general reader's format count limits. Resource exhaustion fails
explicitly. It resolves only provided UUID/stream pairs and creates fixed or
UUID-derived filenames in its own fresh workspace. Nothing follows image paths.

Stored extents seek only within staged regular files; zero extents explicitly
write zeros. A 128 KiB buffer bounds memory. After writing and flushing, the
output is read back and its whole-file SHA-256/length must match the bytes written
and declared capacity. Only then can `FileFixtureReconstructed` be returned,
bound to manifest root and output content digest. Corrupt/incomplete/unsupported
input refuses before the reconstructed output is created. Cancellation and I/O
failure retain staged or partial artifacts for explicit caller cleanup; they
cannot report completed reconstruction. Filesystem crash durability, hostile
same-account changes to staging storage, device writes, offline worker/media
and boot qualification remain outside this fixture profile.
See [file reconstruction evidence](release-evidence/1.9-file-reconstruction-2026-10-13.md).
