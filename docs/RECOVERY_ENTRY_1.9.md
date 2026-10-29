# Installed recovery entry — VS-1907 / #495 and VS-1906 / #494

Implementation contract for the separate media-only host/controller. This does
not qualify a recovery image, support profile, publisher signature or beta.
The read-only `VaultSync.DiskWorker` role and inspection format stay unchanged.

## Executable boundary

The fixed recovery service starts `VaultSync.RecoveryHost` directly as root.
The host admits its installation and boot context before mutable setup, launches
a fixed native trampoline and admits only that exact nonroot controller process.
It then joins the kernel-bound channel to `LinuxRecoveryWorkerOperation` and
`LinuxRecoverySupervisor`; the original native review still requires the
independent physical-console action. Automatic controller acknowledgment is
correlation, never human or device authorization.

The trampoline has no caller-selected executable, UID or filesystem path. It
clears inherited descriptors and environment, removes groups/capabilities, sets
all user/group IDs to 60999, enables no-new-privs, creates a separate session and
executes only the installed controller. Console, target and storage descriptors
must not reach that process. The controller opens image files read-only as its
own user; the privileged host never opens its requested image paths.

This first entry is headless. The graphical recovery flow must use the same Core
client boundary after its presentation/state contract is connected. The ordinary
application recovery page remains read-only; no placeholder GUI flag constitutes
an installed controller or an accepted recovery session.

## Installation and boot admission

`recovery.installation.json` identifies schema 1, role
`offline-recovery-media-v1`, RID `linux-x64`, exact source commit, console culture,
PID 1 systemd executable hash and sorted closure records. Bounds are 1 MiB manifest,
2048 files, 64 MiB per file and 1 GiB total. Unknown/duplicate fields, aliases, missing
runtime/Core/role files, writable ancestry or differing Core binaries refuse.
The host/controller, unchanged isolated reader, native launcher, boot unit and
catalogs are one source-bound closure. This local closure check is not publisher
provenance; final media/assets still require the release signing policy.

Admission combines actual root credentials, direct systemd parent, matching held
executable/build/role, initial UID mapping, shared PID 1 mount/network namespaces,
read-only root/install mounts and globally quiescent network interfaces. A marker,
UUID, supplied boolean or private namespace cannot stand in for these facts.
The selected service explicitly avoids private mount/network namespaces; a
private network can otherwise imply a private mount namespace and change `/sys`.
See [systemd's namespace semantics](https://www.freedesktop.org/software/systemd/man/258/systemd.exec.html).
Native measurements must prove the installed unit and admission agree.

The host checks the launched controller's actual executable/Core, exact kernel
credentials, process lifetime and zero privilege state. The controller checks
its immutable installation, root-owned rendezvous and actual kernel parent/peer;
it does not assume that a nonroot process can inspect root `/proc` executable
links. An adversarial root process is outside this unprivileged-controller threat
boundary. Installed media provenance remains a separate gate.

## Ownership and failure

An exclusive boot-service owner prevents a duplicate from canceling the active
operation. Child processes belong to a verified delegated controller cgroup;
cleanup before connection must not signal a potentially recycled numeric PID.
Cancellation closes/drains the owned channel, child and restore resources, and
preserves the actual outcome rather than inventing completion.

Each operation creates a fresh root-private directory on the fixed ext4 store.
Existing operation paths are never adopted. After confirmed success/cleanup,
source-bound local evidence must persist before removing only proven-owned,
regenerable staging through held no-follow identities. Failure/interruption or
uncertainty retains staging/journals with a bounded reservation policy. Aliases,
unexpected entries and uncertain cleanup prevent a clean result; original image
files and backup permissions are never changed.

## Remaining qualification

Real readonly-root boot, source/role admission, exact-child/cgroup cleanup,
retained-storage behavior and negative cases require their own native evidence.
Catalog admission for English/Italian is not font qualification; only English
console rendering has prior component evidence. Graphical consent remains open.

Root-private images created by earlier capture paths may not be readable by the
dedicated controller user. Report access denial honestly. A reviewed source-handle
broker or export policy is still required for that complete capture/restore loop;
never repair it by chmod on an original backup or privileged opening of a GUI path.

Keep both owners open/In progress and #736 draft until their actual closure
criteria pass. No ordinary desktop elevation, host-device test or release
publication follows from adding the executables or passing a build.

## Component checkpoint — 2026-10-29

Host/controller Release builds pass with warnings-as-errors. The five focused
C# classes pass 34 tests and the producer passes six tests. Unrelated local suites
and repeated localization checks were skipped because no visible strings changed.
These checks do not exercise native admission or deletion. See the
[source-bound receipt](release-evidence/1.9-recovery-entry-2026-10-29.json).
