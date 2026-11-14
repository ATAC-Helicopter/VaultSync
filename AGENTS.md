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

## Release preparation: 1.9.3
This branch is the 1.9.3 preparation head (`work/1.9.3-changelog-protocol`, draft PR #753 into `Dev`), not the active 1.9.0 implementation or the protected release ref. For release-specific work consult `docs/RELEASE_1.9.3.md` and `docs/RELEASE_FAMILY_1.9.md`. Follow the family manifest: do not import another release's scope or rewrite existing work. Keep the draft PR open until real qualification and review gates pass. Preserve signing, localization, evidence, issue tracking and current-head provenance.
