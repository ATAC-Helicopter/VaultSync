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
