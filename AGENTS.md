# VaultSync: Codex working guide

Keep work scoped to the request and current branch. Preserve existing C#/.NET/Avalonia conventions, compatibility, and safe backup behavior. Inspect the working diff before edits; do not overwrite unrelated work.

## Find only relevant context
- Use scoped `rg` / `rg --files` and bounded reads. Avoid whole-repo scans, repeated logs, and mandatory document preloads.
- Consult `CONTRIBUTING.md` for IDs, issue tracking, changelog, and delivery; `ROADMAP.md`, `release/1.9-family.json`, and `docs/RELEASE_FAMILY_1.9.md` for release ownership, only when relevant. Read the affected domain contract before changing its behavior.
- Prefer existing patterns and tests. Make the smallest coherent change; avoid unrelated refactors.

## Safety and compatibility
- Never write to or destructively test real sources, backups, destinations, host disks, or user data without explicit authorization. Use disposable fixtures for recovery checks. A synthetic test or successful build is not proof of installed recovery or release qualification.
- Preserve backup/restore semantics, data formats, cancellation, failure handling, and security boundaries. Localize changed user-facing strings in all maintained locales; follow `docs/LOCALIZATION.md`.
- Respect protected branches, review/CI gates, release signing (`docs/RELEASE_SIGNING.md`), and required evidence. Do not merge, publish, or claim support without qualification.
- Reuse the owning issue and canonical `VS-`/`BUG-` ID for meaningful work. Update relevant release/changelog/tracking records; use the existing `./dev` workflow where available.

## Verification and handoff
- Review the diff. For docs/format-only changes, skip code tests unless a specific gate requires them.
- For behavior changes, run the smallest relevant tests and affected build/check at a meaningful checkpoint. Broaden for shared/core changes, migrations, security, release gates, or failures; avoid default full-suite runs and redundant retries.
- Keep tool output bounded. Report changes, exact checks run or skipped, and remaining risks. Never claim an unrun check passed.

## Active 1.9.0 recovery work
`work/1.9-roadmap-realignment` is the assembled 1.9.0 head and sole draft release PR #736 into `Dev`; `release/1.9.0` is a protected release identity, not the active preparation tree. Use `docs/RELEASE_1.9.0.md`, `docs/RELEASE_FAMILY_1.9.md` and the relevant recovery protocol as needed. Keep later 1.9.x features out of this binary.

The approved joint recovery direction is recorded at `docs/release-evidence/1.9-joint-architecture-approval-2026-10-20.md`; do not reopen that settled direction. It does **not** authorize real host-disk writes, publication or unsupported OS/profile qualification. Native storage/admission fixtures must meet their kernel/mount/inode proof contracts; synthetic or disposable-VM results are not installed boot or system recovery evidence. Follow `docs/RECOVERY_ENTRY_1.9.md` and `docs/VALIDATION_WORKSPACES.md`; clean only proven run-owned artifacts.

All betas retain Windows, macOS and Linux desktop support; new system-recovery qualification is staged Linux GPT/UEFI ext4, then Windows NTFS, then macOS APFS (see `docs/BETA_PROGRAM_1.9.0.md`). Beta diagnostics follow `docs/PRODUCT_DIAGNOSTICS.md`; never broaden consent or upload user paths/data. Preserve issue/PR #736 ownership and verified evidence. Do not merge or promote until all required release gates pass.
