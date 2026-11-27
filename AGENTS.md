# VaultSync: Codex working guide

Keep work scoped to the request and current branch. Preserve existing C#/.NET/Avalonia conventions, compatibility, and safe backup behavior. Inspect the working diff before edits; do not overwrite unrelated work.

## Find only relevant context
- Use Graphify first for indexed code structure, as specified below. For literal text or non-indexed content, use scoped `rg` / `rg --files` and bounded reads. Avoid whole-repo scans, repeated logs, and mandatory document preloads.
- Consult `CONTRIBUTING.md` for IDs, issue tracking, changelog, and delivery; `ROADMAP.md`, `release/1.9-family.json`, and `docs/RELEASE_FAMILY_1.9.md` for release ownership, only when relevant. Read the affected domain contract before changing its behavior.
- Prefer existing patterns and tests. Make the smallest coherent change; avoid unrelated refactors.

## Safety and compatibility
- Never write to or destructively test real sources, backups, destinations, host disks, or user data without explicit authorization. Use disposable fixtures for recovery checks. A synthetic test or successful build is not proof of installed recovery or release qualification.
- Preserve backup/restore semantics, data formats, cancellation, failure handling, and security boundaries. Localize changed user-facing strings in all maintained locales; follow `docs/LOCALIZATION.md`.
- Respect protected branches, review/CI gates, release signing (`docs/RELEASE_SIGNING.md`), and required evidence. Do not merge, publish, or claim support without qualification.
- Reuse the owning issue and canonical `VS-`/`BUG-` ID for meaningful work. Update relevant release/changelog/tracking records; use `./dev` when available. Classification and merge policies are in `docs/ENGINEERING_WORKFLOW.md`: preserve commits with merge commits on non-Stable branches; on Stable use first-parent release merge commits only.

## Verification and handoff
- Review the diff. For docs/format-only changes, skip code tests unless a specific gate requires them.
- For behavior changes, run the smallest relevant tests and affected build/check at a meaningful checkpoint. Broaden for shared/core changes, migrations, security, release gates, or failures; avoid default full-suite runs and redundant retries.
- Keep tool output bounded. Report changes, exact checks run or skipped, and remaining risks. A passing test summary is not an accepted build if the process exits nonzero; serialize MSBuild when outputs collide. Never claim an unrun check passed.

## Active 1.9.0 recovery work
`work/1.9-roadmap-realignment` is the assembled 1.9.0 head and sole draft release PR #736 into `Dev`; `release/1.9.0` is a protected release identity, not the active preparation tree. Use `docs/RELEASE_1.9.0.md`, `docs/RELEASE_FAMILY_1.9.md` and the relevant recovery protocol as needed. Keep later 1.9.x features out of this binary.

The approved joint recovery direction is recorded at `docs/release-evidence/1.9-joint-architecture-approval-2026-10-20.md`; do not reopen that settled direction. It does **not** authorize real host-disk writes, publication or unsupported OS/profile qualification. Native storage/admission fixtures must meet their kernel/mount/inode proof contracts; synthetic or disposable-VM results are not installed boot or system recovery evidence. Follow `docs/RECOVERY_ENTRY_1.9.md` and `docs/VALIDATION_WORKSPACES.md`; clean only proven run-owned artifacts.

All betas retain Windows, macOS and Linux desktop support; new system-recovery qualification is staged Linux GPT/UEFI ext4, then Windows NTFS, then macOS APFS (see `docs/BETA_PROGRAM_1.9.0.md`). Beta diagnostics follow `docs/PRODUCT_DIAGNOSTICS.md`; never broaden consent or upload user paths/data. Preserve issue/PR #736 ownership and verified evidence. Do not merge or promote until all required release gates pass.

## Ponytail (optional Codex plugin)
Use the upstream `DietrichGebert/ponytail` Codex plugin when installed, preferring existing code and the smallest complete, readable change. Do not vendor its global rules, duplicate this file, add speculative abstractions or weaken scoped verification. Project safety, recovery authorization, compatibility, localization, provenance and release gates above always take precedence over minimal-diff preferences. For sensitive changes, review the full affected call path and run risk-appropriate regression/security checks, even if this requires more code or tests. Without the plugin these repository instructions remain sufficient. Install per upstream `INSTALL.md`; review third-party lifecycle hooks before enabling them.

<!-- graphify-rules-start (managed by `graphify init`) -->
## Use Graphify before grep

This repository is indexed by Graphify: a code graph over its call, dependency, and test structure, exposed through a connected Graphify MCP server. Before reaching for grep or reading files, use the Graphify tools your MCP client lists (their exact names and descriptions are in the server's tool list) for what the graph knows and a text search does not:

- find where a symbol, function, or class is defined (instead of grepping for it)
- understand how something works, or where a behavior is handled
- find who calls a function, or what it calls
- see what a change affects (its blast radius) and which tests cover it
- map a file's dependencies and dependents

Fall back to grep or file reads only for what the graph does not model: literal string or comment matches, non-indexed files, or reading a file you have already located. If no Graphify tools are listed, check the MCP server connection.
<!-- graphify-rules-end -->
