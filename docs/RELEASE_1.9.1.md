# VaultSync 1.9.1 — Clone Explorer

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.1`; predecessor integration candidate: `1.9.0`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1911`, `VS-1912`, `VS-1913`, `VS-1914`, `VS-1915`, `VS-1916`, `VS-1977`, `VS-1978`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

BUG-19008 tracks the Windows console-capture isolation defect found during
qualification; the focused fix remains subject to native CI and review.

## Assembled release review — 2026-10-03

PR #746 into Dev is the sole release review; preparation source `fix/1.9.1-console-isolation`.
It includes the original release preparation and all its follow-up corrections.
The earlier preparation PR is superseded. No release auto-merge is enabled.
Shared dependency maintenance is propagated atomically with CLI API compatibility.
See [the complete daily ledger](release-evidence/1.9-work-2026-10-03.md).

## Changelog protocol — 2026-10-04

Own development notes use one explicit-target `Unreleased` section and standard
Keep a Changelog categories. Published history is backfilled without rewriting
tags/assets. Original inherited development notes remain in the evidence ledger;
they do not establish shipped predecessor features. Run scripts/changelog.py check
and scripts/validate_development.sh contracts before updating the owning PR.

Current preparation: `fix/1.9.1-console-isolation` / #746. Protected release identity stays `release/1.9.1`. See the family preparation-head map.
