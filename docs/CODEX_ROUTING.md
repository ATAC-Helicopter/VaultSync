# Codex routing for VaultSync

This is a repository-scoped configuration for Codex. It introduces **subtask-level delegation in a single prompt**, not automatic mutation of the coordinator's model mid-turn.

## Default and limits

- Main coordinator: `gpt-6.1-terra` / `high`.
- Default untyped subagent: `gpt-6.1-terra` / `medium`.
- Custom roles: `vaultscan` = Luna Low; `vaultui` = Terra Medium; `vaultworker` = Terra High; `vaultverify` = Terra Medium; `vaultcritical` = Sol Medium.
- Up to three open subagent threads in each session. More subagents often consume **more** quota; only delegate when the task benefits.
- Highest permitted choice by project policy: **Sol Medium**. This is a **soft policy**, not a technical restriction on manual model selection, CLI flags, or a client that ignores project instructions. If hard enforcement is needed, use an external gateway/provider allowlist.

## Example: mixed backend and UI in one request

Prompt:

> Add a recoverability diagnostics worker and show its progress in the Avalonia UI. Include cancellation, errors and tests. Delegate independently where possible; never exceed Sol Medium.

Codex should first agree on the progress/result/cancellation contract and distinct file ownership. It can then assign:
1. `vaultcritical` to recovery data correctness and worker operations.
2. `vaultui` to views, binding and presentation of the contract.
3. `vaultverify` to targeted tests after the interface is stable.

The coordinator integrates the change and checks the end-to-end behavior. For a noncritical backend worker, use `vaultworker` instead of Sol. UI work need not inherit Sol just because a related Core part is critical.

## Setup and checks

1. Use Codex CLI **0.159.0 or later** for GPT-6.1 Terra, signed in with a model-enabled account.
2. Pull the commit containing `AGENTS.md` and `.codex/`.
3. Open VaultSync as the primary workspace and trust this repository so project-scoped `.codex/config.toml` is loaded. Existing CLI/user command overrides may take precedence.
4. **Start a new Codex session** from the repo root. Check `/status` and ask: `List the VaultSync subagent roles, their models and reasoning ceilings from the project configuration without making any edits.`
5. Try a read-only rehearsal: `Inspect the recovery service and the Avalonia recovery UI using suitably priced subagents; report the separation of work, no edits.`

Only instructions and config files change; no production code is touched. The routing outcome is **not verified in a live Codex session** by this repository change.

Reference: https://learn.chatgpt.com/docs/agent-configuration/subagents

## Integration with repository rules — VS-1980 / #673

Root [AGENTS.md](../AGENTS.md) is the entry point for every agent, including
single-task and resumed work; directory instructions may add constraints.
The existing release, approval, issue/Project tracking, localization, privacy,
provenance and [workspace cleanup](VALIDATION_WORKSPACES.md) gates remain in force.
Custom role files repeat this requirement and narrow the assigned scope only.
Other clients must use the same safety/ownership contract and disclose unsupported
routing rather than pretending that these TOML defaults changed their model.

The coordinator assigns each subtask its owning issue/release, interface contract,
disjoint file set, validation criteria and private workspace budget. A verification
agent writes tests only in explicitly assigned paths. Subagents report evidence,
limitations and cleanup; the coordinator retains tracking, commits, integration
and release responsibility unless a bounded part is explicitly delegated.
No role configuration grants permission to overwrite host devices or bypass reviews.

PR #820 is integrated into the existing 1.9.0 preparation through a merge that
preserves its source commit and the full existing AGENTS.md. This is not a merge
of the release or routing PR into protected Dev. Such integration still needs
its required reviews and exact-head checks. Use Refs #673 in release-gated work.

The three-child limit and model ceiling are both operating policy. The local
configuration requests the thread cap in supporting clients; other clients may
expose different capabilities. No provider-side spending guarantee is asserted.
The running coordinator cannot hot-switch its model. A read-only vaultscan
rehearsal using Luna Low has actually completed in this session; it inspected
integration risks without edits or tests. This does not prove that every custom
role was discovered by every client. Check a fresh trusted session for discovery.

The integrated six TOML files passed syntax parsing with an already installed
TOML parser. The installed 0.164.0-alpha.11.1 CLI accepted `codex features list`
with this project configuration. A separate vaultverify rehearsal using Terra
Medium checked the role models/efforts and inherited rules without edits or
repeating builds. Custom-role discovery in a newly started trusted client is
still a separate check; these rehearsals used explicit role-equivalent model
and instruction settings, not a claim of automatic role-file loading.
