# RFC-P05 — Platform Architecture Decision Register

**Status:** Research-only. Decisions listed as preferred are **not approved for implementation**.  
**Issue:** [#817](https://github.com/ATAC-Helicopter/VaultSync/issues/817).  
**Base:** `release/1.9.1`; no changes to 1.9.0.

## Decision levels

- **Existing product contract**: already supported by current documentation/code and its release gate.
- **Preferred hypothesis**: recommended direction, requires evidence and maintainer ADR.
- **Open**: alternatives have not been qualified.
- **Rejected as unsafe**: violates invariant; reconsider only if explicit new evidence changes risk assessment.
- **Approved**: requires its own dated ADR, owning roadmap ticket and qualifying test reference. No new approvals are created by this RFC.

## Register

| ID | Decision / problem | Present disposition | Required evidence / owner |
| --- | --- | --- | --- |
| PA-001 | Should 2.0 replace planned Resilience? | **No. Preferred:** platform serves Resilience product outcome | user validation and pre-existing 2.0 gate; product owner |
| PA-002 | Which services form the trusted kernel? | **Preferred:** identity, proof, retention, restore authorization and format writer | code call graph and threat review; Core owner |
| PA-003 | Public SDK before 1.9 LTS? | **Rejected for 1.9.x** | requires public ABI, staffing, compatibility and security plan |
| PA-004 | In-process arbitrary DLL plugins? | **Rejected as unsafe** | `AssemblyLoadContext` grants no security restriction |
| PA-005 | Out-of-process workers? | **Preferred candidate**, not selected | OS sandbox prototype on all supported platforms |
| PA-006 | WebAssembly/WASI runtime? | **Open** as limited extension alternative | streaming benchmark, isolation audit, developer ergonomics |
| PA-007 | Extension transport (stdio JSON-RPC / pipe protobuf / WASI)? | **Open** | benchmark and parser/adversarial tests |
| PA-008 | First API category to publish? | **Preferred:** read-only redacted reports | independent worker and privacy qualification |
| PA-009 | Third-party storage writer support? | **Open / high-risk** | credential broker, remote semantics, clean-machine restore |
| PA-010 | Third-party disk imaging, wipe or BSC GC? | **Rejected for ordinary community plugins** | privileged operation may only be first-party audited |
| PA-011 | Full arbitrary UI injection? | **Rejected for first external SDK** | declarative panels first, UI accessibility/security POC |
| PA-012 | Backend capability representation? | **Preferred:** proven/unknown/unsupported | 1.9.3 real provider comparison |
| PA-013 | Plugin-owned backup formats? | **Rejected as default** | missing plugin would endanger independent recovery |
| PA-014 | Event delivery semantics? | **Open** | 1.9.5 replay/dedup and privacy tests |
| PA-015 | Extension package signature protocol? | **Preferred:** TUF-class trust separation | root lifecycle, compromised publisher drill, implementation audit |
| PA-016 | Distribution variants? | **Preferred:** documented channel-specific feature matrix | platform policy/legal review and package tests |
| PA-017 | Marketplace hosting/control plane? | **Open**; no mandatory cloud account | costs, maintenance, privacy review, offline behavior |
| PA-018 | Marketplace billing/revenue? | **Deferred** | only after free curated catalog operationally sustainable |
| PA-019 | Versioning and compatibility periods? | **Open** | 1.9.8 LTS contract, migration fixtures, reviewer capacity |
| PA-020 | Which release owns platform foundations? | **Proposed** in RFC-P02 | approval per release, ticket and customer outcome |
| PA-021 | Branch/roadmap authority (Dev vs release branches)? | **Open operational discrepancy** | compare history, reconcile via regular reviewed PR |
| PA-022 | Review staffing and incident SLAs? | **Open** | maintainers, hours, triage policy and revocation drills |

## Architecture Decision Record (ADR) template

Use `docs/platform/adr/ADR-XXXX-short-name.md` only after the owning decision has real evidence.

```markdown
# ADR-XXXX — Title

Status: Proposed | Accepted | Superseded | Rejected
Date:
Decision owner:
Roadmap issue / release:
Affected components:
Compatibility surfaces:
Threat model / trust boundary:
Context and constraints:
Alternatives considered:
Decision and why:
Consequences / tradeoffs:
Migration and rollback:
Required negative and fault tests:
Windows/macOS/Linux packaging evidence:
Metrics and acceptance criteria:
Approver and references:
Supersedes / superseded by:
```

A research RFC is not an ADR approval. Naming a port does not create a public supported interface.

## Questions to revisit at each release gate

**1.9.1:** Has typed routing reached parity without forcing plugin navigation abstractions? Is clone inspection safely reusable?

**1.9.2:** Can an offline clean machine recover all claimed first-party formats independently? Which parts of the new reader may be private contracts?

**1.9.3:** Do remote providers support the exact consistency and conditional-write semantics needed? What do we do when one does not?

**1.9.4:** Do shared evidence types preserve format-specific limits and unknown/stale states? Has UI/accessibility parity passed?

**1.9.5:** Does the event envelope stay local, optional, redacted and deterministic under crash/replay? Are automation hooks bounded and opt-in?

**1.9.6–1.9.7:** Can BSC tolerate competing writers, interrupted publication and reconstruction with no dynamic plugin dependency?

**1.9.8:** Which private contracts demonstrably stabilized? Do not claim third-party support merely because the interfaces exist.

**Post-1.9:** Are isolation, recovery independence, catalog root trust, reviewer operations, distribution channels and Resilience user outcomes qualified together? If not, a compatible 1.10 is more defensible than an unearned 2.0.

## Study maintenance procedure

1. Keep the evidence date, repository ref, test run and external standards version alongside claims.
2. Record overturned hypotheses here instead of silently rewriting history.
3. Keep new 1.9 tasks out of ROADMAP until the milestone, IDs, scope and regression gates are reviewed.
4. Update all linked RFC documents when a decision changes.
5. Convert only approved and release-relevant internal seams to tracked work; defer public plugin tooling to a separate major-version gate.
6. Do not infer user permission to deploy, install an extension, or change existing backups from the approval of an ADR.

## Next review priority

**First:** resolve PA-021 (branch authority) and PA-002 (real code boundaries).  
**Second:** verify 1.9.2 portable recovery and 1.9.3 provider semantics before publishing any plugin-shaped API.  
**Third:** prototype one hostile read-only worker and exercise token/OS denial.  
**Finally:** only with operational evidence consider marketplace user experience and 2.0 commitment.
