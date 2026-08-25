# Recovery Horizon and Resilience Strategy

This is a maintainer planning document. [`ROADMAP.md`](../ROADMAP.md) remains
the canonical source for approved scope, identifiers, priority, release status,
and delivery state. Candidate concepts here are not public delivery promises.

## Decision summary

VaultSync keeps the following product progression:

| Family | Product role | Governing question |
|---|---|---|
| `1.8` Chronicle | Explain and prove file/project recovery | What was protected, what changed, and what can be recovered? |
| `1.9` Recovery Horizon | Expand recovery to installation, disk, and machine loss | Can recovery proceed when the original installation or operating system is unavailable? |
| `2.0` candidate Resilience | Manage recovery outcomes and failure independence | What failures can the protection strategy survive? |

The proposal supplied on 2026-08-25 is accepted as strategic input with these
corrections:

- `1.8.8` remains the existing stabilization release; it receives no duplicate
  clean-machine item and no reused identifier.
- `VS-1885` remains release-automation static-analysis work.
- `VS-1917` through `VS-1919` are the next available 1.9 identifiers and cover
  architecture prerequisites that were missing from the canonical plan.
- Portable Recovery moves before Offsite Protection, but existing `VS-192x`
  and `VS-193x` identifiers are not renumbered.
- `2.0` remains a gated candidate. A complete visual redesign by itself does
  not justify a major version.

## Ordered delivery model

### 1.8.8 — qualify Chronicle

Finish the active release contract. Clean-machine recovery remains part of
`VS-1882`; repository compatibility fixtures, scale envelopes, and architecture
documentation are evidence within the existing `VS-1823`, `VS-1882`,
`VS-1883`, and `VS-1884` gates unless a discovered defect requires a new
`BUG-18xxx` item.

No disk imaging, provider integration, Protection Plan, or Recovery Graph
implementation enters `1.8.8`.

### 1.9 architecture approval

The first 1.9 work is decision-making and risk reduction:

1. Define the 1.9 information architecture and migration map (`VS-1910`).
2. Define the versioned disk-image format (`VS-1917`).
3. Approve the imaging-engine strategy and supported-system matrix (`VS-1918`).
4. Define shared recovery identities, dependencies, failure domains, and
   evidence provenance (`VS-1919`).
5. Prototype the smallest complete image and independent-recovery paths before
   committing stable support claims.

Architecture work may result in scope reduction, a technology preview, or a
release re-sequence. That is a valid result when the evidence does not support
the original plan.

### 1.9 delivery sequence

| Order | Release | Outcome | Promotion condition |
|---:|---|---|---|
| 1 | `1.9.0` | Disk and bootable recovery foundation | Complete create, validate, boot, restore, and validate loop on the supported matrix |
| 2 | `1.9.1` | Clone Explorer | Read-only inspection and extraction without weakening image integrity |
| 3 | `1.9.2` | Portable Recovery | Clean-machine recovery without the original application database |
| 4 | `1.9.3` | Offsite Protection | Remote data is resumable, verifiable, cost-explainable, and independently recoverable |
| 5 | `1.9.4` | Unified Recovery Experience | Replacement workflows meet parity before legacy removal |
| 6 | `1.9.5` | Continuous Recovery Assurance | VaultSync explains when evidence becomes stale or invalid |
| 7 | `1.9.6` | Stability and LTS Baseline | Formats, migrations, long-duration behavior, and support windows are durable |

If the complete disk-recovery loop cannot meet the stable gate, disk imaging
must remain a clearly labelled preview and cannot block portable project/file
recovery from advancing.

## Architecture questions that must be answered

### Imaging and recovery environment

- Which engine is built, integrated, or invoked, and under what license?
- Which operations require elevated privileges, and how are they isolated?
- Which operating systems, filesystems, partition tables, encryption schemes,
  live-capture mechanisms, Secure Boot configurations, drivers, and hardware
  are supported?
- How are image creation, incomplete state, resumption, validation, boot,
  restore, and post-restore validation represented?
- How are recovery media built, signed, updated, expired, and used offline?

### Compatibility and migration

- Which repository, image, configuration, CLI, automation, and deep-link
  versions remain readable or writable?
- What fails read-only, what may migrate, and what must never mutate an unknown
  future format?
- How does rollback work after a schema or product-model migration?
- What emergency recovery remains available when the current application
  cannot start?

### Security and failure independence

- What happens when the source is compromised or destructive changes have
  already propagated?
- Which paths share a device, site, account, provider, credential, key, or
  control-plane failure domain?
- How are immutable copies, credential loss, key loss, provider loss, and
  recovery-media loss represented without exporting secrets?
- Which claims are measured, simulated, inferred, user-confirmed, stale,
  missing, or unsupported?

### Operational viability

- What are the qualification dataset sizes, durations, fault injections, and
  representative machines?
- What restore-success, false-ready, evidence-age, and recovery-time thresholds
  block promotion?
- What are the provider storage, request, retrieval, and egress implications?
- What is the support and LTS burden for every added engine, format, provider,
  platform, and recovery environment?

## Issue-ready 1.9 architecture entries

These entries are ready to mirror into GitHub issues and Project 7. Their full
scope and acceptance text is canonical in `ROADMAP.md`; issue bodies should add
only implementation notes, dependencies, and evidence links.

| ID | Issue | Title | Priority | Area | Milestone | Start | Target | Status |
|---|---:|---|---:|---|---|---|---|---|
| `VS-1910` | `#501` | Define the 1.9 information architecture and migration map | P0 | UI | `1.9.0` | 2026-07-27 | 2027-03-26 | Todo |
| `VS-1917` | create | Define the versioned disk-image format and compatibility contract | P0 | Core | `1.9.0` | issue creation | 2027-03-26 | Todo |
| `VS-1918` | create | Approve the imaging-engine strategy and supported-system matrix | P0 | Core | `1.9.0` | issue creation | 2027-03-26 | Todo |
| `VS-1919` | create | Define recovery identities, dependencies, failure domains, and evidence provenance | P0 | Core | `1.9.0` | issue creation | 2027-03-26 | Todo |

Common issue metadata:

- assignee: `ATAC-Helicopter`;
- labels: `kind:vs`, `roadmap`, `Feature`, `status:todo`, `priority:p0`,
  `release:1.9.x`, `release:1.9.0`, plus the matching area label;
- Project: `ATAC-Helicopter` Project 7;
- Owner: `Flavio Giacchetti`;
- Team: `Work`;
- Release: `1.9.x`;
- Repository target: `ATAC-Helicopter/VaultSync`;
- Work labels: the exact issue labels relevant to the entry;
- no Completed on value while Todo.

Dependencies:

- all four entries depend on the `1.8.8` release qualification;
- `VS-1904` through `VS-1909` depend on `VS-1917` and `VS-1918`;
- `VS-1908`, portable recovery, offsite recovery, and later resilience work
  depend on the shared semantics established by `VS-1919`;
- `VS-1910` may proceed as research, but legacy workflow removal remains
  prohibited until replacement parity is proven.

## Candidate Resilience model

The candidate unit of configuration is a Protection Plan: the recovery outcomes
and failure scenarios the user requires. The plan may refer to projects, disk
images, destinations, schedules, retention, verification, drills, credentials,
keys, recovery media, devices, sites, and recovery tools.

The internal Recovery Graph represents those dependencies. The Protection Map
is a user-facing view of the graph. Failure-scenario evaluation removes selected
nodes or failure domains and reports the remaining evidence-backed recovery
paths.

These three concepts must share one model. Separate dashboard calculations,
decorative topology, or recommendations that cannot show their evidence do not
satisfy the candidate product contract.

## `1.10` versus `2.0`

Use `1.10` when work is compatible and evolutionary:

- more providers, filesystems, image formats, or automation;
- performance and maintainability improvements;
- optional resilience previews that do not replace the authoritative job model;
- interface improvements that preserve supported contracts.

Consider `2.0` only when:

- Protection Plans become authoritative persisted configuration;
- Recovery Graph semantics drive setup, readiness, and recovery;
- user validation supports the outcome-oriented model;
- a real configuration, automation, or interaction migration is necessary;
- migration, rollback, emergency access, and repository compatibility are
  specified and qualified;
- the replacement interface has complete functional and accessibility parity;
- measurable release criteria prevent false resilience claims.

Do not allocate `VS-20xx` identifiers, create a `2.0` milestone, or publish a
date until this decision gate is approved.

## Candidate backlog after 1.9

Keep these as concepts rather than execution issues until their contracts and
dependencies are understood:

- policy-controlled self-healing protection;
- measured storage and recovery forecasting;
- destructive-change resilience without opaque ransomware classification;
- multi-source recovery orchestration;
- local-first device resilience;
- historical resilience simulation;
- optional shared or team vaults with explicit ownership and audit contracts;
- additional storage providers driven by recovery outcomes rather than parity.

## Project-entry checklist

Before adding any new execution item to Project 7:

1. Confirm the identifier is unused and sequential within its family.
2. Put the canonical scope and acceptance criteria in `ROADMAP.md`.
3. Create an issue only when the item is execution-ready; otherwise retain a
   candidate concept without an execution ID or delivery date.
4. Assign milestone, labels, assignee, Owner, Team, Release, Area, Priority,
   Start date, and Target date.
5. Leave Completed on empty until traceable close, merge, or release evidence
   exists.
6. Add dependency and qualification evidence links.
7. Run `pwsh scripts/sync_project_descriptions.ps1 -ProjectNumber 7 -DryRun`.
8. Reject synchronization if an ID, required date, milestone, or ownership
   source cannot be resolved.
