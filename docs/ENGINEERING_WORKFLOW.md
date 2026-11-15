# VaultSync: engineering workflow and review requirements

This is the maintenance-line operational guide. The active 1.9 development
policy is also documented in `CONTRIBUTING.md` and the corresponding
`docs/ENGINEERING_WORKFLOW.md` on the 1.9 preparation branch.

## Merge policy: Stable is the default mainline

- All PRs targeting **non-Stable** branches: choose GitHub's **Create a merge
  commit**. Do not squash or rebase; preserve the individual original commits
  and contributor attribution.
- For **Stable**: use exactly one **release/hotfix merge commit** in the
  **first-parent history** per qualified promotion. A genuine Git merge
  keeps source commits reachable in the full graph; it does not collapse
  them. `git log --first-parent --oneline Stable` is the intended release view.
  If literally only one reachable commit is desired, GitHub **Squash and merge**
  is a different policy, not a merge commit. Do not silently conflate them.
- No direct feature/fix pushes to Stable, no force updates, no bypassed CI.
  Isolate hotfixes in reviewed branches; leave release PRs Draft until all
  gates are satisfied.

## GitHub ruleset enforcement — manual owner action

As audited on 2026-11-14, the repository has:
- **Stable**: [merge-only release-spine ruleset](https://github.com/ATAC-Helicopter/VaultSync/rules/21286364), already allows only `merge`.
- **Dev and release/***: [history-preservation ruleset](https://github.com/ATAC-Helicopter/VaultSync/rules/21286363), already allows only `merge`.
- **UI**: [linear-history ruleset](https://github.com/ATAC-Helicopter/VaultSync/rules/21286365) currently allows **only squash/rebase** and requires linear history. This conflicts with the policy of preserving all commits on non-Stable branches. The repository owner must edit that ruleset: disable **Require linear history**, select **Allow merge commits** as the only merge method, and keep required review/thread protections. Do not remove protection merely to work around the conflict.
- Other unprotected preparation branches need maintainers to select **Create a merge commit** manually unless a dedicated ruleset is configured without preventing ordinary development pushes.

Repository ruleset updates require admin permission and are **not** completed by changing this documentation. Verify live rulesets before stating the policy is fully enforced.

## Mandatory issue, PR and Project classification

- Use the canonical work ID (`VS-xxxx` or `BUG-xxxxx`) and link to
  an owning issue via `Refs #N` while release-gated, not `Closes` on
  an intermediate preparation merge.
- Label each PR with one valid `kind:*` (except version-prefixed umbrella
  releases under `area:release`), at least one `area:*`, exactly one
  `priority:p0..p3`, one `status:*` and one exact `release:X.Y.Z`.
  A family label may coexist but cannot replace the exact release label.
- Project #7 contains owning issues; keep milestone, assignee, Priority,
  Area, Release and meaningful date/status fields consistent. Do not
  duplicate Project cards for every PR; use Issues as work records.
- The Work Tracking job validates labels and issue links on PR and label
  changes. This becomes a **merge requirement only when configured** as
  a required status check on the target branch. An action alone does not
  enforce branch protection. Manual Project-field checking remains necessary
  when GraphQL access or field-specific automation is unavailable.

## Independent verification with minimal wasted work

1. Check the current PR head SHA and compare its diff to the trusted
   published source. Treat new dependencies, CI changes and links as
   untrusted until reviewed.
2. Use disposable test fixtures and an isolated detached worktree at the
   exact head SHA; respect `global.json` and install the pinned SDK
   side-by-side rather than changing the pin.
3. Run the smallest relevant tests with single-threaded MSBuild for
   independent verification when shared project outputs collide:

   ```bash
   dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj \
     --configuration Release --filter 'FullyQualifiedName~RsyncRunnerTests' \
     -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -warnaserror
   ```

4. Check the **process exit code** as well as the test count. A passing
   xUnit run together with a failed MSBuild is **not** an accepted build.
   Contributors' self-reported counts and a tracking-only workflow do not
   substitute for independent CI. Recheck changed heads.
5. For maintenance backports, require identical production-code baseline
   against the **published latest Stable tag**; cherry-pick only the
   applicable behavior, with negative and ordinary positive regression
   tests. Record the backport in the release notes and retain unrelated
   1.9 features outside the maintenance release.
6. Before any Stable promotion: exact-head build/test matrix on Windows,
   Linux and macOS, native packaging and runtime checks, updater/rollback,
   integrity/provenance and security acceptance. Do not run global suites
   repeatedly for doc-only edits or claim unperformed QA passed.

## Confidential vulnerability coordination

Use the **private GitHub Security Advisory** and temporary private fork
for report details, reproduction, patch review and reporter collaboration.
Do not add exploit-sensitive examples, private hashes/fork paths or draft
advisory details to public PRs, issue comments, changelog, CI logs or
community channels. Invite reporters to the advisory, not repository
administration. Validate platform-specific link and filesystem-race
boundaries privately, coordinate the qualified fix's release and only
then publish the advisory with reporter credit.

See [SECURITY.md](../SECURITY.md) and [RELEASING.md](RELEASING.md).
