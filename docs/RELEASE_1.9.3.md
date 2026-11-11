# VaultSync 1.9.3 — Offsite Protection

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.3`; predecessor integration candidate: `1.9.2`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1921`, `VS-1922`, `VS-1923`, `VS-1924`, `VS-1925`, `VS-1926`, `VS-1927`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Recovery evidence and limitations — 2026-11-11

For VS-1923–1925/1927, distinguish (a) uploaded object/manifest existence,
(b) actual remote download and integrity, (c) clean-machine restore of named
bytes, and (d) dependency/failure-domain independence. None implies the next.
Two buckets in one provider account may share the same account/key failure;
remote availability and future provider access are time-bounded observations.
Keep incomplete transfers, revoked credentials, incompatible versions, cost/
retention limits and untested application state visible. Do not report
"recoverable" merely because a remote upload completed. See
[evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
