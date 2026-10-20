# Protected raw image staging — VS-1907

Owner #495; 1.9.0 preparation PR #736. Same-privilege Linux x64/ext4 staging
under the approved joint direction. This supplies an input capability without writable access, not authenticated worker trust or permission to open a destination.
No writable device, mount, executable descriptor or image-selected path is used.

## Input and admission

The caller owns readable metadata/payload streams. Metadata starts at the envelope
and may be nonseekable; payload must be seekable and start at byte zero. Path
acquisition and trusted worker selection remain the caller's separate boundary.
Raw metadata is snapshotted into owned memory with a 16 MiB total bound plus
one EOF probe; the source cannot select output filenames or storage providers.

Run the independent raw-profile reader before reserving staging space. Invalid,
unknown, truncated or corrupt input cannot allocate a stage or return a session.
Keep the owned metadata snapshot and its verified manifest root. This initial
observation does not make the original payload immutable or prove its creator.

## Copy and seal protocol

1. Reserve full private ext4 output through LinuxRawImageStorage with a new
   staging UUID. The caller must choose a separate staging parent, not reuse the
   ordinary published-image collection. Storage retains its UID/permission,
   all-component symlink refusal, full allocation and held-directory checks.
2. Rewind source payload, copy exactly the verified capacity in <=128 KiB chunks,
   require EOF, and write the owned metadata snapshot. Flush both files.
3. Verify all staged bytes and actual GPT against that snapshot and the initial
   root. Mutation or truncation of the original between checks cannot produce
   a usable stage. Do not substitute newly regenerated metadata for the snapshot.
4. Seal: fchmod both held regular files to 0400, fsync through their writable
   descriptors, reopen fixed children with O_RDONLY/O_NONBLOCK/O_NOFOLLOW and
   O_CLOEXEC, compare held inode, filesystem device, size, UID, permissions and
   link count, then close every writable descriptor owned by this transaction. Merely chmodding an inode
   while retaining a writable descriptor is insufficient.
5. Require read-only stream access and independently verify the sealed copies
   again. Flush image/parent directories and check cancellation before commit.
6. Publish the stage with rename-no-replace and final parent fsync; only success
   returns LinuxRawImageStagingSession. A post-rename sync failure is explicitly
   PublicationUnconfirmed and returns no restore-input capability.

The owned session exposes image assessment publicly and read-only streams only
inside Core. Original caller streams remain caller-owned and can be closed or
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
Untrusted image inspection must remain unprivileged when the worker integration
arrives. Current managed tokens cannot preempt synchronous kernel operations;
bounded worker lifetime/disconnect handling remains required.

The full recovery path, independent boot media, authenticated operation protocol,
exact device consent, overwrite UI in all 19 locales, controlled interruption/
power-cut qualification and supported profile evidence remain open. All four
profiles stay candidates; checksums cannot promote a stage to restored-system
evidence or trusted creator identity. No beta is published by this increment.
