# RFC-P04 — Internal Contracts, Prototype Sequence and Qualification Plan

**Status:** Design draft / proposed acceptance matrix, not an implemented protocol.  
**Issue:** [#817](https://github.com/ATAC-Helicopter/VaultSync/issues/817).  
**Versioning:** Draft-only example fields below have no runtime compatibility guarantee.  
**Scope:** 1.9.1–1.9.8 internal foundations and later candidate 2.0 extension eligibility; 1.9.0 unchanged.

## 1. Architecture contracts to inventory before refactoring

Run a dependency audit on the **actual implementation branch** and record direct consumers, data authority, relevant tests, failure semantics, persistence, timeouts and platform assumptions for:

| Current implementation / document | Observed boundary / research question |
| --- | --- |
| `src/VaultSync.Core/Services/BackupService.cs` | Orchestration vs storage transport, encryption, retry, preflight, retention, concrete repository calls |
| `src/VaultSync.Core/Services/SnapshotExplorerService.cs` | Archive/folder content source resolution, path checks, bounded readers, result evidence |
| `src/VaultSync.Core/Recoverability/RecoverabilityService.cs` | Trusted verifier consumes Snapshot Explorer observations; never delegate verdicts |
| `src/VaultSync.UI/App.axaml.cs`, view models | Startup/lifecycle vs use-case state; route wrapper under VS-1910/VS-1915 |
| `src/VaultSync.CLI` and `docs/CLI_REWORK.md` | Independent commands, structured JSON/errors, no hidden prompting |
| `docs/REPOSITORY_FORMATS.md` | 1.8/1.9 readable payloads, metadata schema, unknown-version refusal |
| `docs/CROSS_MACHINE_SAFETY.md` | Leases, writer identity, conflict provenance and credential separation |
| `docs/BINARY_SOURCE_CONTROL_STRATEGY.md` | Own immutable objects, CAS refs, garbage collection and lock boundaries |
| `docs/UPDATER.md`, `docs/RELEASING.md` | Trusted manifest, exact checksum, package/channel update ownership |

Do not invent dependency claims from file length alone. Capture grep/call graph and test evidence before deciding which interfaces to introduce. The internal reader and provider adapters are potential seams, not prerequisites for 1.9.0.

## 2. Proposed operation envelope (private)

```text
OperationRequest:
  operation_id (opaque stable per attempt)
  type (backup|verify|restore|image-read|...)
  resource_id (not raw path)
  requested_by (human|scheduler|CLI)
  intent + cancellation_id + deadline
  optional dry-run context
OperationPlan:
  immutable plan_id, resource identities, expected bytes/cost/risk
  destructive_targets + authorized capabilities + expiry
OperationResult:
  operation_id + attempt + final_state + reason_code
  committed_scope + verified_scope + warnings + evidence_refs
```

State names `Requested, Planned, Authorized, Staging, Committed, Verified, Partial, Failed, Cancelled` are candidates, not a mandate to flatten different internal state machines. A backup may commit yet fail post-readback; that is not equivalent to `failed_no_write`. Restore staging must be checked *before* replacement and verified *after* replacement where promised.

Design obligations:
- `Plan` is non-destructive; planning must not create a destination.
- authorization binds exact source/destination identities and plan digest, expires, is non-transferable and is invalidated by input changes.
- every write has explicit commit boundary and interruption semantics.
- cancellation and crash do not produce false success; partial states preserve evidence.
- errors are stable machine codes plus localized human explanation, not exception-text contracts.
- preserve CLI existing JSON envelopes and exit behavior through adapters until deprecation is approved.
- progress events do not count as durable completion.

## 3. Backend capability negotiation (internal only)

Proposed type `BackendDescriptor` (not public):
- identity: backend class, destination opaque ID, supported protocol/version;
- read: range-read, seekability, object enumeration semantics, listing freshness;
- write: multipart, conditional create/update, atomic rename, overwrite semantics;
- coordination: lease guarantee, CAS, lock TTL and clock assumptions;
- retention: remote immutability/object lock capability, observed configuration and permissions;
- durability: read-after-write, content readback, publish visibility and checksums;
- cost: metering available / unknown / user-provided; API and egress caveats;
- security: encryption location, auth broker mode, privilege dependency.

Capability status is **Supported and proven / Unsupported / Unknown**, not a misleading Boolean where no test exists. Backend signals never bypass trusted repository publication or lease checks. For BSC, its own repository-specific CAS/GC contracts take precedence; a cloud provider implementing read/write does not thereby support safe multi-writer BSC.

Candidate errors: `not_available`, `unsupported_capability`, `stale_listing`, `identity_changed`, `precondition_failed`, `lease_lost`, `partial_transfer`, `integrity_mismatch`, `secret_unavailable`, `permission_denied`, `cancelled`. Define retry rules per error: integrity failure is not a silent success, and authorization failure is not retried as a different principal.

## 4. Recovery input and evidence

A trusted portable recovery engine must be able to:
1. inspect the repository without relying on the old local app DB;
2. reject unsupported/higher versions read-only;
3. resolve the requested official recovery format and explain missing dependencies;
4. read controlled bytes with strict resource budgets and path containment;
5. validate hashes and declared sizes;
6. present exact target and overwrite plan before mutation;
7. stage, verify staged bytes, commit and post-verify as supported;
8. issue versioned evidence with clear provenance and partial/inconclusive states;
9. recover **offline**, with no extension marketplace or optional provider executable, for all formats claimed as independently supported.

Evidence envelope proposal:

```json
{
  "schema": "proposal-only",
  "evidenceId": "opaque",
  "resourceId": "opaque",
  "producer": "trusted-core",
  "sourceType": "stored-content",
  "scope": { "selectedFiles": 10, "readFiles": 9, "selectedBytes": 1000, "readBytes": 900 },
  "state": "inconclusive",
  "reasonCodes": ["read_limit_reached"],
  "observedUtc": "RFC EXAMPLE",
  "signature": null
}
```

Never reinterpret `inconclusive` as verified and never include plaintext credential, raw unapproved path or user content. If a future extension contributes metadata, record a separate advisory producer; a trusted core re-performs any check used in user-facing recoverability claims.

## 5. Future extension IPC experiment (not in 1.9)

Requirements for a POC:
- handshake authenticates host/worker and verifies compatible protocol range, execution policy, package digest and available capabilities;
- messages are length/depth/time bounded, decoded with strict schema and missing-field behavior;
- capabilities are **host-authorized** per operation; worker-declared permissions grant nothing by themselves;
- content streaming uses chunk quotas, backpressure, cancellation and checksum/evidence flow;
- logs and stdout/stderr never include secrets; stderr is not used as machine protocol;
- no service discovery or public listener is required; local socket/pipe permissions are constrained;
- worker crash, host crash, protocol mismatch, unsupported host build, plugin disable or revoked package yields typed unavailable status;
- no plugin can replace host-owned operations, redirect undo/restore targets or mutate verified evidence.

Compare (A) framed JSON-RPC, (B) protobuf over protected local IPC and (C) restricted WASI imports. Measure startup latency, steady-state memory, transfer throughput on small and multi-GiB fixtures, serialization overhead, backpressure, crash recovery and attack surface. **Choose based on data**, not aesthetics.

## 6. Compatibility/version matrix

Each surface owns a version and compatibility policy:

| Surface | Owner | Initial policy |
| --- | --- | --- |
| Application version | Release engineering | independent from repository/schema version |
| Portable backup metadata | Trusted Core | known-version read/migration; unknown future fail closed |
| Disk image format | Imaging kernel / 1.9 format contract | explicit reader/writer compatibility, incomplete states |
| BSC object/ref format | BSC kernel | no arbitrary plugin mutation; separate format gate |
| CLI JSON and exit codes | CLI | versioned envelopes and migration notices |
| Event envelope | internal assurance | private in 1.9; not public SDK |
| UI route schema | desktop | legacy route adapter and safe unknown fallback |
| Future SDK/API | platform owner | publish only after independent major gate |
| Future plugin manifest | marketplace | strict parser + signatures + declared host range |
| Future plugin IPC schema | platform owner | negotiated, tested across old/new pairs |
| Marketplace root/targets | distribution trust owner | TUF-class rotation/revocation/rollback policy |

Compatibility **must not** be inferred from semantic-version string alone. A test fixture for each supported format predecessor is required.

## 7. Prototype sequence and exit criteria

| Prototype | Purpose | Demonstrable exit evidence |
| --- | --- | --- |
| P0: architecture inventory | capture call graph, side effects, ownership, test map | repo paths, consumers, build baseline, risky cut points |
| P1: internal read-only adapter | change reader implementation without changing proof | identical evidence on folder/ZIP, malicious path fixtures, limits, cancellation |
| P2: internal storage adapter | two backends with explicit capabilities | strict conditional operations, partial uploads, readback, stale/unknown cases |
| P3: independent recovery | no live DB or network | fresh offline VM restores known official formats, known bad data rejected |
| P4: event envelope | stable internal observable operations | duplicate/cancel/restart matrix, privacy scrub, CLI JSON untouched |
| P5: native worker sandbox | isolate hostile no-op plugin | deny filesystem/credentials/network and survive crash on each OS |
| P6: streaming IPC | extension reads authorized fake fixture | bounded memory/throughput, revocation, worker termination, no escalation |
| P7: signed catalog | trusted package lifecycle | reject tamper/rollback/replay/revoked/expired key; offline disabled state |
| P8: one community pilot | usability/review sustainability | end-to-end review, permission updates, docs, incident drill |
| P9: 2.0 integration decision | platform plus Resilience | user proof, compatibility, packaging, staffing and measured risk |

P0–P4 may inform 1.9.x **only** when accepted by corresponding release gates. P5–P9 are research/post-1.9 by default. Do not make a stable release wait for unrelated POCs.

## 8. Test strategy

### Unit and contract tests
- deterministic canonical path normalization; Unicode/case-variant, traversal, symlink, rooted path and duplicate entry.
- full hash and size, false checksum, late mutation, incomplete read, over-budget file and cancellation.
- operation identity, state transition, plan expiry, changed destination identity and reauthorization.
- backend capability triple-state and explicit unsupported behavior; fault injection on every remote call.
- event ID deduplication, redaction and privacy; no output on disabled hooks.
- serialization unknown mandatory field, oversized/deep payload, malformed UTF-8, protocol downgrade.

### Integration and fault tests
- local filesystem, SMB/NAS, S3-compatible object store (emulated and representative real), B2 and SFTP as support gates require.
- fresh profile and clean machine recovery; machine clock divergence, offline mode, temporary auth failure.
- kill processes during copy, staging, manifest publish, image write, BSC pack, ref CAS, retention preflight and update.
- concurrent BSC clients with network loss, stale lease, competing writers and failed GC/repack.
- restore without original app DB, without optional plugins, with disconnected marketplace and revoked future extension.
- command parity: direct CLI/API and desktop action produce equivalent outcomes and error categories.

### Platform/distribution tests
- Windows installer versus Store package; Windows x64/arm64 only when shipped.
- macOS Intel/Apple Silicon direct DMG; separate App Store feasibility decision if ever pursued.
- Linux AppImage/deb/tar and declared supported architectures with permissions, Wayland/X11, filesystem/mount variants.
- no unsafe sandbox fallback, no hidden root requirement, no spontaneous background worker process.

### Security adversarial fixtures
- malicious plugin attempts arbitrary home directory read, network egress, local socket binding, DB modification, credential extraction, temporary file symlink swaps, CLI command replacement and trusted evidence spoofing.
- malicious catalog: tampered digest, dependency swap, signature downgrade, expired metadata, revoked publisher, rollback and key rotation.
- malicious backup: decompression bomb, path traversal, duplicate entries, encrypted descriptor corruption, oversized manifest.
- missing backup plugin: prove official supported formats are still independently recoverable.

## 9. Qualification thresholds

Do not invent throughput or reliability numbers. Before a POC, record:
- baseline backup/read/verify/restore time, p50/p95 latency, memory, CPU and storage bytes, cold/warm;
- minimum representative dataset and topologies (small many-file, multi-GiB, NAS latency, low disk space, encrypted archive, BSC large history);
- an explicit **zero false-success** policy under seeded corruption, incomplete stages and unsupported formats; report test scope and confidence limits;
- measured recovery correctness on all supported official formats and platforms;
- worker startup/IPC overhead budget only after observing native first-party baseline;
- deterministic deadline, cancellation and retry behavior; cap resource usage;
- reviewer throughput and ongoing support costs before opening submissions publicly.

Approval should be blocked by an unexplained regression, an unsupported platform being silently reported as supported, a security property without a negative test, or loss of emergency recovery. Performance improvement is not a reason to erase verification evidence.

## 10. Rollback and safe migration

- land private interfaces behind existing behavior; preserve old implementation until equivalence tests pass;
- migrate one workflow, not all backup categories at once;
- never rewrite user repositories just to align in-memory abstractions;
- snapshots and config upgrades require explicit schema decision plus independent restore fixture;
- retain emergency read-only access and a last known good direct installer;
- revert code and UI routes without deleting or reinterpreting repository bytes;
- ensure a future plugin version rollback cannot downgrade trust policy or forget a revocation.

## 11. Release gate checklist template

```text
[ ] Customer outcome and existing ROADMAP ticket named
[ ] Baseline / proof of need and consumers documented
[ ] Security authority: read/write/secret/verify/authorize separated
[ ] Unchanged format, metadata and CLI behavior or approved migration
[ ] Supported platform and packaging impact inventoried
[ ] Cancellation, corruption, contention and offline fixtures pass
[ ] Performance regression within measured and approved budget
[ ] Clean-machine recovery and rollback remain possible
[ ] Docs + code + tests updated together; no public SDK promise
[ ] Explicit owner approves scope, ID, milestone, branch and release gate
```

## 12. Maintainer artifacts on acceptance

For each accepted technical ADR: commit ID/PR, affected tests and their runs, documentation changes, owner, package matrix, deprecation notice, blast radius, emergency procedure, and measurable results. Rejected choices stay documented in [DECISION_REGISTER.md](DECISION_REGISTER.md). Until that evidence exists all structures in this RFC are **proposal-only**.
