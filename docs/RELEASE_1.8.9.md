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

This patch is a broad stabilization and renewal release: fix known and discovered
bugs, harden safety and edge cases, retire maintainability debt, smooth core
workflows, apply practical quality-of-life improvements, and keep every primary
page purposeful and usable without horizontal page scrolling. It does not change
the backup format or introduce the larger 1.9 recovery features.

## Review findings and changes

| Finding | Change |
| --- | --- |
| Backup history refresh rebuilt every project group, resetting expansion and loaded pages. | Reconcile groups by project ID, update summaries in place, explicitly bind expansion two-way, and preserve the number of loaded rows. |
| Shared collection reconciliation replaced an existing item when inserting before it. | Insert the new item and retain surviving items, avoiding destructive replacement notifications across all callers. |
| Projects and backup project summaries cleared their bound lists during reload. | Reconcile refreshed lists without a collection Reset. |
| Rebuilt project models could send selection back to the first project. | Match the previous selected project by ID before applying fallback selection. |
| Backup deletion replaced the selected project-summary object before selection restoration. | Reconcile project rows by ID and update the selected row in place so the first project never takes over. |
| The Linux updater handoff test expected Unix separators on Windows CI. | Build the expected working directory with the host path API while retaining Linux command assertions. |
| Dependabot proposed only part of the directly pinned rendering family. | Update Avalonia, SkiaSharp, and HarfBuzzSharp coherently and gate central package changes with an alignment test. |
| Desktop analysis retained cancellation, complexity, repeated-key, and overload-order findings. | Make task ownership explicit and simplify the flagged UI helpers without changing behavior. |
| Padded page content forced its minimum width to the outer viewport width. | Measure Dashboard, Backups, and Settings from the padded available width and enforce the rule with a UI policy test. |
| Maintained runtime and rendering packages received coordinated patch releases. | Service Microsoft libraries to 10.0.12, SkiaSharp to 4.152.0, and HarfBuzzSharp to 14.2.1.200 as complete package families. |
| Icon-and-label pill stacks could stretch independently of centered text. | Center stack containers inside shared status pills and backup tags. |
| Muted text and light-theme semantic colors were too faint. | Increase muted-text contrast, darken light-theme success/warning/error colors, and use dark labels on the dark theme's blue accent. |
| Custom-theme muted text was blended toward its background. | Apply the existing readable-text contrast check against all three configured surfaces. |
| Interrupted metadata preview copies had no startup cleanup path. | Delete failed copies immediately and prune only stale GUID-owned read-copy workspaces. |

The shared collection fix applies to existing callers in Backups, Projects,
Dashboard, History, Schedule, Recovery, project folders, and the tray panel.
It preserves surviving item identities; it does not make newly constructed row
models identical to their predecessors. Backup project groups explicitly retain
their instances and expansion state. Removing content above the viewport can
still move content naturally, and deleting the final group can clamp scrolling
to the new page extent.

## Page-purpose audit

| Page | Primary user purpose |
| --- | --- |
| Dashboard | See protection state and the next action needing attention. |
| Projects | Choose what is protected and manage project-specific rules. |
| Backups | Run protection, inspect restore points, and act on destinations. |
| Schedule | Understand and control when automatic protection runs. |
| History | Audit events and curate recovery-relevant snapshot metadata. |
| Recovery | Prove recoverability through readiness, verification, and drills. |
| Guide | Complete setup and learn the shortest path through core workflows. |
| Settings | Configure global behavior, integrations, appearance, and diagnostics. |

No primary page is retained as a placeholder or duplicate destination. At the
default 1200-pixel window, navigation labels now remain visible; the compact
icon rail is reserved for windows below 1100 pixels.

## Automated validation

- Full .NET suite: **863 passed, 0 failed, 0 skipped** on macOS, including the
  Avalonia application build.
- New regression coverage: insertion without replacing survivors, reorder and
  removal without Reset, expanded and collapsed group refreshes, loaded-page
  preservation after deletion, selected project-row identity and aggregate
  updates, empty-selection continuity, summary updates, and removal of an empty group.
- Existing backup comparison, paging, and theme tests pass.
- Release preparation: **77 Python script tests passed**; canonical/public
  metadata consumer validation and `git diff --check` passed.
- Hosted Windows, Linux, and macOS build/test jobs pass at the current head;
  CodeQL, SonarQube, YAML, Store metadata, and dependency submission are green.

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
| [`BUG-18158` / #635](https://github.com/ATAC-Helicopter/VaultSync/issues/635) | Keep Linux updater handoff qualification portable on Windows CI | In progress |
| [`VS-1892` / #631](https://github.com/ATAC-Helicopter/VaultSync/issues/631) | Polish shared pill alignment and theme readability | In progress |
| [`VS-1893` / #632](https://github.com/ATAC-Helicopter/VaultSync/issues/632) | Prepare 1.8.9 release identity and repository tracking | In progress |
| [`VS-1894` / #633](https://github.com/ATAC-Helicopter/VaultSync/issues/633) | Qualify 1.8.9 desktop continuity and release artifacts | Todo |
| [`VS-1895` / #636](https://github.com/ATAC-Helicopter/VaultSync/issues/636) | Service Avalonia and coordinated rendering dependencies | In progress |
| [`VS-1896` / #637](https://github.com/ATAC-Helicopter/VaultSync/issues/637) | Clear remaining desktop Sonar maintainability findings | In progress |
| [`BUG-18159` / #638](https://github.com/ATAC-Helicopter/VaultSync/issues/638) | Prevent page-width bindings from restoring horizontal overflow | In progress |
| [`VS-1897` / #639](https://github.com/ATAC-Helicopter/VaultSync/issues/639) | Audit and renew 1.8.9 safety, usability, and repository health | In progress |
| [`BUG-18160` / #640](https://github.com/ATAC-Helicopter/VaultSync/issues/640) | Reclaim abandoned metadata read-copy workspaces | In progress |

Canonical scope: [ROADMAP.md](../ROADMAP.md#189--bug-fixes-and-everyday-polish).
In-progress work is implemented locally or under preparation; it is not marked
Done until integrated and verified. `VS-1894` is the release-blocking evidence gate.

- [Milestone 1.8.9](https://github.com/ATAC-Helicopter/VaultSync/milestone/16)
- [Delivery Project](https://github.com/users/ATAC-Helicopter/projects/7)
