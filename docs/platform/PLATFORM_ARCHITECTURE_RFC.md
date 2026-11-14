# RFC-P01 — VaultSync Platform Architecture

**Status:** Proposal / research; not implementation-approved.  
**Scope:** Internal 1.9.x foundations after 1.9.0, plus a possible 2.0 modular platform.  
**Issue:** [#817](https://github.com/ATAC-Helicopter/VaultSync/issues/817).  
**Authority:** [ROADMAP.md](../../ROADMAP.md) and [Recovery Horizon strategy](../RECOVERY_HORIZON_STRATEGY.md).  
**Do not change:** 1.9.0 roadmap, release contract, beta scope, gates or code under this RFC.

## 1. Product architecture thesis

VaultSync is not a generic automation shell. Its authority is to produce **verifiable recovery outcomes**. A future extension system may add capabilities, but it must not redefine the evidence required to claim a recovery point is usable. The existing candidate `2.0 Resilience` product direction (Protection Plans, Recovery Graph, Protection Map, scenario evaluation) stays distinct from a **Platform** implementation layer; the two concepts complement rather than replace one another.

Proposed architectural stack:

```text
  Desktop (Avalonia)     CLI / Headless     Independent Recovery
           \                 |                     /
                   Application use cases
            typed commands, plans, progress, errors
                             |
     Policy/Authorization and Trusted Recovery Kernel
    provenance | identity | verification | mutation gate
                             |
    Capability broker + INTERNAL versioned service ports
      |           |              |                |
  storage    image readers   assurance     BSC services
  adapters   and writers    & evidence     (own format)
      \           |              |               /
          Built-in implementations (1.9.x)
                             |
           Future extension transport (2.0 only)
              isolated worker / constrained WASM
                             |
         Curated catalog, signatures, revocation
```

The arrows express logical dependencies, **not** a runtime plugin system in 1.9.x. The trusted layer must never call back into an extension for an integrity verdict or destruction authorization.

## 2. Repository observations (release/1.9.0 and release/1.9.1 snapshot, 2026-10-25)

- The solution has `VaultSync.Core`, `VaultSync.UI`, `VaultSync.CLI`, tests and benchmarks. Project separation is not a supported extension API.
- `src/VaultSync.Core/Services/BackupService.cs` contains about 4,100 lines and mixes orchestration, preflight, archive, retention, retry, configuration and storage concerns. This is a candidate for internal seams, not a mandate for a risky simultaneous rewrite.
- `RecoverabilityService.AnalyzeAsync` directly calls `SnapshotExplorerService.ReadStoredFileEvidenceAsync`. Introduce an internal read-only byte-observation port **only when it preserves exact hash/read/limit semantics**.
- `src/VaultSync.UI/App.axaml.cs` owns substantial startup/lifecycle wiring. `docs/ROUTE_ARCHITECTURE_1.9.md` already proposes typed routes under VS-1910/VS-1915. Reuse that proposal, do not invent a second router.
- The existing repository metadata schema, writer lease, forward-version rejection, backup payload semantics and emergency recovery procedure are authoritative; internal refactors must not mutate them accidentally.
- `release/1.9.1` contains a 1.9.0–1.9.8 roadmap; `Dev` was observed to contain an older 1.9.0–1.9.6 plan. This RFC must not silently reconcile the branches or allocate conflicting IDs.

These are targeted file and contract observations, **not** a full call-graph or build audit. An inventory script and cross-platform test report are required before an implementation ADR is accepted.

## 3. Trusted Computing Base (TCB)

Keep these inside audited first-party components:
- logical resource identities and source/destination identity confirmation;
- path normalization, traversal/link handling, canonical read boundaries and copy-on-write staging;
- protected-point/retention rules, repository leases, commit/publish ordering, unknown-format rejection;
- backup content hash verification, proof provenance and evidence aggregation;
- encryption design and secret references; secrets never enter extension metadata, event payloads or generic logs;
- destructive operation planning/confirmation and final write authorization;
- independent archive, image and BSC recovery support for **officially supported** formats.

A third party may produce input data or a bounded source observation; the trusted core independently validates size, hash, identity, authorized target and write outcome. No extension is permitted to say `verified=true` on behalf of VaultSync. The UI may render external analysis labeled **unverified/advisory**.

## 4. Module taxonomy and activation

| Class | Example | 1.9.x status | Candidate 2.0 trust |
| --- | --- | --- | --- |
| Trusted kernel | identity, verified recovery, retention decisions | already first-party | inside app/recovery |
| Built-in feature | image reader, offsite provider, BSC command | narrow internal ports | signed first-party |
| Declarative pack | exclusions, metadata mappings, templates | existing preset concepts | content validated; cannot confer safety |
| Community read-only | report formatter, post-job notification | research only | sandboxed + narrow permission |
| Community external IO | S3-like cloud provider, export tool | research only | brokered credentials/IO with constrained worker |
| Privileged operation | disk imaging, boot media, wipe | never general plugin extension | first-party broker with explicit elevation |

Prohibition: an extension cannot acquire a role simply by claiming it in a manifest; runtime policy determines which operations exist and which identities are accessible.

## 5. Internal application ports (1.9.x; do not call these public SDKs)

Define a port only where the feature already has at least two consumers, a recovery/test consumer, or an approved foreseeable provider with materially different semantics. Prefer slim interfaces and immutable result objects:
- `IRecoveryContentReader`: bounded stream/observation from a known approved source, never unchecked paths. Existing verification remains authoritative.
- `IBackupDestinationBackend`: typed object/stream operations and **capability negotiation**. Do not assert universal atomic rename, list consistency or leases.
- `IApplicationOperation` or explicit domain handlers: plan / authorize / execute / observe, cancellation and partial-failure vocabulary shared between UI and CLI.
- `IEvidenceSink`: append evidence with provenance, timestamps, scope and limits. A sink does not certify evidence.
- `IEventPublisher`: publish post-commit, redacted typed notifications with documented idempotency/replay semantics.
- `IBscRepositoryBackend`: restricted to the BSC domain and its own publication, lock and GC contracts. No universal `IStorageProvider` that erases invariants.

Initial references should remain implementation-private and shipped together; only after qualification should a public interface assembly or remote schema be considered.

## 6. Use-case boundaries

**Operation model** (proposal): `requested -> planned -> authorized -> staged -> committed -> verified/partially_verified/failed`. A read-only query never advances the mutation state. A safety check is not a substitute for a transaction or durable commit. Cancellation can arrive at any state; distinguish `cancelled_without_mutation`, `cancelled_with_checkpoint`, and `committed_with_late_cancel`.

**Evidence model:** standardized `evidence.kind`, `scope`, `source_id`, `observed_at`, `read_bytes`, `expected_digest`, `observed_digest`, `status` and `provenance`. Secret material, raw credentials and unapproved absolute paths are forbidden; schema is hypothetical, not authorized on disk.

**UI model:** typed route to resources through a resolver; feature panels contribute view models only through first-party composition in 1.9. A future 2.0 plugin UI may initially be limited to strictly declarative cards, never arbitrary Avalonia XAML, reflected assemblies or untrusted executable view models.

**CLI model:** the existing 1.9 CLI rework is a foundation for stable commands/results but does not become a plugin command host by default. Extensions must not hijack existing commands or override exit status.

## 7. Options evaluated

| Pattern | Benefit | Cost/risk | Recommendation |
| --- | --- | --- | --- |
| Static feature separation | easy performance/security accounting, no dynamic code | still ships all features | adopt in 1.9.x where useful |
| `AssemblyLoadContext` | dependency version isolation | **not security isolation**, shared process privilege | exclude for community-untrusted code |
| Trusted out-of-process worker | fault isolation, language-agnostic protocol | process/IPC/restart management | prototype for future 2.0 |
| Native sandboxed worker | OS-enforced permissions | Windows/macOS/Linux differences; packaging burdens | prototype on all three platforms before commitment |
| WebAssembly/WASI | constrained portability, capability-based resource access | runtime/interface/ecosystem limitations | evaluate for non-privileged extensions, not universal engine |
| Remote hosted plugin execution | centralized policy/review | privacy, recurring costs, offline failure | reject as mandatory requirement |
| Plugin-driven storage format | easy ecosystem experimentation | portable recovery becomes plugin-dependent | reject for authoritative recovery formats |

### IPC draft (not yet selected)

Prefer a small schema-driven protocol across stdio or a protected local socket/pipe. Version negotiation and authorization precede work. A worker authenticates to the host through a per-launch capability, not user-supplied network endpoints. Messages are size/depth/rate bounded. Session loss must terminate or fail safely, and must never silently commit pending destructive actions. Evaluate length-delimited JSON-RPC, protobuf/gRPC and WASI component boundaries using measured prototypes; **no transport is approved by this document**.

## 8. Compatibility and offline survival

Distinct versioned surfaces: application binary, core operation contract, independent recovery reader, repository format, CLI/JSON, future extension manifest, future IPC schema, and future marketplace trust metadata. A major application version must **not** imply a repository format upgrade.

Rules:
1. Existing 1.8/1.9 repos remain readable with documented version limits. Unknown future versions fail closed/read-only.
2. Installer/extension updates are not allowed to delete or migrate repositories as a side effect.
3. A missing future plugin may make a convenience provider temporarily unavailable; it must not alter existing bytes, tamper with retention or produce fake green recoverability.
4. A first-party portable recovery tool must identify supported official payloads without network, a previous app database or extension binaries; external formats can show `unsupported` with byte-preserving export guidance.
5. Write migrations require backups, forward-only schema decisions, rollback plans and interrupted-write tests; no silent downgrade.
6. A protection plan cannot infer cross-site independence from different labels alone; recovery edges need provenance and measured evidence.

## 9. Relationship to candidate 2.0 Resilience

A `ProtectionPlan` is an outcome/policy specification, **not** a list of activated plugins. The `RecoveryGraph` records actual dependencies and failure domains, not a decorative diagram. Capabilities discovered from providers may inform feasibility but cannot invent a recovery proof. The `ProtectionMap` displays facts, stale evidence and unknowns separately.

Candidate 2.0 must pass the pre-existing major-version approval gate: meaningful persisted model, validated user benefit, documented migration, complete UI parity, confidence correctness and independently proven recovery. The platform must add its own security, distribution and ecosystem sustainability gates before any public marketplace claim.

## 10. Explicit non-goals

- No work, issue scope, branch mutation or release promises for 1.9.0.
- No `2.0` date or new VS-20xx identifiers.
- No mandatory accounts, cloud control plane, continuous telemetry or remote plugin execution.
- No arbitrary downloadable DLLs running inside `VaultSync.UI`.
- No marketplace payment system, review rating algorithms or store-front redesign in 1.9.
- No wholesale conversion of working components into interfaces without testable consumer value.
- No third-party destructive disk/image drivers, credential readers, repository metadata writers or evidence attesters.

## 11. Implementation decision protocol

See [foundations map](FOUNDATIONS_1_9.md), [security model](EXTENSION_SECURITY.md), [contracts/test plan](CONTRACTS_AND_QUALIFICATION.md) and [decision register](DECISION_REGISTER.md). Approval requires a code owner, rationale, rejected alternative, tests, rollback behavior, supported-platform matrix, assigned roadmap ticket, and compatibility notes. Until then, this document describes **candidate architecture only**.

## Sources

Repository: `ROADMAP.md`, `docs/RECOVERY_HORIZON_STRATEGY.md`, `docs/ROUTE_ARCHITECTURE_1.9.md`, `docs/REPOSITORY_FORMATS.md`, `docs/CROSS_MACHINE_SAFETY.md`, `docs/RECOVERABILITY_ENGINE.md`, `docs/BINARY_SOURCE_CONTROL_STRATEGY.md`, `docs/UPDATER.md`, `SECURITY.md`. External: Microsoft AssemblyLoadContext API, Apple App Review 2.5.2 / Mac App Store 2.4.5, Windows AppContainer, The Update Framework specification. See the source URLs in [security](EXTENSION_SECURITY.md).
