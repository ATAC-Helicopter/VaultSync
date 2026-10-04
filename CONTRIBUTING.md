# Contributing

Thanks for contributing to VaultSync.

## 1) Planning Model
Use `VS-xxxx` as the default work-item format for planned engineering work.

ID model:
- `VS-xxxx` (default): roadmap, implementation, test, release execution
  - release family by hundreds (example: `15xx` for `1.5`)
- `ISS-xxxx` (optional): grouped issue/UX cleanup batches in changelog-only tracking
- `BUG-xxxx` (optional): explicit bug-fix IDs in changelog tracking
- `REL-xxxx` (optional): release-gate follow-up tracking
  - changelog version-family numbering: `1.0.x -> 10xxx`, `1.1.x -> 11xxx`, ... `1.8.x -> 18xxx`

Rules:
- For planned feature work, always create/use `VS-xxxx` in `ROADMAP.md`.
- In `CHANGELOG.md`, use `VS-xxxx` for `Added` entries by default (even when backfilling historical non-roadmap additions).
- Keep one primary scope per ID.
- Reuse the same ID in PR description, validation notes, and changelog entry when applicable.

## 2) Before You Start
- Active development is **1.9.0** on `work/1.9-roadmap-realignment`, assembled
  in sole release PR #736. Read [the working context](AGENTS.md) and
  [the release contract](docs/RELEASE_1.9.0.md); keep later-release work in its
  owning preparation branch and PR.
- Check `ROADMAP.md` and open issues.
- Confirm acceptance criteria before coding.
- For risky or cross-cutting changes, align scope first.

## 3) Development Setup
Keep the working copy outside iCloud Drive, OneDrive, Dropbox, or another live file-sync root. On macOS, a directory ending in `.nosync` prevents iCloud from taking ownership of repository files and breaking builds or Git operations.

1. Install .NET 10 SDK.
2. Restore:
   `dotnet restore`
3. Run the UI on macOS or Linux:
   `dotnet run -f net10.0 --project src/VaultSync.UI/VaultSync.UI.csproj`
4. Run the UI on Windows:
   `dotnet run -f net10.0-windows10.0.19041.0 --project src/VaultSync.UI/VaultSync.UI.csproj`

### Rider development checks

The shared `VaultSync Contracts` and `VaultSync Release Checks` configurations
run existing repository checks with compact output and retained temporary logs.
They use Rider's bundled Shell scripts plugin and Bash/Python/.NET from PATH;
the checked-in `/bin/bash` interpreter suits Linux/macOS. Windows contributors
can run the commands in section 7 directly or select their installed Bash path.

- `bash scripts/validate_development.sh contracts`: family/metadata and script tests.
- `bash scripts/validate_development.sh release`: also the warning-as-error Release
  solution build and core tests, with a TRX report in the printed log directory.

The build uses `-m:1` to avoid simultaneous builds of Core under different UI
target-framework properties. Run it after any IDE build finishes. Shared run
configurations have no additional before-launch build, preventing duplicate work.
These checks do not qualify UI behavior, disk recovery, upgrade or release approval.
See AGENTS.md for scoped semantic search, refactoring, debugging and diagnostics.

## 4) Implementation Rules
- Keep changes focused.
- Avoid unrelated refactors in the same PR.
- Keep heavy operations off the UI thread.
- Add or update tests for behavior changes.
- Prefer localization keys over hardcoded UI text.

## 5) Documentation Rules
When behavior changes, update in the same PR:
- `docs/wiki/*` and/or `docs/HELP.md`
- `CHANGELOG.md`
- `ROADMAP.md` if ticket state/scope changed
- `docs/WHATS_NEW.md` for user-facing release highlights

Use `docs/README.md` and `DOCUMENTATION.md` as structure references.

### Changelog Style Rules
`CHANGELOG.md` follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/).
Use a linked `## [Unreleased]` section for the active version declared in
`release/release-metadata.json`; show its target version beneath the heading.
Published headers use `## [version] - YYYY-MM-DD`, preserving historical
versions and publication dates. At release cut, rename Unreleased to the
published version/date and open a fresh Unreleased section for the next version.

Group entries under these exact headings, omitting empty groups:
- `Added`: new capabilities or documentation actually present on the branch.
- `Changed`: changes to existing behavior, dependencies or maintained contracts.
- `Deprecated`: capabilities scheduled for removal, with migration guidance.
- `Removed`: capabilities or dependencies actually removed.
- `Fixed`: corrected defects.
- `Security`: resolved vulnerabilities; do not reclassify every safety feature.

Use that order. Do not add Maintenance, Planning, daily-date or implementation
subheadings. Pending architecture documents must explicitly say draft/review;
plans do not establish shipped behavior. Keep development ledgers and historical
technical detail in docs/release-evidence rather than changelog entries.
Run `python3 scripts/changelog.py check` before commit; shared documentation
standards must be propagated to every owning release PR without importing its
siblings' unreleased features. Never rewrite published Git tags or packages
merely to normalize historical notes.

Keep every `CHANGELOG.md` entry intentionally short, including historical backfills:
- Write for release readers, not implementers.
- Prefer one user-facing outcome per bullet.
- Aim for `18-22` words per bullet, with a maximum of **22 words**, excluding
  the owning `[ID]` prefix. Count whitespace-separated words; shorter clear
  entries are fine and do not need padding.
- Do not list internal implementation details unless they change user expectations or upgrade behavior.
- If a change needs deep explanation, keep the short summary in `CHANGELOG.md` and put the detail in the issue, PR, or `docs/WHATS_NEW.md`.

Preferred bullet format:
- `[ID] Short user-facing result.`

Examples:
- Good: `[VS-1721] Added app-wide custom tag colors with in-context editing from Projects.`
- Bad: `[VS-1721] Added a visual picker, swatches, preview card, shared appearance helper, and a Settings pointer flow for app-wide tag color editing.`

## 6) Pull Request Requirements
- Reference the related ID (`VS-xxxx` preferred).
- Describe what changed and why.
- List compatibility/migration impact if any.
- Include validation evidence (build, tests, manual checks).

## 6.0) Repository Workflow Standard (PR-first)
- Standard workflow is **PR-first** (feature/fix branches into `Dev` via PR).
- Merge feature, fix, and release PRs into `Dev` with a **merge commit**. Do not
  squash or rebase them at merge time: `Dev` must retain the source commits and
  their original identities.
- Promote `Dev` into `Stable` with a **merge commit**. `Stable` is a shipped-
  release spine: do not squash, rebase, or fast-forward a promotion into it.
- Release branches use `release/<version>` and receive the same deletion,
  force-push, review, and status-check protection as `Dev`.
- Direct pushes are reserved for:
  - emergency maintainer hotfixes
  - metadata-only maintenance (for example: label/board sync scripts)
  - explicit owner decision
- A Stable hotfix still goes through a hotfix branch and merge commit so the
  Stable first-parent history remains release/hotfix merges only.
- Even for direct pushes, keep issue/roadmap/changelog links exactly as with PRs.

## 6.1) Issue And PR Linking Rules
Keep planning, implementation, and release tracking connected:
- Every meaningful PR should link to at least one issue.
- Preferred PR body footer:
  - `Closes #123` for completed work that should close immediately on merge.
  - `Refs #123` for partial/incremental work or release-gated work that should remain open until release.
- If an issue does not exist yet, create it before opening the PR for non-trivial work.
- Keep issue titles aligned with roadmap/changelog IDs when applicable:
  - Feature work: `VS-xxxx: concise scope`
  - Bug-fix work with changelog bug IDs: `BUG-xxxxx / VS-xxxx: concise scope`
  - Example (feature): `VS-1601: richer restore flows`
  - Example (bug): `BUG-15020 / VS-1578: harden app config reads against transient file locks`
- Update issue metadata when opening/updating a PR:
  - labels (`kind:*`, `priority:*`, `release:*`, `status:*`, optional human label `Feature/Improvement/Idea`)
  - assignment and project status
- When PR merges:
  - issue should be closed (`Closes #...`) or explicitly left open with next-step notes
  - project status should be updated (`Done` for closed; `In progress`/`Todo` otherwise)
- Release-gated policy:
  - For unreleased work (for example under `1.5.1 - Unreleased`), keep issue open and set project status `In progress` (or `Done` only when you intentionally track implementation-complete but unreleased in your process).
  - Close the issue when the release cut/merge policy says the item is truly shipped.
- CLI formatting rule:
  - For `gh issue comment` and similar commands, use a PowerShell here-string (`@' ... '@`) or `--body-file`.
  - Do not pass escaped newline text (`\\n`) in quoted one-liners, to avoid literal backslash-n output in GitHub comments.

## 6.2) Stacked Pull Requests

Use a stack when one reviewable change depends on another that has not merged.
Use separate PRs against the release branch for independent work so reviewers
can merge either one without waiting for the other.

- Open the first PR against its intended integration branch (for example,
  `release/1.9.0`). Open each dependent PR against the preceding PR's head
  branch. Keep one focused change per layer and verify that each PR's **Files
  changed** view contains only that layer's work.
- In every stacked PR, identify its immediate parent PR and branch, its final
  target branch, and the complete merge order. Update these links and the PR
  base when the stack changes. The top layer must not obscure the review diff
  of a lower layer.
- Merge from the bottom upward with merge commits. After a parent merges,
  retarget its child to the parent's integration branch, review the resulting
  diff, and rerun required checks before merging the child. Repeat until the
  stack is integrated. Do not squash, rebase, force-push, or bypass the ruleset
  of a protected release branch, `Dev`, or `Stable`.
- Keep issue and Project state tied to actual integration. A partial layer uses
  `Refs #123`; use `Closes #123` only on the PR whose merge satisfies the issue's
  acceptance criteria and closure policy. Update the issue and Project when a
  layer merges. A draft, passing check, or merge into an intermediate stack
  branch does not by itself complete release-gated work.
- The release PR from `release/<version>` to `Dev`, and promotion from `Dev` to
  `Stable`, remain separate merge-commit steps with their own required checks.

### Recovery Horizon review grouping

The maintainer requested one open draft PR per 1.9 release on 2026-10-03.
Assemble that release's contracts, features, fixes and changelog into its sole
preparation PR against Dev; close duplicate preparation/fix PRs as superseded
without deleting branches or rewriting protected refs. The review head may be
an unprotected preparation branch while release/<version> retains its reviewed
history. This convention changes review grouping, not required approvals or
release qualification. Keep release PRs draft; no automatic release merge.
Shared maintenance must be propagated without moving future features forward.
The daily ledger records each release, source head and qualification status.

## 7) Quality Gates
Run before requesting review:
- `dotnet build VaultSync.sln --configuration Release -warnaserror -p:UseSharedCompilation=false`
- relevant tests for touched areas
- smoke check of impacted UI flows if UI changed

Pull requests and pushes to `Dev`/`Stable` also run CI and release-quality workflows:
- Windows: full solution Release build plus core tests.
- Linux: generic UI target Release build plus core tests.
- macOS: generic UI target Release build plus core tests.
- PR quality: release metadata, dependency vulnerability, localization, and packaging checks where applicable.
- Warnings are treated as errors so review fixes do not hide compiler noise.

## 8) Release Hygiene
- Ensure changelog entries are categorized (`Added`, `Changed`, `Fixed`).
- Ensure docs and localization are updated for new user-facing behavior.
- Ensure roadmap states are accurate for completed/remaining work.

## 9) Reporting Bugs
Use `docs/wiki/Reporting-Bugs.md`.
Security issues: follow `SECURITY.md`.

## 10) Standard Project Operations
These are the default procedures for everyone contributing to VaultSync.

Source of truth:
- Product planning: `ROADMAP.md`
- Release notes: `CHANGELOG.md`
- User-facing release summary: `docs/WHATS_NEW.md`
- Project board: `ATAC-Helicopter` Project `#7` (`@VaultSync Roadmap`)

Required workflow for changes:
1. Implement code/docs changes.
2. Update `CHANGELOG.md` in the active unreleased version section.
3. If scope/priority/status changed, update `ROADMAP.md`.
4. If release-facing UX changed, update `docs/WHATS_NEW.md` when preparing release notes.
5. Keep project metadata aligned when needed:
   - `Owner`: `Flavio Giacchetti`
   - `Team`: `Work` (solo setup)
   - Status/date policy:
     - every item: traceable Start and Target dates are required
     - `Todo`: no Completed on date
     - `In progress`: Start date reflects the earliest approved planning or
       implementation date, never the date of a later board edit
     - `Done`: Completed on reflects the closing, merge, or release evidence
       defined by the roadmap protocol

Changelog/roadmap consistency rules:
- Use IDs when available (`VS-xxxx`, `ISS-xxxxx`, `BUG-xxxxx`, `REL-xxxxx`).
- Do not renumber existing roadmap IDs.
- Keep changelog entries in the correct version block.
- Keep changelog bullets concise and user-facing.
- Keep roadmap priorities and status accurate.

Project board rules:
- Use one project board.
- For issue-backed items, use native labels/repository linkage.
- Prefer issue-backed roadmap items over drafts.
- If drafts are temporarily used, convert them to issues once scope is execution-ready.
- Keep fallback project fields aligned when needed:
  - `Repository target`
  - `Work labels`

Commit/push defaults:
- Default active branch: `Dev` (unless explicitly specified otherwise).
- The maintainer authorizes commits and pushes of completed, validated work to
  the preparation head of the owning release PR. Do not ask again for routine
  delivery; protected reviews, merge-only integration and release gates remain.
- Public branch, issue, PR, commit and Project names describe product/work scope,
  never assistants, agents, models or AI systems. Do not add tool attribution.
- If asked to commit everything, include all modified/new files unless paths are excluded.

Reference:
- `DOCUMENTATION.md`
- `docs/README.md`
