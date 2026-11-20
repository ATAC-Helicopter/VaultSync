# VaultSync 1.9.6 — Binary Source Control Foundation

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.6`; predecessor integration candidate: `1.9.5`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1981`, `VS-1982`, `VS-1983`, `VS-1984`, `VS-1985`, `VS-1986`, `VS-1987`, `VS-1989`, `VS-1990`, `VS-1992`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Recovery evidence and limitations — 2026-11-11

For canonical BSC IDs VS-1983/1987/1990, distinguish verified repository
objects/graphs from independently materialized changeset bytes and from
application-specific or compiler/build behavior. Exact BSC byte reconstruction
does not mean a game project builds, produces identical binaries or runs on
a different toolchain/architecture (including hardware-specific compiler
flags). No arbitrary project build or post-checkout script is an automatic
recovery probe. Maintain explicit history, key, format, index/pack and GC
fault evidence and Preview status until the existing BSC stable gate passes.
This does not block earlier disk releases. See
[evidence and semantic scope](RECOVERY_EVIDENCE_SCOPE_1.9.md).
