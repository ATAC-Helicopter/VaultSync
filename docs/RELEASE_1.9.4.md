# VaultSync 1.9.4 — Unified Recovery Experience

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.4`; predecessor integration candidate: `1.9.3`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1941`, `VS-1942`, `VS-1943`, `VS-1944`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Recovery evidence and limitations — 2026-11-11

VS-1942/1944's shared Recovery Inspector must separate recorded copy,
payload-integrity proof, restored/read-back bytes, measured cold boot,
optional named application/service checks and failure independence. Present
actual method, build, profile, time/freshness, covered scope and unresolved
dependencies. Accessible labels must distinguish passed, failed, interrupted,
unsupported, inconclusive, stale and not-run. A green aggregate cannot silently
upgrade a missing boot/app check. Historical 1.8.x file proofs retain their
limited meaning. This is a later UI contract, **not** a 1.9.0 UI claim. See
[evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
