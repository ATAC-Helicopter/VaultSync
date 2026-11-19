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

## Recovery evidence and limitations — 2026-11-11

Clone Explorer is **read-only inspection**, not a recovery qualification
service. VS-1911–1914 should distinguish a discovered image, parsed filesystem,
byte-verified file and completed selective extraction. Every claim names
content/profile/reader version, bytes actually checked, and unsupported or
truncated cases. Searching or extracting a file does not establish a valid
database/application state, independently bootable restored OS, or successful
whole-system restore. Unknown formats fail closed; do not execute files merely
to inspect them. See [evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
No additional beta.1 gate or new shipped capability is implied.
