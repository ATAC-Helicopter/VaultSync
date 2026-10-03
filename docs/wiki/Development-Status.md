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

## Qualification follow-up

The 1.9.1 Windows suite exposed BUG-19008 / #745; [fix PR #746](https://github.com/ATAC-Helicopter/VaultSync/pull/746)
has passed full Windows/Linux/macOS checks and CodeQL; integration still requires
external review. Sonar fails before analysis while
looking up the new PR key; a rerun did not resolve it. Full remote CI is not
claimed green. The corrected release/1.9.0 tree still awaits [#736](https://github.com/ATAC-Helicopter/VaultSync/pull/736)
external review/merge under the repository rulesets.

[Handbook/CI fix #747](https://github.com/ATAC-Helicopter/VaultSync/pull/747)
corrects the 1.9.5 package examples. BUG-19009 / #748 ensures protected PRs
run required checks even when only embedded Markdown changes.
