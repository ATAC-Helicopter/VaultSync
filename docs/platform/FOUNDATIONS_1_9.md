# RFC-P02 — 1.9.1–1.9.8 Internal Foundation Map

**Status:** Candidate landing zones, not ROADMAP execution scope.  
**Issue:** [#817](https://github.com/ATAC-Helicopter/VaultSync/issues/817).  
**Hard exclusion:** 1.9.0 remains exactly as currently planned; no beta/release gate, implementation, testing or schedule is added by this RFC.

## Guiding rule

Never smuggle a 2.0 feature into a 1.9 ticket. An internal foundation is eligible only if it directly helps the **owning 1.9.x capability**, is narrowly testable, preserves old behavior, can be rolled back, and does not create public API support obligations. Otherwise schedule it as a post-1.9 prototype or reject it.

The repository branch `release/1.9.1` has the canonical-looking **1.9.0–1.9.8** plan with BSC in 1.9.6–1.9.7; `Dev` was found to retain an older 1.9.0–1.9.6 plan. This document maps to the release branch but does not resolve planning authority. Before converting any row to an execution ID, audit branch ancestry, tickets, milestone/project fields and the current `ROADMAP.md`.

## Proposed release mapping

| Release | Existing customer outcome and tickets | Allowed internal slice | Explicitly deferred |
| --- | --- | --- | --- |
| **1.9.0** | Disk/boot foundation and CLI rework | **NONE: frozen** | Every item in this RFC |
| **1.9.1** | Clone Explorer, typed desktop shell (`VS-1911–1916`), CLI discoverability (`VS-1977–1978`) | Internal route resolver and operation/view state separation; candidate read-only image-content adapter with bounded stream semantics | Extension navigation, marketplace page, dynamic UI loading |
| **1.9.2** | Portable Recovery (`VS-1931–1935`, `VS-1976`) | Split independent recovery reader from installed-app services; explicit format-capability descriptors; deterministic failure reasons | Third-party recovery decoders, public file-format plugin contract |
| **1.9.3** | Offsite Protection (`VS-1921–1927`) | Internal backend ports with capability negotiation, conditional writes, resumable transfer and durability evidence | Public provider SDK, generic impossible guarantee of storage atomicity |
| **1.9.4** | Unified Recovery Experience (`VS-1941–1944`) | Shared immutable evidence/view models and typed inspector adapters for official recovery types | Arbitrary third-party Avalonia views, UI theme/plugin ABI |
| **1.9.5** | Continuous Recovery Assurance (`VS-1951–1956`), CLI qualification (`VS-1979`) | Internal post-commit event envelope, redacted local hooks and common result vocabulary; no broker running third-party code | Remote webhooks by default, execution of untrusted scripts, plugin event bus |
| **1.9.6** | BSC Foundation (`VS-1983–1987` and associated gate tickets) | Keep BSC publication/lock/object-store boundary internal with explicit repository backend contract and fault-injection hooks | Universal pluggable object format, plugin-driven garbage collection |
| **1.9.7** | BSC collaboration, sparse/resumable transfer and scale (`VS-1988–1993`) | Capability/evidence matrix for BSC backend, replay-safe transfer result, platform-specific qualification | Open-ended community binary merge drivers |
| **1.9.8** | Stability and LTS (`VS-1961–1963`) | Inventory internal seams/compatibility and offline recovery; baseline for possible future public contracts | Public extension SDK or marketplace LTS guarantee |

Numbers shown here refer to *existing* execution work, not a claim that the foundation is already approved within those tickets. Any genuine new implementation scope needs a separate issue/ID, explicit owner, milestone, dependencies, validation and roadmap review.

## 1.9.1: view and inspection seams

- Reuse `docs/ROUTE_ARCHITECTURE_1.9.md` for the typed route design. Wrap legacy view models rather than replacing navigation, current selection and startup behavior wholesale.
- A typed route contains resource IDs, never absolute paths, keys or untrusted query material.
- Create a read-only inspection interface only if clone browsing and ordinary Snapshot Explorer meaningfully reuse safe resolution semantics. **Do not unify formats with different trust and consistency rules merely to share a type name.**
- Acceptance evidence: navigation round trip/back/forward; legacy last-page compatibility; stale IDs; canceled page changes; reader path containment, symlink/traversal/duplicate entries; no data mutation from browse.
- Stop condition: route refactor changes accessibility or source/target identity semantics outside this release's review capacity.

## 1.9.2: independent recovery boundary

- Depend only on supported portable formats, supplied recovery secrets and explicitly selected destinations; never the live app database.
- Separate discovery, read, cryptographic validation, restore planning, targeted staging, commit and post-restore readback.
- A supported repository remains usable when all hypothetical plugins and catalog services are unavailable.
- Unknown formats, missing encryption keys or inaccessible destinations fail with typed `unsupported` / `locked` / `unavailable`; **do not silently skip**.
- Acceptance: clean VM, offline, fresh configuration, intentionally corrupted metadata, wrong password, interrupted staging, old supported versions, crossed destination identity. Restored bytes are independently hashed.
- Stop condition: the new abstraction masks format-specific evidence or weakens the existing `RecoverabilityService` verdict semantics.

## 1.9.3: provider/backend seam

- Distinguish object store, file tree, mounted filesystem and network transport; not all are repositories or directories.
- Proposed `BackendCapabilities`: atomic replace/rename? conditional write? range read? immutable retention? consistent listing? multipart resumption? remote lease? readback? request/egress charges?
- Unknown/unproven capability remains `unknown` or `unsupported`, never `true`.
- A destination profile must not imply its `Object Lock` is configured, supported, durable or independently recoverable. Report actual observed evidence.
- Acceptance: two distinct backends plus fake fault backend; partial multipart upload, competing writers, lost credentials, object mutation, pagination gaps, 429/5xx, latency, egress budget; clean-machine offsite recovery.
- Stop condition: a generic interface requires optimistic assumptions about atomicity, ordering, write visibility or transaction semantics.

## 1.9.4: Recovery Inspector contracts

- Inspector cards are trusted first-party renderings of structured evidence; do not let an analyzer set pass/fail arbitrarily.
- Official recovery types may produce view-specific labels but use a common provenance/status vocabulary and stable source IDs.
- Preserve accessibility, localization, responsive sizing and interrupted-work state defined by `VS-1944`.
- Acceptance: folder/archive/image/offsite items with missing/stale/partial/unsupported proofs render distinct states; zero false-ready on injected bad evidence.
- Stop condition: compatibility requires loss of format-specific warning or transformation of advisory input into verified claim.

## 1.9.5: event and automation boundary

- Events must be emitted **after durable operation state** and identify whether outcome is final, partial, failed or interrupted.
- Suggested internal topics: `backup.committed`, `backup.verification.completed`, `recovery.drill.completed`, `restore.committed`, `destination.health.changed`.
- Redact absolute paths/secrets by default; use local opaque resource identity. Distinguish event ID from operation ID.
- Define at-most-once versus at-least-once and delivery history **before** clients rely on it; consumers must handle duplicate/replayed events. Suppress unauthorized background work.
- Do not expose an execution engine for arbitrary scripts. 1.9 hooks are only those already explicitly approved under `VS-1955`.
- Acceptance: scheduler restart, interrupted process, user disabling hooks, access loss, no background analytics, no unexpected secret leaks or live CLI stdout contamination.
- Stop condition: internal events become a public compatibility surface accidentally.

## 1.9.6–1.9.7: BSC separation

BSC has its own content-addressed object store, refs, leases, lock ownership, checkout rules, garbage collection and publish-before-visible constraints. It is **not** a snapshot-backup adapter disguised as source control.

- 1.9.6: isolate immutable object writer, validator, ref compare-and-swap and lock policy behind first-party-only contracts.
- 1.9.7: support resumable transfer with verified object reachability; sparse cache may be rebuilt, not trusted; offsite replication may be claimed only after verifiable durability.
- Acceptance: interrupted pack publication, power loss, corruption, concurrent NAS writers, stale locks, GC/repack, branch conflict, heterogeneous filesystem case/path handling, clean-machine reconstructability.
- Stop condition: extension-friendly shortcuts introduce garbage collection ambiguity, lock bypass, orphaned reachable objects or optimistic verification.

## 1.9.8: stable contract inventory and LTS boundary

- Record which internal seams will remain private, which may become public later, and why.
- Snapshot tests for 1.8.x/1.9.x repository layouts, CLI JSON, config migration, backup encryption descriptors, provider metadata and BSC formats.
- Exercise rollback, interrupted upgrade, absent provider and offline recovery with supported artefacts.
- Publish **only actual supported compatibility guarantees**. A first-party internal API has no promised community backward compatibility.
- Deferring a future marketplace must not delay or weaken 1.9.8 LTS qualification.

## Backlog candidates: do not allocate IDs yet

Research-only backlog, ordered by prerequisite rather than promise:
1. Code-level dependency inventory and architecture tests for UI/Core/CLI coupling.
2. Disposable source adapter prototype to validate `IRecoveryContentReader`.
3. Provider capability contract and two-backend experiment.
4. Versioned/redacted event envelopes with no external execution.
5. Reproducible standalone restore test across supported 1.9 formats.
6. Windows/macOS/Linux sandbox worker POC under explicit deny-all defaults.
7. Package identity, revocation and trust-root publication exercise.
8. Minimal third-party read-only report extension, malicious fixtures and behavior review.
9. UI and CLI plugin-management prototypes **only if** POCs 6–8 qualify.

## Change-control template for a later approved 1.9.x ticket

**Release/customer outcome:** existing 1.9.x result improved  
**Owner/ID/dependencies:** allocated per ROADMAP protocol, never reused  
**Existing behavior baseline:** explicit tests and performance numbers  
**New private port:** type, consumers, error semantics, cancellation  
**Security boundary:** who can read/write/authorize/attest; where secrets are held  
**Compatibility:** database, repository, CLI, UI, installer and offline recovery  
**Negative/fault tests:** corruption, interruption, concurrency, unsupported versions  
**Rollback:** revert without repository mutation/data loss  
**Non-goals:** no public plugins/SDK/marketplace, no 1.9.0 scope  
**Gate:** approve / defer / reject with reproducible evidence

## Planning discipline

This RFC is deliberately **not** an edit to `ROADMAP.md`, `docs/RELEASE_1.9.0.md`, `CHANGELOG.md` or `docs/WHATS_NEW.md`. Only after release owner approval should small implementation tasks be integrated into the canonical 1.9.x plan. Preserve the separate major-version gate from `docs/RECOVERY_HORIZON_STRATEGY.md`.
