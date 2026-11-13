# VaultSync Engineering Workflow: Review, Tracking, Security and Release

This guide consolidates operational decisions. For detailed contracts see
[CONTRIBUTING](../CONTRIBUTING.md), [work tracking](WORK_TRACKING.md),
[workspaces](VALIDATION_WORKSPACES.md), [releasing](RELEASING.md),
and [security](../SECURITY.md).

## Merge policy and release ownership

- **Non-Stable target branches:** always **Create a merge commit**, preserving all
  individual commits and authors. Do not squash/rebase contributor work.
- **Stable:** one *real merge commit* per qualified release or emergency hotfix
  on Stable's **first-parent history**. No direct feature/fix commits, rebase,
  fast-forward or force push. Git's complete DAG still exposes incoming
  commits under that merge. Getting literally one commit in the entire history
  would require squashing, which is *not* a merge commit.
- For 1.9.0, independent fixes target `work/1.9-roadmap-realignment` and
  enter `Dev` through the single consolidated release PR #736. Later releases
  retain their own sole preparation PR and predecessor integration gates.
- Stable maintenance/security patches stay isolated from later 1.9 features;
  use the existing release PR and never publish directly from an unqualified
  development head.

## Issue, PR, labels and Project #7

- Reuse exactly one owning canonical `VS-xxxx` or `BUG-xxxxx` issue, linked
  in the PR as `Refs #N` while release-gated, or `Closes #N` only when
  closing the issue is actually appropriate.
- Every engineering PR must have **one** exact `release:X.Y.Z` label, **one**
  `priority:p0..p3`, **one** `status:*`, at least one `area:*`, and
  exactly one `kind:*`. Version-prefixed `area:release` umbrella PRs may
  omit `kind:*` because they aggregate mixed work. An additional family
  label `release:X.Y.x` is allowed.
- The Work Tracking workflow checks reference and label completeness when a
  PR changes or its labels change. To make this a **merge requirement**, add
  `Tracking policy` to the target branch's required checks **once the
  workflow is present there**. A successful tracking check is *not* CI or
  release qualification.
- Project #7: make the owning **Issue** the work item (auto-add filter
  `is:issue`, not a duplicate card for every PR). Align its Status, Priority,
  Area, Release, milestone, assignee and dates to the issue and release plan.
  A release-gated merged implementation stays **In progress** until accepted
  and shipped; only then mark **Done** and close as completed.
- `./dev audit --release <version>` or `--all-releases` requires a complete
  API/Project snapshot; missing permissions or partial data do not count as a
  pass. Project date sync needs its configured token and field types; do
  not assume it is active.

## Minimal independent review and test sequence

1. Read the exact head SHA and scoped diff. Review tests, process invocation,
   path handling, workflow edits, dependencies and secret/network access.
2. Compare tests claimed by the contributor with actual independent evidence.
   A local report, `action_required`, or tracking-only CI is not proof that
   .NET builds and regressions passed.
3. Use a detached **worktree**, disposable data and the exact reviewed SHA.
   Respect the pinned SDK in `global.json`; install it side-by-side rather
   than changing that pin to bypass a test.
4. Run the smallest relevant test filter first. Only broaden to affected
   build, full core suite, or native Windows/macOS/Linux qualification when
   scope or release/safety gate requires it. Docs-only changes need relevant
   formatting, linkage and script tests; do not run unrelated full suites.
5. Record command, SDK, OS, SHA, pass/fail counts, exclusions, and remaining
   risks. Re-review if the head changes, then merge using the target policy.

### Example: PR #849 (rsync cleanup)

From the primary repository checkout:

```bash
git fetch origin pull/849/head
git worktree add --detach ../VaultSync-pr849-test FETCH_HEAD
cd ../VaultSync-pr849-test
git rev-parse --short HEAD
dotnet --version
dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~RsyncRunnerTests' \
  -warnaserror -p:UseSharedCompilation=false
```

The reviewed PR #849 head is `817d9e0d` as of 2026-11-14. The SDK
pin is `10.0.404`; install it if unavailable. The expected regression
filter selects three tests. Do not merge based solely on the contributor's
reported run. After retaining the test receipt and returning to the primary
checkout, remove *only* the worktree you created:

```bash
git worktree remove ../VaultSync-pr849-test
```

Do not use broad `git clean` or `reset --hard` to clean test fixtures.

## Private security workflow

- Receive and investigate sensitive reports through a **draft private GitHub
  Security Advisory**, not a public exploit issue, PR or chat log.
- Use its temporary private fork for confidential regression tests, evidence
  and minimal remediation. Invite the reporter as an *advisory collaborator*
  for independent review, without granting general repository write/admin.
  A private Discord channel is suitable for coordination, not canonical
  security evidence.
- Validate a real affected shipped baseline, normal safe controls, boundary
  refusals and platform-specific filesystem semantics/races. Report known
  unqualified scenarios; never infer remote exploitability or cross-platform
  safety from disposable Linux tests alone.
- Complete private review and release gates before coordinated integration,
  release, public advisory and contributor attribution. Backport to supported
  future development branches after it is safe to disclose.
- Never include embargoed paths, reproduction payloads, private fork commit
  references, private email contents or secrets in public branch documents.

## Efficient agent and workflow operation

Use the short `AGENTS.md` and only the relevant domain contract; scoped
searches, minimal diff, targeted tests and bounded logs. Avoid redundant
full-suite retries, overlong context dumps, model/reasoning routing or
automatic permissions changes. Preserve existing project history. A request
to prepare a merge is not authorization to publish a release or bypass
protected branch rules.
