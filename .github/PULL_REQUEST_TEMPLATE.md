## Summary
-

## Linked Issues
Refs #
<!-- Use Closes # only when this PR's merge completes the issue. -->

## Stack (if applicable)
- Parent PR and branch:
- Final target branch:
- Merge order:
- Focus of this layer:

## Validation
- [ ] Exact reviewed head SHA, `global.json` SDK and environment recorded
- [ ] Targeted affected test(s) or script checks completed, with exact outcome
- [ ] Relevant build/full suite completed when scope or release gates require it
- [ ] Independent verification distinguished from contributor-provided results
- [ ] Release-impacting change: `powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -TargetVersion <version> -ReleaseTrack <track> -TargetMilestone <milestone>`
- [ ] Script/workflow change: relevant script tests, for example `python -m unittest tests.scripts.test_download_stats`
- [ ] Manual UI/CLI check, if applicable:

## Release Notes
- [ ] `CHANGELOG.md` updated under a Keep a Changelog category, or not needed
- [ ] `python3 scripts/changelog.py check` passes
- [ ] `docs/WHATS_NEW.md` updated or not needed
- [ ] `ROADMAP.md` updated or not needed
- [ ] Owning issue, release PR and native Project status/dates align with actual evidence

## Risk
-

## Classification and merge policy
- [ ] Exactly one exact `release:X.Y.Z`, one `priority:*`, one `status:*`, at least one `area:*`
- [ ] One `kind:*`, except version-prefixed release umbrella PRs with `area:release`
- [ ] Issue and Project #7 ownership/milestone reflect the proposed scope
- [ ] Non-Stable: merge commit preserves individual commits; Stable: qualified
      first-parent promotion/hotfix merge commit only (not squash)

## Work tracking

Include the canonical work ID in the title and `Refs #N` for the owning issue.
Reuse the sole release PR and keep release-gated issues open. Run `./dev status` before pushing.
