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

## Recovery evidence and limitations — 2026-11-11

VS-1961–1963 must validate evidence interpretation/version compatibility
alongside disk, portable, offsite and BSC formats. Migration tests preserve
older observations without silently converting stale, unsupported or untested
checks into current successes. Long-duration and large-data regression
measures disk bytes, independent restore/boot where supported, and separately
declared application probes, key/toolchain/OS changes, cancellation and real
hardware scope. An LTS window is a documented reader/writer/recovery contract,
not a guarantee of semantic correctness for every archived application or
historical compiled binary. See
[evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
