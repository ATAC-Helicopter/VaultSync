## Summary
-

## Linked Issues
Refs #
<!-- Use Closes # only when this PR's merge should close the issue. -->

## Stack (if applicable)
- Depends on:
- Merge order and final target:

## Validation
- [ ] `dotnet build VaultSync.sln --no-restore -m:1 /p:UseSharedCompilation=false`
- [ ] `dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj --no-restore -m:1 /p:UseSharedCompilation=false`
- [ ] Release-impacting change: `powershell -ExecutionPolicy Bypass -File scripts/release_readiness_gate.ps1 -TargetVersion <version> -ReleaseTrack <track> -TargetMilestone <milestone>`
- [ ] Script/workflow change: relevant script tests, for example `python -m unittest tests.scripts.test_download_stats`
- [ ] Manual UI/CLI check, if applicable:

## Release Notes
- [ ] `CHANGELOG.md` updated or not needed
- [ ] `docs/WHATS_NEW.md` updated or not needed
- [ ] `ROADMAP.md` updated or not needed

## Risk
-
