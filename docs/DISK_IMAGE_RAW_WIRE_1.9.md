# Raw offline image reader wire contract — VS-1917

Version: VSIMAGE envelope 1.0, manifest schema 2, origin schema 1, source kind 2.
Direction approved [2026-10-20](release-evidence/1.9-joint-architecture-approval-2026-10-20.md).
This fixes the reader bytes for the first raw-block profile before a product
writer. It does not qualify a filesystem/platform, durable capture or restore.
Future incompatible changes require a new discriminator and permanent fixtures;
never reinterpret these bytes or silently upgrade an existing image.

## Container and numeric rules

A self-contained image directory has exactly the product filenames
`image.metadata` and `payload.raw`. These names are chosen by trusted code,
never read from metadata. Secure directory acquisition, repository ancestry,
symlink refusal, permissions and durable publication are separate writer/worker
requirements. This reader accepts caller-held streams and opens no paths.

All integers are unsigned little-endian except the signed timestamp noted below.
All UUIDs use 16 network-order bytes, including GPT UUIDs converted from native
GPT encoding. Digests are raw 32-byte SHA-256, not hexadecimal text. Byte offsets
below are relative to each record. No padding exists except specified text zeros.
Unknown versions, required features and source kinds refuse; reserved bits/bytes
must be zero. No compression, encryption, parents, URLs or executable descriptors.

Limits: positive sector-aligned capacity up to 8 TiB; logical/physical sectors
512 or 4096, physical >= logical; **16 MiB for all metadata bytes including the
24-byte envelope and 64-byte completion**, thus manifest <= 16 MiB minus 88.
The general draft reader's older 16 MiB manifest ceiling is unchanged. GPT must
have **exactly 128 entry slots in both arrays**, not merely <=128 used partitions;
used entry numbers are 1–128. Generic fixture inspection retains its 1,024-slot
ceiling. Payload verification reads at most 128 KiB per chunk. These limits are
refusal ceilings, not measured supported capacities or filesystem qualification.

## Exact metadata records

Envelope (24 bytes):

| Offset | Width | Value |
| --- | --- | --- |
| 0 | 8 | ASCII `VSIMAGE` followed by NUL |
| 8 | 2 | Major 1 |
| 10 | 2 | Minor 0 |
| 12 | 4 | Required features 0 |
| 16 | 4 | Exact manifest byte length |
| 20 | 4 | Reserved 0 |

Manifest header (64 bytes):

| Offset | Width | Value |
| --- | --- | --- |
| 0 | 4 | Schema 2 |
| 4 | 16 | Nonempty image UUID |
| 20 | 16 | Nonempty capture UUID |
| 36 | 8 | Positive generation |
| 44 | 8 | Whole-disk capacity |
| 52 | 4 | Logical sector bytes |
| 56 | 4 | Part count 1 |
| 60 | 4 | Extent count 1 |

Origin header (216 bytes), immediately after manifest header:

| Offset | Width | Value |
| --- | --- | --- |
| 0 | 4 | Origin schema 1 |
| 4 | 4 | Source kind 2: claimed offline block source |
| 8 | 16 | Nonempty source UUID |
| 24 | 16 | Nonempty observation UUID |
| 40 | 4 | Physical sector bytes |
| 44 | 8 | Signed UTC Unix milliseconds, 0–253402300799999 |
| 52 | 16 | Nonempty GPT disk UUID |
| 68 | 32 | GPT metadata fingerprint, defined below |
| 100 | 8 | First usable LBA |
| 108 | 8 | Last usable LBA |
| 116 | 4 | Used partition count, 0–128 |
| 120 | 32 | Tool version ASCII, letters/digits/`.`/`+`/`-`, nonempty |
| 152 | 64 | Tool source commit, exactly 40 or 64 lowercase hex characters |

Text is zero-padded to field width; nonzero bytes after the first zero refuse.
Source kind 1 remains the historical regular-file fixture claim. Generic schema-2
verification reads either kind; this raw-block profile refuses kind 1. Kind 2 is
an unauthenticated claim: changing it and recomputing an unkeyed root cannot prove
that a device was acquired offline or by trusted software.

Each used partition observation is 64 bytes, in increasing entry-number order:
entry number at 0 (4 bytes), type UUID at 4 (16), unique partition UUID at 20
(16), byte offset at 36 (8), byte length at 44 (8), attributes at 52 (8), reserved
zero at 60 (4). GUIDs are nonempty; ranges are positive, aligned, nonoverlapping
and wholly within usable LBAs. Reserved attribute bits 3–47 must be zero.

One 56-byte part record follows: nonempty part UUID at 0 (16), stored length
at 16 (8, exactly capacity), payload SHA-256 at 24 (32). Then one 44-byte extent:
logical offset at 0 (8, zero), length at 8 (8, exactly capacity), kind at 16
(4, zero/stored), the same part UUID at 20 (16), part offset at 36 (8, zero).
Manifest length is exactly `64 + 216 + 64 * usedPartitionCount + 56 + 44`.
There are no omitted, zero-synthesized or external regions.

Completion (64 bytes) immediately follows the manifest: `VSCOMP` plus two NUL
bytes at 0 (8), same image UUID at 8 (16), same generation at 24 (8), SHA-256
of the exact manifest bytes at 32 (32). Metadata EOF immediately follows.
`payload.raw` is exactly capacity bytes with matching digest and EOF.

## Payload GPT verification

The protective MBR, primary/backup GPT 1.0 92-byte headers, their CRCs, both
128-entry/128-byte arrays and CRCs must pass the existing bounded
[GPT rules](DISK_IMAGE_FORMAT_1.9.md#bounded-gpt-layout-inspection--2026-10-15).
Both entry arrays must be byte-identical even if CRCs collide. Parsed disk UUID,
usable range and every used partition observation must match the manifest.
Names are not persisted separately, but original GPT sectors remain in payload.

The metadata fingerprint is SHA-256 over, in order: ASCII
`VaultSync.GptLayout.v1` plus NUL; logical-sector uint32; capacity uint64;
whole logical sector containing protective MBR; whole primary-header sector;
primary entry array; backup entry array; whole backup-header sector. Original
sector bytes are hashed, including allowed original GPT content.

## State, completion and trust

`DiskImageRawProfileVerificationService` snapshots bounded metadata, checks the
profile/limits before payload access, then verifies completion/root/coverage and
all raw bytes, and independently inspects actual GPT. Unsupported variants,
missing/truncated bytes, corruption, read failures and caller cancellation are
distinct. Only success exposes the observed image assessment. It never writes,
mounts, opens a device, executes a descriptor or authorizes restore.

A valid completion record proves serialization consistency, **not durable
publication**. The forthcoming writer must flush payload and metadata, finish
readback, flush the image directory and parent, and publish completion only
through the agreed durable protocol. Interrupted partial directories are not
ordinary restore inputs. No automatic resume is implemented; a new attempt is
required until source continuity/checkpoints are qualified.

The caller supplies readable streams positioned at the metadata envelope and
payload byte zero; payload must be seekable, independent, and exactly capacity.
Metadata may be nonseekable. Streams remain caller-owned and their positions
advance. Later reads require held immutable/protected staging and fresh worker
checks: this result cannot certify future bytes, source consistency, producer
trust, physical identity, filesystem semantics, bootability or target safety.
The core technical reason keys are not exposed as UI text; a desktop flow must
supply all 19 locale translations when integrated.

## Metadata encoding

`DiskImageRawMetadataEncodingService.Encode` accepts image/capture/part IDs,
generation, bounded source/layout claims and a lowercase SHA-256 payload digest.
It owns a bounded partition snapshot, validates claims and emits exactly the
records above with their completion hash. Both geometries must match the
independently encoded permanent bytes exactly. Invalid input returns no bytes
or root. Earlier fixture writers are unchanged.

`MetadataEncoded` means serialized caller-owned bytes, not captured payload,
verified GPT, trusted producer, disk access or durable publication. For example,
a syntactically valid 8 TiB claim still refuses verification against a short
payload. The future writer must verify acquired/staged bytes and honor durable
publication ordering before persisting the completion portion; this pure codec
has no file/stream side effects and cannot fulfill those gates itself.

## Permanent compatibility corpus

`tests/VaultSync.Core.Tests/Fixtures/disk-image-raw-profile-v1.json` contains
independently Python-encoded 512/4096 cases: valid synthetic block claims,
legacy fixture, incomplete/missing completion, trailing bytes, root corruption,
unknown envelope/schema/origin, over-capacity, invalid geometry/generation/map,
forged layout with a valid root, corrupt payload and total-metadata overflow.
Reproduce with `python3 scripts/generate_raw_profile_fixtures.py`; it imports no
product encoder. These fixtures are synthetic bytes, not native capture proof.
The earlier schema-1 and source-kind-1 fixtures remain unchanged and readable.
