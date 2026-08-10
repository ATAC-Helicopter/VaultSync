# VaultSync Roadmap

This is the canonical product and delivery roadmap for VaultSync.

It records what shipped, defines the active release, reserves identifiers for
planned work, and establishes the boundary between the Chronicle (`1.8`) and
Recovery Horizon (`1.9`) release families.

> **1.8 proves and explains recovery.**
>
> **1.9 expands what VaultSync can recover.**

## How to read this roadmap

- `Released` sections are historical records derived from tags, commits,
  `CHANGELOG.md`, and their linked GitHub issues.
- `Active` is approved implementation scope.
- `Planned` is sequenced but may be refined before its release branch opens.
- `Candidate` is directional and must not be presented as committed delivery.
- `P0` means release-blocking reliability, security, or usability work.
- `P1` means primary release value.
- `P2` means valuable work that may move without invalidating the release.

## Work-item protocol

- Product and engineering work uses `VS-xxxx`.
- Defects use `BUG-xxxxx`.
- Release gates may use `REL-xxxxx`.
- The first two digits of a `VS` identifier map to the release family:
  `18xx` for `1.8` and `19xx` for `1.9`.
- The remaining digits are allocated sequentially and are never reused.
- A work item spanning releases must be split into release-specific IDs.
- Changelog entries use the owning work ID; internal test-only or documentation
  notes may omit an ID when they do not represent separate user-facing scope.
- Issue, pull-request, project, changelog, and roadmap IDs must agree.

### Canonical execution-ticket format

```text
- [ ] `VS-xxxx` `P1` Clear one-line scope.
  - Scope: ...
  - Acceptance: ...
```

The checkbox is the delivery state. GitHub Project status, milestone, labels,
assignee, and dates mirror this file rather than defining a second roadmap.

## Product arc

| Family | Name | Product question |
|---|---|---|
| `1.7` | Sentinel | Can I trust the stored data? |
| `1.8` | Chronicle | What was protected, what changed, and what can be recovered? |
| `1.9` | Recovery Horizon | Can I recover from a disk or machine-level failure? |

---

# VaultSync 1.8 — Chronicle

## Family promise

> Understand what was protected, inspect how it changed, and see the evidence
> that supports recovery.

Chronicle remains focused on file/project protection, history, inspection,
verification evidence, understandable recovery decisions, usability, trust,
and stabilization. It does not add disk cloning or a new backup category.

## Released history

### 1.8.0 — History and Recovery Foundation

**Released:** 2026-06-20
**Tag:** `v1.8.0`
**Stable integration:** `874358e` / PR #365

Delivered:

- `VS-1802` history and recovery metadata foundation;
- `VS-1803` first-class History and Recovery navigation;
- `VS-1804` project timeline and event inspector;
- `VS-1805` labels, notes, tags, protected points, and known-good markers;
- `VS-1806` Dashboard recovery awareness and workflow links;
- `VS-1807` Recovery readiness, coverage, and prioritization;
- `VS-1812` roadmap, metadata, and localization alignment;
- `VS-1813` clear Backups/History responsibilities;
- `VS-1814`–`VS-1818` analysis, release-security, and maintainability work;
- `VS-1819` History shaping and analyzer cleanup;
- `VS-1820` release-readiness hardening;
- `BUG-18045`, `BUG-18047`–`BUG-18055` reliability, updater, path,
  responsiveness, cancellation, and dependency corrections.

### 1.8.1 — Recovery Assessment

**Released:** 2026-06-25
**Tag:** `v1.8.1`
**Release metadata:** `7d0cf0a`

Delivered:

- `VS-1824` portable Recovery assessment export;
- `VS-1825` encrypted staging and credential integration hardening;
- `VS-1826` Recovery search and focused triage;
- `VS-1827` milestone-scoped release gates;
- `BUG-18056` Linux updater and shared metadata fixes;
- `BUG-18057` Linux single-instance enforcement.

### 1.8.2 — Snapshot Explorer

**Released:** 2026-07-04
**Tag:** `v1.8.2`
**Release metadata:** `18f2eee`

Delivered:

- `VS-1808` asynchronous Snapshot Explorer browsing, preview, search, and
  selective restore;
- `VS-1821` initial core-service complexity and path-write hardening;
- `VS-1822` initial desktop view-model complexity cleanup;
- `VS-1828` supported dependency refresh;
- `VS-1829` first-run setup checklist;
- `VS-1830` duplicate changelog-ID release warning;
- `BUG-18058` macOS single-instance enforcement.

### 1.8.3 — Snapshot Compare and Change Intelligence

**Released:** 2026-07-16
**Tag:** `v1.8.3`
**Release PR:** #410
**Stable integration:** `ab0a27b`

Delivered:

- `VS-1809` snapshot comparison, changed-file navigation, text diffs, and
  change-intelligence signals;
- `VS-1831`–`VS-1836` release-script, explorer, snapshot, CLI, UI, and
  analyzer hardening;
- `VS-1837` release metadata alignment;
- `VS-1838` supported patch dependency refresh;
- `VS-1839` Avalonia 12.1 and .NET 10 UI stack;
- `VS-1840` app-wide security, nullability, lifecycle, CI, and performance
  hardening;
- `VS-1841`–`VS-1846` compare navigation, localization, density, and
  presentation refinement;
- `BUG-18059`–`BUG-18065` diagnostics, restore containment, diff recovery,
  credential prompts, diff presentation, folder picking, and large-diff fixes.

### 1.8.4 — Recovery Proof

**Released:** 2026-07-24
**Tag:** `v1.8.4`
**Release PR:** #440
**Stable integration:** `f3170e8`

Delivered:

- `VS-1810` non-destructive drills, reachable 3-2-1 guidance, explicit offsite
  confirmation, and protected-point recommendations;
- `VS-1847` strictly redacted, user-reviewed crash-report drafts;
- `VS-1848` local byte-level recovery proof, restore-plan simulation,
  deterministic evidence, and verified-point retention safety;
- `VS-1849` supported servicing dependency refresh;
- `VS-1850` curated themes and the rebuilt Appearance studio;
- `BUG-18066`–`BUG-18077` linked-content, destination identity, cancellation,
  upload, path, case-sensitivity, repeated crash review, theme, and Recovery
  lifecycle fixes.

---

## 1.8.5 — Recovery Confidence

**Status:** Released
**Tagline:** *Know before you need it.*
**Released:** 2026-08-02
**Tag:** `v1.8.5`
**Release PR:** #499
**Stable integration:** `464eab5`

### Release objective

Turn the existing Recovery capabilities into a direct, evidence-backed answer:

> Can I recover this project right now, why does VaultSync believe that, and
> what should I do next?

States take precedence over scores. A percentage may summarize evidence, but it
must never hide a missing, failed, stale, inferred, or unsupported check.

### Execution tickets

- [x] `VS-1851` `P0` Define the recovery-confidence state and evidence model.
  - Scope: represent current state, decisive blocker, evidence type, freshness,
    provenance, verification scope, drill scope, and recommended action.
  - Acceptance: every state is explainable without relying on color or an
    opaque percentage; measured, simulated, inferred, and user-confirmed
    evidence remain distinct.
- [x] `VS-1852` `P1` Add a project-level Recovery Inspector.
  - Scope: show the decisive state, reachability, credential, verification,
    restore-plan, drill, and offsite evidence with basis, freshness, limitations,
    and next action in one inspectable project card.
  - Acceptance: a user can explain the current recovery state and identify the
    next useful action without opening Settings or reading documentation.
- [x] `VS-1853` `P1` Add a Recovery Checklist and universal evidence actions.
  - Scope: backup created, destination reachable, integrity verified, restore
    plan valid, restore tested, and offsite status; expose the reason, evidence,
    and safe proof, test-restore, or protection action where applicable.
  - Acceptance: incomplete checks identify a safe corrective action and never
    present an opaque recovery score as sufficient evidence.
- [x] `VS-1854` `P0` Add a guided isolated restore drill.
  - Scope: select a recovery point and safe test folder, restore representative
    content, verify bytes, record file-open confirmation, and persist evidence.
  - Acceptance: the result explicitly states what was tested, what was skipped,
    and whether the operation was a simulation or a real restore.
- [x] `VS-1855` `P1` Record recovery evidence as first-class History events.
  - Scope: recovery proofs (including verification and availability), isolated
    restore drills, protected points, and evidence exports with source identity.
  - Acceptance: status changes can be traced to timestamped evidence.
- [x] `VS-1856` `P1` Upgrade the Recovery Evidence Report.
  - Scope: add app/build and source identity, project protection and drill
    results, redacted evidence methods and paths, unresolved reasons, report ID,
    and checksum.
  - Acceptance: the export is deterministic, redacted, portable, and describes
    limitations without implying guaranteed recovery.
- [x] `VS-1857` `P1` Extend first-backup completion through recovery proof.
  - Scope: distinguish backup completion from verification and test restore,
    with `Verify recovery`, `Run test restore`, and `Finish for now` actions.
  - Acceptance: onboarding progress survives dismissal and never marks recovery
    complete from a copy operation alone.
- [x] `BUG-18078` `P0` Keep onboarding guidance clear of its target controls.
  - Scope: keep guidance in a responsive card outside the full-window pointer
    route; retain visible Back, primary, and Continue later controls.
  - Acceptance: target pages remain interactive and the card stays bounded at
    narrow supported window sizes without dismissing the guide.
- [x] `BUG-18079` `P1` Preserve the Portuguese Dashboard count placeholder.
  - Scope: keep the localized weekly-snapshot hint aligned with the English
    format-argument contract.
  - Acceptance: localization validation reports no missing, extra, blank,
    duplicate, or placeholder-mismatched keys.
- [x] `VS-1858` `P1` Service and simplify the supported dependency baseline.
  - Scope: apply compatible storage and rendering patch updates, remove
    genuinely unused direct dependencies, document intentional compatibility
    pins, and keep vulnerability checks clean.
  - Acceptance: restore, warning-as-error builds, tests, NuGet vulnerability
    checks, and Windows, macOS, and Linux CI pass without losing notifications,
    charts, color editing, localization, secure storage, or CLI behavior.
- [x] `VS-1859` `P1` Publish the guided product walkthrough and current visual
  documentation.
  - Scope: document the complete app, refresh Recovery and onboarding visuals,
    and keep a reproducible no-key narration and caption workflow.
  - Acceptance: the repository contains current screenshots, guided-tour help,
    and a maintainable walkthrough production path.
- [x] `VS-1860` `P0` Harden unsigned release integrity and private local data.
  - Scope: bind installers, patch manifests, and patch archives to GitHub asset
    digests and exact sizes; reject unsafe archive paths and links; restrict
    Unix configuration and application-data permissions; preserve immutable
    workflow action pins; reduce macOS native payload size without mixing ABIs.
  - Acceptance: tampered or unverified updates fail closed, private Unix data
    is owner-only, security regression tests pass, vulnerability checks remain
    clean, and thinned macOS builds pass a real launch test.
- [x] `BUG-18080` `P0` Reject impossible backup-target capacity readings.
  - Scope: use the runtime filesystem API on macOS and validate total/free byte
    relationships before computing or enforcing a free-space percentage.
  - Acceptance: capacity percentages stay within zero and one hundred; invalid
    readings disable the threshold check instead of reporting negative space.
- [x] `BUG-18081` `P1` Remove avoidable view-binding errors from normal use.
  - Scope: bind virtualized project, backup, destination, and credential
    templates through stable named view roots and expose null-safe diff state.
  - Acceptance: a clean startup and ordinary page construction produce no
    missing-ancestor or null selected-diff binding diagnostics.
- [x] `BUG-18082` `P0` Keep CLI self-tests out of production metadata.
  - Scope: use a unique system-temporary database and workspace by default,
    while retaining an explicit `--db` path for intentional integration tests.
  - Acceptance: success, failure, and cancellation clean isolated state; an
    ordinary self-test never inserts a project or snapshot into the user store.
- [x] `BUG-18083` `P0` Keep first-run onboarding interactive with the app.
  - Scope: replace the screen-covering guide with a compact card that leaves
    its target page usable and retains Back, Continue later, and primary actions.
  - Acceptance: setup can continue without dismissing the guide, including at
    narrow supported window sizes.

### Explicitly out of scope

- disk or partition cloning;
- bootable recovery media;
- native cloud/object-storage destinations;
- standalone restore application;
- major navigation replacement;
- project groups unless all release-critical Recovery work is complete.

### Release gates

- state-model unit and transition coverage;
- real isolated restore-drill test for folder and archive backups;
- encrypted and unavailable-destination evidence tests;
- stale-evidence and unsupported-check presentation;
- deterministic report/checksum validation;
- onboarding overlap tests at supported scale and width breakpoints;
- Windows, macOS, and Linux build/test gates.

### Unsigned distribution policy

Direct desktop packages are intentionally unsigned because paid platform
signing programs are not part of the supported release budget. Signing and
notarization are therefore not a `1.8.5` release gate. Every direct release must
instead publish through the official repository, expose SHA-256 asset digests,
verify updater downloads before execution, document SmartScreen and Gatekeeper
warnings, and fail closed when integrity metadata is missing or inconsistent.

---

## 1.8.6 — Everyday Clarity

**Status:** Released
**Tagline:** *Powerful when needed. Obvious by default.*
**Released:** 2026-08-10
**Tag:** `v1.8.6`
**Release PR:** #532

Delivery note: `1.8.6` proceeds directly to the stable release after
qualification. It does not have a beta build or prerelease GitHub release.

- [x] `VS-1861` `P0` Replace the current overlay tour with task-based first-run
  setup around source, destination, schedule, review, and recovery verification.
  - Scope: use a resumable setup workspace and contextual next actions instead
    of a modal screen-covering tour; preserve skip, defer, restart, keyboard,
    scaling, and screen-reader paths; measure completion only from real app
    state.
  - Acceptance: a new user can create, verify, and understand one recoverable
    backup without hunting through unrelated Settings or having guidance cover
    the control it describes.
  - Exit rule: if usability testing cannot make the guided flow faster and
    clearer than the ordinary app, remove forced onboarding and retain only a
    first-run checklist, sample-safe defaults, and discoverable help.
- [x] `VS-1862` `P1` Add a dedicated Schedule experience with modes, quiet
  hours, next run, and delay explanations.
- [x] `VS-1863` `P1` Reorganize Dashboard hierarchy around protection,
  activity, required actions, next run, and latest verified recovery point.
- [x] `VS-1864` `P1` Surface consistent scanning, hashing, writing, verifying,
  retrying, queued, and waiting states.
- [x] `VS-1865` `P1` Make project edit, pause, removal, stored-data deletion,
  repository assignment, inclusions, and exclusions explicit.
- [x] `VS-1866` `P1` Standardize backup, snapshot, restore-point,
  verification, known-good, protected, and drill terminology with contextual
  help.
- [x] `VS-1867` `P1` Complete accessibility and destructive-action preview
  work across primary workflows.
- [x] `VS-1811` `P2` Add project groups and group health if the core
  experience scope is complete. _(Existing issue #363.)_

Project folders completed after the core usability scope as persistent folders,
not inferred tag views. A project has one explicit folder assignment; folders
expand to show their projects and support deliberate snapshot, backup, pause,
and resume actions without replacing per-project controls. Renaming preserves
membership, and deleting a folder moves its projects to Ungrouped without
deleting source files, snapshots, or backups. Schedule, Backups, Recovery, and
History carry the same folder identity.

- [x] `BUG-18090` `P0` Restrict rendered rich-text navigation to approved web,
  mail, and Store URI schemes.
- [x] `BUG-18091` `P1` Refresh Backups when project tags, external identity, or
  folder membership changes.
- [x] `BUG-18092` `P1` Surface previously silent cache and deferred-backup
  background failures in diagnostics.

---

## 1.8.7 — Trust and Portability

**Status:** Planned
**Tagline:** *Show the proof.*

- [ ] `VS-1871` `P1` Expose build, channel, commit, runtime, architecture,
  package, and update-source information.
- [ ] `VS-1872` `P0` Publish artifact checksums and a machine-readable release
  manifest from one release source of truth.
- [ ] `VS-1873` `P1` Generate and publish a Software Bill of Materials and
  build provenance where supported.
- [ ] `VS-1874` `P1` Export a portable, checksummed Recovery Evidence Package.
- [ ] `VS-1875` `P1` Strengthen explicitly redacted support bundles.
- [ ] `VS-1876` `P1` Document repository layouts, manifests, encryption
  envelopes, compatibility, and emergency recovery expectations.
- [ ] `VS-1877` `P1` Add source-machine identity, repository writer locking,
  and explicit dual-boot/concurrent-writer guidance.
- [ ] `VS-1878` `P1` Synchronize website, updater, changelog, Store metadata,
  badges, and public roadmap from canonical release metadata.

Signing and notarization remain desirable trust work, but availability and cost
must not make truthful checksums, manifests, SBOMs, or provenance optional.

---

## 1.8.8 — Chronicle Stabilization

**Status:** Planned and required before `1.9.0`
**Tagline:** *A stable foundation for larger recovery.*

- [ ] `VS-1823` `P0` Establish large-history and high-file-count performance
  budgets with repeatable benchmarks. _(Existing issue #382.)_
- [ ] `VS-1821` `P1` Finish backup and metadata orchestration decomposition
  needed for fault isolation. _(Existing issue #380.)_
- [ ] `VS-1822` `P1` Finish oversized desktop view-model decomposition needed
  for fault isolation. _(Existing issue #381.)_
- [ ] `VS-1881` `P0` Run the complete Windows, macOS, and Linux release matrix.
- [ ] `VS-1882` `P0` Harden interruption, cancellation, archive corruption,
  retention, migration, and clean-state recovery.
- [ ] `VS-1883` `P1` Close localization, accessibility, scaling, contrast, and
  narrow-layout defects.
- [ ] `VS-1884` `P1` Service supported dependencies, installers, updater, and
  prior-version compatibility.

The mandatory exit matrix includes plain and encrypted backup/restore,
clean-machine recovery, interruption, destination disconnection, corruption
detection, retention safety, patch fallback, prior-version restore
compatibility, and smoke tests on all supported operating systems.

---

# VaultSync 1.9 — Recovery Horizon

## Family promise

> Recover from disk and machine-level failure with inspectable evidence and a
> bootable path that does not depend on the installed operating system.

`1.9.0` will not ship disk cloning as an isolated feature. The stable release
requires cloning, validation, image-to-disk recovery, and bootable recovery
media to be ready and tested together.

## 1.9 UI migration program

The 1.9 interface is a staged information-architecture migration, not a
single-release visual rewrite. Existing workflows remain available until their
replacement is complete, keyboard-accessible, localized, and proven against
the same underlying operations.

Migration principles:

- organize around user intent: `Protect`, `History`, `Recover`, and `Manage`;
- separate routine status and next actions from expert configuration;
- keep one authoritative route/state model instead of page-specific navigation
  flags and reflection-based fallbacks;
- preserve deep links, selected project, filters, and unfinished work when
  navigating;
- migrate one workflow family at a time behind stable service contracts;
- remove the legacy shell only after parity and usability qualification;
- treat themes as presentation over shared semantic resources, not separate
  layouts;
- do not use hidden telemetry to judge the redesign; rely on explicit
  usability sessions, opt-in feedback, and local diagnostics.

## Existing 1.9 foundation IDs

- [ ] `VS-1902` `P1` Add trusted application signing where operationally and
  financially feasible. _(Tracked by #113.)_
- [x] `VS-1903` `P2` Add background integrity audits with alerts.
  _(Historical issue #114 is complete; later scheduling work receives a new
  release-specific ID.)_

## 1.9.0 — Disk and Bootable Recovery Foundation

**Status:** Planned; architecture work may begin only after the `1.8.5` design
contracts stabilize.
**Tagline:** *Recover when the installed system cannot.*

- [ ] `VS-1910` `P0` Define the 1.9 information architecture, route model,
  workflow boundaries, navigation invariants, and legacy-shell migration map.
- [ ] `VS-1904` `P0` Build an isolated disk/partition cloning engine with
  explicit operation states and checkpoint contracts.
- [ ] `VS-1905` `P0` Add safe source/destination identity, overwrite previews,
  capacity checks, interruption semantics, and block validation.
- [ ] `VS-1906` `P0` Produce bootable recovery media for supported UEFI
  systems with offline device discovery and recovery workflows.
- [ ] `VS-1907` `P0` Restore supported images to a disk or partition from the
  bootable environment.
- [ ] `VS-1908` `P1` Record clone, validation, boot, and restore evidence with
  source-machine and tool-version identity.
- [ ] `VS-1909` `P0` Qualify the complete clone-to-bootable-recovery path on
  representative hardware and virtual machines.

### 1.9.0 stable release gate

- source and destination cannot be confused silently;
- destructive actions require an exact wipe/overwrite preview;
- interrupted clones are rejected or safely resumable;
- clone verification detects incomplete or corrupt images;
- recovery media boots independently of the installed OS;
- supported images can be restored from that environment;
- restored media passes the documented validation procedure;
- limitations for filesystems, encryption, Secure Boot, and live-system
  capture are explicit;
- no claim of universal bare-metal recovery is made.

## 1.9.1 — Clone Explorer

- [ ] `VS-1911` `P1` Browse supported image partitions and files read-only.
- [ ] `VS-1912` `P1` Search and selectively extract files from images.
- [ ] `VS-1913` `P1` Inspect image creation, verification, and compatibility.
- [ ] `VS-1914` `P2` Compare supported clone images where safe and practical.
- [ ] `VS-1915` `P0` Introduce the adaptive 1.9 shell and typed route/state
  infrastructure without removing the existing workflow views.
- [ ] `VS-1916` `P1` Migrate Dashboard and Recovery entry points into
  goal-oriented home and recovery workspaces.

## 1.9.2 — Offsite Protection

- [ ] `VS-1921` `P0` Add resumable S3-compatible object-storage destinations.
- [ ] `VS-1922` `P1` Add Backblaze B2 and SFTP destination profiles.
- [ ] `VS-1923` `P0` Validate remote manifests and clean incomplete uploads.
- [ ] `VS-1924` `P1` Explain immutability, object lock, retention, and cost.
- [ ] `VS-1925` `P1` Prove clean-machine recovery from supported offsite data.
- [ ] `VS-1926` `P1` Migrate Projects, Backups, and Schedule into one
  progressive-disclosure Protection workspace.
- [ ] `VS-1927` `P1` Add multi-destination health scoring and safe,
  explainable automatic failover.

## 1.9.3 — Portable Recovery

- [ ] `VS-1931` `P0` Ship a standalone desktop restore utility.
- [ ] `VS-1932` `P1` Generate an emergency recovery kit.
- [ ] `VS-1933` `P0` Publish the supported backup and encryption format
  specification.
- [ ] `VS-1934` `P0` Define compatibility, migration, deprecation, and
  emergency read-only policies.
- [ ] `VS-1935` `P1` Migrate History, Snapshot Explorer, and Settings into
  focused activity, inspection, and management workspaces.

## 1.9.4 — Unified Recovery Experience

- [ ] `VS-1941` `P0` Complete unified project, file, image, storage, and
  recovery navigation and retire the legacy shell after parity.
- [ ] `VS-1942` `P1` Add a shared Recovery Inspector across recovery types.
- [ ] `VS-1943` `P2` Revisit project groups and high-density organization if
  `VS-1811` moved out of 1.8.
- [ ] `VS-1944` `P0` Qualify the complete 1.9 interface across supported
  widths, scaling, keyboard, screen-reader, theme, and interrupted-work states.

## 1.9.5 — Recovery Operations

- [ ] `VS-1951` `P1` Schedule full verification and recovery drills.
- [ ] `VS-1952` `P1` Add user-controlled stale, missed, offline, credential,
  and offsite alerts.
- [ ] `VS-1953` `P1` Add CLI and structured headless recovery reporting.
- [ ] `VS-1954` `P2` Add multi-machine summaries without introducing hidden
  telemetry or mandatory hosted services.
- [ ] `VS-1955` `P2` Add explicit local automation hooks for approved backup,
  verification, and restore events.
- [ ] `VS-1956` `P2` Bring the CLI to documented parity with stable,
  automation-safe desktop workflows.

## 1.9.6 — Stability and LTS Baseline

- [ ] `VS-1961` `P0` Stabilize disk-image, offsite, and portable-recovery
  formats.
- [ ] `VS-1962` `P0` Complete long-duration, large-dataset, migration,
  filesystem, and fault-injection qualification.
- [ ] `VS-1963` `P1` Publish the supported compatibility window and LTS policy.

---

# Candidate backlog

These items are not assigned to a release until their contracts are approved:

- `VS-1801` full repository backup mode including `.git` (tracked by #296);
- additional object-storage and WebDAV providers;
- `VS-1971` optional shared/team vault workflows with explicit ownership,
  access, conflict, and audit contracts;
- enterprise deployment and centralized administration;
- universal boot media or guaranteed cross-hardware bare-metal recovery.

# Roadmap governance

- `ROADMAP.md` is the only canonical planning document.
- `CHANGELOG.md` records shipped behavior, not future promises.
- `docs/WHATS_NEW.md` explains the active release in user-facing language.
- GitHub milestones define release gates.
- GitHub Project 7 mirrors roadmap execution state.
- A release branch and draft PR contain only work for that release.
- Stable history is never rewritten to make a release branch appear cleaner.
- Scope changes must update this file, the owning issue, milestone, project
  fields, and draft PR description together.
