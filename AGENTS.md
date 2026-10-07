# Agent Development Rules

## Work tracking

For non-trivial code, behavior, packaging, security, release, or user-facing documentation work, keep the change tied to a GitHub issue.

Before starting new work, prefer:

```bash
./dev issue <bug|feature|change|security|maintenance> "Concise scope" --branch current
```

Use `--branch new` when the work should have a dedicated branch. The default is deliberately `current`; branch creation is a choice, not a ritual.

When a public/existing issue already describes the work, adopt it instead of creating a duplicate:

```bash
./dev adopt <issue-number> --type bug --branch current
```

The helper allocates the canonical VaultSync ID from the active release family, adds available repository labels, attempts to add the issue to Project #7, and associates the active branch with the issue in local git config.

Release-family inference order is:

1. explicit `--release X.Y`;
2. an active `release/X.Y[.Z]` branch;
3. `activeRelease.version` in `release/release-metadata.json`;
4. the linked `Unreleased` target version or legacy unreleased entry in `CHANGELOG.md`.

Use `./dev status` before committing if the branch-to-issue association is unclear.

### Before every push

For any non-trivial tracked change, work tracking is a push prerequisite:

1. Run `./dev status` and confirm the active branch is associated with the intended issue and canonical ID.
2. If tracking is missing, use `./dev issue ... --branch current` or `./dev adopt ... --branch current` before pushing. Do not reconstruct the ticket from commits afterward.
3. Confirm the issue scope/status still matches the implemented work and update required roadmap/changelog/release metadata.
4. A dedicated branch remains optional unless another repository/release rule requires one.
5. Tiny typo/format-only maintenance may use the repository's explicit tracking-skip mechanism; larger work must not silently bypass tracking.


## Commit and PR references

Include the canonical ID in meaningful commit subjects when practical, for example:

```text
fix(BUG-18177): reject incomplete recovery evidence
feat(VS-1898): add recovery report export
```

PRs must contain both a canonical ID and an issue link (`Closes #N`, `Fixes #N`, `Resolves #N`, or `Refs #N`). Use `./dev pr` to create the standard PR automatically from the branch association.

Only use `Tracking: skip - <reason>` for genuinely untracked maintenance. `Tracking: bootstrap` exists only for the tracker bootstrap itself.

## Release notes

PR labels feed GitHub's native generated release-note categories through `.github/release.yml`. Preview notes with:

```bash
./dev notes --tag vX.Y.Z --previous-tag vA.B.C --target <branch>
```

The generated notes are a release drafting aid; the curated `CHANGELOG.md` remains authoritative until the release workflow explicitly changes that policy.

Release ownership and preparation instructions take precedence over this helper.
Use the existing owning issue and sole release PR, with `Refs` for release-gated work.
ID allocation fails closed if the complete remote issue audit is unavailable; it
never silently allocates using only a local roadmap. Permission or network failures
must be resolved before creating a new numbered work item.
