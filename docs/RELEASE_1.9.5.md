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

## Recovery evidence and limitations — 2026-11-11

VS-1951–1954/1956/1979 add **opt-in**, scheduled, bounded verification/drill
policies and structured evidence reporting. A scheduled integrity check does
not count as a boot drill; an isolated VM cold boot counts only for qualified
compatible profiles. Do not boot arbitrary user backups, run recovered
programs or execute unreviewed application check scripts automatically.
Explicit consent, disposable targets, trusted runner isolation, bounded CPU/
I/O/storage/time, network-off defaults and clear failure/skip reports are
prerequisites for optional VM/application probes. Human/hardware drills retain
independent operator and environment provenance. Reports show last-run,
stale/unsupported/not-run and missed states without hidden telemetry.
Do not introduce any of this as a new 1.9.0-beta.1 feature. See
[evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
