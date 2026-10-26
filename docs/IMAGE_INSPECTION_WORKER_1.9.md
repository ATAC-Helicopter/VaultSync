# Isolated image inspection development contract — VS-1904

Owner [#492](https://github.com/ATAC-Helicopter/VaultSync/issues/492), sole draft
PR #736, 1.9.0. The Linux x64 worker now has an internal image-inspection operation
separate from inventory. It requests bytes from two caller-held read-only streams;
it receives no filename, device selector, executable, credential or native handle.
The inspector opens no disk, writes no image and grants no restore authority.

The Linux raw staging path now requires this reader before allocation and after
sealing its private copy. The restore engine consumes the bound summary and exact
metadata/payload hashes without reparsing hostile structures in its caller. No
in-process fallback exists when the reader is unavailable. See
[staging integration](RAW_IMAGE_STAGING_1.9.md) and its
[source-bound measurements](release-evidence/1.9-reader-staging-2026-10-22.md).
This is not the completed trusted broker: installed executable/build/role review,
writable operation authority and desktop/media packaging remain open. Other
capture/inspection entry points require their own boundary review. All support
profiles remain candidates.

## Launch, ownership and bootstrap

The internal client selects the fixed adjacent `VaultSync.DiskWorker` apphost;
only local process tests can override installation and deadline. No shell or PATH
fallback exists. A missing/final-symlink executable refuses. Ancestor ownership,
installation/publisher integrity, exact worker role/build qualification and signed
distribution are still required; this path check is not executable trust.

The child starts with inherited privileges, solely to initialize trusted runtime
code. Before any image-byte request it authenticates the actual kernel parent
through the private mode-0700 Unix rendezvous and the existing PID/UID/GID/pidfd
binding. The parent binds its actual launched child. These are connect-time
credentials: dropping privileges afterward does not make SO_PEERCRED a current
UID measurement or per-message authentication. The inherited pipes carry frames.

Inherited environment is cleared, removing loader/profiler/startup hooks and
credentials. Fixed settings disable runtime diagnostics, use invariant culture
and request a 256 MiB managed heap. Framework-dependent development execution
derives DOTNET_ROOT only from the parent's already executing shared runtime;
self-contained deployments resolve their adjacent runtime. Neither image nor UI
input chooses this value. Exact serviced-runtime packaging remains a release gate.

Trusted bootstrap verifies immutable embedded synthetic metadata/raw bytes to
initialize parser, crypto and asynchronous hash dependencies. These 596/65,536-byte
resources are module-owned fixtures, never caller data or recovery evidence.
Their fixed hashes have regression checks. No untrusted image is parsed before
the policy below. Unknown/missing dependencies fail; no filesystem-open fallback
exists after policy installation.

The caller exclusively owns two distinct readable/seekable/nonwritable streams
for the entire operation. Lengths are snapshotted and rechecked before reads;
ownership is retained and positions may change. This does not itself seal caller
files against same-user/admin mutation. Production staging must supply immutable
held bytes and exact operation binding, not reopen paths from a summary.

## Irreversible child policy

Only Linux x64 is implemented. Unsupported kernel operations refuse, without
unsandboxed inspection. The child-only entry must never run in the app/broker.

1. Inspect inherited descriptors before reducing privileges. Admit the private
   peer, standard/runtime pipes, known eventpoll/eventfd/pidfd anonymous descriptors,
   read-only already-loaded installation/runtime files and read-only urandom.
   CLR `doublemapper` memory requires its exact anonymous name, close-on-exec,
   regular kind, zero links, current ownership and tmpfs identity. Unknown sockets,
   regular writable files, raw devices and other anonymous interfaces refuse.
   The JIT retains W^X support; no unsafe compiler setting is added.
2. If started as root, clear supplementary groups and use libc/NPTL setresgid/
   setresuid wrappers to set real/effective/saved identities to 65534. Nonroot
   operation retains the caller's nonzero UID/GID. Every thread must have matching
   real/effective/saved/filesystem IDs and zero inheritable/permitted/effective/
   ambient capabilities. Elevated/mismatched identities refuse.
3. Disable dumpability and set no-new-privileges. Apply CPU, file-size, core, file
   descriptor, data and virtual-address limits. Native/data limits account for
   existing CLR reservations; these are allocation bounds, not cgroup resident-
   memory containment or measured large-image performance qualification. CPU is
   capped at 120 seconds, cores/file growth at zero and descriptors at 128. Data
   permits baseline VmData plus 512 MiB up to 2 GiB; virtual address space permits
   baseline VmSize plus 2 GiB up to 16 GiB. Oversized baselines refuse.
4. Install the compiled classic-BPF syscall allowlist with seccomp TSYNC across
   all threads. Any nonzero return, including an unsynchronized thread ID, refuses.
   Other architectures are killed; unknown syscalls and the x32 namespace deny.
   No caller supplies or extends the filter.

The filter admits bounded runtime operations on existing descriptors/memory,
clocks, private pipes/events and synchronization. New filesystem/device opens,
filesystem mutation, socket/connect/accept/socketpair, exec, process forks, foreign
memory/ptrace, privilege changes, mounts/namespaces, io_uring, BPF and pidfd_getfd
deny. Thread-only clone requires VM/SIGHAND/THREAD and an explicit flag mask;
pointer-based clone3 returns ENOSYS for that auditable fallback. Signals target
this process only; CPU-affinity changes target only the current thread (PID zero),
as required by CLR thread initialization. Socket I/O targets only the pinned connected Unix-stream peer;
getsockopt permits only SO_TYPE/SO_ERROR/SO_PEERCRED, never a new peer pidfd.

After receiving the complete result, the parent independently inspects all child
task statuses while the exact peer is alive: nonroot IDs, zero capabilities,
NoNewPrivs=1 and Seccomp=2. A retiring task requires a new complete stable before/
after set, at most three attempts and 256 tasks. Missing/unreadable/changed tasks
cannot become a partial pass. Peer checks bracket observation to refuse PID reuse.
This confirms kernel state, not publisher identity or an external security review.

## Fixed binary protocol, development version 1

Integers are little-endian; UUIDs use RFC/network byte order. No JSON, paths,
reason strings, arbitrary display text or raw image bytes appear in a result.

| Frame | Layout |
| --- | --- |
| Request, 48 bytes | `VSIMREQ1`, version u32=1, operation u32=1, request UUID, metadata length u64, payload length u64 |
| Read, 40 bytes | `VSIMREAD`, exact request UUID, source u32 (0 metadata / 1 payload), count u32, offset u64 |
| Read reply | count u32, reserved u32=0, exactly count bytes |
| Result, 152 bytes | `VSIMRSL1`, exact request UUID, state u32, policy marker u32=1, facts below |

Result facts start at offset 32: image UUID (16), capture UUID (16), generation
(8), manifest root (32), verified bytes (8), logical sector (4), physical sector
(4), whole-payload SHA-256 (32). Verified facts require nonzero UUIDs/generation,
bounded sector-aligned capacity equal to the supplied payload length, supported
512/4096 geometry and canonical hashes. For Invalid/Unsupported/Incomplete/Corrupt/
Failed all facts are zero. The marker is not a sandbox or authorization token.

Each read requests 1–131,072 bytes wholly within one declared stream. The parent
rejects aliases, changed lengths, foreign operations/sources, overflow and extra
interfaces. Per operation, total read bytes are bounded by
`3*payload + 4*metadata + 1 MiB`; requests are bounded by
`4*floor((payload+metadata)/4096) + 16384`. Metadata is at most 16 MiB and payload
at most 8 TiB, refusal ceilings rather than qualified capacities. The verified
reader independently hashes the entire payload after content/layout verification.

The child remains alive through the parent's result/policy checks and terminal
peer acknowledgement. Exact stdout EOF, empty stderr and process exit zero are
mandatory. A partial result, error output, timeout or early/nonzero exit cannot
yield an observation. The child deadline/CPU ceiling is 120 seconds; the normal
parent deadline is 130 seconds. Cancellation kills a remaining worker and bounds
cleanup; synchronous blocked kernel I/O still requires operational qualification.

## Evidence and remaining gates

See [source-bound measurements](release-evidence/1.9-image-inspection-2026-10-22.md).
The subsequent [coverage/maintainability follow-up](release-evidence/1.9-sonar-coverage-2026-10-22.md)
retains those results at their original source. It bundles parent read context,
separates host dispatch, clarifies override parameter/field names and uses
asynchronous deadline cancellation without changing wire bytes or syscall policy.
ABI/runtime references: [seccomp and TSYNC](https://man7.org/linux/man-pages/man2/seccomp.2.html),
[NPTL credential wrappers](https://man7.org/linux/man-pages/man2/setresuid.2.html),
[CLR anonymous JIT mappings](https://github.com/dotnet/runtime/blob/v10.0.13/src/coreclr/minipal/Unix/doublemapping.cpp),
[CLR self-affinity initialization](https://github.com/dotnet/runtime/blob/v10.0.13/src/coreclr/pal/src/thread/thread.cpp#L1519).

No UI/telemetry field is added. All observations remain internal technical data;
future UI must localize outcomes in every maintained locale. Never upload raw
protocol bytes, images, journals, exception text, dumps or laboratory traces.

Trusted installation/role/protocol review, bounded writable operation integration,
controlled offline authority and overwrite consent, independent
media/UI, full filesystem/OS-boot/crash/power-cut recovery, installed upgrade,
signed packages and protected reviews remain open. Kernel compromise, arbitrary
administrator interference and cgroup containment are not qualified by this work.
No beta cut, tag, asset run, publication or merge is authorized by these checks.

## Capture GPT operation — 2026-10-23

VS-1904/#492 adds a second, separate read-only grammar on the same fixed inspector
host. `VSGPREQ1` cannot be decoded as an image request, and its parent accepts only
`VSGPRSL1`, never an image result. The source is one exclusively caller-held
read-only stream. Source index 1 supplies bounded bytes; source index 0 has length
zero and any nonempty metadata request refuses. No path, raw descriptor, source
name, tool/origin text or writable command crosses this grammar.

| Request, 48 bytes | Offset / layout |
| --- | --- |
| Magic/version/kind | 0: `VSGPREQ1`; 8: u32=1; 12: u32=2 |
| Operation | 16: nonzero RFC/network UUID |
| Geometry | 32: logical sector u32 (512/4096); 36: reserved u32=0; 40: capacity u64 |

| Result | Offset / layout |
| --- | --- |
| Fixed header, 128 bytes | 0: `VSGPRSL1`; 8: exact operation UUID; 24: state u32; 28: reason u32; 32: policy marker u32=1; 36–47: zero |
| Positive facts | 48: capacity u64; 56: logical sector u32; 60: partition count u32; 64: disk UUID; 80/88: first/last usable LBA u64; 96: GPT-metadata SHA-256 (32 bytes) |
| Partition, 64 bytes each | 0: entry number u32; 4/20: type/partition UUID; 36/44/52: offset/length/attributes u64; 60: reserved u32=0 |

All integers remain little-endian and UUIDs network-order. Refusals have no facts,
zero partitions and exactly 128 bytes. Enum values use the existing fixed GPT
assessment states/reasons; only LayoutInspected/LayoutOnly carries facts. The
parent admits at most 128 partitions (8,320 total result bytes) before allocation,
requires exact requested capacity/sector, canonical reserved bytes, unique ordered
entry/partition identities, aligned bounded nonoverlapping ranges and valid GPT
attributes. A digest is an observation, not provenance or device authorization.
The independent synthetic wire fixture is `disk-gpt-inspection-v1.json`.

The existing held-source byte/read-count ceilings, runtime warmup, irreversible
privilege drop/seccomp policy, independent live kernel-state validation, terminal
peer ACK, stdout EOF, empty stderr, exit zero and deadlines remain mandatory.
Shared transport does not relax the original image grammar or its fixed result.
The GPT reader parses in its restricted child; the parent decodes only the bounded
flat layout record for capture metadata encoding. Capture still needs a trusted
installed build, source/offline authority and full recovery qualification.

[Capture integration](LINUX_RAW_CAPTURE_1.9.md) now requires isolated source GPT
checks before allocation and after copying, then sealed-copy image inspection
before publication. No missing/failed/timed-out reader has an in-process fallback.

## Bounded digest reads — BUG-19039 / #816

A full 3 GiB OS capture exposed excessive authenticated pipe traffic during the
reader's independent payload digest. The generic stream hashing API requested
4 KiB blocks; a separate 2 MiB oracle probe measured 512 requests, versus 16 with
the existing exact-length 128 KiB reader, with identical SHA-256. The worker now
uses that reader for its digest and trusted warmup. Wire framing, byte/call
budgets, kernel policy, deadlines, cancellation and no-fallback behavior remain
unchanged. Every pass rereads bytes; no payload cache is introduced. This is a
request-count observation, not a qualified throughput/capacity claim.
