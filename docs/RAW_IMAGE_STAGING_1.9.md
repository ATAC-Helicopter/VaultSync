# Protected raw image staging — VS-1907

Owner #495; 1.9.0 preparation PR #736. Linux x64/ext4 staging with mandatory isolated inspection
under the approved joint direction. This supplies an input capability without writable access, not authenticated worker trust or permission to open a destination.
No writable device, mount, executable descriptor or image-selected path is used.

## Input and admission

The caller owns readable metadata/payload streams. Metadata starts at the envelope
and may be nonseekable; payload must be seekable and start at byte zero. Path
acquisition and trusted worker selection remain the caller's separate boundary.
Raw metadata is snapshotted into owned memory with a 16 MiB total bound plus
one EOF probe; the source cannot select output filenames or storage providers.

Before reserving space, run the fixed adjacent isolated reader against the owned
metadata snapshot and a non-owning read-only view of the caller payload. The
caller may retain writable input access; this first view is not sealed. Unknown,
truncated or corrupt input and unavailable/failed/timed-out inspection refuse
without allocating a stage. There is no in-process parser fallback. Validate the
canonical bounded summary and exact payload length before using its capacity.
Keep all observed IDs/root/geometry and whole-payload SHA-256 for comparison;
this first observation does not make the original immutable or prove its creator.

## Copy and seal protocol

1. Reserve full private ext4 output through LinuxRawImageStorage with a new
   staging UUID. The caller must choose a separate staging parent, not reuse the
   ordinary published-image collection. Storage retains its UID/permission,
   all-component symlink refusal, full allocation and held-directory checks.
2. Rewind source payload, copy exactly the observed capacity in <=128 KiB chunks,
   require EOF, and write the original owned metadata snapshot. Flush both files.
3. Seal: fchmod both held regular files to 0400, fsync through writable descriptors,
   reopen fixed children with O_RDONLY/O_NONBLOCK/O_NOFOLLOW/O_CLOEXEC, compare held
   inode, filesystem device, size, UID, permissions and links, and close every
   transaction-owned writable descriptor. Read-only reopens disable managed read
   buffering (BUG-19036): repeat seeks/hash/copy observe the held inode, not stale
   bytes from a previous pass. Callers provide their own bounded transfer buffers.
   Merely chmodding is insufficient.
4. Require readable nonwritable access. Run the isolated reader on these SAME held
   sealed streams; require every bounded result field and whole-payload hash to
   equal the first observation. The parent never parses manifest or GPT records.
5. Bind the session to the snapshot's exact metadata SHA-256/length and the isolated
   reader's whole-payload SHA-256/capacity. Independently hash both held streams,
   with bounded reads, exact length and EOF, before directory flush/publication.
   Any source/copy/seal mutation or lying/inconsistent reader summary refuses.
6. Flush image/parent directories, check cancellation, publish with rename-no-replace
   and final parent fsync. Only confirmed publication returns the owned session;
   a post-rename sync failure returns PublicationUnconfirmed without a session.

The session exposes a summary assessment and logical/physical geometry publicly;
read-only streams, metadata hash and reader result remain internal. Declared origin,
tool strings and parser-owned coverage/layout graphs are not carried into the
privileged consumer. Original metadata bytes remain unchanged in the private copy. Original caller streams remain caller-owned and can be closed or
changed after staging; the session must continue to verify independently. Its
assessment is byte-integrity evidence, not authenticated origin, boot validation,
or authorization. A stage UUID is distinct from the original image UUID and
never renames or edits the original image. Stage files remain private on disposal;
UI lifecycle/explicit cleanup and capacity guidance remain integration work.

A future restore consumer must use the owned stage, fresh target handle/geometry,
repository/staging ancestry and explicit overwrite authority. It must never
reopen the original image after preview or accept these streams as permission
to write a disk. No public raw handle/path or writable source is exposed.

## Interruption and trust limits

Cancellation before publication returns Interrupted and no session. Cancellation
after committed publication cannot retroactively deny the owned session. Every
I/O, seal, changed-inode, corruption, directory-flush or rename refusal returns
no usable capability. Partial stages are not ordinary restore inputs or resumable
captures. A failed disposal during refusal stays a failure, never success.

A trusted OS, administrator and same-UID peers remain assumptions; 0400 and an
O_RDONLY descriptor do not contain malicious root or a compromised worker.
Both image parsing passes now use the unprivileged syscall-constrained reader;
its fixed executable path is not trusted installation/build/role qualification.
Its existing deadline/disconnect policy applies per inspection, with no weaker
fallback. Repeated full inspection/copy/hashing adds I/O; no large-image throughput
or capacity is qualified. Current managed tokens cannot preempt synchronous kernel
operations; the future writable operation still needs a bounded lifetime protocol.

The full recovery path, independent boot media, authenticated operation protocol,
exact device consent, overwrite UI in all 19 locales, controlled interruption/
power-cut qualification and supported profile evidence remain open. All four
profiles stay candidates; checksums cannot promote a stage to restored-system
evidence or trusted creator identity. No beta is published by this increment.


## Native repository observations

Sessions now privately retain statx filesystem device numbers from original
regular input descriptors and their staging parent. Unknown source ancestry
does not invalidate byte inspection, but it blocks a
[native target description](LINUX_RESTORE_TARGET_PREVIEW_1.9.md). Those numbers
are not trusted image claims, persistent physical identity or permission to write.


## Internal write-engine consumer — 2026-10-21

VS-1907 now consumes exclusively caller-owned sealed sessions in an internal
[write/read-back engine](RAW_DEVICE_RESTORE_1.9.md). It revalidates the entire image
before writing an already-held target and verifies the complete target afterward.
A sealed session, image root or review UUID still grants no writable capability.
The engine supplies no path opener, privilege, offline proof, product journal
acquisition or UI/media integration. Native writes are currently measured only
against explicitly selected new disposable-VM disk files; support stays unqualified.


## Reader-first consumer integration — 2026-10-22

The restore engine now checks fixed metadata/payload hashes before Started and
hashes exact target read-back against the isolated reader's payload SHA-256.
Neither staging nor that writer reparses hostile metadata/GPT in its caller.
Preview/opener compare bounded geometry from the session instead of declared
origin graphs. Byte equality carries the reader's GPT check to read-back; it does
not prove a bootable OS, durable recovery, creator identity or device authority.
Other capture/inspection entry points and installed worker trust are separate
qualification work. See [source-bound integration evidence](release-evidence/1.9-reader-staging-2026-10-22.md).
