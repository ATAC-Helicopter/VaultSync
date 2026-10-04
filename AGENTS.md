# Release preparation: VaultSync 1.9.1

This copy prepares 1.9.1; it does not change the global active product release 1.9.0.
Read ROADMAP.md, release/1.9-family.json, CONTRIBUTING.md and docs/RELEASE_1.9.1.md.
Owning draft release PR: #746 into Dev; current public head: `fix/1.9.1-console-isolation`.
Use this preparation copy only for this release or shared standards; do not switch
it to the 1.9.0 head or import its unreleased features. The main 1.9.0 workspace
remains work/1.9-roadmap-realignment / #736. Protected release refs require
reviewed PRs; this preparation head preserves their existing commits.
The complete current-head map is in docs/RELEASE_FAMILY_1.9.md.

## Working copy and delivery

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
- Cross-release documentation/validation standards apply to every open release
  PR, using its real head and active metadata. Never copy one release's
  unreleased features into another; `release/1.9-family.json` owns the IDs.
- Keep #746 draft until its actual release gates pass. Do not merge or promote
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
