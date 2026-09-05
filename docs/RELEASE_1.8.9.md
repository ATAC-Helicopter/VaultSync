# VaultSync 1.8.9 — Bug fixes and everyday polish

Status: unreleased; active implementation and qualification.

## Release identity

| Field | Value |
| --- | --- |
| Current stable | `1.8.8` / `v1.8.8`, published 2026-09-02 |
| Active target | `1.8.9`, stable channel, planned stage |
| Planning started | 2026-09-05 |
| Stable target | 2026-09-09 |
| Maximum date | 2026-09-16 |
| Working branch | `release/1.8.9` |
| Integration / promotion | `Dev`, then `Stable` through merge commits |
| Primary patch predecessor | Exact `1.8.8`; new-release qualification pending |
| Additional patch candidates | `1.8.2`, `1.8.3`, `1.8.5`, `1.8.6`, `1.8.7` per platform, only if final inventory qualification accepts them |
| Store package version | `1.8.9.0`; upload qualification pending |
| Tagline | *Keep your place. Work with clarity.* |

The seven-day target and fourteen-day ceiling run from the September 2 Stable
release. P0 qualification blocks promotion; unfinished non-blocking work moves
to the next patch. Patch assets remain opt-in and the installer fallback must
be qualified for the exact predecessor. Historical 1.8.8 notes remain in the
changelog and roadmap; its superseded working contract has been removed.

## Scope

This patch prioritizes UI continuity,
confirmed workflow fixes, and modest visual improvements. It does not change the
backup format or introduce the larger 1.9 recovery features.

## Review findings and changes

| Finding | Change |
| --- | --- |
| Backup history refresh rebuilt every project group, resetting expansion and loaded pages. | Reconcile groups by project ID, update summaries in place, explicitly bind expansion two-way, and preserve the number of loaded rows. |
| Shared collection reconciliation replaced an existing item when inserting before it. | Insert the new item and retain surviving items, avoiding destructive replacement notifications across all callers. |
| Projects and backup project summaries cleared their bound lists during reload. | Reconcile refreshed lists without a collection Reset. |
| Rebuilt project models could send selection back to the first project. | Match the previous selected project by ID before applying fallback selection. |
| Icon-and-label pill stacks could stretch independently of centered text. | Center stack containers inside shared status pills and backup tags. |
| Muted text and light-theme semantic colors were too faint. | Increase muted-text contrast, darken light-theme success/warning/error colors, and use dark labels on the dark theme's blue accent. |
| Custom-theme muted text was blended toward its background. | Apply the existing readable-text contrast check against all three configured surfaces. |

The shared collection fix applies to existing callers in Backups, Projects,
Dashboard, History, Schedule, Recovery, project folders, and the tray panel.
It preserves surviving item identities; it does not make newly constructed row
models identical to their predecessors. Backup project groups explicitly retain
their instances and expansion state. Removing content above the viewport can
still move content naturally, and deleting the final group can clamp scrolling
to the new page extent.

## Automated validation

- Full .NET suite: **847 passed, 0 failed, 0 skipped** on Linux, including the
  Avalonia application build.
- New regression coverage: insertion without replacing survivors, reorder and
  removal without Reset, expanded and collapsed group refreshes, loaded-page
  preservation after deletion, summary updates, and removal of an empty group.
- Existing backup comparison, paging, and theme tests pass.
- Release preparation: **74 Python script tests passed**; canonical/public
  metadata consumer validation and `git diff --check` passed.

## Desktop qualification still required

Automated model checks do not prove the rendered scroll offset, focus behavior,
or text alignment on each desktop platform. Before release:

1. On Backups, expand several projects, load more than 20 rows, scroll down,
   and delete a backup. Check expansion, loaded depth, viewport, and keyboard
   focus after both cancellation and confirmation.
2. Repeat with the newest backup, the last backup in a group, and background
   backup completion. Check that totals update and removed groups disappear.
3. Refresh Projects with a non-first project selected; exercise History,
   Schedule, Recovery, and Dashboard while scrolled down.
4. Inspect dark, light, and custom themes in normal and compact density at
   100%, 150%, and 200% scaling, including long translated pill labels.
5. Capture the isolated demonstration profile following
   [the screenshot library guidance](images/README.md). Refresh
   `Backup_Page.png`, `Projects_Page.png`, and the two theme gallery images
   after visual verification. Existing screenshots were not replaced with
   unverified renders in this pass.

Windows/macOS desktop verification and a live Linux scroll/focus walkthrough
are not yet recorded. These are outstanding qualification steps, not completed
test claims. Version stamping is prepared; publication and final artifact
qualification remain pending.

## Tracked work

| ID | Work | State |
| --- | --- | --- |
| [`BUG-18155` / #628](https://github.com/ATAC-Helicopter/VaultSync/issues/628) | Preserve backup group expansion and loaded history during refreshes | In progress |
| [`BUG-18156` / #629](https://github.com/ATAC-Helicopter/VaultSync/issues/629) | Preserve surviving UI rows during collection refreshes | In progress |
| [`BUG-18157` / #630](https://github.com/ATAC-Helicopter/VaultSync/issues/630) | Retain selected project identity after refreshed models are rebuilt | In progress |
| [`VS-1892` / #631](https://github.com/ATAC-Helicopter/VaultSync/issues/631) | Polish shared pill alignment and theme readability | In progress |
| [`VS-1893` / #632](https://github.com/ATAC-Helicopter/VaultSync/issues/632) | Prepare 1.8.9 release identity and repository tracking | In progress |
| [`VS-1894` / #633](https://github.com/ATAC-Helicopter/VaultSync/issues/633) | Qualify 1.8.9 desktop continuity and release artifacts | Todo |

Canonical scope: [ROADMAP.md](../ROADMAP.md#189--bug-fixes-and-everyday-polish).
In-progress work is implemented locally or under preparation; it is not marked
Done until integrated and verified. `VS-1894` is the release-blocking evidence gate.

- [Milestone 1.8.9](https://github.com/ATAC-Helicopter/VaultSync/milestone/16)
- [Delivery Project](https://github.com/users/ATAC-Helicopter/projects/7)
