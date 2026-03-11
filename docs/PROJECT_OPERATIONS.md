# VaultSync Project Operations Playbook

This document is the practical checklist for maintaining planning/release/project-board consistency.

## 1) Planning And Execution

### When a feature/fix is requested
1. Implement code changes.
2. Add/adjust tests when appropriate.
3. Update `CHANGELOG.md` (active unreleased section).
4. Update `ROADMAP.md` if scope/status/priority changed.
5. Update `docs/WHATS_NEW.md` only when preparing release/user-facing summary.

### ID conventions
- Feature/task IDs: `VS-xxxx`
- General issue IDs: `ISS-xxxxx`
- Bug IDs: `BUG-xxxxx`
- Release-gate IDs: `REL-xxxxx`

Issue-title convention:
- Feature: `VS-xxxx: concise scope`
- Bug-fix mapped to changelog bug IDs: `BUG-xxxxx / VS-xxxx: concise scope`

Keep IDs stable once published in roadmap/changelog/project board.

## 2) Changelog Maintenance

### Required format
- Keep per-version sections in descending order.
- Use:
  - `### Added`
  - `### Changed`
  - `### Fixed`
  - `### Follow-up` (optional)

### Entry quality
- One behavior change per bullet.
- Mention impacted area (UI, backup, metadata sync, localization, etc.).
- Use IDs where applicable.

## 3) Roadmap Maintenance

### Keep in sync
- Priorities (`P0/P1/P2`)
- Implementation status (`[x]` / `[ ]`)
- Acceptance criteria and dependencies for active work

### Completion updates
- Mark finished tickets in the execution backlog.
- Update current release status summary.
- Move truly finished highlights into `Completed (highlights)` when requested.

## 4) GitHub Project Board (`VaultSync Roadmap`, org project #7)

### Solo operating model
- `Owner`: `Flavio Giacchetti`
- `Team`: `Work` (single-team)

### Status/date policy
- `Todo`: no `Start date`, no `Completed on`
- `In progress`: `Start date` required
- `Done`: `Completed on` required

### Repository/labels behavior
- Issue-backed items:
  - Native repository linkage + native issue labels.
- Draft items (exception path only):
  - Use only when work is not execution-ready.
  - Convert to issue once scope is actionable.
  - Keep fallback fields filled while draft is used:
    - `Repository target`: `ATAC-Helicopter/VaultSync`
    - `Work labels`: normalized label string

## 4.1) Issue/PR Lifecycle
- Open issue first for non-trivial work.
- Preferred delivery model is PR-first into `Dev`.
- Direct pushes are exception-only (maintainer emergency/metadata maintenance/explicit owner decision).
- Link PR to issue with:
  - `Closes #...` when complete and intended to close at merge time.
  - `Refs #...` when partial or release-gated (keep issue open until release).
- Keep issue labels and project fields current while PR is active.
- On merge:
  - issue closes automatically if `Closes #...` is used
  - project status/date fields must reflect final state

### Unreleased work policy
- If implementation is done but release is not shipped yet:
  - keep issue open
  - keep project item in `In progress` (with `Start date`)
  - close at release cut (or when your release branch policy considers it shipped)

### GitHub CLI comment formatting
- For multiline issue comments, always use:
  - PowerShell here-string body (`@' ... '@`) or
  - `--body-file`.
- Avoid escaped newline strings (for example `\\n`) in one-line quoted commands, because GitHub will render them literally.

## 5) Recommended Label Set

Apply to issue-backed items:
- `kind:vs|iss|bug|rel|roadmap`
- `status:todo|in-progress|done`
- `area:core|ui|performance|security|localization|docs|release|ops`
- `release:1.5.1|1.6.x|1.7.x|long-term`
  - Preferred for active planning: `release:1.5.1|1.6.x|1.7.x|1.8.x|1.9.x`
  - Legacy/archived items may still use `release:long-term` on the board.
- Keep metadata-only tags (`vaultsync`, `solo-maintainer`) in project fields, not issue labels, to reduce issue-list noise.
- Human-readable classification labels are preferred in issue lists:
  - `Feature`
  - `Improvement`
  - `Idea`

## 6) Branching/Release Defaults

- Ongoing work branch: `Dev`
- Stable release branch: `Stable`
- Tag format: `vX.Y.Z`
- Push only when explicitly requested.

## 7) Practical Verification Checklist

Before declaring completion:
- [ ] Build/tests pass (or clearly note what was not run).
- [ ] Changelog updated in correct unreleased version.
- [ ] Roadmap status reflects current implementation state.
- [ ] Project board fields normalized (owner/team/status/dates).
- [ ] New localization keys added to `strings.en.json` and tracked for translation.
- [ ] PR references relevant issues and issue states are updated.
