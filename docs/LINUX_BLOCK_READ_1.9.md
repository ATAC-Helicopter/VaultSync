# Held Linux block read handles — VS-1904

Owner #492; 1.9.0 preparation #736. Development adapter under
[the approved joint direction](release-evidence/1.9-joint-architecture-approval-2026-10-20.md).
It is not connected to desktop, media or the inventory protocol.

## Caller and selection boundary

`LinuxBlockReadService.OpenAsync` runs with existing caller privileges. There is
no elevation, privileged listener or image-selected native command. Linux x64
is the only ABI enabled; other platforms return UnsupportedPlatform. The caller
selects a kernel leaf name from its observation, never an arbitrary path.

Admission requires a recent (at most 30 seconds, not future) Observed snapshot,
resolved swap view, exact whole-disk selection, unique observed hardware identity,
nonzero disk sequence, supported 512/4096 geometry, aligned positive capacity
and no observed mount, swap or holders/use. The hard policy bound is 8 TiB;
this does not qualify any capacity. Unknown use remains Unknown.

Fresh native inventory before and after opening must retain boot/namespace and
selected device data. The fixed `/dev/<kernel-name>` node opens using O_RDONLY,
O_EXCL, O_NOCTTY, O_NONBLOCK, O_NOFOLLOW and O_CLOEXEC. No create, truncate,
write, mount, discard or mutable block ioctl is used. A symlink leaf is refused.
The source process and its OS/device namespace must themselves be trusted; this
is not containment or protection against a compromised administrator/kernel.

`statx` uses the held descriptor with AT_EMPTY_PATH and requires block-device
type. Fixed getter ioctls bind rdev major/minor, byte capacity, logical/physical
sectors, disk sequence and kernel read-only state to observations. An unavailable
getter returns Unverifiable, never a proven change or a usable source. Changed
values invalidate the binding; refused paths return no source and close handles.

## Reader lifetime

The returned seekable stream owns the descriptor and exposes no native handle,
path or write capability. Reads serialize position updates and are capped at
128 KiB each. Bounds/overflow, exact capacity and premature EOF are checked.
Native binding is checked before and after each read, including EOF checks.
A changed/unavailable binding, I/O failure or early EOF closes the source.
Cancellation is explicit; a caller must dispose an abandoned source.

The operation runs inside the caller process. Synchronous kernel operations
are not made preemptible by a managed token; a future authenticated worker needs
its own bounded orchestration and disconnect policy. This stream alone does
not implement the inventory client's process deadline.

O_EXCL obtains the kernel block claim and rejects in-use claims, including the
measured mounted/concurrent cases. It does **not** prove the absence of every
other raw writer, global offline consistency, trusted origin or a capture point.
A controlled independent recovery environment, full offline/write-protection
policy, authenticated worker, repository ancestry and exact overwrite approval
remain separate requirements. This source never grants destination authority.

## ABI references and measured scope

Fixed values/layouts were checked against Linux v6.12
[block getter definitions](https://raw.githubusercontent.com/torvalds/linux/v6.12/include/uapi/linux/fs.h),
[open flags](https://raw.githubusercontent.com/torvalds/linux/v6.12/include/uapi/asm-generic/fcntl.h),
[statx layout](https://raw.githubusercontent.com/torvalds/linux/v6.12/include/uapi/linux/stat.h)
and the Linux [open](https://man7.org/linux/man-pages/man2/open.2.html) /
[statx](https://man7.org/linux/man-pages/man2/statx.2.html) manuals.
The actual Debian/QEMU x64 run exercises those getters and read offsets;
synthetic tests do not substitute for native ABI evidence.

See [current source tests and native measurements](release-evidence/1.9-linux-block-read-2026-10-20.md).
All support rows remain unqualified. No image, boot medium, installed updater
or beta was qualified by this read primitive.


## Read-only capture consumer

The same-privilege [Linux raw capture adapter](LINUX_RAW_CAPTURE_1.9.md) consumes
this held source only when its kernel read-only flag is set. Copy/EOF/GPT reads
retain the checks above. Its output is a verified private raw image, not a writable
device capability, authenticated worker, recovery environment or qualified disk.
