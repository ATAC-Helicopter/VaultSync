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
- Each execution item has exactly one owning identifier. A bug-labelled item
  must never carry a `VS-xxxx` identifier, including legacy dual-ID titles.
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
Every project item requires Start and Target dates; Done items also require
Completed on. Missing Start dates trace to issue or pull-request creation.
Missing Target dates trace, in order, to the item's milestone due date, the
published date of its historical release, the matching release-horizon
milestone, or the close/merge date of completed unscheduled work. The sync
fails closed when none of those sources provides a defensible date.

Before synchronizing descriptions, run
`pwsh scripts/sync_project_descriptions.ps1 -ProjectNumber 7 -DryRun` and review
the structured change report. The synchronizer reconstructs wrapped roadmap
titles for matching, preserves manually maintained issue contracts, writes
only bodies carrying its `Synced from ROADMAP.md` ownership marker, and audits
the required project dates. A non-dry run repairs traceable missing dates and
refuses partial updates when any date lacks a canonical source.

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
**Stable integration:** `430dc15` / PR #539
**Published assets:** 18 qualified assets on 2026-08-10

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

**Status:** Released on 2026-08-21 as `v1.8.7`.
**Tagline:** *Show the proof.*
**Stable target:** 2026-08-24
**Working branch:** `release/1.8.7`
**Integration target:** `Dev`

The shipped behavior is summarized in `CHANGELOG.md` and `docs/WHATS_NEW.md`.
Durable repository and cross-machine contracts remain in
`docs/REPOSITORY_FORMATS.md` and `docs/CROSS_MACHINE_SAFETY.md`.

Minor releases target a weekly train and must not remain open longer than two
weeks after the preceding Stable release. Release-blocking safety and regression
work stays in the active train; incomplete non-blocking polish moves forward to
the next minor rather than silently extending the release. Major releases begin
after the planned minor train is complete and use explicit beta qualification.

- [x] `VS-1871` `P1` Expose build, channel, commit, runtime, architecture,
  package, and update-source information.
  - Scope: define one build-information contract used by the desktop About and
    diagnostics surfaces plus machine-readable CLI output; distinguish version,
    channel, commit, runtime, architecture, package kind, update source, and
    whether the build is official without treating unsigned packages as signed.
  - Acceptance: a user or support bundle can identify the exact running build
    without inspecting filenames, and unavailable values are shown as unknown
    rather than guessed.
  - Completed 2026-08-17: one schema-versioned record now drives Settings copy,
    startup diagnostics, support bundles, recovery reports, and CLI JSON. Release
    publishes stamp the source commit and distribution facts; unstamped or
    incomplete builds cannot present themselves as official.
- [x] `VS-1872` `P0` Publish artifact checksums and a machine-readable release
  manifest from one release source of truth.
  - Scope: generate version, channel, tag, commit, compatible predecessors,
    asset names, platform, architecture, package kind, byte size, and SHA-256
    from the artifacts that are actually published; keep the manifest itself
    outside its own digest set and validate every consumer against one schema.
  - Acceptance: changing an asset, version, or digest makes release validation
    fail, while an offline user can validate every downloaded package using the
    published manifest and documented commands.
- [x] `VS-1873` `P1` Generate and publish a Software Bill of Materials and
  build provenance where supported.
  - Scope: create an SBOM for each self-contained platform artifact, attest the
    published artifact rather than an intermediate build directory, and expose
    online and offline verification instructions.
  - Acceptance: SBOM schemas validate, provenance binds each package to the
    repository, workflow, and commit that produced it, and verification is
    exercised in the release-candidate gate.
  - Completed 2026-08-21: final manifest-listed packages receive validated SPDX
    2.3 documents tied to their exact SHA-256 and RID-specific dependency graph.
    A commit-pinned GitHub action attests final package provenance and each SBOM;
    candidate automation exercises both API-backed and downloaded-bundle trust
    and passed manifest, SBOM, online-attestation, and offline-bundle verification
    for every supported package.
- [x] `VS-1874` `P1` Export a portable, checksummed Recovery Evidence Package.
  - Scope: package a versioned JSON record, readable report, package manifest,
    checksums, build identity, recovery state, drill evidence, and repository
    identity without backup payloads, credentials, encryption secrets, or raw
    unrestricted local paths.
  - Acceptance: repeated exports of the same evidence are deterministic,
    tampering is detected, schema compatibility is explicit, and the package
    can be inspected without VaultSync.
  - Completed 2026-08-20: Recovery exports now produce one ZIP with canonical
    JSON, readable Markdown, a versioned manifest, and a standard SHA-256 index.
    Repository identities are pseudonymous and local paths are redacted;
    validation rejects altered, missing, duplicate, traversing, unexpected, or
    unsupported content.
- [x] `VS-1875` `P1` Strengthen explicitly redacted support bundles.
  - Scope: define an allowlisted bundle schema, path pseudonymization, secret
    denylist, size limits, and a review screen that lists every included file
    and category before export.
  - Acceptance: automated fixtures containing credentials, tokens, passwords,
    user paths, and encryption material cannot leak them; users can cancel or
    remove optional sections before the archive is written.
  - Completed 2026-08-21: support exports use a generated-file allowlist,
    bounded sanitized diagnostic and telemetry inputs, path and identity
    pseudonyms, structured and configured-secret redaction, a SHA-256 manifest,
    and an explicit review where optional sections can be removed or cancelled.
- [x] `VS-1876` `P1` Document repository layouts, manifests, encryption
  envelopes, compatibility, and emergency recovery expectations.
  - Scope: document supported repository records and versions, portable versus
    machine-local fields, encryption descriptors, legacy behavior, manual
    recovery, locks and leases, release verification, and failure recovery.
  - Acceptance: documentation matches executable schemas and tests, includes a
    clean-machine recovery path, and states every known compatibility limit.
  - Completed 2026-08-21: repository-format, cross-machine, metadata-sync,
    disaster-recovery, updater, and release guidance now cover schemas 1–3,
    portability, leases, merge/rollback, clean-machine inspection, emergency
    restore, release integrity, provenance, and unsigned-package limitations.
- [x] `VS-1877` `P0` Add source-machine identity, repository writer locking,
  and explicit dual-boot/concurrent-writer guidance.
  - Scope: use a durable installation identity and a repository-scoped lease
    with owner, operation, nonce, heartbeat, expiry, and application version;
    allow safe read-only inspection, explicit stale takeover, and diagnostic
    evidence without relying on process-local semaphores or machine names.
  - Acceptance: two 1.8.7 clients cannot write concurrently, interrupted leases
    recover predictably, NAS/SMB and clock-skew cases are covered, and the UI
    states that pre-1.8.7 clients cannot cooperate with the lease protocol.
  - Completed on the 1.8.7 release branch on 2026-08-16 in PR #546, including
    per-destination owner inspection and explicit nonce-bound stale takeover.
- [x] `VS-1878` `P1` Synchronize website, updater, changelog, Store metadata,
  badges, and public roadmap from canonical release metadata.
  - Scope: make public and in-app release consumers derive from or validate
    against the canonical release contract, including dry-run generation before
    publication.
  - Acceptance: CI rejects inconsistent public metadata and one unpublished
    release-candidate run produces every expected consumer without publishing.
  - Completed 2026-08-21: a schema-versioned release contract validates the
    desktop, CLI, installer, Store manifest, updater guidance, changelog,
    What’s New, website fallback, release page, roadmap, tag, date, branches,
    and qualified predecessor. A deterministic command renders public, Store,
    and release-summary outputs without publishing; CI and release assets reject
    drift, while the website refreshes the latest stable tag from GitHub.
- [x] `VS-1879` `P0` Replace two-way cross-machine settings import with a
  versioned, reviewable, and reversible merge contract.
  - Scope: persist a durable writer identity, per-record revision and base
    revision, field-level portable-value provenance, and an explicit merge plan;
    classify local-only fields separately, keep imports preview-only until the
    user confirms conflicts, and make Keep local publish or remember a durable
    resolution instead of rediscovering the same conflict.
  - Acceptance: independent edits on two machines never silently overwrite one
    another; non-overlapping changes merge, overlapping changes show old, local,
    and remote values with timestamps and writers; accepting either side is
    durable and auditable; the operation can be undone before the next write.
  - Completed 2026-08-21 in PR #546: durable per-source merge bases,
    field-level three-way planning, automatic non-overlapping merges, and
    resolution results that retain independent edits are implemented and
    tested. Guarded repository writes and schema-version-3 base/provenance and
    resolution export followed on 2026-08-17, together with Base/local/remote
    presentation and bounded undo that expires after the next portable write.
    Reviewed decisions now run through one core service, and a file-backed
    two-installation qualification proves convergence, conflict preservation,
    restart durability, repeat-import suppression, undo, and later convergence.
- [x] `VS-1880` `P1` Simplify and standardize shared application code without
  changing user-visible behavior.
  - Scope: consolidate repeated retry, path, serialization, status, dialog,
    lifecycle, and projection logic behind focused tested primitives; decompose
    oversized backup, metadata, Dashboard, Settings, Projects, and history
    orchestration; remove confirmed dead code; and document the few intentional
    platform-specific duplications that cannot safely share an implementation.
  - Acceptance: every touched behavior retains regression coverage, no new
    Sonar duplication is introduced, the repository duplication baseline falls
    release over release, all remaining duplicated blocks are reviewed and
    justified or tracked, and builds remain warning-free on every supported
    platform.
  - Completed 2026-08-21: shared metadata-export leases, mount parsing and
    validation, preset resolution, theme calculations, retry paths, and release
    utilities replaced repeated implementations with regression-tested
    primitives. Sonar reports `0.0%` duplication on new code and the branch-wide
    duplication density fell from the Stable baseline of `2.5%` to `1.9%`;
    release builds remain warning-free.

### Confirmed defects entering 1.8.7

- [x] `BUG-18098` `P1` Preserve complete wrapped roadmap ticket titles, scope,
  and acceptance text when synchronizing GitHub issues and Project entries.
  - Acceptance: parser fixtures cover multiline titles and nested scope bullets,
    and a dry run reports exact changes without rewriting valid issue contracts.
  - Completed on the 1.8.7 release branch on 2026-08-15 in PR #546.
- [x] `BUG-18099` `P0` Service the .NET runtime and coordinated Microsoft
  packages to the security-fixed `10.0.11` baseline or newer validated patch.
  - Acceptance: all current runtime-pack Dependabot alerts are closed, direct
    and runtime-pack vulnerability audits agree, and self-contained packages on
    every supported RID contain the qualified runtime patch.
  - Completed: SDK `10.0.303`, runtime `10.0.11`, and coordinated Microsoft
    packages were pinned on 2026-08-12; unused cross-RID restore declarations
    were removed, CI audits a real self-contained publish, and release jobs
    verify the runtime embedded in every supported RID.
- [x] `BUG-18100` `P0` Restore the permanent `Dev` branch and prevent Stable
  promotion merges from automatically deleting it.
  - Completed: `Dev` was restored at the exact `v1.8.6` Stable commit on
    2026-08-12 and automatic head-branch deletion was disabled.
- [x] `BUG-18101` `P0` Stop cross-machine metadata import from applying
  unreviewed settings or repeatedly resurfacing a rejected remote edit.
  - Scope: encryption policy and key references, auto-backup state, avatar
    color, tombstones, and all existing conflict fields must follow an explicit
    portability and conflict policy; Keep local must be durable.
  - Acceptance: imports do not silently apply machine-local key references or
    destructive tombstones, every changed portable field appears in preview,
    writer attribution is record-specific, and resolved conflicts stay resolved.
  - Completed on the 1.8.7 release branch on 2026-08-16 with version-2 project
    writer/revision records, durable conflict decisions, complete portable-field
    review, local-only key and destination handling, and destructive-import gates.
- [x] `BUG-18102` `P0` Prevent deferred metadata replay from overwriting a
  destination that changed while it was unavailable.
  - Acceptance: deferred stores are lease-protected, flush at most once into an
    empty metadata destination, and remain preserved for merge review when the
    destination already contains metadata.
  - Completed on the 1.8.7 release branch on 2026-08-12 together with durable
    writer protection for every existing metadata export and tombstone path.
- [x] `BUG-18103` `P1` Modernize outdated utility windows and restore theme
  consistency across Snapshot Explorer, metadata-import review, and updater UI.
  - Acceptance: the utility windows use the current compact layout and dynamic
    theme resources without regressing browsing, preview, import, or update
    behavior.
  - Completed on the 1.8.7 release branch on 2026-08-13 in PR #546.
- [x] `BUG-18104` `P0` Correct stale preset exclusions and eliminate Windows
  preset-resolution drift.
  - Acceptance: current generated state is excluded, shared editor and Git
    control files remain protected, live `.git` internals remain gated by
    `VS-1801`, unsupported negation rules are absent, and every backup path uses
    the shared preset resolver with regression coverage.
  - Completed on the 1.8.7 release branch on 2026-08-13 in PR #546.
- [x] `BUG-18105` `P1` Prevent metadata-import previews from double-counting
  projects and backups represented by both metadata and legacy folders.
  - Acceptance: a preview reports each project, snapshot, and backup once,
    remains read-only, and repeated previews return the same result.
  - Completed on the 1.8.7 release branch on 2026-08-15 in PR #546.
- [x] `BUG-18106` `P1` Normalize credential-free macOS SMB mount diagnostics.
  - Acceptance: mount errors contain neither raw nor URL-escaped passwords,
    replace known credential-bearing share URLs with their credential-free
    display identity, and preserve unrelated diagnostic text.
  - Completed on the 1.8.7 release branch on 2026-08-15 in PR #546.
- [x] `BUG-18107` `P0` Export snapshot tombstones only for snapshots actually
  deleted during metadata import.
  - Acceptance: snapshots retained by local backups and unknown remote snapshot
    IDs never produce deletion tombstones; each locally deleted unreferenced
    snapshot produces exactly one tombstone.
  - Completed on the 1.8.7 release branch on 2026-08-15 in PR #546.
- [x] `BUG-18108` `P1` Stop repeated immutable updater manifest downloads.
  - Acceptance: canonical and platform patch manifests are reused across
    application restarts only when their release URL, exact size, and trusted
    SHA-256 identity still match; tampered and linked entries fail closed.
  - Completed on the 1.8.7 release branch on 2026-08-15 in PR #546.
- [x] `BUG-18109` `P0` Bound disposable local storage and reject unbacked
  macOS managed mount paths.
  - Acceptance: startup cleanup applies tested age and size limits only to
    re-creatable VaultSync data; databases, configuration, credentials,
    backups, and mount contents remain outside cleanup; a managed mount path
    cannot accept backup bytes unless SMB or NFS is currently mounted.
  - Completed on the 1.8.7 release branch on 2026-08-15 in PR #546.
- [x] `BUG-18110` `P1` Complete the recovery and workflow localization pass.
  - Acceptance: Recovery evidence, repository-writer controls, History,
    backup-widget, picker, verification, and restore text use maintained locale
    keys; responsive evidence cards remain readable in every theme.
  - Completed on the 1.8.7 release branch on 2026-08-20 in PR #546.
- [x] `BUG-18111` `P0` Close the remaining Sonar safety and stale-branch defects.
  - Acceptance: recovery exports avoid public temporary roots, cache cleanup is
    limited to private application storage, all retry mounts are cleaned up,
    and unreachable analysis branches are removed.
  - Completed on the 1.8.7 release branch on 2026-08-20 in PR #546.
- [x] `BUG-18112` `P0` Keep passive destination work from unlocking credentials.
  - Acceptance: startup, maintenance, metadata, cleanup, and history probes
    reuse already-mounted destinations but never read Keychain or establish a
    new mount; explicit tests and backups retain auto-mount behavior.
  - Completed on the 1.8.7 release branch on 2026-08-20 in PR #546.
- [x] `BUG-18113` `P0` Correct macOS launch-on-login placement. _(Issue #559.)_
  - Acceptance: the LaunchAgent lives under the actual user home, the erroneous
    Documents path is removed safely, and startup synchronization does not
    kickstart a duplicate app process.
  - Completed on the 1.8.7 release branch on 2026-08-20 in PR #546.
- [x] `BUG-18114` `P0` Make macOS bundle identity and updates coherent.
  _(Issues #560 and #561.)_
  - Acceptance: both DMGs contain `VaultSync.app` and an Applications shortcut;
    1.8.7 uses a full-DMG migration, future patches address the complete bundle,
    and installed version metadata is verified before success.
  - Completed on the 1.8.7 release branch on 2026-08-20 in PR #546.
- [x] `BUG-18115` `P0` Clear the release-SBOM security and backup-cleanup quality gate.
  - Scope: confine SBOM filesystem access to the approved build workspace,
    reject path-bearing artifact/index filenames, and make backup destination
    cleanup explicit across credential retries.
  - Acceptance: traversal attempts fail before filesystem access, valid SBOM
    generation and validation remain deterministic, and every retained mount
    resolution is cleaned exactly once.
  - Completed on the 1.8.7 release branch on 2026-08-20 in PR #546.

### Delivery sequence

1. **12–16 August:** contracts, issue repair, release manifest, serviced runtime,
   machine identity, writer leases, and the first three-way merge slice.
2. **17–19 August:** finish guarded cross-machine writes, provenance, resolution
   export, bounded undo, and two-machine safety fixtures.
3. **20–21 August:** build identity, platform SBOM/provenance, Recovery Evidence
   Package, and reviewed support export.
4. **22 August:** repository and public metadata synchronization plus bounded
   code cleanup.
5. **23 August:** unpublished stable-candidate qualification across supported
   platforms, upgrade paths, storage, localization, accessibility, and security.
6. **24 August:** merge through `Dev` to `Stable`, publish, verify, and close the
   milestone.

### Release gates

- every published asset is represented by an exact size and SHA-256 digest;
- platform SBOMs validate and build attestations verify online and offline;
- two 1.8.7 clients cannot write concurrently to one repository;
- divergent cross-machine edits are previewed and resolved without silent loss;
- upgrades from 1.8.6 preserve local projects, repositories, and recovery data;
- support and evidence exports pass adversarial privacy and tamper tests;
- all maintained translations, themes, accessibility paths, SonarQube, CodeQL,
  dependency audits, and supported-platform builds pass.

Signing and notarization remain desirable trust work, but availability and cost
must not make truthful checksums, manifests, SBOMs, or provenance optional.

---

## 1.8.8 — Chronicle Stabilization

**Status:** Active and required before `1.9.0`
**Tagline:** *A stable foundation for larger recovery.*
**Planning started:** 2026-08-24
**Stable target:** 2026-08-28
**Maximum date:** 2026-09-04
**Working branch:** `release/1.8.8`
**Integration target:** `Dev`

The maintained kickoff, 1.8.7 feedback snapshot, code baseline, delivery order,
and qualification gates are in [`docs/RELEASE_1.8.8.md`](docs/RELEASE_1.8.8.md).

- [x] `VS-1823` `P0` Establish large-history and high-file-count performance
  budgets with repeatable benchmarks. _(Existing issue #382.)_
- [ ] `VS-1821` `P1` Finish backup and metadata orchestration decomposition
  needed for fault isolation. _(Existing issue #380.)_
- [ ] `VS-1822` `P1` Finish oversized desktop view-model decomposition needed
  for fault isolation. _(Existing issue #381.)_
- [ ] `VS-1881` `P0` Run the complete Windows, macOS, and Linux release matrix.
- [ ] `VS-1882` `P0` Harden interruption, cancellation, archive corruption,
  retention, migration, and clean-state recovery. _(Active: plain and encrypted
  compression interruption is qualified, and encryption observes cancellation
  between copied chunks; restart cleanup now rejects invalid resume checkpoints;
  remaining lifecycle boundaries are still open.)_
- [ ] `VS-1883` `P1` Close localization, accessibility, scaling, contrast, and
  narrow-layout defects.
- [x] `BUG-18116` `P1` Prevent unintended horizontal page and dialog scrolling
  while preserving purpose-built file, diff, and log panes. _(Issue #569;
  delivered on the release branch and awaiting integration through #568.)_
- [x] `BUG-18117` `P0` Confine retention deletion across filesystem links and
  preserve the indexed restore point when the selected backup root is unsafe or
  cannot be deleted. _(Issue #570; delivered on the release branch and awaiting
  integration through #568.)_
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
