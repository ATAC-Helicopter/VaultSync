# Changelog protocol delivery — 2026-10-04

All 44 published sections and own unreleased entries use Keep a Changelog 1.1.0
and at most 22 words, with original history and inherited development evidence
archived separately. No published tags/assets were rewritten or future features
imported into earlier releases. Shared contract checks and 113 Python tests pass
on all nine preparation copies. Main 1.9.0 also passes the zero-warning Release
build and 937 .NET tests via Rider. Remote CI is separate, head-specific evidence.

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
