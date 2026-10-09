# VaultSync 1.9.0 — Disk and Bootable Recovery Foundation

Planning release; current stable is 1.8.9. Stable target: 2027-03-26.
Active development branch: `work/1.9-roadmap-realignment`; sole release PR:
[#736](https://github.com/ATAC-Helicopter/VaultSync/pull/736) into `Dev`.
Protected release identity: `release/1.9.0`; its older ref is not the assembled
working tree. Promotion to `Stable` uses reviewed merge commits.
No disk support or predecessor upgrade is qualified yet.

## Delivery order

1. Approve VS-1910 typed routes and adaptive UI architecture, VS-1917 versioned
   image format, VS-1918 engine/isolation and supported-system matrix, and
   VS-1919 recovery identities, dependencies, evidence and failure domains.
2. Implement VS-1904 capture and VS-1905 exact source/destination and wipe
   previews; incomplete/corrupt captures fail explicitly.
3. Implement VS-1906 independent boot media and VS-1907 image-to-disk restore.
4. Record VS-1908 evidence and qualify VS-1909 Capture → Validate → Boot →
   Restore → Validate on the declared hardware/VM/filesystem/encryption matrix.
5. Qualify exact 1.8.9 upgrades, distribution integrity and user-data preservation.

Unsupported Secure Boot, live capture, encryption and filesystem combinations
must be explicit. If the complete loop fails, imaging remains Preview.
There is no universal bare-metal recovery claim. BSC prerequisites do not
block this release. The proposed route draft is still awaiting review.

The [VS-1917 image-format review draft](DISK_IMAGE_FORMAT_1.9.md) now specifies
logical coverage, integrity, completion/checkpoints, unknown-version rejection,
independent inspection and a permanent fixture plan. It is not an approved
serialization or implementation. Engine/support decisions (VS-1918), shared
identity/evidence alignment (VS-1919), fixtures and joint approval remain open.
The [VS-1919 identity/evidence review draft](RECOVERY_IDENTITIES_1.9.md) defines
generation binding, dependency/failure correlation, evidence provenance and
freshness, privacy and read-only portable inspection. A single-observation
applicability prototype has synthetic fixtures; stable schemas, dependency
evaluation and integration remain open. Joint architecture approval is still required.
The [VS-1918 engine ADR](adr/001-imaging-engine-1.9.md) proposes an internal
raw/offline worker and explicit support exclusions. The executable
[qualification matrix](../release/disk-support-1.9.0.json) has four candidates
and zero qualified profiles. `python3 scripts/disk_support.py check` verifies
structural evidence rules; `--require-qualified` intentionally fails until actual
approved, measured recovery evidence exists. This adds no engine or device access.
Qualification runs must begin at or after valid retained engine approval; an
approval recorded after capture cannot qualify that earlier run.
Local architecture/workflow progress and its verification limits are recorded
in [the October 4 ledger](release-evidence/1.9-work-2026-10-04.md).

## Scope realignment on 2026-10-03

The future CLI rework is preserved in the 1.9.5 branch; discovery and completion
are adapted to the supported flat CLI in 1.9.1. BUG-19001 remains an independent
safety fix. VS-1980 retains kickoff ownership. Updater organization-transfer
compatibility and AppImage packaging fixes remain maintenance prerequisites.
Windows notification and xUnit modernization are separately tracked maintenance
work, not evidence that disk recovery is ready.

See [family ownership, gates and ID crosswalk](RELEASE_FAMILY_1.9.md),
[canonical roadmap](../ROADMAP.md) and [route draft](ROUTE_ARCHITECTURE_1.9.md).

Legacy watcher safety is retained: finite read-only text dry runs, explicit `--db`,
shutdown cancellation/drain and explicit failure. Resource/JSON commands stay in 1.9.5.

Raw command arguments are not logged, preserving secret input safety.

BUG-19009 requires CI/CodeQL/Sonar triggers to include documentation changes:
protected PR contexts must exist and bundled CLI documentation affects artifacts.

## Assembled release review — 2026-10-03

PR #736 into Dev is the sole 1.9.0 release review, including its maintenance,
contracts and tracking. Its preparation source is work/1.9-roadmap-realignment;
#683 is superseded. It stays draft and is not eligible for automatic promotion.
BUG-19010/#749 and BUG-19011/#750 qualify dependency ABI/API coherence.
The unused Inter package is removed without changing bundled/system fallbacks.
See [the complete daily ledger](release-evidence/1.9-work-2026-10-03.md).

## Linux startup qualification

BUG-19012/#751 tracks the installed Linux process blocked during native font
initialization. Relaunch passes initialization but window visibility, triggering
input and durable remediation remain open. See [the evidence](release-evidence/linux-startup-2026-10-04.md).
A living process or completed startup log is insufficient desktop qualification.

## Repository tracking — 2026-10-07

VS-1980/#673 distributes the shared helper from #759/#758. Use `./dev adopt` for existing owning issues and `./dev status` before pushes; reuse this release's preparation PR. Allocating IDs requires a complete remote audit. This tooling does not qualify product or release gates.

## Route adapter prototype — 2026-10-07

VS-1910/#501 adds a pure typed-route mapping prototype and compatibility/refusal tests, as preparation for the proposed route API review. It does not replace current pages, saved state or navigation commands. See [route contract](ROUTE_ARCHITECTURE_1.9.md); architecture approval and platform UI parity remain open.

VS-1918 qualification input hardening (2026-10-09) rejects ambiguous/non-finite/non-UTF-8 JSON and bounds actual reads against growing files. Four candidate profiles remain unqualified; evidence parsing does not approve an engine or prove recovery.

## Navigation and observation prototypes — 2026-10-09

VS-1910/#501 adds a page-only resolve-before-commit coordinator with bounded
back/forward history and explicit cancellation/supersession. VS-1919/#576 adds
a read-only single-observation applicability classifier, separating provenance,
binding, chronology, outcome and measurement basis. Both remain review prototypes
without UI, persistence or device integration. The existing confidence service
also fixes future-dated verification/drill results incorrectly being treated as fresh.
See [current development evidence](release-evidence/1.9-prototypes-2026-10-09.md).
Joint architecture approval, platform parity and measured recovery gates remain open.

VS-1918/#575 CI follow-up (2026-10-09) replaces interpreter-dependent nesting
refusal with an explicit 64-container bound for JSON qualification input.
See [repair evidence](release-evidence/1.9-json-depth-2026-10-09.md).
