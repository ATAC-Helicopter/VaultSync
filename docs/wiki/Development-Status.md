# Development status — Recovery Horizon

Current published stable: **1.8.9**, September 16, 2026. The complete supplied
roadmap was reconciled on October 3, 2026. Existing work IDs remain unchanged;
the BSC aliases in the supplied document have an explicit crosswalk.

## Current release and preserved work

1.9.0 owns disk capture/validation, bootable media, disk restore and complete
post-restore evidence. Architecture approval and the disk/boot implementation
are still pending. The draft typed route contract is the current UI design work.

The implemented resource CLI has moved to **release/1.9.5**. Help, legacy
handbook and completion belong to **release/1.9.1**, adapted to the command tree
present there. Later release branches are scaffolds and do not establish product
readiness. Independent project-removal and text watcher safety fixes remain in
the 1.9.0 work. All prior work is retained in archive/1.9-before-roadmap-20261003.

See [complete release plan](Release-Plan.md),
[CLI development](CLI-Development.md),
[family contract](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/RELEASE_FAMILY_1.9.md),
and [Project #7](https://github.com/users/ATAC-Helicopter/projects/7).
Protected release integration remains PR-based; branch creation does not mean
that integration, native qualification or publication has completed.

## Remaining gates

Approve VS-1910/1917/1918/1919 before destructive disk implementation. Qualify
Capture → Validate → Boot → Restore → Validate on the declared matrix.
Portable, offsite, unified recovery, assurance and BSC follow in their own
releases. BSC gates cannot block earlier releases. No new individual patch dates
were invented; the existing family horizon remains planning context.

## Consolidated release PRs and qualification

There is one open draft PR per release: 1.9.0 #736, 1.9.1 #746,
1.9.2 #738, 1.9.3 #739, 1.9.4 #740, 1.9.5 #747, 1.9.6 #742,
1.9.7 #743 and 1.9.8 #744. Duplicate preparation/fix PRs are closed as
superseded; source branches and commits are preserved. All release PRs remain
draft and none is enabled for automatic merge.

Dependabot #661 (test SDK) and #720 (CodeQL) were reviewed and merged into Dev.
BUG-19010/#749 fixes incomplete rendering-family updates; BUG-19011/#750 ports
CLI overrides with the Spectre API upgrade. Those fixes are assembled in 1.9.0
and propagated to the implemented 1.9.1/1.9.5 CLI variants. Unused Inter font
packaging is removed; remaining library consumers and replacement candidates
are documented in the dependency inventory.

Full local suites after propagation: 1.9.0 937 .NET/107 Python; 1.9.1 942/116;
1.9.5 1056/116, warning-free full solution builds. BUG-19008 fixes captured console
disposal; BUG-19009 schedules mandatory workflows for embedded docs changes.
Current-head remote CI must be rechecked after release regrouping. Sonar previously
failed before analysis while looking up new PR keys; it is not claimed green.

[Complete daily work ledger](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/1.9-work-2026-10-03.md)
and [dependency audit](https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/1.9-dependencies-2026-10-03.md)
record actual work, preserved history and remaining release gates.
