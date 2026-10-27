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

## Adopt an existing issue

Public Issue Forms stay unchanged. When an existing report becomes active engineering work:

```bash
./dev adopt 758 --type bug --branch current
```

If the issue already has a canonical ID, it is reused. Otherwise the helper allocates one and prefixes the issue title.

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
