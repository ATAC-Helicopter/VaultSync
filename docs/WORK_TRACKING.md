# Repository-native work tracking

VaultSync uses GitHub Issues as the durable record for tracked work. The local helper removes the repetitive parts: ID allocation, issue creation/adoption, optional branch creation, branch association, PR linkage, Project insertion, and generated release-note categorization.

## Start new work

Keep the current branch:

```bash
./dev issue bug "Restore accepts incomplete recovery metadata" --branch current
```

Create a dedicated branch from the current HEAD:

```bash
./dev issue feature "Add recovery evidence export" --branch new
```

Useful options:

```text
--release 1.9       Override release-family inference.
--priority P1       Apply a matching priority label when it exists.
--area recovery     Record the affected area in the issue body.
--context "..."     Add extra context to the issue.
```

`--branch current` is the default. `--branch new` creates a type-prefixed branch such as `fix/BUG-18177-...` or `feat/VS-1898-...`.

## ID allocation

The helper preserves VaultSync's existing ID families instead of introducing a second numbering system:

- features/changes/security/maintenance: `VS-<release-family><2-digit sequence>`;
- bugs: `BUG-<release-family><3-digit sequence>`.

For example, 1.8.x work follows `VS-18xx` and `BUG-18xxx`; 1.9.x follows `VS-19xx` and `BUG-19xxx`.

The allocator scans `ROADMAP.md`, `CHANGELOG.md`, and all pages of GitHub Issues, then selects the next unused ID in the inferred release family. Historical IDs are never renumbered.

Command input/output uses UTF-8 explicitly, including the complete GitHub audit
on Windows. Before issue creation, the allocated identifier is reserved in local
Git config (`vaultsync.reservedWorkId`). The audit includes these reservations
while remote issue listings catch up. Failed creation leaves the reservation
intact; never recycle the identifier. These local reservations do not coordinate
independent workspaces or concurrent remote writers.

Defects use one owning `BUG-xxxxx` identifier; features and maintenance keep their
`VS-xxxx` identifiers. Active changelog `Fixed` entries require BUG ownership.
Link related development issues by reference rather than dual BUG/VS titles.
Preserve published historical identifiers. Retained functionality or compatibility
maintenance belongs in its appropriate category, not automatically in `Fixed`.

## Adopt an existing issue

Public Issue Forms stay unchanged. When an existing report becomes active engineering work:

```bash
./dev adopt 758 --type bug --branch current
```

If the issue already has a canonical ID, it is reused. Otherwise the helper allocates one and prefixes the issue title.

Adoption preserves the roadmap priority, or the existing single priority when
there is no roadmap entry. `--priority` explicitly overrides that choice and
requires the roadmap to be updated consistently. Conflicting priority labels
are reconciled in one issue edit; unrelated labels are retained. An unresolved
conflict without a canonical priority or a denied edit aborts adoption. The
default must never append P2 beside an existing P0/P1 label (BUG-19033/#792).

## Branch association

Tracking data is stored locally under the active git branch:

```text
branch.<name>.fgIssue
branch.<name>.fgWorkId
branch.<name>.fgWorkType
branch.<name>.fgWorkTitle
```

It does not add tracking files to commits and does not force a dedicated branch.

Inspect the association with:

```bash
./dev status
```

## Release audit and issue lifecycle

Before every tracked push, verify both local association and remote state:

```bash
./dev status
./dev audit --release 1.9.0 --output /tmp/vaultsync-tracking.json
```

Use the actual owning version on other branches. `./dev audit` defaults to the
active metadata version, stripping a prerelease suffix. `--all-releases` checks
every owning ID in `release/1.9-family.json`, including conditional IDs. The
complete paginated issue listing excludes PRs; owning IDs must lead the issue
title. Project membership/field pagination truncation or unavailable APIs abort
the audit without a passing result. Native Project ownership is checked by
account and number; another account's Project #7 cannot satisfy this contract.

The read-only audit detects missing/duplicate owners or Project items, wrong
milestones, missing assignees, conflicting priority/status labels, missing exact
release labels, inconsistent board status/priority, missing Area/Release and
invalid/missing/inconsistent dates. It exits 1 for discrepancies, 2 for incomplete
execution, and 0 for complete consistent metadata. It never creates, closes,
reopens, assigns or edits anything, and cannot decide feature acceptance from
labels or a passing build. Review implementation evidence separately.

Create/adopt before implementation, move started work to In progress in both
issue labels and Project, and update delivered scope, remaining acceptance and
commit/PR evidence with every meaningful increment. The creation helper's
best-effort Project insertion warning does not satisfy the before-push audit.
Fix missing access or retain/report the precise unresolved condition.

Release-gated issues stay open/In progress until their owning closure gates
pass. Close completed shipped work as completed; conditional scope proven
unnecessary may close as not planned with linked proof. Done is the final issue
disposition, not a promise that an unnecessary feature was built. Completed on
must match the retained closure date. Start dates for planned records may come
from issue creation; actual starts must be described separately when different.
Target dates may reference the retained release-family horizon for unscheduled
releases; label them as planning, never a publication commitment.

## Pull requests

Once the branch is tracked:

```bash
./dev pr --draft
```

The helper creates a PR to `Dev` by default, includes the canonical ID, and links the issue. Use `--keep-open` for release-gated or incremental work so the PR uses `Refs #N` instead of `Closes #N`.

`.github/workflows/tracking.yml` checks PRs targeting every branch. Meaningful PRs require:

1. a canonical work ID;
2. a closing/reference keyword to a GitHub issue.

Direct pushes to every branch are audited but not failed: commits without an ID are surfaced as warnings. This keeps the safety net useful without making emergency or metadata maintenance impossible.

## Generated release notes

`.github/release.yml` categorizes GitHub-generated release notes into Fixed, Added, Security, Changed, Maintenance, and Other Changes.

Preview them before a release:

```bash
./dev notes \
  --tag v1.9.0 \
  --previous-tag v1.8.9 \
  --target release/1.9.0 \
  --output /tmp/vaultsync-1.9.0-notes.md
```

This does not silently rewrite `CHANGELOG.md`. VaultSync's curated changelog remains the source of truth, while generated notes remove the clerical work and provide a cross-check for omissions.

## Requirements

- Python 3
- Git
- GitHub CLI (`gh`) authenticated for the repository
- Project write permission if automatic insertion into Project #7 is desired

If Project insertion is unavailable because the local GitHub token lacks project scope, issue creation still succeeds and the helper prints a warning instead of rolling back the work item.

Release ownership and preparation instructions take precedence over this helper.
Use the existing owning issue and sole release PR, with `Refs` for release-gated work.
ID allocation fails closed if the complete remote issue audit is unavailable; it
never silently allocates using only a local roadmap. Permission or network failures
must be resolved before creating a new numbered work item.


## Complete remote snapshots

BUG-19037/#813 uses GraphQL cursor connections for remote issue enumeration;
identifier reservation also reads all PR titles and bodies. Every connection must
reach its reported total with unique global issue/PR numbers, stable counts and
nonrepeated cursors. Labels and assignees must be complete, correctly shaped
connections. API errors, missing rows, changed counts or partial metadata fail
before an audit can pass or an ID can be allocated; retry only a fresh read.
No weaker REST fallback or local-only allocation exists. Native Project metadata
remains independently queried/checked. This is a shared validation standard for
all release preparations; it changes no release acceptance or closure criteria.

On October 22, REST listings returned varying incomplete subsets despite direct
access to missing owners. Counted GraphQL enumeration independently returned all
569 issues and 170 PRs before the new tracker ticket was created. This identifies
an observed enumeration failure, not a proven service/CLI root cause. See
[source-bound evidence](release-evidence/1.9-tracking-cursors-2026-10-22.md).
