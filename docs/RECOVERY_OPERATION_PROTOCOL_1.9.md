# Recovery operation transport — VS-1907 / #495

Development transport for the approved offline recovery direction, on the sole
1.9.0 preparation PR #736. This is **not** a privileged worker host, elevation
endpoint or completed restoration UI. The existing read-only DiskWorker role
does not gain a writable command. No support profile is promoted.

## Authority and connection ownership

The future trusted supervisor must establish installed build/role trust, a
controlled offline boot, exact operation/device authority and actual human
confirmation before invoking the restore owner. Neither a Unix connection, an
operation/review UUID, the framing role nor a valid image supplies that authority.
An acknowledgment must never, by itself, call the device-writing engine.

`LinuxRecoveryOperationChannel` adopts one socket and its kernel credential /
peer-pidfd binding on successful construction. A binding for another socket or
an injectable test-provider binding cannot authenticate it. Every read/write
rechecks the actual peer lifetime and credentials. Each direction has an
independent sequence, starting at one, with no wraparound. Invalid direction,
operation, sequence, length or partial-frame failure closes the entire channel;
there is no retry or stream resynchronization. Sender/receiver gates serialize
each direction separately, including the complete descriptor handoff. Cancelling
a waiter that has not acquired its gate does not close the active owner.

Sends and descriptor handoffs have ten-second ceilings. Receives have a
two-minute inactivity ceiling. A future dispatcher must process controller
heartbeats during long operations and emit actual worker status/progress, monitor
channel failure and cancel/dispose its owned staging/restore resources. The
channel alone cannot supervise stalled synchronous native I/O or finish a journal.

## Fixed development framing

All integers are little-endian; UUIDs use RFC/network byte order. The 40-byte
header is checked before allocating or reading any body.

| Offset | Field |
| --- | --- |
| 0 | Eight bytes `VSRPC001` |
| 8 | Version u32, exactly 1 |
| 12 | Message kind u32 |
| 16 | Nonzero operation UUID, exactly the connection's expected operation |
| 32 | Nonzero sequence u32, exactly the next value for this direction |
| 36 | Body length i32, bounded by kind and at most 65,536 bytes |

| Sender | Kind | Body bytes |
| --- | --- | --- |
| Controller | 1 OfferImages | 0, followed by one atomic descriptor handoff |
| Controller | 2 InventoryRequest | 0 |
| Controller | 3 Prepare | 80: observation UUID plus zero-padded 64-byte kernel selector |
| Controller | 4 Confirm | 16: nonzero review UUID acknowledgment, **not human authority** |
| Controller | 5 Cancel / 6 Heartbeat | 0 |
| Worker | 257 Ready / 258 Staging / 261 Executing | 0 |
| Worker | 259 Inventory | 1–65,536 |
| Worker | 260 Review | 1–4,096 |
| Worker | 262 Progress | 24 |
| Worker | 263 Outcome | 64 |
| Worker | 264 Refused | 4 |

The Prepare selector permits only ASCII letters, digits, underscore and hyphen,
with a maximum 63-byte name and mandatory zero padding. It cannot contain a path,
NUL followed by hidden text, whitespace or a normalized non-ASCII alias. Actual
native observation/freshness/identity checks remain mandatory. Response
bodies remain untrusted until their strict typed codec and context checks pass.
Runtime state/consent validation must be completed before a production dispatcher
consumes them. This development grammar is not a public
recovery API or compatibility promise.

## Typed worker response bodies

The internal `LinuxRecoveryResponses` codecs are the next dispatcher boundary.
They validate payloads separately from framing and return display/correlation
DTOs. They do not recreate an authoritative `LinuxBlockInventory`, open devices,
prove offline state, authenticate builds or grant human/device write authority.
All integers are little-endian and UUIDs use network byte order. Decoders reject
truncation, trailing data, unknown enums/flags and nonzero reserved bytes before
returning a value. There is no arbitrary diagnostic text, path, boot namespace or
hardware-identity string. These local response bodies are not diagnostic uploads.

| Kind | Typed body | Context required by the consumer |
| --- | --- | --- |
| Inventory | 32-byte header plus at most 256 fixed 168-byte device entries | Native state/reason and topology consistency; a redacted observation only |
| Review | 304 bytes: operation/review/image/capture IDs, generation, manifest root, observed time, full overwrite length, one device entry and inventory observation ID | Exact expected operation, inventory observation and selected kernel name |
| Progress | 24 bytes: review ID and durably acknowledged byte count | Exact review, full overwrite bound and optional previous durable count |
| Outcome | 64 bytes: review ID, outer state/reason, independent optional writer state, journal/cleanup/terminal flags and acknowledged/uncertain ranges | Exact review, geometry and optional last progress |
| Refused | Four-byte explicit pre-execution refusal code | Dispatcher phase; never a substitute for a post-write outcome |

The inventory header contains observation ID, UTC timestamp, state/reason,
unresolved-swap flag and entry count. Entries contain bounded zero-padded kernel
and parent selectors, native major/minor numbers, capacity, optional disk
sequence, logical/physical sector sizes, kind/identity/use classifications and
read-only/mount/swap/holder flags. Bounds mirror native inventory geometry rather
than promising support: zero-capacity observations and non-profile sectors can
be shown, while duplicate selectors/device numbers or inconsistent parent/use
claims refuse. Unsupported/incomplete/changed observations keep their explicit
failure state instead of being converted to an observed snapshot. Unsafe native
selector spelling refuses instead of being normalized into another device name.

A review is stricter: a verified full image and exact whole-disk selection with
supported geometry, matching nonzero identifiers/generation/disk sequence, an
immutable manifest-root binding and no direct in-use/read-only indication.
The review body explicitly carries the inventory observation ID at offset 288;
the decoder binds both that wire value and the supplied inventory to the exact
expected observation. An identical device list from a different observation
cannot accept an earlier review. Observation correlation is not a freshness or
global-offline proof. The trusted
owner must retain its original native context and recheck it before execution.

Progress means durable `RangeFlushed` bytes only. It does not mean read-back has
passed. Counts must be monotonic at chunk boundaries, with the exact final size
allowed. Outcomes preserve both the outer lifecycle result and the optional
writer result: full acknowledged bytes with failed read-back remain failed;
failed cleanup can override an internally completed write. Uncertain write
ranges and missing terminal journal/cleanup evidence cannot become success.
A successful result requires full write/read-back evidence, durable journal and
confirmed cleanup. An acknowledgment/review ID alone cannot supply any of these.

The dispatcher must still enforce state ordering, trusted operation ownership,
real progress callbacks, selection freshness and actual human confirmation. These
codecs cannot enforce those runtime facts by themselves. The existing frame
length limits remain an outer refusal bound; the typed body length is stricter.

## Read-only image descriptor handoff

`LinuxRecoveryImageTransfer` uses Linux x64 `SCM_RIGHTS` on the very same
authenticated socket. The controller sends one byte `0x56` with exactly two
descriptors: metadata, then payload. No image-selected path reaches a privileged
file opener. The receiver uses `MSG_CMSG_CLOEXEC`, verifies close-on-exec and
native `O_RDONLY`, rejects `O_PATH`, non-regular and empty files, and checks
16 MiB / 8 TiB length ceilings before adoption. Metadata and payload cannot be
aliases of the same native device/inode. It does not parse image content.

Ancillary storage is capped at 256 bytes. Every delivered rights slot is adopted
for cleanup even when the record is surplus, truncated, malformed or accompanied
by unexpected control data. The kernel closes descriptors omitted by ancillary
truncation. Unknown flags/control records, wrong marker/count, lost peer and
cancellation refuse. Successful leases own two read-only FileStreams with native
filesystem observations compatible with the existing staging ancestry checks.
These limits are refusal bounds, not qualified image sizes.

Descriptor passing shares the underlying open-file description with the sender.
The sender obtains private read-only descriptor copies using `F_DUPFD_CLOEXEC`.
The syscall receives SafeHandle arguments with runtime-managed lifetime; no
`DangerousGetHandle` or manual dangerous reference is used. Two owned copy leases
survive backpressure/original-stream disposal and close on every send outcome.
The caller must hand off exclusive use; it is not a snapshot or immutable input.
The existing isolated reader, root-owned sealed staging copy and exact byte
rechecks remain necessary before device preparation/execution. Receiving files
does not authorize restoration or prove trusted origin, offline state or boot.

## Historical framing verification — 2026-10-26

Portable/local Release checks pass: 2,229 Core tests, 293 Python tests and zero
warnings/errors. The 46 focused recovery transport cases include pure malformed
wire vectors plus native Linux branches; non-Linux hosts do not execute Linux
descriptor syscalls. Native branches on old kernels retain refusal rather than
falling back from peer pidfds to numeric PIDs.

An independent Python producer running UID/GID 65534 and a root .NET consumer
passed 137 named checks in an offline Debian 13 disposable VM on Linux
6.12.111. Those include real descriptors, read-only/ancestry/ownership checks,
surplus/truncated rights cleanup, peer death/wrong UID, fragmented frames, replay,
wrong operation/direction, oversized length and cancellation. This source-bound
run is separate from previous raw-device restoration/OS-boot evidence. See
[the receipt](release-evidence/1.9-recovery-transport-2026-10-26.json).

The subsequent lifetime refinement passes fresh Release checks with 2,230 Core
tests and 47 focused cases. A complete new native run passes 153 checks, including
a .NET sender blocked on a full socket whose original streams are then disposed,
independent Python verification of the received descriptors/content/close-on-exec
flags, and refusal/cleanup when rights are attached to the wrong protocol phase.
The earlier 137-case evidence remains historical. The hosted 783be53 test failure
was an exclusive-open error in the test fixture, corrected before rechecking the
new preparation head. See the [follow-up receipt](release-evidence/1.9-recovery-transport-lifetime-2026-10-26.json).

Hosted S3869 review subsequently rejected both raw SafeHandle number extractions.
The next implementation replaces manual dangerous references with two owned
`F_DUPFD_CLOEXEC` copies. A complete new independent native run passes all 153
checks, explicitly proving that original handles close while the copies retain
valid data and are reclaimed after sending. The source checksums again match the
measured Portable PDB; the previous runs remain historical. See the
[copy-based receipt](release-evidence/1.9-recovery-transport-copies-2026-10-26.json).
Native access mode is checked on the actual copies, including when a writable
descriptor is wrapped in a managed read-only stream. This is not suppression of
the analyzer finding or a new operation-authority claim.

## Typed response verification — 2026-10-28

The 52 focused response cases pass, including independent wire vectors and
observation-bound review replay refusal. The unchanged Release build passed with
zero warnings/errors; the interrupted mandatory Core gate was completed on
October 28 with `--no-build --no-restore`: 2,282 passed, zero failed/skipped.
Retained October 26 contract results include 299 Python tests and 1,959 keys in
19 locales. No docs-only rebuild or unrelated test cycle was added. See the
[source-bound receipt](release-evidence/1.9-recovery-responses-2026-10-28.json).

Remaining: trusted supervisor/installation and clean unprivileged UI launch,
stateful dispatcher and actual consent/destination flow, owned
engine execution/progress, generated boot media, full platform/profile and
failure loops, package provenance and protected review/release gates. Keep #495
open, Project In progress and #736 draft. No host-device write or release
publication follows from these transport tests.
