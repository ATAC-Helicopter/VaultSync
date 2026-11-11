# VaultSync 1.9.7 — Binary Collaboration and Scale

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.7`; predecessor integration candidate: `1.9.6`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1988`, `VS-1991`, `VS-1993`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Recovery evidence and limitations — 2026-11-11

VS-1988/1991/1993 must separately evidence deterministic merge *planning*
and conflicts, verified sparse/full materialization, cache/transfer byte
integrity, complete reachable-object replication and multi-client NAS/backend
qualification. A merge without textual conflict is not proof that a binary
asset remains semantically valid in a particular reader/version. Weak CAS,
missing objects and uncertain correlated failure domains remain explicit.
Do not execute retrieved assets or scripts as an implicit validation.
See [evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
