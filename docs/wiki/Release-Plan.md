# Recovery Horizon release plan

The maintainer selected immutable existing work IDs and separation by release.

| Release | Scope |
|---|---|
| 1.9.0 | Disk and bootable recovery foundation |
| 1.9.1 | Clone Explorer; legacy CLI discovery, help and completion |
| 1.9.2 | Portable recovery |
| 1.9.3 | Offsite protection |
| 1.9.4 | Unified recovery experience |
| 1.9.5 | Continuous recovery assurance; preserved resource CLI rework |
| 1.9.6 | Binary source control foundation |
| 1.9.7 | Binary collaboration and scale |
| 1.9.8 | Stability and LTS baseline |

1.9.0 keeps its March 26, 2027 planning target. Later releases remain unscheduled;
September 24, 2027 is the existing family horizon, not a per-release commitment.
All branches preserve prior work and require predecessor integration and native
qualification before promotion. The supplied BSC VS-1970–1982 aliases map to
canonical VS-1981–1993, issues #703–715; existing CLI IDs are not reused.

[Canonical family contract and detailed crosswalk](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/RELEASE_FAMILY_1.9.md)
contains complete stable gates and branch workflow. [Development status](Development-Status.md)
separates implemented slices from pending engines and release evidence.

## One draft PR per release

| Release | Sole PR | Work present |
|---|---|---|
| 1.9.0 | [#736](https://github.com/ATAC-Helicopter/VaultSync/pull/736) | Foundation contracts, safety/maintenance, dependency cleanup and tracking |
| 1.9.1 | [#746](https://github.com/ATAC-Helicopter/VaultSync/pull/746) | Explorer contract; legacy CLI discovery, bundled guides/completion and console fix |
| 1.9.2 | [#738](https://github.com/ATAC-Helicopter/VaultSync/pull/738) | Portable metadata/contract scaffold |
| 1.9.3 | [#739](https://github.com/ATAC-Helicopter/VaultSync/pull/739) | Offsite metadata/contract scaffold |
| 1.9.4 | [#740](https://github.com/ATAC-Helicopter/VaultSync/pull/740) | Unified recovery metadata/contract scaffold |
| 1.9.5 | [#747](https://github.com/ATAC-Helicopter/VaultSync/pull/747) | Assurance contract and preserved resource CLI/fixes |
| 1.9.6 | [#742](https://github.com/ATAC-Helicopter/VaultSync/pull/742) | BSC foundation scaffold |
| 1.9.7 | [#743](https://github.com/ATAC-Helicopter/VaultSync/pull/743) | Collaboration/scale scaffold |
| 1.9.8 | [#744](https://github.com/ATAC-Helicopter/VaultSync/pull/744) | Stability/LTS scaffold |

All remain draft. Preparation heads preserve protected release history; no
release has been promoted. Original duplicate PRs are superseded and retained.
