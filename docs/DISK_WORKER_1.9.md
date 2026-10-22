# Read-only disk worker development contract

Owner: VS-1904 / [#492](https://github.com/ATAC-Helicopter/VaultSync/issues/492).
Preparation: #736, 1.9.0. Status: **development transport**, not an approved
privileged protocol or a supported recovery workflow. Review with
[ADR 001](adr/001-imaging-engine-1.9.md), the image and identity contracts.

`VaultSync.DiskWorker` is a separate executable in the solution. Its sole operation
calls the fixed `LinuxBlockInventoryService` sysfs/proc observer. It accepts no
arguments, filenames, image metadata, executable names, shell commands, device
selectors or credentials. It opens no raw device and performs no capture/restore.
On other operating systems the body reports UnsupportedPlatform.

The executable is not connected to desktop/CLI launchers, updater assets or release
packaging. A local developer can build and test it; it must not be shipped as an
elevated helper. It has the launching user's privileges, with no elevation and no
OS sandbox. Separate process execution alone does not establish containment.

## One-shot protocol version 1

The parent creates private inherited binary standard-input/output pipes and must
close its input writer after exactly one request. There is no listening socket,
named public endpoint, interactive command parser or subsequent operation.
Integers are unsigned little-endian; UUIDs are 16 bytes in RFC/network order.

| Request offset | Length | Value |
| --- | --- | --- |
| 0 | 8 | ASCII `VSINVREQ` |
| 8 | 4 | Version 1 |
| 12 | 4 | Operation 1: inventory |
| 16 | 16 | Nonzero operation UUID |

EOF after byte 32 is mandatory. Truncation, any trailing byte (including another
request), empty UUID, unknown version or operation refuses before observation.
At most 33 input bytes are consumed. The host applies a 30-second deadline to
the request, observation and output, starting after runtime initialization.
Cancellation is cooperative inside the service; the executable also bounds its
wait when a standard pipe read does not honor cancellation. Runtime startup and
OS scheduling are outside that application deadline.

| Response offset | Length | Value |
| --- | --- | --- |
| 0 | 8 | ASCII `VSINVRSP` |
| 8 | 4 | Version 1 |
| 12 | 4 | Operation 1 |
| 16 | 16 | Exact request operation UUID |
| 32 | 4 | UTF-8 JSON body byte count, at most 1,048,576 |
| 36 | declared count | `LinuxBlockInventory` JSON, numeric enum values |

The full bounded body is prepared before publishing the header. The provider
independently limits observations to 256 devices and bounded metadata; JSON depth
is limited to eight. Strings are technical metadata, never display-ready UI text.
Raw hardware/boot/namespace identifiers belong only on the private channel and
must be redacted from public reports. Standard error contains no protocol data
or exception/path diagnostics. No state is persisted by this worker.

| Exit | Meaning |
| --- | --- |
| 0 | Complete transport response; inspect inventory State/Reason separately |
| 64 | Arguments or request refused |
| 65 | Response encoding/bounds failure |
| 70 | Unexpected internal failure, without private diagnostic text |
| 74 | Input/output failure |
| 77 | Access denied outside the observer's typed result |
| 124 | Deadline/cancellation |

Exit zero does not mean Observed, offline, verified or authorized. Incomplete,
ChangedDuringRead and UnsupportedPlatform remain typed inventory results. The
Core client bounds reads and requires a complete matching version/operation/UUID,
valid declared body, EOF and process exit zero; partial output or any failure
cannot become a usable result. The development client below implements this
transport boundary; desktop integration remains open.

## Remaining security and integration work

Operation correlation is not authentication, freshness proof or replay prevention.
Before privilege or writes, review trusted worker selection/build provenance,
authenticated channel/peer ownership, cancellation on disconnect, hostile-image
inspection separation, global use/exclusivity, fresh handle identity, repository
ancestry and exact overwrite authorization. Ordinary files and these observations
cannot stand in for that boundary. No elevated generic file-open endpoint exists.
The worker never converts Unknown use to Offline or grants restore permission.

There is no destructive state, checkpoint/resume or interrupted-write record in
this increment. Those need the reviewed VS-1917/1919 contracts and actual durable
capture/restore behavior. Qualification still needs independent recovery media
and the complete five-stage measured loop. Current validation and its limits:
[worker evidence](release-evidence/1.9-disk-worker-2026-10-17.md).

## Parent transport client — 2026-10-20

`DiskInventoryWorkerClient.ObserveAsync` selects only the fixed adjacent native
apphost. Public callers supply cancellation only: no image, UI or protocol value
can choose a program, argument or working directory. The executable must exist
and cannot itself be a symlink; no PATH or shell fallback is used. This check is
not publisher-signature validation or protection against a modified installation
or linked ancestor directory. The client is unprivileged and not yet called by
the desktop, recovery media or release packaging. Tests copy the apphost and its
runtime descriptors into the test output only.

The parent sends one request and closes stdin, concurrently watches stdout,
stderr and process exit, and rejects the first failing exchange task. A 35-second
parent deadline covers worker startup and the worker's own 30-second deadline.
Caller cancellation remains cancellation, rather than a usable partial result.
On failure, cancellation or timeout, a running worker is killed with a process-tree
request and its exit wait is bounded to two seconds. This is best-effort cleanup,
not a sandbox or a guarantee about detached descendants. The real worker spawns
no child processes.

Response handling requires exact full-width version/operation, matching nonempty
operation UUID, 1–1,048,576 body bytes, complete input, EOF, empty stderr and exit
zero. Only one diagnostic byte is examined, never retained or logged. Missing
workers and failures return typed outcomes with no inventory. Non-observed
inventory results remain explicit outcomes, never empty successful observations.

The strict JSON reader rejects duplicate, missing required and unknown properties,
unknown enums, null/oversized device collections, duplicate names/numbers, invalid
geometry, nonexistent/self parents, inconsistent state/reason or use flags, and
control characters in hardware identifiers. Metadata observations still cannot
establish global offline state, authenticated identity, freshness or write
authority. The operation UUID correlates transport only. No writable device
handle or capture/restore operation is added by this client.

The worker project explicitly selects invariant globalization so its technical
protocol can start on a minimal offline Linux environment without ICU. This
applies only to the worker process, not desktop languages. Localized recovery
UI/media culture dependencies remain a separate packaging requirement. Current
[client/runtime measurements](release-evidence/1.9-disk-worker-client-2026-10-20.md)
retain the initial missing-ICU failure and subsequent exact native results.

## Native read adapter boundary — 2026-10-20

Core now contains [a Linux x64 read adapter](LINUX_BLOCK_READ_1.9.md). The
inventory executable and protocol still perform no raw-device access: the new
adapter is not wired into that worker, desktop or recovery media. It executes
with its caller's existing privileges, offers no elevated endpoint and exposes
no writable stream or arbitrary native command. Authenticated source operations
and capture consistency remain required before product integration.


## Kernel peer binding prerequisite — 2026-10-21

`LinuxWorkerPeerBinding` is an internal Linux x64 component for an existing
connected Unix stream. A future trusted launcher must supply the exact expected
process ID and connect-time UID/GID; never derive that expectation from image or
wire claims. It checks kernel `SO_PEERCRED` and obtains `SO_PEERPIDFD` from that
same socket. It does not reopen a numeric PID, so process-ID reuse cannot redirect
that process lease. Credential/connection/liveness checks surround acquisition.

The held pidfd and its epoll monitor must be close-on-exec. Nonblocking polling detects task exit or
reaping without reading process data, sending signals or acquiring another
process's files. Rechecks inspect connection closure and connect-time credentials;
any failure invalidates the lease permanently. Disposal is idempotent and closes
only the owned pidfd and monitor. The caller still owns the socket and must close/cancel its
operation on refusal. No application bytes are consumed by these checks.

Missing kernel support refuses with no numeric-PID or uncredentialled fallback.
The first implementation is restricted to Linux x64 UAPI; no new platform/profile
support is claimed. Kernel references:
[Unix credentials](https://man7.org/linux/man-pages/man7/unix.7.html),
[Linux 6.12 socket options](https://github.com/torvalds/linux/blob/v6.12/include/uapi/asm-generic/socket.h),
[pidfd lifetime polling](https://man7.org/linux/man-pages/man2/pidfd_open.2.html).
The implementation uses the socket-bound pidfd option rather than pidfd_open.

### Security limits and integration

This binds a connection to a kernel process, not to a trusted executable build,
authenticated human consent, current global device use, privileged role or write
operation. SO_PEERCRED describes credentials when connect/listen/socketpair ran;
it does not prove current credentials after a transition. Inherited sockets and
forked senders are not per-message authenticated by this option. The native test
proves invalidation when the original peer dies while a fork retains the socket;
it does not claim to prevent every inherited-socket sender while that peer lives.
Socket closure with queued bytes is observed after those bytes drain; the future
protocol must separately bound requests, EOF, disconnect and deadlines.

The existing inventory worker/client still use their development pipe transport.
No listener, elevation helper, executable selection, device operation or desktop
path is added here. Trusted build/installation selection, authenticated channel
integration and per-operation consent, unprivileged hostile-image parsing,
controlled offline authority, sandboxing, recovery UI/media and writable restore
remain required. Internal test providers cannot be selected by a wire message.
No raw IDs, paths or free-form peer diagnostics enter product reporting.

Current retained source/runtime/native measurements and explicit limits:
[peer evidence](release-evidence/1.9-worker-peer-2026-10-21.md).


### Safe native interop follow-up — BUG-19034, 2026-10-21

Hosted analysis of 6a69f75 reports S3869 on manual SafeHandle extraction, even
though the earlier poll call paired reference acquisition/release. The current
implementation removes that pattern: epoll_ctl receives both owned SafeHandles,
and epoll_wait receives the owned monitor SafeHandle. Native-call marshalling
protects their lifetime automatically; the registered pidfd stays rooted through
the wait. No raw descriptor is extracted for a polling structure.

Both descriptors require close-on-exec and are released on refused acquisition,
failed recheck or disposal. The x64 epoll_event ABI is explicitly packed to
12 bytes, with a fixed one-event/zero-timeout wait. Relevant
[kernel layout](https://github.com/torvalds/linux/blob/v6.12/include/uapi/linux/eventpoll.h)
and [close-on-exec creation](https://man7.org/linux/man-pages/man2/epoll_create.2.html)
are retained references. No security exception, rule/threshold change or new
operation authority is introduced. Current source/runtime/FD-leak/native results:
[interop evidence](release-evidence/1.9-peer-safe-interop-2026-10-21.md).


## Inventory peer integration — 2026-10-21

VS-1904/#492 connects the internal kernel binding to the actual same-privilege
Linux x64 inventory launcher and child. This supersedes the earlier prerequisite's
statement that the inventory pipes are unchanged in isolation; the binary frame
schema remains version 1. Windows, macOS and other Linux architectures retain the
existing unprivileged pipe-only development transport. No platform is promoted
into a qualified disk profile by this change.

Before starting the fixed adjacent apphost, the Linux x64 parent creates an
exclusive temporary directory, verifies mode 0700 and no directory link, and
listens on a private Unix stream socket named `peer`. The generated endpoint is
the sole additional startup argument (`--inventory-peer <endpoint>`); no request
can choose a program, inventory provider, command or device. The path is bounded
by the Unix socket limit. The listener/socket are owned and removed on completion,
refusal or cancellation; cleanup never recursively removes unexpected entries.
A cleanup permission/I/O failure can leave the private directory for local
administrative cleanup; its path is never included in product diagnostics.

The parent requires connect-time PID equal to the process it actually started,
with its own effective UID/GID. The child requires the kernel-reported parent PID
(getppid), rechecks that relationship during binding, and requires its own
effective UID/GID. Both acquire the existing socket-bound process lifetime lease.
This is intentionally same-privilege only: a credential change, wrong peer,
unsupported socket-bound pidfd, inaccessible endpoint or failed binding refuses.
Linux x64 never retries a failed rendezvous using unauthenticated pipes. A
wrong connection consumes the one-shot attempt and fails closed.

The three bounded one-byte phases are startup ready (`31` hex), response ready
(`32`) and accepted (`33`). After mutual binding, the child announces startup;
only then does the parent send its existing correlated 32-byte request and stdin
EOF. The child emits/flushed the existing bounded response and announces response
ready, retaining its process and socket while awaiting acceptance. The parent
validates the exact response header/body/schema and rechecks the peer before
acknowledging. Only this terminal send has no post-send liveness check: it permits
the child to exit, so such a check would race a legitimate exit. The parent then
requires stdout EOF with no extra byte, empty stderr and exit zero before returning
any inventory. Early buffered EOF, malformed data, wrong phase or nonzero exit
never returns an inventory. Cancellation/deadline covers connection, phases,
pipes and process exit; existing bounded process-tree cleanup still applies.

The request's operation UUID remains on the existing frame, not the auxiliary
socket. No identity or path is emitted as telemetry. Legacy direct no-argument
worker invocation remains a development pipe endpoint; the Linux x64 parent does
not fall back to it. There is still no desktop launcher or packaged recovery
medium. Both paths remain read-only namespace observations, never global Offline.

This is kernel process identity/lifetime integration, not a trusted installation
or publisher check, authenticated per-message origin after fork/credential
transition, a sandbox, elevation, authorization for a device, human confirmation
or offline/source-consistency proof. Same-UID or administrator compromise is not
contained. Hostile image isolation, privileged broker/operation protocol, native
restore/journal/read-back, media/UI and full recovery/upgrade qualification remain
open. See [source-bound checks](release-evidence/1.9-worker-channel-2026-10-21.md).

## Native restore acquisition prerequisite — 2026-10-22

VS-1904/#492 now supplies an internal exclusive writable target opener outside
this executable's inventory protocol. Only the disposable-VM fixture calls it;
the worker still cannot receive a writable operation. Sealed image facts, the
unstarted operation-bound journal and original/staging/journal ancestry are checked
around fresh inventory and held native identity. openat2 excludes all symlinks,
requests O_RDWR/O_EXCL and has no weaker fallback. It grants no operation/consent/
offline authority; trusted build/role, hostile-image sandbox and actual writable
worker integration remain open. See [native contract](LINUX_RAW_RESTORE_OPEN_1.9.md)
and [current evidence](release-evidence/1.9-restore-open-2026-10-22.md).
