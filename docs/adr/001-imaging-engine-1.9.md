# ADR 001 — Offline imaging engine and qualification boundary

Status: **proposed**, 2026-10-04. Owner: VS-1918 / [#575](https://github.com/ATAC-Helicopter/VaultSync/issues/575).
Review: sole 1.9.0 PR #736. This is a recommendation, not approval or a support
announcement. No imaging engine, elevated helper or boot medium is implemented.
Review with VS-1917 format, VS-1919 evidence and VS-1910 route contracts.

## Problem and options

VaultSync needs a capture/restore implementation independently usable after the
original installation and database are lost. The first implementation must be
maintainable, have bounded input handling and explicit source/target authorization,
and preserve partition tables, gaps and boot regions rather than copying files.

| Option | Benefits | Costs and qualification boundaries |
| --- | --- | --- |
| Internal full raw-block engine, offline first | One logical byte-coverage model; no filesystem allocation parser or foreign image dependency in the first format; reusable independent verifier | Own device isolation, short-read/write handling, durable checkpoints and native OS adapters; copies unused blocks; needs full source-capacity storage and extensive fault tests |
| Invoke pinned Partclone binaries in an isolated worker | Existing used-block imaging across several filesystems; upstream inspect/restore utilities | Filesystem-specific support/versions, native dependencies, adapter/container compatibility, upstream license distribution review and independent recovery packaging; upstream support is not VaultSync qualification |
| Build on native OS backup/snapshot APIs | May provide platform-specific consistency for live capture | Several platform implementations, snapshot/writer semantics, privileges and restore environments; volume snapshots do not automatically provide complete disk/boot recovery |
| Invoke a generic copy command | Useful comparison baseline in a disposable lab | Exit code cannot supply identity binding, checkpoints, target authorization, image completeness or independent boot qualification; unsuitable as the product contract alone |

Recommendation: start with an internal, full raw-block **offline** profile and
isolated worker, retaining a later explicit adapter boundary for filesystem-aware
engines. Do not implement filesystem parsers, sparse free-space omission, resize,
foreign-image import or live snapshots in this first profile. This inference
follows the proposed full-coverage format and solo maintenance scope; it remains
subject to maintainer review and the measured feasibility gate below.

Partclone describes used-block filesystem utilities, raw copy and image checking
in its [upstream feature documentation](https://partclone.org/features/).
Its upstream [COPYING](https://github.com/Thomas-Tsai/partclone/blob/master/COPYING)
contains GPL version 2. The local VaultSync product is MIT licensed. No Partclone
code/binary is added here; any integration needs a pinned version, provenance,
third-party notices and distribution/license review before bundling. This ADR
makes no conclusion that process separation alone resolves license obligations.

Microsoft documents that [volume locking](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_lock_volume)
fails for system/page-file volumes and volumes with open files.
[VSS](https://learn.microsoft.com/en-us/windows/win32/vss/volume-shadow-copy-service-overview)
coordinates requesters, providers and writers for volume snapshots. These limits
support deferring live Windows disk capture; VSS availability alone does not
qualify the complete independent disk recovery loop.

## First proposed recovery profile

Capture and restore happen in independently booted Linux x64 recovery media,
with source filesystems offline and unmounted. Desktop Windows/macOS/Linux
applications retain their existing file-backup support; this proposal does not
claim native live/system-disk imaging on all three platforms.

Candidate source systems: GPT/UEFI Linux ext4 and Windows NTFS, without disk or
image encryption, for both 512-byte and 4096-byte logical sectors. Store complete
raw disk scope including the EFI system partition and layout metadata. Restore
to a destination at least as large as the source, with matching logical geometry;
no partition relocation, filesystem resize or sector translation. Validate GPT
backup-header handling on a larger destination before claiming that case.

Every candidate starts **unqualified**. The machine-readable plan is
[release/disk-support-1.9.0.json](../../release/disk-support-1.9.0.json); it is
qualification metadata, not an image wire format. Its zero qualified rows are
intentional. Unlisted combinations are unsupported until explicitly reviewed
and qualified, even if raw bytes appear readable.

Defer/reject: live or mounted sources, Secure Boot enabled, encrypted sources or
images, APFS/macOS bare-metal recovery, LVM/RAID/multi-disk layouts, unsupported
sector sizes, BIOS/MBR, filesystem-aware sparse images, corrupted-source rescue,
network payloads and delta ancestors. Each refusal identifies the unsupported
condition before a writable device handle is opened. Do not silently downgrade
consistency or convert an unreadable region to zeros.

## Privilege and isolation contract

The desktop UI/CLI orchestrator stays unprivileged. Device enumeration returns
observations, never authorization. A bounded typed worker protocol passes a
single operation ID, exact source/destination observations and overwrite scope;
no shell command string, executable path from an image, arbitrary argument or
credential is accepted from untrusted manifests.

An elevated worker re-observes identity/capacity/geometry and mount/use state
immediately before access, opens only authorized device handles and rejects
identity changes. The image repository must not live on the restore target.
A preview is invalidated when selection, identity, geometry or operation scope
changes. Keep platform-specific authorization and device opening behind a
reviewed adapter; no generic privileged file-open endpoint.

Separate hostile image inspection from elevated writes. Validate bounded metadata,
content roots, complete coverage and required features before authorizing restore.
Use a locally trusted, versioned worker/recovery build, an authenticated local
channel, bounded messages and redacted diagnostics. Ordinary files are not
accepted as substitutes for physical identity checks in product restore mode.

Cancellation before destructive work closes handles without writes. After writes
start, record interrupted state and exact acknowledged ranges; never report
rollback or success without evidence. Flush/acknowledgement order and resume
contracts align with VS-1917. Worker exit, loss of UI/channel or power failure
cannot create a completed image or completed restore record. Protocol, timeout,
privilege escalation and durable-state implementations require security review.

## Qualification procedure

Use disposable QEMU UEFI machines first, then dedicated physical SATA and NVMe
hardware. Record actual firmware, controllers/drivers, OS build, filesystem,
geometry, capacities, tool/engine/recovery-media builds and source content root.
A VM result does not qualify a physical machine; additional measured rows must
name their own tested environment. Nothing in this task opens host raw devices.

For each concrete profile/environment, retain one bound operation run containing:

1. Capture the offline source including partition/boot/gap bytes; record complete
   map and payload roots. Test deterministic content across mapped regions.
2. Independently validate the image with the original source unavailable; mutate
   payload, truncate parts and remove completion records in separate negative runs.
3. Boot the pinned recovery media independently without the original installation,
   database, network downloads or secret store. Observe usable device discovery.
4. Restore to a separately identified disposable destination after reviewing the
   exact overwrite scope. Test wrong device, smaller capacity, mounted source,
   changed identity and cancellation/restart as separate refusal/failure runs.
5. Validate recorded target byte coverage and boot the restored operating system;
   inspect expected files/application state. Record actual interactive desktop
   behavior where applicable; process existence is insufficient evidence.

Each run binds profile, run/image identity, tool and recovery builds, UTC times,
actual hardware/environment and measured outcomes. Every stage report also names
its required checks: capture coverage/durable completion, independent image
integrity, independent media/device discovery, authorized destination/writes,
and target byte coverage, restored OS boot, expected files and interactive desktop.
Keep artifacts with hashes;
public copies are redacted. Simulation, user confirmation, a build or file-only
round-trip cannot replace these measurements. Corruption/interruption/refusal
fixtures and physical coverage remain additional stable release gates, even
when a measured positive loop exists.

`scripts/disk_support.py check` validates the plan and evidence references.
`--require-qualified` additionally fails when no profile is qualified. A passing
structural check is not proof of recovery: the checker cannot authenticate a
producer or establish that an artifact describes an honest physical execution.
Review retained evidence and attestations separately before changing support.

## Approval and next implementation boundary

Approve the engine option, initial source/recovery platform scope, licenses,
worker trust/privilege boundary and joint VS-1917/VS-1919 semantics. Record approval
in the matrix with its repository evidence reference; do not flip it just to
make CI green. Candidate support remains visible as unqualified after approval.
The retained approval must be valid before capture begins. Runs predating that
approval remain unqualified and must be repeated under the approved decision.

Then implement file-backed deterministic prototypes and fault fixtures, followed
by reviewed OS device adapters in disposable environments. Measure full raw-image
space/time, memory bounds, interruption/resume and independent restore feasibility.
If that cost or qualification burden is infeasible, revisit Partclone integration
before broadening scope. Full destructive hardware qualification and the other
release gates are required before Stable; a failed loop leaves imaging Preview.

## Qualification record structure

This is the repository review-tool contract, not an image or product evidence
wire format. A qualified row adds a concrete `environment` and `hardwareKind`
(`virtual` or `physical`). Its `evidence` is five ordered `{path, sha256}` artifact
references, one per stage. Each artifact is a UTF-8 JSON object with:

- `stage`, `profileId`, `kind: measured`, `outcome: passed` and UTC `observedAt`
  in `YYYY-MM-DDTHH:MM:SSZ` form (fractional seconds are allowed).
- The exact row `environment`/`hardwareKind`, nonempty `runId`, lowercase
  SHA-256 `imageRoot`, and nonempty `captureBuild` and `recoveryBuild` bindings.
- `checks`, mapping each required stage check below to `passed`.

| Stage | Required checks |
| --- | --- |
| capture | complete-coverage, durable-completion |
| validate-image | payload-integrity, independent-source-unavailable |
| boot | independent-media, device-discovery |
| restore | destination-identity, authorized-overwrite, durable-writes |
| validate-restored | target-byte-coverage, restored-os-boot, expected-files, desktop-interactive |

Bindings match across all stages, observed times follow stage order and no
observation is in the future. Artifact paths stay inside the repository, exist
and match their recorded digest; external/symlink escapes and oversized reports
are rejected. This gate reads ordinary review artifacts only, with no elevation
or raw-device access. Keep interrupted/negative attempts in the work ledger.

Engine approval changes `decision` to `approved` and records `approval.by`,
`approval.observedAt`, and an `approval.artifact` path/hash. A named reviewer and
retained approval record are required, but this metadata does not cryptographically
authenticate that reviewer. Actual approval must come from the maintainer's
review; do not manufacture records. Unit-test reports stay synthetic and temporary,
and must never become release evidence.
