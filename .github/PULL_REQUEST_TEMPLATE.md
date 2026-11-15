## Summary
-

## Linked Issues
Closes #

## Validation
- [ ] Exact reviewed head SHA, SDK, operating system and test results recorded
- [ ] Smallest affected test suite run independently; exited successfully (not only xUnit result)
- [ ] Relevant build / full suite completed when required by the release gate
- [ ] Serial MSBuild (`-m:1 -p:BuildInParallel=false`) used if shared artifacts collide
- [ ] Release-impacting change: `powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -TargetVersion <version> -ReleaseTrack <track> -TargetMilestone <milestone>`
- [ ] Script/workflow change: relevant script tests, for example `python -m unittest tests.scripts.test_download_stats`
- [ ] Manual UI/CLI check, if applicable:

## Release Notes
- [ ] `CHANGELOG.md` updated or not needed
- [ ] `docs/WHATS_NEW.md` updated or not needed
- [ ] `ROADMAP.md` updated or not needed

## Metadata and merge policy
- [ ] One `priority:*`, `status:*`, exact `release:X.Y.Z`, and at least one `area:*`
- [ ] One `kind:*` unless a version-prefixed `area:release` umbrella PR
- [ ] Owning issue, milestone and Project #7 fields align with the release
- [ ] Non-Stable: **Create a merge commit**; Stable: qualified **first-parent** merge commit
- [ ] Confidential security details withheld from public PRs until coordinated disclosure

## Risk
-
