# VaultSync Platform Research — documentation index

**Status:** Proposed research, not an approved architecture or shipped feature.  
**Research issue:** [#817](https://github.com/ATAC-Helicopter/VaultSync/issues/817)  
**Planning base:** `release/1.9.1`, 2026-10-25.  
**Scope exclusion:** `1.9.0` is frozen for this research. No changes to its roadmap, implementation, beta, gate, or release contract.

## Purpose

Explore a future modular VaultSync platform and a curated extension ecosystem while preserving the product's core promise: stored bytes remain verifiable and recoverable independently of an extension host, an online marketplace, or the original installation.

These documents are **research and acceptance proposals**, not work items. Only `ROADMAP.md` can approve delivery scope and IDs. Nothing here grants a public extension API, a supported plugin runtime, or a commitment that VaultSync 2.0 will ship. The existing `2.0 Resilience` concept remains subject to its formal decision gate.

## Reading order

1. [PLATFORM_ARCHITECTURE_RFC.md](PLATFORM_ARCHITECTURE_RFC.md): boundaries, layering, extension models, compatibility, design alternatives, legacy migration and candidate 2.0.
2. [FOUNDATIONS_1_9.md](FOUNDATIONS_1_9.md): narrow, qualified internal foundations proposed across 1.9.1–1.9.8, without modifying 1.9.0.
3. [EXTENSION_SECURITY.md](EXTENSION_SECURITY.md): adversaries, process isolation, permission brokerage, credentials, supply-chain trust, review/revocation, platform distribution.
4. [CONTRACTS_AND_QUALIFICATION.md](CONTRACTS_AND_QUALIFICATION.md): draft data contracts, failure semantics, evidence, IPC, testing matrix, migration, decision gates and prototype order.
5. [DECISION_REGISTER.md](DECISION_REGISTER.md): alternatives, open architecture decisions, ADR protocol and discovery questions.

## Canonical references, not superseded

- [Roadmap](../../ROADMAP.md) — delivery authority and immutable IDs.
- [Recovery Horizon and Resilience Strategy](../RECOVERY_HORIZON_STRATEGY.md) — 1.9 sequence and 2.0 decision gate.
- [1.9 route architecture](../ROUTE_ARCHITECTURE_1.9.md) — VS-1910/VS-1915 migration boundaries.
- [Repository formats](../REPOSITORY_FORMATS.md), [Cross-machine safety](../CROSS_MACHINE_SAFETY.md) and [Recoverability engine](../RECOVERABILITY_ENGINE.md) — integrity, evidence and recovery safety.
- [Binary Source Control strategy](../BINARY_SOURCE_CONTROL_STRATEGY.md) — independent repository model; not a plug-in format.
- [Updater](../UPDATER.md), [Releasing](../RELEASING.md), [Privacy](../PRIVACY.md), [Security](../../SECURITY.md).
- [1.9.0 release contract](../RELEASE_1.9.0.md) — reference only; remains unchanged.

## Scope control

An internal port, service boundary, value object or test contract can be adopted by a 1.9.x release only if it advances that release's **existing** customer outcome, passes regression qualification, and does not require new third-party execution privileges. Otherwise move it to research or post-1.9.

**Not in 1.9.x by default:** public plugin SDK, extension host for untrusted code, arbitrary assembly loading, remote code deployment, Marketplace storefront, developer accounts, ratings, extension billing, signed-plugin root-key distribution, third-party UI injection.

## Review checkpoints

- After 1.9.0 beta qualification: re-evaluate the approved 1.9.1 seam inventory, without changing 1.9.0.
- Prior to each 1.9.x architecture gate: propose only the smallest independently valuable internal contracts, with baseline behavior and rollback tests.
- Before a 2.0 major-version decision: prove a minimal out-of-process extension prototype, offline recovery, one real provider, least privilege per platform, distribution compliance, and revocation.
- Revise this RFC when tests overturn assumptions; do not backdate decisions as if implemented.

## Evidence notation

- **Observed:** from a named repository path/branch, not necessarily fully audited.
- **Proposed:** architectural intent subject to approval.
- **To verify:** requires code, test, legal/policy, or packaging evidence.
- **Rejected:** documented reason and revisit conditions.

The platform team must not report a verification as passed merely because this RFC proposes it.
