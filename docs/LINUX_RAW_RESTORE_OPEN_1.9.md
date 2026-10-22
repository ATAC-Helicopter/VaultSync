# Internal Linux restore target acquisition — VS-1904

Owner [#492](https://github.com/ATAC-Helicopter/VaultSync/issues/492), preparation
PR #736, 1.9.0. This internal Linux x64 primitive acquires an exclusive writable
block descriptor for the existing [write engine](RAW_DEVICE_RESTORE_1.9.md).
Only the private disposable-VM fixture invokes it. No inventory-worker operation,
public API, elevation, desktop action or media launcher reaches it.

## Preconditions and exact binding

A future trusted broker must establish executable/role trust, authenticated
operation ownership, actual human overwrite consent and controlled offline
execution **before** calling. An observation, request UUID, acknowledged preview
or journal does not establish that authority. This is a native acquisition
prerequisite, not the missing authorization protocol. It must not be exposed
directly to unprivileged callers. The broker must exclusively own stage, journal
and operation lifetime throughout acquisition and execution.

The fixed internal call accepts a sealed staging session, exact typed restore
request, published private journal and recent expected inventory. No path,
executable, shell command or credential is accepted. Strict journal-header
validation refuses unsupported geometry, unsafe target names, observed busy,
read-only, ambiguous or partition targets before any native open. Stage image,
capture, generation, root, capacity and both sector geometries must match.
A writable or disposed stage refuses.

The journal must match the exact request, remain published/live/unpoisoned and
contain no appended records. Its held filesystem must still resolve uniquely
outside the target, to the same whole-disk observation recorded at publication.
A started journal cannot reopen a target here. This is neither automatic resume
nor a replay-resistant capability; creating a journal never grants permission.

Expected and independent inventories must be recent (at most 30 seconds, never
future), complete and without unresolved swaps or duplicate names/device numbers.
Boot, namespace, exact target and uniquely resolved original image/staging
repository disk identities must remain equal. Unknown, stacked, changed or target
ancestry refuses. Original image, staging and journal cannot live on the target.
Namespace-only use remains Unknown.

## Native acquisition and ownership

1. Check request, stage, ancestry and journal preconditions.
2. Independently reobserve and repeat binding and journal checks.
3. Open only `/dev/<validated-kernel-name>` using Linux x64 `openat2`,
   `O_RDWR|O_EXCL|O_NOCTTY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC` and
   `RESOLVE_NO_SYMLINKS`. Every component, including magic links, is covered.
   Unsupported kernels refuse; no weaker open fallback or retry exists.
4. Inspect held block kind, device number, disk sequence, capacity, both sector
   geometries and read-only state. Ordinary files refuse.
5. Independently reobserve all bindings/ancestry/journal and reinspect the held
   descriptor. Changes close it without writing or appending a journal record.
6. Transfer into the bounded native adapter, which independently rechecks access
   flags, close-on-exec and held identity. Recheck the lease and live stage/
   unstarted journal before returning ownership.

Before successful adoption the opener owns cleanup; afterward the adapter owns
the descriptor. Refusal, cancellation and acquisition exceptions release the
current owner. Returned ownership must be disposed or transferred directly to
the engine, which closes it on every execution outcome. Opening alone does not
write/flush data or append records. The engine subsequently performs its existing
full stage verification and durable intent ordering. Native busy/access/
unavailable/unsupported outcomes remain typed. Managed cancellation cannot
preempt synchronous kernel open, ioctl or close.

Linux block-device `O_EXCL` refuses kernel-claimed busy devices; it does not stop
a privileged program ignoring exclusivity and issuing raw I/O. It is not proof
of global inactivity, OS consistency, physical power-loss durability or recovery
success. Controlled offline execution and isolation remain broker/media duties.
ABI references: [open(2)](https://man7.org/linux/man-pages/man2/open.2.html),
[openat2(2)](https://man7.org/linux/man-pages/man2/openat2.2.html).

## Validation and release limits

See [source-bound evidence](release-evidence/1.9-restore-open-2026-10-22.md).
Ordinary-file fault fixtures exercise ordering/cleanup only. Actual native tests
use precisely selected disposable virtual targets; no host raw device is passed.
Outcomes are internal technical states; no UI or telemetry field is added.
Future UI must translate every surfaced outcome in every maintained locale.

Trusted writable worker/protocol/consent/offline authority, hostile-image sandbox,
independent recovery media/UI, crash/power-cut/full filesystem boot recovery,
installed predecessor upgrade, signed packages and protected reviews remain open.
All four disk profiles stay candidates; beta is not feature complete.
