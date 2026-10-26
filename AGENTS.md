# VaultSync Codex routing

Goal: minimize **total Codex Plus usage per correct, tested change**. Do not under-select for data safety. More agents are not automatically better.

**Ceiling:** never choose more than `gpt-6.1-sol` / `medium` (**GPT-6.1 Sol / medium**). No Sol high/xhigh/max or Astra. This is an instruction, **not** hard enforcement against client/user overrides. Root defaults to `gpt-6.1-terra` / `medium`; it cannot swap its own model mid-turn, but subagents can use distinct configurations.

## Pick the route from risk, uncertainty and coupling

- **Root alone, zero subagents:** small docs/fixes, routine UI, obvious isolated changes, routine test commands. Default for short work.
- **`vaultscan` (Luna Low):** independent, sizeable read-only investigation; not a single search.
- **`vaultui` (Terra Medium):** routine substantial Avalonia AXAML, styles, presentation and bindings in multi-part requests.
- **`vaultworker` (Terra High):** tricky UI async logic, substantial noncritical Core/CLI or multi-file integration/debugging.
- **`vaultcritical` (Sol Medium):** any credible risk to backup/restore, disk writes/imaging, data deletion/retention, encryption/keys, repository formats/migrations, cross-machine identity, recoverability or security; also ambiguous high-coupling architecture. Escalate **before editing**, even for tiny changes.
- **`vaultverify` (Terra Medium):** genuinely separate noncritical tests/reviews, never redundant routine checks. Critical invariants stay with `vaultcritical`.

If a cheap tier is uncertain, finds surprising coupling, cannot prove assumptions or repeatedly fails, escalate only that slice. Do not retry identical prompts. If Sol Medium is insufficient, report unverified risks rather than quietly downgrading or violating the cap.

## Multiple subtasks within one user request

Define interface, progress, cancellation, error and state contracts **before** independent edits. For worker + UI, route backend to `vaultworker` or `vaultcritical` depending on risk and presentation to `vaultui` (or `vaultworker` if complex). Run routine integration/tests in root. Spawn **at most two** specialists concurrently and only if independent work saves time/context; otherwise delegate sequentially or use root. Keep write sets disjoint, handoffs precise, and avoid duplicate scans/reviews. Finish the **entire prompt**, not just one subsystem.

## Engineering safety

Preserve existing C#/.NET and Avalonia conventions, compatibility and safe backup semantics. Paths: `src/VaultSync.Core`, `src/VaultSync.CLI`, `src/VaultSync.UI`, `tests/VaultSync.Core.Tests`. Never run destructive tests on user backups, drives or production data without explicit authorization. Make the smallest correct diff and add meaningful regressions. Run focused checks first (e.g. `dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj`), broaden for cross-module risk, and verify UI build when relevant. Never claim unrun tests passed. Conclude with actual changes, executed checks and remaining uncertainty; name agent tiers only if used.

Detailed scenarios and measurement procedure: `docs/CODEX_ROUTING.md`.
