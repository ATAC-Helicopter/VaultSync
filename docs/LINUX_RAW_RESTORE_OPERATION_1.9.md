# Owned Linux restore operation — VS-1907

Owner [#495](https://github.com/ATAC-Helicopter/VaultSync/issues/495), preparation
PR #736, 1.9.0. `LinuxRawRestoreOperation` connects the existing staging, native
review, persistent journal, exclusive opener and byte writer in one owner.
It is internal worker-side execution code; only controlled disposable-VM callers
currently invoke its native path. No desktop action, public writable API,
elevation route or new worker command is added by this integration.

## Authority and ownership

The trusted caller must establish installed worker/role trust, authenticated
operation ownership, controlled offline execution and actual human overwrite
consent before executing. Operation/review UUIDs acknowledge a description;
they cannot supply those prerequisites. The architecture approval permits the
retained disposable-VM tests, not maintainer host-device writes or publication.
The [native opener](LINUX_RAW_RESTORE_OPEN_1.9.md) keeps all independent checks.

The new [operation transport](RECOVERY_OPERATION_PROTOCOL_1.9.md) supplies bounded
framing and read-only source-descriptor handoff for future integration. It does
not provide the supervisor/build/role/offline/human/device prerequisites above,
nor does a Confirm/review UUID call this owner or grant writable access.

Preparation takes exclusive ownership of the sealed staging session on entry,
including refusal/cancellation. It creates the native read-only preview and one
review with a new operation ID. Invalid bounds, unavailable/changed selection or
failed review creation release the stage and any acquired preview. A returned
operation owns them until execution or explicit disposal; input streams are not
adopted, reopened or substituted by this owner.

The caller must not concurrently use/dispose the transferred stage or acknowledge
its review independently. The review exposes only the already bounded image,
exact target and overwrite range. Existing wall/monotonic 30-second review expiry
and native repository exclusion remain unchanged.

## One execution

1. Consume the attempt before asynchronous work. Foreign IDs and duplicates
   refuse; a duplicate never cancels or disposes the active attempt.
2. Acknowledge the exact review and release its read-only exclusive claim.
3. Independently rehash the sealed image, then obtain fresh inventory. Refuse
   changed boot/namespace/target/repository ancestry before journal acquisition.
4. Publish/acquire the operation-bound persistent journal outside the target.
5. Acquire the native writable descriptor using the existing independently
   reobserving opener. This closes the observation-to-write gap; no weaker open,
   retry or observation-only write authority exists.
6. Run the existing writer: durable intent, bounded write, target flush, durable
   acknowledgement and full read-back. Preserve its acknowledged prefix,
   uncertainty and terminal-record result unchanged.
7. Release target, journal, preview and stage on every outcome. The target lease
   is idempotent after a confirmed engine close; an unconfirmed close receives
   one final attempt. All remaining resources are attempted even if one fails.

Overall `WrittenAndReadBack` requires confirmed cleanup. A cleanup failure changes
the overall result to Failed/CleanupUnconfirmed while retaining the writer's
separate observation. Neither byte verification nor resource closure proves
filesystem consistency, physical power-loss durability or successful OS boot.
`JournalAcquired` means the owner received a live published journal, not that a
failed acquisition left no partial/unconfirmed filesystem artifact.

Both ownership/cleanup gate waits explicitly use `CancellationToken.None` so
caller cancellation cannot strand transferred resources before cleanup.
Disposal cancels the active lifetime, waits for its serialized owner and releases
an abandoned review. Caller cancellation and the monotonic execution timer share
the same cleanup path. The hard execution ceiling is 24 hours; local fault tests
may shorten it but never increase it. Timeout reports Interrupted/TimedOut,
retaining write uncertainty and journal outcomes. Synchronous native I/O/close
cannot be preempted by a managed token; the privileged process supervisor remains
responsible for an unresponsive worker. There is no automatic resume or rollback.

## Verification and integration limits

Twenty-seven portable lifecycle/fault cases cover 512/4096-byte profiles, exact
review binding, changed image/selection on both sides of acknowledgement,
publication/acquisition failures, replay, concurrent calls/disposal, cancellation,
deadline, partial writes, bad read-back, unconfirmed journal flush and cleanup
failure. Their targets are ordinary byte arrays and synthetic native observations;
these tests never establish physical device or human authority.

The [source-bound full OS loop](release-evidence/1.9-owned-restore-2026-10-26.md)
now passes offline 3 GiB capture, independent recovery restart without the source,
native restoration/durable journal read-back, whole-disk host equality and a UEFI
ext4 boot of the restored system with three matching fixture hashes. Separate
512/4096 native fault cases retain partial-sector writes and honest uncertain
ranges. This is a private lab caller, not production worker/human authorization
or generated product media. Failed attempts remain distinct. UI/media, support,
installed upgrade, signed packages and protected reviews remain release gates;
no profile or beta is promoted by this owner alone.
