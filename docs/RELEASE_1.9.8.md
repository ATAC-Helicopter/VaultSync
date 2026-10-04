# VaultSync 1.9.8 — Stability and LTS Baseline

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.8`; predecessor integration candidate: `1.9.7`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1961`, `VS-1962`, `VS-1963`, `VS-1928`, `VS-1929`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Changelog protocol — 2026-10-04

Own development notes use one explicit-target `Unreleased` section and standard
Keep a Changelog categories. Published history is backfilled without rewriting
tags/assets. Original inherited development notes remain in the evidence ledger;
they do not establish shipped predecessor features. Run scripts/changelog.py check
and scripts/validate_development.sh contracts before updating the owning PR.

Current preparation: `work/1.9.8-changelog-protocol` / #757. Protected release identity stays `release/1.9.8`. See the family preparation-head map.
