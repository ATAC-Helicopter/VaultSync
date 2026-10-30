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

The source-bound component receipts below measure readonly-root boot, source/role
admission, exact-child/cgroup cleanup and retained storage on tiny synthetic
disks. Full OS/profile/fault and final media/package loops remain unqualified.
Catalog admission for English/Italian is not general font qualification; the
active Cocoa frame demonstrates only the English console. Graphical consent
and other locale/platform rendering remain open.

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

## Teardown follow-up — 2026-10-29

The first installed-entry consent probe reached verified restoration and persisted
its final staging-cleanup receipt, but the service then exited 74. This run remains
failed evidence. The serviced runtime automatically removes directly bound Unix
socket paths on disposal; the subsequent identity check therefore saw its own
missing socket. A delegating endpoint now preserves the exact Unix address while
leaving pathname deletion exclusively to held-parent, identity-checked cleanup.
It also prevents automatic deletion of an unexpected replacement. Cgroup lifetime
checks and timeouts are unchanged. See the
[serviced runtime implementation](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L3185).

Eight affected tests pass on macOS, including three real socket regressions.
Their POSIX pathname branches are explicitly limited to Linux/macOS; Windows API
availability does not qualify these filesystem semantics. No unrelated suite was
repeated. A new exact-source installed consent probe is required before declaring
this teardown repaired natively.

## Installed component evidence — 2026-10-29

The repaired source `856ba6069e0d8b8de2fa15a430b89efd1618edd3` passes the
installed consent probe: real readonly-root admission, fixed nonroot controller,
physical VT review, synthetic keyboard confirmation, 64 KiB write/read-back,
retained completion evidence, current staging removal and service/QEMU exit 0.
The independent target hash changes from zero-filled bytes to the expected image;
the socket and controller subgroup are absent afterward. A separate no-input
probe retains its failed staging with zero writes and no execution/journal,
unchanged target and clean controller/socket shutdown. Its expected cancellation
exit is 124; the collector's initial 74 expectation remains a recorded assertion
failure, corrected by an exact-review/source offline audit without rerunning the
VM. Both role runtime audits pass the 10.0.13 minimum.

These use synthetic input and a tiny golden GPT image, not an installed OS or
observed human consent. New headless framebuffer captures remain incomplete even
with delayed capture; active-display rendering and graphical recovery stay open.
The earlier complete real frame is retained privately with its actual source and
scope. It is not final-source visual qualification. See the
[native receipt](release-evidence/1.9-installed-recovery-entry-2026-10-29.json).

The targeted active-display follow-up uses the same installed source and owned
VM, with Cocoa graphics instead of the headless backend. Its full English review
frame is readable and captured for private editorial use. The no-input timeout
returns the expected 124 directly, leaves the target unchanged and cleans the
socket/controller; the fresh failed stage remains retained. QEMU exits normally.
The native app connector could not select the standalone executable, so this is
actual framebuffer/rendering evidence, not native-window accessibility coverage.
Headless captures remain a laboratory limitation; other fonts/locales, the
Avalonia wizard and final media/profile rendering remain open.

## Isolated CI storage scope

The hosted follow-up repairs two unreachable nullable cleanup calls in the
nonroot source opener. The already-opened metadata stream still closes if
opening the payload fails. The successful source/peer/operation path and all
production admission checks remain unchanged.

Real storage-lifecycle coverage uses a separate Linux CI fixture. Its launcher
creates fresh owned scratch data, then confines the required test class in new
mount/PID namespaces. A private tmpfs hides `/srv`; an owned ext4 directory is
bound at the fixed store path. The original proc view is retained read-only to
check actual outer namespace identity after the inner proc remount. Kernel UID,
mount, filesystem and inode proofs are required before test storage creation;
a request flag alone cannot enable privileged filesystem operations.

The fixture runs only existing compiled storage tests, with a clean privileged
environment and bounded process lifetime. It requires executed-case evidence,
TRX and covered `StorageLease.Create`/`Complete` ranges in the Microsoft report.
Sonar imports both independently validated original managed reports; existing
thresholds and production-source inclusion remain unchanged. See
[Microsoft's coverage collection and merge interface](https://learn.microsoft.com/en-us/dotnet/core/additional-tools/dotnet-coverage).

This checks real artifact ownership, receipts, retention and cleanup on synthetic
files. It does not execute the recovery host, open a block device, qualify media
or replace the installed native probes above. The actual hosted outcome must be
recorded separately; adding a fixture is not a passing native test result.

The local follow-up checkpoint executes 35 test rows but initially exits1 because
an xUnit assertion-style warning is treated as an error. After the equivalent
Ordinal `Assert.StartsWith` correction, only the affected exact class is rerun:
one row passes with exit0. Linux-specific helper/storage bodies are not executed
on macOS. Shell syntax passes and the wrapper refuses macOS before creating a
results directory. The independent containment review found an abort-trap
ownership check omission, now guarded by exact directory identity and the
reviewer's verified-working-directory hardening. Hosted native execution and the
80% gate are pending; see the
[follow-up receipt](release-evidence/1.9-recovery-storage-checks-2026-10-29.json).

The first required CI fixture attempt on `d2cb475` reaches real namespace/storage
execution but fails when creating a hardlink across the scratch path and its
bind-mounted store alias. Its errno was not captured, so the earlier value is
not reconstructed. The corrected fixture places both synthetic names on the
same proved mount, with the original outside the operation. It requires real
nlink2 plus preservation of both names/bytes; future link failures include errno.
No production guard changes. Later scenarios in that failed attempt are not
counted as executed; the repaired exact-head CI result remains pending.

The repaired seven-scenario fixture passes on `e09204c`, with distinct real
mount/PID namespaces, private tmpfs `/srv`, ext4 backing and matching source/store
inode proof. The compiled checkout is `4a930b96a5962281120ac627875958ab3ad7566a`,
distinct from the analyzed PR head. Its actual namespace/TRX/Microsoft report
artifact is retained. The job then fails the existing health check on the
intermediate merged report, so no successful quality gate is claimed. A local
single-input merge reproduction preserves valid native ranges; the old job did
not archive its main/merged outputs, so their contents are not reconstructed.
The workflow now validates and imports both original reports through Sonar's
[documented multiple-report parameter](https://docs.sonarsource.com/sonarqube-server/analyzing-source-code/test-coverage/test-coverage-parameters).
It archives both inputs for diagnosis. Missing/empty input still fails before
analysis; no range data, coverage exclusions or thresholds are changed.

## Admission and liveness regression follow-up — 2026-10-30

The `264a896` scanner revision imports both independently validated original
reports. All seven isolated filesystem scenarios pass; the compiled checkout is
`025b7759af5947352c0ad083ec99d10e6f7a9b64`, distinct from the analyzed PR head.
Analysis `ed5f4cdc-f2a7-472e-977c-01ed0fd14b04` leaves only the coverage condition
red (79.4%, minimum 80%); reliability/security/maintainability are A, duplication
is 0.0% and reviewed hotspots 100%. Scanner success alone is not gate success.

The next test-only increment checks compiled fixed-role identity, real interface
flags and readonly-root observations, protected-ancestry rejection and ordinary
installation refusal without runtime/store mutation. These native assertions run
only after the existing namespace/mount/inode proof; macOS does not execute them.
Worker regressions check lifetime limits, resource disposal before cancellation
refusal, preparation refusal without review/execution, and an actual 20-second
stalled-stage status with zero durable progress and functioning cancellation.
No production or launcher code changes; no authority is supplied by test flags.
The graphical controller still needs a serialized intent/review/cancellation
boundary; the observer callback is not that interaction contract. Existing
source-access, full-media/profile and package qualification gates remain open.

The 2026-10-30 focused Release checkpoint passes 43 cases with warnings as errors.
Linux-only assertions subsequently pass in the `3ce3e8d` isolated CI fixture;
unrelated local suites and repeated localization checks were skipped. See the
[admission/liveness receipt](release-evidence/1.9-recovery-admission-liveness-2026-10-30.json).

## Serialized controller interaction

The controller client requires an explicit interaction provider. It requests
read-only image streams, a target from the worker's validated inventory, and an
acknowledgment of the exact operation/review. Only one choice may be pending.
The receive/heartbeat loop continues while a choice is open; presentation cannot
hold up a worker refusal, timeout or terminal result. Operation, observation and
review identifiers are checked again before any corresponding send. Neither a
selected target nor a review acknowledgment grants physical overwrite authority.

The provider retains ownership of all returned image streams, including late or
rejected choices. It keeps accepted streams alive until the conversation has
finished draining, then releases its own resources. Only read-only handles cross
the existing channel; the host never opens a provider-selected path. Pending tasks receive
cancellation and are drained at teardown; a late answer cannot send a new action.
Providers must finish promptly on cancellation and marshal presentation to their
own UI thread. Raw transport/native errors are not localized presentation text.

A separate interaction cancellation signal requests one graceful protocol Cancel
while continuing to receive the worker's actual refusal/outcome. A null choice
has the same meaning. The lifetime token remains the immediate abort boundary.
Cancellation is checked before consuming a choice or sending a confirmation;
completed writes/read-back and uncertain cleanup retain their actual outcomes.
The explicit headless adapter preserves the installed laboratory path; there is
no implicit automatic acknowledgment for a future graphical provider. This is
the client interaction boundary, not an installed Avalonia wizard or media/UI
qualification. The original independent physical-console supervisor is unchanged.

All 18 focused client cases pass in warning-as-error Release configuration on
macOS. Eleven new cases cover deferred choices, exact correlation, graceful
cancellation and late results/failures. These are transcript fixtures, not native
media/UI evidence; no unrelated suite or VM was run. See the
[interaction receipt](release-evidence/1.9-recovery-controller-interaction-2026-10-30.json).

## Graphical installed controller — 2026-10-30

The media controller now includes an isolated Avalonia wizard. The ordinary
desktop application remains read-only; this is not an elevation route. Image
sources are opened by admitted UID 60999 and held until client/choice drain.
The wizard uses all 19 shared catalogs, selected-culture numbers, RTL direction,
accessible labels and state-preserving language changes. Targets come only from
the validated inventory. Review acknowledgment has no physical write authority.

The stager emits schema 2 with an explicit headless/graphical mode and optional
Xorg executable hash; schema 1 remains headless-compatible. Graphical mode requires
the source-bound display launcher, boot unit and exact 19 controller catalogs.
No other nested input directory is accepted. Install the selected host template
as `vaultsync-recovery.service`, retaining its fixed cgroup identity. The separate
display unit owns VT2 and the fixed distro `/usr/lib/xorg/Xorg` executable. It uses
filesystem Unix X11 only, with TCP and abstract-local listening disabled. Distro
X11 modules/libraries, ICU and complete fallback fonts remain media dependencies.
Their actual packaging/rendering and final provenance are separate qualification.

A fresh root-owned `/run/vaultsync-recovery-display` contains a per-boot cryptographic
Xauthority record readable only by controller group 60999, plus a separate private
controller-writable temporary directory. The launcher supplies fixed display,
authentication and temporary paths after dropping privileges. Display admission
checks held immutable files, root-owned endpoint and cookie/runtime permissions;
it never inherits a desktop display or enables a caller-selected endpoint. The
native bootstrap forwards termination to its unreaped child without a lost-stop
race, waits for exit, and removes only matching owned entries. Existing or uncertain
runtime is retained and cannot be adopted on restart. No global X socket/lock is
deleted by cleanup. The unit has no automatic restart.

The physical-console reader waits for actual VT1 activation within the original
30-second review deadline, then performs unchanged context validation, input flush
and exact typed challenge. The wizard explains Ctrl+Alt+F1 and return via
Ctrl+Alt+F2. Switching terminals or acknowledging a GUI review does not authorize
an overwrite. After the actual worker outcome and storage receipt, the host keeps
the owned controller alive for the result screen until close or a 10-minute bound,
while continuing boot monitoring. Lifetime shutdown ends this presentation wait;
controller exit status cannot rewrite the worker outcome or authorize another write.

Controller Release build passes with warnings as errors after repairing one
Avalonia constructor-access error. The 112 focused client/admission/console/launch
cases and 8 producer tests pass. Both native launchers compile with strict hardened
flags on disposable Debian 13 x64. Ordinary local tests are not native GUI/media
proof. The first installed graphical/no-NIC rendering, authentication, physical
confirmation, final-window and cleanup probes remain pending. No support profile,
old root-private source access, product boot media or beta is qualified by this
component checkpoint. See the [graphical receipt](release-evidence/1.9-graphical-controller-2026-10-30.json).
