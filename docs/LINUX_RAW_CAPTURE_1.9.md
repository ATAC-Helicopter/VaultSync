# Linux raw capture and private publication — VS-1904

Owner #492, sole preparation PR #736. Implements the first read-only source to
raw-image pipeline under the approved joint direction. This is a same-privilege
Core adapter, not an authenticated/elevated worker or desktop/media workflow.
All support profiles remain unqualified. No writable device handle is acquired.

## Admission and claims

The public entry takes an exact inventory selection, image/capture/part/source
UUIDs, generation and bounded tool claims. Linux x64 is the only native ABI. The
existing held-source adapter must admit a fresh whole-disk observation, bracket
the exclusive open with native inventory, and bind type, capacity, device number,
disk sequence and geometry through the descriptor. Capture additionally requires
kernel read-only state. Unsupported, mounted, ambiguous or changed views refuse.

The full GPT is inspected before storage creation, using the raw profile's
128-entry policy and 8 TiB refusal ceiling. Physical/logical geometry must match
the source and be 512/4096 with physical >= logical. Current image IDs and origin
records remain claims, not authenticated producer identity or universal offline
proof. A trusted recovery environment and worker/build review remain necessary.

Images contain every byte, including unused sectors and potentially deleted
content. They are not encrypted. The future UI must explain protected storage;
none of this payload or raw metadata is uploaded by product diagnostics.

## Trusted storage boundary

The caller selects an existing absolute output parent, never an image-selected
path. `openat2` refuses symlinks and magic links in every component. Dot traversal,
NUL and overlong paths refuse. The parent must belong to the effective UID, have
no group/other permissions, and reside on ext4 with enough available blocks for
the full payload plus the metadata policy bound. There is no fallback for
unsupported syscalls or filesystems. No existing directory is chmodded.

Hold parent/child directory descriptors throughout the operation. Create a
random `.vaultsync-incomplete-<uuid>` child using mkdirat 0700, flush its parent,
and exclusively create fixed `payload.raw` and `image.metadata` files at 0600.
Held statx checks require regular type, current UID and one link for new files.
Reserve full payload allocation with fallocate KEEP_SIZE before reading source
payload bytes. ENOSPC/preallocation failure cannot become success. No recursive
cleanup or overwriting occurs on failure; partial data stays private.

This boundary assumes a trusted OS, administrator and same-user processes. It
does not contain a malicious root/UID peer or promise that a user-renamed parent
retains its original pathname. Returned image names are relative to the held
parent. Future reopen/restore must acquire and reverify bytes independently.
Source/destination repository ancestry and authenticated privilege are still
worker/restore requirements, not conferred by these filesystem permissions.

## Copy, verification and durable publication

1. Copy exactly source capacity in chunks of at most 128 KiB, hashing the bytes.
   The source session rechecks its native identity before/after every read.
2. Check EOF through that session and re-inspect source GPT. A changed layout,
   missing/trailing data, I/O error or cancellation never publishes an image.
3. Flush payload to disk. Encode the frozen metadata format with the copied
   digest and source GPT observations; write and flush metadata.
4. Rewind both held output streams and run the independent full raw reader:
   completion/root, length, all payload bytes and actual GPT must verify.
5. Flush the image directory and parent. Check cancellation before the commit.
6. Atomically rename the partial child to the fixed image UUID using
   renameat2(RENAME_NOREPLACE), then fsync the parent. An existing image, empty
   directory, link or file cannot be replaced.

Only that sequence returns PublishedVerified with a root and image directory
name. It is a measured byte/publication observation, not restored-system or boot
evidence. A pre-rename cancellation returns Interrupted; truncated input returns
Incomplete; failed I/O returns Failed. A rename followed by failed parent sync
returns PublicationUnconfirmed, without a successful root/name or destructive
rollback. Cancellation after the rename does not undo a completed commit.

An incomplete directory must never be treated as an ordinary published restore
input even if its metadata happens to be complete. There is no automatic resume.
A valid standalone completion record still does not prove historic durability,
source consistency, a trusted creator or that later bytes remain unchanged.

## Verification boundaries and remaining work

Synthetic stream tests inject cancellation, source/layout changes, corruption,
flush, rename, parent-sync and disposal failures. Native filesystem tests execute
only on Linux x64/ext4; other hosts record that this native success was not run.
The disposable UEFI VM probe separately measures actual native source/storage
calls. First SCSI lab attempts encountered a WWID attribute returning EINVAL;
the inventory refused without creating an image. No I/O guard is relaxed.

Full worker authentication, trusted build/privilege, desktop UI/localization,
independent media, safe image staging and image-to-device restore/read-back,
power-failure qualification, 1.8.9 upgrades and release signatures remain open.
No profile is promoted by this pipeline or its small GPT fixtures.

References: Linux [openat2](https://man7.org/linux/man-pages/man2/openat2.2.html),
[fsync](https://man7.org/linux/man-pages/man2/fsync.2.html),
[rename](https://man7.org/linux/man-pages/man2/rename.2.html),
[fallocate](https://man7.org/linux/man-pages/man2/fallocate.2.html),
and [statx](https://man7.org/linux/man-pages/man2/statx.2.html).
