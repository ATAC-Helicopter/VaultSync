# Internal raw-device write engine — VS-1907

Owner #495; sole preparation PR #736. This is the first bounded write/read-back
engine behind internal interfaces. It has no public restore API,
elevated endpoint, desktop route or recovery launcher. A separate internal
[native acquisition prerequisite](LINUX_RAW_RESTORE_OPEN_1.9.md) now supplies
an exclusive target in the disposable-VM lab; it grants no device authority. The approved
joint direction permits implementation and disposable-VM tests; it does not
supply host-device authorization or qualify any support profile.

## Admission and trust

The trusted caller must exclusively own the sealed staging session, an exact
already-authorized/exclusively held target and a private durable journal outside
that target. A request binds operation/review UUIDs, image/capture/generation/root
and a whole-disk observation. UUIDs and observations are correlation data, not
human consent, executable provenance, an offline proof or device authority.
Neither an image nor an unprivileged request can select a writable path here.

Admission requires nonempty operation/review identity, exact staged image facts,
matching target observation, whole disk with unique observed identity/nonzero
disk sequence, no observed mount/swap/holder, writable state and equal capacity,
logical and physical geometry. Unknown use stays Unknown. The sealed metadata
and payload are independently reverified in full before the first journal/write.
Corrupt/disposed or still-writable stages refuse. Ordinary file test providers
exercise ordering only; they are never native physical identity substitutes.

The Linux x64 adapter adopts an already-held descriptor; it does not itself open it or
prove its exclusive claim. Ownership transfers only on successful adoption.
The caller retains a refused descriptor. It independently checks native block
kind/device number/capacity/geometry/disk sequence/read-only state with the existing
held-handle inspector. Access must be O_RDWR, with FD_CLOEXEC and without O_APPEND
(offset semantics) or O_DIRECT (unsupported managed-buffer alignment). Ordinary
files, closed/invalid handles, unsupported geometry and observed busy targets
refuse. Native writes are capped at 128 KiB, sector aligned and within capacity.
Held identity is checked around writes/flushes and on read-back through the
existing read-only stream facade. No discard, resize, repartition ioctl, source
path reopen or generic privileged file-open endpoint is introduced.

The adapter does not establish current global use, repository/journal ancestry,
trusted build/role, exact human consent or controlled offline state. These are
required future broker obligations before any production device operation.
The only native writer caller currently lives in the private disposable-VM lab.
All public preview/review APIs remain read-only and grant no writable capability.

## Journal and I/O order

The [private journal implementation](RAW_RESTORE_JOURNAL_1.9.md), added on
2026-10-22, now supplies the native persistent provider described below. Earlier
statements that only the lab supplied durable storage are historical. The
privileged broker/writable operation protocol and UI/media integration remain
unimplemented; the journal never supplies device authority or automatic resume.

`IJournal.AppendDurableAsync` must return only after its exact typed record is
committed to trusted local storage. The internal version-1 provider now uses
private binary records, preallocation, held-parent ancestry checks, file/directory
flushes and rename-no-replace publication. The earlier JSONL provider remains
historical lab evidence. There is no stable public journal compatibility or
resume API. Trusted broker/build/role integration and retention controls remain
required. The journal is local technical evidence; never upload it or raw
payloads/logs.

1. Rehash bound staged metadata/payload and recheck held target, then durably append Started.
2. Read at most 128 KiB from the sealed payload. Recheck the target and durably
   append WriteIntent with exact offset/length and intended SHA-256.
3. Mark that interval uncertain before calling the writer. Cancellation or a
   native exception can occur after some bytes changed; never infer zero writes.
4. Write the interval, recheck identity, flush target storage, recheck again,
   and durably append RangeFlushed with the same range/hash. Only then advance
   acknowledged bytes and clear that interval's uncertainty.
5. After all ranges, independently run the full raw verifier against the held
   target: length, payload digest, completion/root and actual primary/backup GPT.
   Recheck identity and durably append ReadBackVerified before reporting the
   narrowly named WrittenAndReadBack byte observation.
6. Release the target on success, refusal, cancellation and failure. A cleanup
   failure changes the outcome to Failed and attempts a final failure record;
   a preceding read-back record remains a byte observation, not completed recovery.

A RangeFlushed record acknowledges successful target flush plus journal return,
not independent per-range read-back, physical power-loss durability or an OS boot.
The final verification is separate. SHA-256 proves byte binding, not authorship.
Full target capacity must match the staged image; there is no larger-disk GPT
relocation, sparse omission, sector translation or automatic resume/rollback.

## Interruption and limits

Failures report the contiguous acknowledged prefix and at most one uncertain
interval. Write/flush/acknowledgement failure cannot advance that prefix.
An unconfirmed journal append is not treated as persisted. Cancellation/failure
attempts a terminal record without the cancelled caller token; failure to retain
it is reported explicitly. Cancellation after durable ReadBackVerified does not
undo that observation. Cancellation before any write leaves bytes untouched;
its intent range may still conservatively be marked uncertain.

The engine assumes trusted OS/administrator/same-UID storage and exclusive caller
ownership of stage/target/journal. The mandatory staging path now runs the
[isolated reader](IMAGE_INSPECTION_WORKER_1.9.md) before allocation and on the sealed
copy. The session binds only bounded facts, geometry and exact metadata/payload
hashes, not declared origin/tool/coverage graphs. Before Started, the writer hashes
both sealed streams and requires exact lengths/EOF. After writing, exact full target
SHA-256 must match the reader-verified payload. This carries byte/GPT identity
without parsing hostile records in the writer; it is not filesystem/OS-boot or
power-loss evidence. Same-UID/admin mutation and trusted executable/build/role
qualification remain separate assumptions/gates. See
[integration measurements](release-evidence/1.9-reader-staging-2026-10-22.md).
Synchronous flush and
uninterruptible kernel I/O cannot be preempted by a
managed token; the future integrated bounded worker must enforce operation lifetime
and disconnect behavior. The current inventory rendezvous is not that protocol.

All outcomes and exceptions are internal technical states, not UI strings or
telemetry fields. Future UI must localize every surfaced outcome in all locales.
Full privileged broker/operation consent, journal lifecycle/retention controls,
independent recovery medium, UI, power-cut/crash qualification, full filesystem
recovery/boot/interactive verification and installed upgrade/package/signature
checks remain open. All four disk profiles remain candidates.


## Explicit release outcome — BUG-19034, 2026-10-21

Hosted fcb47e8 reports S2583 on the cleanup flag assigned inside finally.
Existing successful/failed-disposal tests exercise both paths, with no measured
native data failure. The release helper now returns an explicit outcome instead
of relying on flag mutation through exception control flow. Expected cleanup
errors still produce Failed and attempt a failure record; unexpected execution
exceptions still release the target before propagating and never append a
read-back completion. No journal order, native authority, wire format or quality
threshold changes. See [current checks](release-evidence/1.9-restore-cleanup-2026-10-21.md).

## Owned operation integration — 2026-10-25

VS-1907/#495 now supplies an internal [single-owner operation](LINUX_RAW_RESTORE_OPERATION_1.9.md)
joining sealed staging ownership, expiring native review, fresh inventory,
persistent journal, exclusive acquisition, this writer and cleanup. Replays
cannot cancel an active owner; timeout/disposal preserve uncertain write results.
The engine's byte observation remains separate from overall cleanup success.
The trusted caller still establishes worker/offline/device/human authority;
there is no new public writable or elevated endpoint.
