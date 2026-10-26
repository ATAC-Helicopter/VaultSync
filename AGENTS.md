# VaultSync Codex: quality-first quota efficiency

This file governs repository work in Codex. Implement complete requests without unnecessary delegation. The goal is **the lowest total usage that produces correct, tested code**, not the cheapest model on every isolated step.

## Non-negotiable ceiling and project rules

- Never select a model stronger or more expensive than **GPT-6.1 Sol / medium** for this project. Specifically do not invoke Sol high/xhigh/max or Astra.
- This is a cooperative project policy, **not** an enforced account/provider cap. Explicit client or user overrides can bypass it.
- Root defaults to GPT-6.1 Terra / medium; choose a stronger specialized subagent **before** risky work. Root itself cannot change model midway through an active turn.
- Respect repository boundaries: `src/VaultSync.Core`, `src/VaultSync.CLI`, `src/VaultSync.UI` (Avalonia AXAML), `tests/VaultSync.Core.Tests`. Preserve data integrity, backward compatibility, security and recoverability.
- Never run destructive tests on real disks, live backups or user data without explicit authorization. No unsafe "quick fixes" to verification, recovery or security.

## Route by actual risk and complexity, not by keywords alone

Make a brief internal risk assessment from expected touched paths, invariants, dependencies, ambiguity and available tests. You do not need to publish a verbose planning preamble.

| Route | When |
| --- | --- |
| **Root Terra Medium only (zero subagents)** | Readme/docs, tiny well-specified edits, ordinary presentation tweaks, obvious localized fixes, quick diagnosis. This is the default for small tasks. |
| **vaultscan: Luna Low** | Independent *read-only* narrow discovery when offloading a nontrivial investigation saves root context; not for one grep or known file. |
| **vaultui: Terra Medium** | Substantial but straightforward AXAML, styling, binding or presentational ViewModel changes in a mixed request. |
| **vaultworker: Terra High** | Noncritical multi-file changes, tricky async UI state, nontrivial service integration, debugging requiring careful reasoning or regressions. |
| **vaultcritical: Sol Medium** | Any change with credible risk to backups/restores, actual disk writes/imaging, retention/deletion, encryption/keys, repository formats/migrations, cross-machine safety, recovery confidence or security guarantees. Also use for ambiguous high-coupling architecture and correctness-sensitive cross-layer decisions. Choose proactively, not only after failure. |
| **vaultverify: Terra Medium** | A genuinely independent focused test/integration check that saves time or context. Do not delegate routine test commands; safety-critical verification belongs to vaultcritical. |

If a simple-looking fix changes a critical invariant, **risk wins** and vaultcritical owns that slice. If a UI task has tricky multi-module state handling, upgrade from vaultui to vaultworker. If the low-tier agent is uncertain or cannot test its assumptions, escalate the specific slice; do not repeat the same failed prompt or accept unverifiable code.

## Multi-part requests in one turn

- Decompose a mixed request into backend, UI, tests **only when the boundaries matter**. Agree on DTOs/events/progress/error/cancellation contract first.
- For `worker + UI`: assign backend to vaultworker **or** vaultcritical depending on risk, UI to vaultui if independent, then let root integrate and run checks. For complex UI use vaultworker; serialize overlapping file writes.
- Spawn at most **two** specialists concurrently, and only if independence or separated context meaningfully improves quality or total work. Sequential delegation is valid within the same prompt.
- The main agent should handle simple changes and routine verification itself. No automatic "scan + implement + verify" fan-out for small tasks; no extra reviewer unless the outcome is genuinely uncertain or high-risk.
- Use disjoint edit ownership, compact handoffs with exact paths/contract, and reuse evidence. Never duplicate repository scans or large-context reviews without a new reason.
- Do not let low-cost agents touch a safety-critical slice on the assumption that a later review will catch mistakes.

## Verification and delivery

- Prefer existing patterns; make the smallest correct diff; avoid speculative refactoring and unrelated formatting.
- Run the narrowest useful build/test first; expand when integration risk or failures justify it. For Core use `dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj` as appropriate; validate Avalonia compilation if UI changed.
- Do not claim tests passed unless they ran. Do not run unsafe real-device tests.
- Finish the whole user request, including integration, then report result, checks and meaningful uncertainty concisely. State subagent tiers only when one was actually used.
- If the requested result cannot be delivered safely under Sol Medium, report the limitation and required verification instead of silently under-selecting or violating the ceiling.
