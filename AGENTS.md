# VaultSync: Codex routing and engineering guidance

This repository uses an economical multi-agent policy. Keep developer control of data-safety decisions.

## Model and reasoning ceiling

- **Absolute routing policy for this project:** highest permitted configuration is `gpt-6.1-sol` at `medium`. Never request, spawn or recommend Sol at high/xhigh/max, or another more expensive model as a workaround.
- Prefer `gpt-6-luna` for safe lightweight inspection and `gpt-6.1-terra` for implementation, at the lowest reasoning effort adequate for each bounded task. The coordinator defaults to Terra High, while subagents are configured independently.
- Available specialist roles in `.codex/agents/`: `vaultscan` (Luna Low, read only), `vaultui` (Terra Medium), `vaultworker` (Terra High), `vaultverify` (Terra Medium), `vaultcritical` (Sol Medium).
- **Important:** these are agent instructions and defaults, not a provider-side quota or an enforceable model ceiling against user/client overrides. Do not claim otherwise.
- Avoid agent spawning for trivial, dependent or tiny work. Maximum three simultaneously open subagents. Reuse completed agent results; do not duplicate searches or run redundant reviews.

## Route every multi-part request by subtask, including within a single prompt

1. Briefly identify components, touched file scopes, data-integrity risks and acceptance criteria. Skip elaborate planning on trivial tasks.
2. For a mixed feature (e.g. backend worker + UI + tests), identify the public interface / payload / events / state contract **before** parallel work. Assign each edit owner a disjoint write set.
3. Select agents per subtask rather than applying the highest tier to the entire prompt:
   - Simple inventory, read-only investigation or docs lookup: `vaultscan`.
   - Avalonia AXAML, Views, Styles, presentation-only ViewModels: `vaultui`.
   - Ordinary Core/CLI implementation, safe services and contained bug fixes: `vaultworker`.
   - Risky Core services, backup, restore, recovery, disk imaging, encryption, retention, migrations, destructive or compatibility-sensitive changes: `vaultcritical`.
   - Targeted tests, independent validation and integration checks: `vaultverify`.
4. Work in parallel only when tasks are genuinely independent and write scopes do not overlap. Otherwise run them sequentially under the same user request. The coordinator integrates the full feature and runs appropriate tests. Do not stop after finishing only backend or only UI.
5. If a Terra worker discovers that its scope is actually critical, stop that portion and delegate to `vaultcritical`; keep independent UI, docs or tests on Terra. Never increase Sol above Medium.
6. Report which parts used which role (model/effort), final tests and any unverified or unsafe areas. Never claim an autonomous model switch occurred in the active coordinator turn; subagents have independent configurations.

## Safety and quality rules

- VaultSync protects user data. Preserve backup/restore correctness, recoverability guarantees, encryption guarantees, version compatibility and fail-safe behavior.
- Never run destructive tests on real disks/devices, blindly delete backups or weaken verification. Require explicit user authorization for actions that could modify production/user data.
- Match existing C#/.NET patterns. Relevant locations: `src/VaultSync.Core`, `src/VaultSync.CLI`, `src/VaultSync.UI` (Avalonia AXAML), `tests/VaultSync.Core.Tests`.
- For meaningful changes add or update regression tests. Prefer a focused `dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj` when available; validate build/UI separately where feasible. If tools/OS block a check, state the limitation rather than claiming success.
- Respect user requirements and existing architectural boundaries. Avoid incidental refactors, broad formatting or unrelated file edits.
