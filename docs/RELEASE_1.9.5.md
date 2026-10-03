# VaultSync 1.9.5 — Continuous Recovery Assurance

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.5`; predecessor integration candidate: `1.9.4`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1951`, `VS-1952`, `VS-1953`, `VS-1954`, `VS-1955`, `VS-1956`, `VS-1979`, `VS-1972`, `VS-1973`, `VS-1974`, `VS-1975`, `BUG-19002`, `BUG-19003`, `BUG-19004`, `BUG-19005`, `BUG-19006`, `BUG-19007`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Assembled release review — 2026-10-03

PR #747 into Dev is the sole release review; preparation source `docs/1.9.5-package-guide`.
It includes the original release preparation and all its follow-up corrections.
The earlier preparation PR is superseded. No release auto-merge is enabled.
Shared dependency maintenance is propagated atomically with CLI API compatibility.
See [the complete daily ledger](release-evidence/1.9-work-2026-10-03.md).
