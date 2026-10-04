# VaultSync 1.9 — complete family alignment

Reviewed planning input imported on 2026-10-03. ROADMAP.md remains canonical;
the supplied document is retained verbatim for provenance, not as executable
instructions. Existing GitHub IDs remain immutable by the maintainer's choice.
The source digest and release ownership are in [1.9-family.json](../release/1.9-family.json).

## Release ownership and branch workflow

| Release | Scope | Branch | Stable target |
|---|---|---|---|
| 1.9.0 | Disk and Bootable Recovery Foundation | `release/1.9.0` | 2027-03-26 |
| 1.9.1 | Clone Explorer | `release/1.9.1` | Unscheduled |
| 1.9.2 | Portable Recovery | `release/1.9.2` | Unscheduled |
| 1.9.3 | Offsite Protection | `release/1.9.3` | Unscheduled |
| 1.9.4 | Unified Recovery Experience | `release/1.9.4` | Unscheduled |
| 1.9.5 | Continuous Recovery Assurance | `release/1.9.5` | Unscheduled |
| 1.9.6 | Binary Source Control Foundation | `release/1.9.6` | Unscheduled |
| 1.9.7 | Binary Collaboration and Scale | `release/1.9.7` | Unscheduled |
| 1.9.8 | Stability and LTS Baseline | `release/1.9.8` | Unscheduled |

The 2027-09-24 family horizon is planning context, not eight new release dates.
All release branches are seeded separately; later branch trees are scaffolds,
not proof that earlier products have shipped. Before integration, merge the
qualified predecessor and resolve/revalidate contracts, version consumers and
feature parity. Use reviewable PRs and merge commits into Dev, then Stable;
never reset protected release history. The archive branch preserves all prior
implementation, tests, developer diaries and evidence at its recorded commit.

1.9.0 removes the prematurely integrated resource CLI cohort through a normal
reviewable commit/PR. 1.9.1 adapts help, guides and generated completion to the
flat commands that actually exist there. 1.9.5 retains the entire implemented
resource CLI and related tests, including the finite watcher-preview fix;
its guides/completion describe that newer command tree. Synchronize improvements
from 1.9.1 when integrating 1.9.5; do not make 1.9.1 depend on later commands.
VS-1972/1973/1974/1975 and BUG-19002–19007 follow 1.9.5; VS-1977/1978 remain
1.9.1, VS-1976 remains 1.9.2, VS-1979 remains 1.9.5, VS-1980 remains 1.9.0.
BUG-19001 and legacy text watcher preview/cancellation safety are independent
backports retained in 1.9.0. They add no grouped command tree or JSON contracts.
VS-1902 signing remains feasibility work; delivered VS-1903 stays closed.
VS-1928/1929 are independent maintenance gates, tracked with the LTS baseline;
they do not expand the 1.9.0 feature promise. VS-1943 and candidate VS-1971
remain conditional and are not pulled into execution just by this import.

## Immutable ID crosswalk

The supplied BSC IDs collide with existing CLI/shared-vault tickets. These are
**document aliases only**. Use the existing canonical ID in code, issues,
commits, Project and milestones. Never interpret the alias as that live ticket.

| Supplied BSC alias | Canonical BSC work ID | Existing issue |
|---|---|---|
| `VS-1970` | `VS-1981` | [#703](https://github.com/ATAC-Helicopter/VaultSync/issues/703) |
| `VS-1971` | `VS-1982` | [#704](https://github.com/ATAC-Helicopter/VaultSync/issues/704) |
| `VS-1972` | `VS-1983` | [#705](https://github.com/ATAC-Helicopter/VaultSync/issues/705) |
| `VS-1973` | `VS-1984` | [#706](https://github.com/ATAC-Helicopter/VaultSync/issues/706) |
| `VS-1974` | `VS-1985` | [#707](https://github.com/ATAC-Helicopter/VaultSync/issues/707) |
| `VS-1975` | `VS-1986` | [#708](https://github.com/ATAC-Helicopter/VaultSync/issues/708) |
| `VS-1976` | `VS-1987` | [#709](https://github.com/ATAC-Helicopter/VaultSync/issues/709) |
| `VS-1977` | `VS-1988` | [#710](https://github.com/ATAC-Helicopter/VaultSync/issues/710) |
| `VS-1978` | `VS-1989` | [#711](https://github.com/ATAC-Helicopter/VaultSync/issues/711) |
| `VS-1979` | `VS-1990` | [#712](https://github.com/ATAC-Helicopter/VaultSync/issues/712) |
| `VS-1980` | `VS-1991` | [#713](https://github.com/ATAC-Helicopter/VaultSync/issues/713) |
| `VS-1981` | `VS-1992` | [#714](https://github.com/ATAC-Helicopter/VaultSync/issues/714) |
| `VS-1982` | `VS-1993` | [#715](https://github.com/ATAC-Helicopter/VaultSync/issues/715) |

## Twelve cross-release invariants

1. Stable support follows explicit evidence; unsupported scope is visible.
2. Data formats are versioned with declared reader/writer compatibility.
3. Recovery validates recorded bytes, not just readable metadata.
4. Destructive operations show exact source, target and overwrite scope.
5. Portable recovery works without the original database or installation.
6. Keys and credentials never appear in plaintext recovery bundles or logs.
7. Authoritative objects precede guarded ref publication; caches/indexes rebuild.
8. BSC rejects stale expected heads; no silent last-writer-wins publication.
9. Dirty and untracked workspace data survives checkout, sync and cancellation.
10. GC/repack never retire the last verified reachable recovery copy.
11. Backend limitations, free-space needs and interruption states are explicit.
12. Later BSC gates cannot block earlier recovery releases.

## Per-release stable gates

- **1.9.0:** architecture approval; Capture → Validate → Boot → Restore →
  Validate, supported hardware/VM/OS/filesystem/encryption/live-capture matrix.
  Failed qualification leaves imaging Preview; no universal bare-metal claim.
- **1.9.1:** read-only image browse, search/selective extraction and comparison;
  typed adaptive shell/state/deep links; Dashboard/Recovery migration parity.
- **1.9.2:** standalone utility and emergency kit recover on a clean machine
  without original installation/DB; formats/key recovery/compatibility explicit;
  History/Explorer/Settings migration preserves functionality.
- **1.9.3:** resumable S3/B2/SFTP transfer and manifest validation; incomplete
  upload cleanup, immutability/retention/cost explained; clean-machine restore;
  unified protection, multi-destination health and evidence-based failover.
- **1.9.4:** shared Recovery Inspector and navigation across project/file/image/
  storage/recovery workflows; accessible/localized/theme/compact layout parity
  before removal of legacy UI; project groups only if evidence justifies them.
- **1.9.5:** scheduled full checks and isolated drills, stale/missed/offline/key
  alerts, structured CLI reports, explicit hooks and documented stable parity;
  multi-machine summaries carry visible scope and failure independence.
- **1.9.6:** separate BSC repository; local/NAS create/open, second-machine
  attach/sync/lock/commit/history/exact restore; dirty/corrupt/stale rejection;
  encryption recovery and GC/repair interruption safety. Preview if unqualified.
- **1.9.7:** three-way path planning, explicit binary conflict resolution,
  sparse workspaces, verified cache/resumption and reachable-object replication;
  scale and fault matrix; weak backend CAS permits read-only replicas only.
- **1.9.8:** disk/offsite/portable/BSC format stabilization, long-duration faults,
  migration/large-dataset regression and published compatibility/LTS window.

## Next work

Disk engine, image format and recovery identity contracts remain unapproved;
there is no bootable recovery engine yet. Continue with VS-1910/1917/1918/1919
reviewable decisions and prototypes before destructive implementation.
The preserved CLI is an implemented development slice, not a completed 1.9.5.
BSC implementation has not begun. 1.10 covers compatible incremental expansion;
2.0 remains conditional on an authoritative product-model/compatibility change,
not visual redesign alone.

Current publication, preserved branches, PRs and test limits are recorded in
[the full-roadmap alignment evidence](release-evidence/1.9-realignment-2026-10-03.md).
BUG-19008 tracks the Windows console-isolation issue found in the 1.9.1 slice;
its fix is included in sole release PR #746; current-head native checks and review remain required.

## Consolidated release review and dependency audit

The maintainer requested one PR per release on 2026-10-03: 1.9.0 #736,
1.9.1 #746, 1.9.2 #738, 1.9.3 #739, 1.9.4 #740, 1.9.5 #747,
1.9.6 #742, 1.9.7 #743 and 1.9.8 #744. All remain draft; none is eligible for
automatic release promotion. Duplicate follow-up/preparation PRs are superseded
and branches are preserved. Preparation heads need not rewrite protected refs.
See [the complete daily ledger](release-evidence/1.9-work-2026-10-03.md) and
[dependency removal/replacement evidence](release-evidence/1.9-dependencies-2026-10-03.md).
BUG-19010 and BUG-19011 are shared maintenance foundation defects, propagated
to implemented later CLI slices without changing ownership or claiming release.

## Current preparation heads — 2026-10-04

Use these draft PRs for ongoing work. Protected `release/*` refs retain their
release identities and reviewed history; they are not necessarily the assembled
preparation head. One release preparation PR targets Dev for each version.

| Release | Preparation head | Sole draft PR | Superseded preparation |
| --- | --- | --- | --- |
| 1.9.0 | `work/1.9-roadmap-realignment` | [#736](https://github.com/ATAC-Helicopter/VaultSync/pull/736) | — |
| 1.9.1 | `fix/1.9.1-console-isolation` | [#746](https://github.com/ATAC-Helicopter/VaultSync/pull/746) | — |
| 1.9.2 | `work/1.9.2-changelog-protocol` | [#752](https://github.com/ATAC-Helicopter/VaultSync/pull/752) | #738 |
| 1.9.3 | `work/1.9.3-changelog-protocol` | [#753](https://github.com/ATAC-Helicopter/VaultSync/pull/753) | #739 |
| 1.9.4 | `work/1.9.4-changelog-protocol` | [#754](https://github.com/ATAC-Helicopter/VaultSync/pull/754) | #740 |
| 1.9.5 | `docs/1.9.5-package-guide` | [#747](https://github.com/ATAC-Helicopter/VaultSync/pull/747) | — |
| 1.9.6 | `work/1.9.6-changelog-protocol` | [#755](https://github.com/ATAC-Helicopter/VaultSync/pull/755) | #742 |
| 1.9.7 | `work/1.9.7-changelog-protocol` | [#756](https://github.com/ATAC-Helicopter/VaultSync/pull/756) | #743 |
| 1.9.8 | `work/1.9.8-changelog-protocol` | [#757](https://github.com/ATAC-Helicopter/VaultSync/pull/757) | #744 |

All remain draft. Shared changelog validation passed locally on every owning
head; this is not platform, predecessor, disk or release qualification. Future
release dates remain unscheduled. Project target dates use the existing family
planning horizon, not an invented publication commitment.
