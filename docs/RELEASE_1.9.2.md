# VaultSync 1.9.2 — Portable Recovery

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.2`; predecessor integration candidate: `1.9.1`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1931`, `VS-1932`, `VS-1933`, `VS-1934`, `VS-1935`, `VS-1976`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Recovery evidence and limitations — 2026-11-11

For VS-1931–1934/1976, separately demonstrate clean-machine discovery,
compatible independent reader/unlock, exact restored bytes and any further
declared checks. Do not silently depend on the source SQLite database or
installation. Missing credentials/keys, format incompatibility and device/
software-version mismatches remain explicit. Restoring bytes does not prove
Keychain/DPAPI/TPM unlock, licenses, drivers or application correctness.
A missing application test is "not tested", not "passed". See
[evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
