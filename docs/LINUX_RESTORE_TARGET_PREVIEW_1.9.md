# Native restore target observation — VS-1905

Owner #493; sole 1.9.0 preparation PR #736. This connects a sealed raw staging
session to fresh Linux device observations and a read-only exclusive handle.
It provides an overwrite description prerequisite; it never grants Offline
status, user consent, privilege or a writable device capability.

## Input and filesystem ancestry

The staging session privately records filesystem device numbers from statx on
the caller-held regular metadata/payload file descriptors and its held staging
parent. These are kernel observations, not image-supplied IDs or paths. Memory
or pipe inputs remain inspectable/stageable, but unknown repository ancestry
refuses a native target preview. No filesystem number is added to public product
diagnostics or exposed through the public staging session.

Each input/staging filesystem must resolve uniquely in a fresh inventory to
a whole disk or a partition with one known whole-disk parent. Missing/duplicate
numbers, stacked/unknown relationships and missing parents refuse. Any proposed
target containing original metadata, original payload or staging is excluded.
This first implementation deliberately refuses unsupported ancestry rather than
guessing through RAID/LVM/network/pseudo filesystems.

## Fresh selection and held description

The caller's selected native observation must match a new independently acquired
inventory: boot ID, mount namespace and the complete selected device record.
The repository mapping comes from that new kernel view, not caller-supplied
repository relationships. Non-Observed, stale/future, unresolved-swap and oversized
views refuse. The selected target must be a whole disk whose capacity, logical
and physical sectors exactly match the staged image. Kernel read-only targets
refuse; a false read-only flag alone is not write-access permission.

The existing Linux read adapter then re-observes around a fixed O_RDONLY/O_EXCL
open and checks held type/device number/disk sequence/geometry/read-only state.
Mounted, ambiguous, changed or uninspectable selections refuse. An explicit descriptor
binding check runs again without reading disk content or changing stream position. Closed staging or late
refusal closes every acquired target handle.

Success exposes ObservationOnly with a timestamp, immutable native target data
and image assessment. The object owns the read-only target claim, while the
caller retains staging ownership. Recheck performs another native handle check
and expires after 30 seconds, or immediately when stage/target closes or changes.
Disposal releases the claim. Unknown use stays Unknown; it is never relabeled
Offline. Hardware identifiers are private technical data; UI integration must
escape unsafe display characters and use translated field labels.

A future writer cannot upgrade this descriptor or accept it as consent. It must
use authenticated operation/worker authority, prove its controlled offline
environment, bind exact translated overwrite consent to the image/root/generation
and destination identity, reopen a write handle with fresh observations and
recheck ancestry/identity/geometry before any destructive byte. No writable handle
is opened by this service or its tests.

## Integration limits

The earlier pure DiskRestorePreviewService contract remains caller-observation
modeling. Its Offline input must not be manufactured from this namespace view.
This native description has a separate type to preserve that boundary.

Authenticated broker/privilege, recovery media, exact overwrite UI in every
maintained locale, safe device writes, interrupted-write records, read-back/boot
qualification and installed upgrade/signing gates remain open. All support
profiles remain candidates; no beta is qualified by a successful preview.
