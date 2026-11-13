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

## Automatic GitHub Issue classification and ID assignment

The repository's `.github/workflows/issue-auto-triage.yml` reacts to new/edited/labeled,
reopened and closed Issues; a daily scheduled reconciliation can recover missed
ID events from a burst, processing up to five accepted unnumbered Issues per run.
Maintainers can also use `workflow_dispatch` with an
existing issue number (for example #843). GitHub only executes `issues` workflows
when their YAML is present on the **default branch** (`Stable`). A workflow
merged only into a release-preparation branch is not active yet.

- Issue Forms already label bug reports (`bug`) and feature requests
  (`enhancement`). The workflow adds `kind:bug` or `kind:feature` when available;
  explicit `kind:change`, `kind:maintenance` and `kind:security` also map to `VS`.
  It never guesses work type from untrusted issue prose. Mixed types fail closed.
- Unclassified feedback and support do **not** receive invented engineering IDs.
- Accepted issues with an exact release target but no priority default to the
  repository's preliminary `priority:p2` label where available; maintainers must
  still validate it against the roadmap.
- Open issues without a status receive `status:todo`; closed issues receive
  `status:done` and reopened issues return to Todo unless independently retriaged.
  Existing In progress labels remain unchanged.
- The work ID is allocated only when the issue has **exactly one target-release
  label**, with an exact version, e.g. `release:1.9.0`; `release:1.8.x` from older
  public report forms is not a target release. The maintainer must first decide
  where a reported issue belongs and remove stale/conflicting release labels.
  Canonical IDs already leading issue titles are preserved, not renumbered.
- The allocator reuses the complete, fail-closed local/remote audit in
  `scripts/work_tracking.py` (Issues, PRs, roadmap and changelog), then prefixes
  the issue title with the next `BUG-19xxx` or `VS-19xx` owning ID as applicable.
  Changes use the same IDs in their PR and changelog, not a second identifier.
- GitHub Actions uses a single `concurrency` group for this workflow's ID
  allocations. Independent local `./dev` operations do **not** participate in
  that lock; use the audit and review ID ownership before integration.
- Automation never edits `ROADMAP.md`, `CHANGELOG.md`, the release manifest, or
  protected release refs; the implementing contributor still maintains the
  release-specific records under review.

### Project #7: built-in automatic enrollment

In the existing `ATAC-Helicopter` Project #7, use
**Project → … → Workflows → Auto-add to project → Edit**; choose the
`ATAC-Helicopter/VaultSync` repository and set the filter to `is:issue`.
**Save and turn on workflow.** This is the supported GitHub-native solution for
future Issue additions and does not require a personal token. Where offered,
enable the built-in Item added → Todo, Item closed → Done, Item reopened → Todo
workflows. Preserve release-gated issues as open/In progress until their actual
closure criteria are satisfied.

**Auto-add is not retroactive.** Add #843 and any existing untracked Issues
separately, or reconcile them through `./dev adopt` where permissions allow.
The CLI's Project insertion is best-effort and warns if Project write access is
missing; never treat that warning as a successful insertion.

The repository Issue workflow handles labels, preliminary priority, maintainer
assignment for otherwise-unassigned issues, and the release milestone when an
exact release target is set. It never overwrites a conflicting milestone or
changes an external contributor's existing assignment. Issues without a clear
type remain in triage rather than receiving a fabricated Bug/VS ID.

The optional `.github/workflows/project-date-sync.yml` and
`scripts/project_date_sync.py` synchronize two date fields of **personal**
Project #7 once all of these activation conditions are met:

1. A dedicated `VAULTSYNC_PROJECT_TOKEN` repository secret authorizes **read
   and write** to the owner's GitHub Projects v2. A normal `GITHUB_TOKEN`
   cannot write personal Project fields.
2. The workflow exists on the repository's default branch. Before that, its
   event and scheduled triggers are not active.
3. `Start date` and `Completed on` fields exist as Date fields in Project #7.

The sync reads a **complete** paginated Project snapshot. For new issues created
on/after 2026-11-13, it sets the initially missing Start date when it first
observes `In progress`. Moving a status manually on the personal Project board
is detected by the **daily** synchronization, not instantly; the recorded day
is the first observation, not an invented historical start. Older issues need
review of their real start dates.

For `Done` issues, it sets Completed on to the issue's actual GitHub close
date; when Project says Done but the issue is still open, it **refuses** to
invent a completion. On reopening, it clears stale Completed on values for
non-Done items. No change ever touches Target date, closure state, roadmap
priority, branch protections, Project status, or release-gated acceptance.
Existing release-gated issues must remain Open/In progress until shipped.

This date sync is opt-in and unverified against the live Project until the
secret and workflow are activated. Continue using `./dev audit --release 1.9.0`
for alignment, since neither automated issue triage nor date sync currently
fills Priority/Area/Release and Target date Project fields. Project enrollment
still requires the built-in Auto-add setting or authorized `./dev adopt`.


For a 1.9.0 external contribution, first set the exact release target,
classification and priority on its Issue, then let the workflow assign its ID.
Contributors branch from `work/1.9-roadmap-realignment` and use
`./dev pr --base work/1.9-roadmap-realignment --draft --keep-open` to submit
a separate reviewed PR for inclusion in the consolidated release PR #736.
