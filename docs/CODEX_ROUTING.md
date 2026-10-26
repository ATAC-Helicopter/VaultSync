# VaultSync Codex adaptive model routing

## Objective

Spend **less of the ChatGPT Plus Codex allowance per *correct, validated feature***, without under-selecting models for dangerous or ambiguous work. This configuration is a heuristic, not an automatic token quota meter or guaranteed savings. Subagents also incur their own context and token usage.

## Configurations

| Role | Model | Effort | Selection |
| --- | --- | --- | --- |
| Root coordinator | gpt-6.1-terra | medium | Default; completes simple requests on its own |
| vaultscan | gpt-6-luna | low | Only useful, isolated read-only research |
| vaultui | gpt-6.1-terra | medium | Substantial but routine presentation |
| vaultworker | gpt-6.1-terra | high | Medium-risk substantial coding and complex UI |
| vaultcritical | gpt-6.1-sol | medium | Critical data safety or hard architectural decisions |
| vaultverify | gpt-6.1-terra | medium | Noncritical, nonredundant independent test work |

The project config enables subagents, but allows only **two simultaneously spawned agents**, excluding root. This is a concurrency ceiling, **not** an instruction to spawn two per task. In particular: a one-file harmless edit should stay in root with **zero** subagents.

Model choice is based on **blast radius + uncertainty + interdependencies + available regression checks**, not simply folder names. A one-line change to backup deletion can be more dangerous than a new UI screen. A tricky async ViewModel often warrants Terra High. Vaultcritical is chosen **before** changing risk-critical data paths.

### Mixed feature: a backend worker plus its UI

User asks: "Add a progress-reporting recovery worker and display its state/cancellation/errors in the Avalonia UI. Build it and verify the feature."

Expected flow, all within **one prompt**:
1. Root identifies expected contracts, touched files and safety invariants without a costly duplicate scan.
2. Recovery semantics and worker: **vaultcritical / Sol Medium**, because data correctness matters.
3. Independent AXAML/UI presentation: **vaultui / Terra Medium**, once progress/error/cancel contract is agreed. These two may run in parallel if write scopes are disjoint.
4. Integrate and run focused tests in root. Do **not** spawn a third agent just to run `dotnet test`. If test design itself is difficult, return critical tests to vaultcritical or delegate independent noncritical tests to vaultverify sequentially.

For a noncritical background service, use **vaultworker / Terra High** for the backend instead. For a simple UI fix, use root only. For complex presentation concurrency, hand the affected part to vaultworker. The model used by one piece never forces unrelated subtasks onto the same expensive tier.

### Avoiding false savings

- A cheap worker that makes incorrect changes is not cheap once retries, follow-up debugging and user verification are counted.
- Don't fan out for short tasks or duplicate scans. Prefer precise file ranges and small handoffs.
- Never "test" a risky destructive change on actual drives or live backup destinations.
- Avoid all-or-nothing escalation. Escalate only the critical *slice* to Sol Medium.
- No automatic multi-agent retry loops. On first meaningful failure, diagnose evidence; escalate if uncertainty or severity warrants it.
- If a task is too risky or ambiguous even for Sol Medium, request stronger evidence/tests rather than overriding the user cap.

## Activating and checking

1. Use the owning preparation head from `docs/RELEASE_FAMILY_1.9.md`; active 1.9.0 uses `work/1.9-roadmap-realignment` / #736. Protected Dev integration still requires its reviews; do not switch to its older tree or merge the release merely to activate routing.
2. Open the project as a **trusted** workspace in a supported Codex client. Start a **new** Codex session after merging; project `.codex/config.toml` is loaded at startup, subject to higher-precedence client/user overrides.
3. Confirm active root model and reasoning with `/status` if available; expect **Terra Medium**. Review custom agents under `.codex/agents/`.
4. Run `python3 scripts/verify_codex_routing.py` to check static config, models and Sol ceiling.
5. Try a dry, read-only prompt: `Explain how you would split a recovery worker plus UI and tests using the repository roles, without modifying files. Minimize agent overhead; no model above Sol Medium.`

**Limitations:** Runtime delegation depends on the installed Codex client supporting repository agents and on signed-in model availability. A fixed coordinator **does not** dynamically change its own model mid-turn: the multi-model effect is from independently configured subagents. The cap is a **soft agent policy**; the only hard model/effort limits here are the configurations of the provided roles. Provider-side enforcement would need external restrictions. The PR has no impact on VaultSync production functionality.

## Measuring improvement, not guessing

Use the same five representative VaultSync tasks with the previous Sol Medium setup and this routing. For each record whether acceptance criteria passed on the **first meaningful attempt**, tests, manual correction time, elapsed time and Codex allowance used (`/status` or your usage screen if exposed). Keep the profile only if it passes the **same quality bar** and makes the allowance go further. Do not claim a percent reduction without measuring it.

Docs: [Codex subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents), [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

## Shared repository gates and measured availability — 2026-10-26

Every agent reads [root AGENTS.md](../AGENTS.md) and applicable directory rules;
roles narrow scope and never replace release ownership, tracking, localization,
signing, privacy, approvals or [workspace cleanup](VALIDATION_WORKSPACES.md).
This includes directly invoked, resumed and single-task agents. The coordinator
assigns interfaces, exact disjoint write sets, acceptance and an owned workspace
budget, then integrates and updates tracking. Unsupported clients must disclose
routing limitations rather than claiming automatic model selection.

Both source revisions from #820 are retained by merge commits in sole release
preparation #736. The update supersedes the initial three-child/Terra High defaults
with two children/Terra Medium. Prior explicit Luna Low, Terra Medium and Sol
Medium rehearsals remain historical. The current client now exposes all five
custom roles; the root model of an existing turn still cannot hot-switch.
No protected Dev merge, release acceptance or provider spending enforcement is
implied by configuration discovery.

Local session metadata can expose cumulative input, cached-input, output and
reasoning-output tokens. Cached input is a subset of input; reasoning output is
a subset of output. Never add those subsets twice. Keep counters, session/task
boundaries, snapshot time and active model/effort in a private measurement ledger.
Deduplicate resumed counters and record counter resets instead of adding snapshots.
Do not commit raw session logs, prompt text, account metadata or quota information.

The three earlier rehearsal sessions are different tasks/lengths, not a matched
before/after sample. Their counters do not prove savings. The available metadata
does not expose actual Plus allowance debit or per-model quota weights. Thus token
changes and plan allowance savings are separate claims. Use naturally occurring
comparable work and acceptance/rework outcomes; avoid paid duplicate benchmark
runs merely to demonstrate savings. Report no percentage until comparable measured
samples exist. The next clean-session Terra Medium baseline must be checked in
the client rather than assumed from this TOML update.
