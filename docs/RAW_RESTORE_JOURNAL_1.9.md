# Private raw-restore journal — VS-1907

Owner #495; sole draft PR #736. This internal version-1 journal supplies the
[write engine](RAW_DEVICE_RESTORE_1.9.md) with persistent ordered appends. It opens
no writable device and grants no offline, producer, privilege or human-consent
capability. There is no automatic resume or public stable journal format.

## Creation and location

A trusted caller supplies the exact operation/review/image/capture/generation/root
and target observation, plus an existing private ext4 parent. UUIDs and image
claims remain correlation data. All strings, geometry, capacity and device
numbers are bounded/canonical before allocation. Capture/restore limits remain
8 TiB and 512/4096 geometry; this ceiling is not a qualified capacity.

Before any creation, take an independent fresh inventory and require the expected
boot, mount namespace and exact target observation. Open the parent with the
existing Linux x64 openat2 no-symlink/no-magic-link boundary. Through the held
parent descriptor, resolve its filesystem number via the fresh unique whole-disk
or direct-partition ancestry. Ambiguous, stacked, unknown or target-disk storage
refuses **before mkdir/file creation**. Ordinary filesystem paths and image claims
cannot override that check. Reobserve target, boot/namespace and the repository's
whole-disk identity after allocation and before publication. Source/staging
ancestry remains the separate preview/broker obligation.

The shared native transaction creates a distinct `.vaultsync-journal-incomplete-`
private child (0700), `journal.header` and `records.bin` (0600, exclusive, one link,
owned by effective UID). Existing capture/staging retain their original names.
Preallocate the maximum record budget before returning a journal: two 128-byte
records per 128 KiB payload chunk, plus three terminal/start slots. The header is
256 bytes; available space also includes the shared 16 MiB metadata reserve.
Full-policy 8 TiB images reserve about 16 GiB for records, not RAM; large-capacity
space/time feasibility remains unqualified. The small measured cases use only
640/1408 reserved record bytes, rounded by the filesystem.

Write/flush the header, flush the initially empty record file and held directories,
then rename-no-replace to the operation UUID and fsync its parent. Only confirmed
publication returns an owned writer. Failure retains private partial state;
post-rename sync failure may leave a visible header without a usable returned
writer. Reused operation IDs never clobber existing journals. No recursive
cleanup, stale-journal adoption, appending to old files or capture import occurs.

## Internal version-1 byte contract

All integer fields are little endian; UUIDs use network byte order. Reserved
bytes are zero. Inspection requires an independently supplied expected binding;
a file is never a source of device authority.

| Header offset | Bytes | Meaning |
| --- | --- | --- |
| 0 | 8 | ASCII `VSJRNL01` |
| 8 | 4 | Version 1 |
| 12 | 4 | Reserved |
| 16, 32, 48, 64 | 16 each | Operation, review, image, capture UUIDs |
| 80, 88 | 8 each | Generation, capacity |
| 96, 100 | 4 each | Logical, physical sector bytes |
| 104 | 8 | Disk sequence observation |
| 112, 116 | 4 each | Device major/minor |
| 120 | 32 | Image manifest root |
| 152 | 32 | SHA-256 of strict UTF-8 kernel name, NUL, canonical device number, NUL, hardware candidate |
| 184 | 40 | Reserved |
| 224 | 32 | SHA-256 of header bytes 0–223 |

Kernel name is bounded ASCII; hardware candidate is bounded, has no control
characters and must encode as strict UTF-8. The fingerprint is local binding,
not persistent physical identity or authenticated provenance.

Each 128-byte frame contains `VSR1` at 0, sequence UInt64 at 4, kind UInt32 at 12,
offset/length UInt64 at 16/24, intended data SHA-256 at 32, previous frame hash at
64 and SHA-256 of bytes 0–95 at 96. The first previous hash is the header checksum.
Kinds are Started=0, WriteIntent=1, RangeFlushed=2, ReadBackVerified=3,
Interrupted=4 and Failed=5. Non-data kinds require a zero data-hash field.
Checksums detect alterations/reordering/mixing; they are not signatures and cannot
contain an attacker controlling the OS, account or complete journal bytes.

## State, append and inspection

Started is first and has zero range. Each intent starts exactly at the acknowledged
prefix, with the next complete bounded chunk and a lowercase SHA-256. Only its
exact range/hash can be acknowledged. ReadBackVerified requires the full capacity,
no pending intent and range `[0, capacity)`. Failed/Interrupted terminate a started
history and describe exactly the outstanding intent, or zero range when none is
pending. Read-back may be followed only by Failed for a target cleanup error.
Nothing follows Failed/Interrupted. The state machine and sequence budget are
checked before every append and again during inspection.

Serialize appends through the owned writer. Confirm stream position/length,
encode the entire frame, write and Flush(true), then advance the in-memory cursor.
Any unconfirmed write/flush permanently poisons the writer: no later terminal
record can pretend durability. Cancellation before an attempted append can still
allow a valid terminal record. Invalid binding/state is a controlled engine
failure; a second terminal refusal cannot escape cleanup as an unhandled failure.
The journal and target have separate ownership; callers dispose both correctly.

Inspection reads fixed buffers and bounded history without reopening any path.
It requires exact expected header, validates each sequence/hash/link/range/state,
and distinguishes Invalid, Incomplete, Failed, Interrupted and ReadBackVerified.
Missing/truncated headers or tails never become verified. For incomplete histories,
only the observed acknowledged prefix is retained and the remaining capacity is
conservatively uncertain. Read-back is a byte observation, not restored OS boot,
a historical physical durability guarantee, permission to write or a resume token.

All journal material stays private/local. Never upload frames, raw payloads,
roots, device details or free-form errors through beta diagnostics. No new UI
strings or reporting fields are introduced. Future review/retention UI must be
localized and must not weaken the independent authorization boundary.

Trusted writable worker/protocol/build/role integration, controlled offline state,
image sandbox, qualified recovery media/UI, physical power-cut/crash recovery,
full filesystem/OS-boot/interactive validation and installed upgrade/package/
signature/protected reviews remain open. All support profiles stay candidates.
