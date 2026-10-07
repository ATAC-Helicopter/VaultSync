# Active development: VaultSync 1.9.0

The active release is 1.9.0, unless the maintainer explicitly selects another
release. Read ROADMAP.md, release/1.9-family.json, CONTRIBUTING.md and
docs/RELEASE_1.9.0.md before implementation.

## Working copy and delivery

- The assembled 1.9.0 development branch is `work/1.9-roadmap-realignment`.
  Its sole release PR is #736, targeting `Dev`. Keep this workspace on that
  branch for work that must ship with 1.9.0, and add its commits to that PR.
- `release/1.9.0` is the protected release identity; it is not the current
  assembled preparation head. Do not switch to its older tree to start work,
  rewrite it, or create another 1.9.0 release PR merely because of its name.
- Before editing, inspect Git status and the PR head. Preserve local/untracked
  work and existing commits. Follow merge-only protection and required reviews.
- Use the family manifest to check ownership. Discovery/help/completion belong
  to 1.9.1; resource CLI automation belongs to 1.9.5. Work for later releases
  goes to their own preparation branches/PRs, not into the 1.9.0 binary.
- Reuse the owning issue and update the 1.9.0 changelog, relevant contracts,
  documentation and native GitHub Project tracking with each meaningful change.
- Changelog entries use one user-facing result and at most 22 words, excluding
  the `[ID]` prefix; see CONTRIBUTING.md for the counting and detail-placement rules.
  Follow Keep a Changelog 1.1.0: `Added`, `Changed`, `Deprecated`, `Removed`,
  `Fixed`, `Security`, ISO release dates, and a linked `Unreleased` section.
- Commit and push completed, validated work to the owning release PR's
  preparation head without asking again. This is standing maintainer
  authorization. Preserve protected-branch reviews, merge-only history and
  release gates; it does not authorize bypass, automatic merge or publication.
- Never name a public branch, issue, PR, commit, project item or other artifact
  after an assistant, model, agent or AI system. Use product/release/work scope.
  Do not add automated-tool attribution to public titles or commit messages.
- With every meaningful change, update the owning issue and release PR,
  native Project #7 status and traceable dates, CHANGELOG.md, ROADMAP.md and
  relevant contracts/user docs. Use `Refs` while release-gated work stays open.
  Keep historical evidence separate from current-head test results.
- Find current preparation heads and sole PRs in the preparation map in
  docs/RELEASE_FAMILY_1.9.md. Refresh that map and owning contracts whenever
  a preparation PR is superseded; protected release identities stay separate.
- Cross-release documentation/validation standards apply to every open release
  PR, using its real head and active metadata. Never copy one release's
  unreleased features into another; `release/1.9-family.json` owns the IDs.
- Keep #736 draft until its actual release gates pass. Do not merge or promote
  a release merely because compilation or mandatory CI checks pass.

When the maintainer changes the active release, update this working context and
the release contract together so the next session starts in the correct place.

## Rider and contributor workflow

- Confirm Rider's solution projects and Git root once per session. Reuse the
  connected IDE; do not add a second MCP server for the same Rider instance.
- For text/file discovery, start with scoped `rg`/`rg --files`. For C# symbols,
  references and dependencies, use Rider's semantic tools with bounded results.
  Read only the relevant line ranges; expand when a result is incomplete.
- Use Rider's build start/state tools for incremental development, and its
  executable test locations when available. The required Release command in
  CONTRIBUTING.md remains the review gate; a Debug IDE build cannot replace it.
  Shared `VaultSync Contracts` and `VaultSync Release Checks` configurations
  provide concise repository validation; full logs and TRX paths are printed.
- Use file/batch diagnostics after code edits. An empty Problems View is not
  fresh analysis; check timeout, partial-result and unsupported-file markers.
- Use the refactoring skill and Rider for semantic refactors; use the debugging
  skill and debugger for runtime questions that source/tests cannot resolve.
  Profile only when performance evidence is needed. Database and game-engine
  tools are used only for tasks involving those systems.
- Batch independent reads and keep tool output small: consume structured
  summaries, retain full logs as local artifacts, and inspect failures narrowly.
  Do not enumerate every tool schema or repeatedly read the full roadmap.
- If an IDE tool fails or cannot discover a test, record the limitation and
  use the repository's CLI checks. Do not interpret missing IDE results as pass.
- Ask the maintainer about unresolved repository conventions or architectural
  choices after checking the canonical contracts; continue independent work.

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
