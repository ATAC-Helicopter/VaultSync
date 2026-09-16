# VaultSync 1.8.9 — Bug fixes and everyday polish

Status: unreleased; implementation integrated into Stable through #634 and #660.
Native upgrades qualified in the initial Stable build, but its source
`adafac380df41a29552929fbd451ddd36c9d9139` and
[run 35075261085](https://github.com/ATAC-Helicopter/VaultSync/actions/runs/35075261085)
are superseded by the BUG-18176 safety fix. Rebuild every final asset from the
subsequent Stable merge and verify its native qualification and uploaded
canonical manifest, which records the exact source commit. Neither the initial
Stable assets nor pre-Stable draft attachments may be promoted as final.

The owner [explicitly approved a documented maintainer exception](https://github.com/ATAC-Helicopter/VaultSync/pull/634#issuecomment-5694495168)
for missing review approval and interactive Windows/Linux/Store-runtime results.
This permitted Stable promotion; those checks remain incomplete in #633, not
passed. Publication and Partner Center submission were not authorized.

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
| Primary patch predecessor | Exact `1.8.8`; native executable upgrade matrix passed |
| Additional patch candidates | `1.8.2`, `1.8.3`, `1.8.5`, `1.8.6`, `1.8.7` per platform, only if final inventory qualification accepts them |
| Store package version | `1.8.9.0`; archive identity/runtime verified, packaged behavior pending |
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
Final safety follow-up [#665](https://github.com/ATAC-Helicopter/VaultSync/pull/665)
preserves pre-existing doctor probe-named files and drains both sync-tool output
streams. It passes all hosted platform/CodeQL checks, 924 .NET tests, 100 script
tests, and the PR Sonar gate at 82.4% new-code coverage. The earlier 919/97
counts below describe historical qualification; the final suites contain 924/100.
Stable's existing branch baseline must also pass after final promotion; no
coverage-baseline reset or failed automated check is covered by the exception.

| Finding | Change |
| --- | --- |
| Backup history refresh rebuilt every project group, resetting expansion and loaded pages. | Reconcile groups by project ID, update summaries in place, explicitly bind expansion two-way, and preserve the number of loaded rows. |
| Shared collection reconciliation replaced an existing item when inserting before it. | Insert the new item and retain surviving items, avoiding destructive replacement notifications across all callers. |
| Projects and backup project summaries cleared their bound lists during reload. | Reconcile refreshed lists without a collection Reset. |
| Rebuilt project models could send selection back to the first project. | Match the previous selected project by ID before applying fallback selection. |
| Backup deletion replaced the selected project-summary object before selection restoration. | Reconcile project rows by ID and update the selected row in place so the first project never takes over. |
| The Linux updater handoff test expected Unix separators on Windows CI. | Build the expected working directory with the host path API while retaining Linux command assertions. |
| Dependabot proposed only part of the directly pinned rendering family. | Update Avalonia, SkiaSharp, and HarfBuzzSharp coherently and gate central package changes with an alignment test. |
| Desktop analysis retained cancellation, complexity, command-path, and version-ordering findings. | Make task ownership explicit, use absolute OS notification commands, and simplify the flagged helpers without changing behavior. |
| Padded page content forced its minimum width to the outer viewport width. | Measure Dashboard, Backups, and Settings from the padded available width and enforce the rule with a UI policy test. |
| Maintained runtime and rendering packages received coordinated patch releases. | Service Microsoft libraries to 10.0.12, SkiaSharp to 4.152.0, and HarfBuzzSharp to 14.2.1.200 as complete package families. |
| Maintained runtime and validation tooling received compatible updates. | Update Dapper to 2.1.86, Microsoft.NET.Test.Sdk to 18.10.0, xUnit analyzers to 2.1.0, CodeQL Action to 4.38.0, and setup-java to 6.0.1 while retaining xUnit v2. |
| Icon-and-label pill stacks could stretch independently of centered text. | Center stack containers inside shared status pills and backup tags. |
| Muted text and light-theme semantic colors were too faint. | Increase muted-text contrast, darken light-theme success/warning/error colors, and use dark labels on the dark theme's blue accent. |
| Custom-theme muted text was blended toward its background. | Apply the existing readable-text contrast check against all three configured surfaces. |
| Interrupted metadata preview copies had no startup cleanup path. | Delete failed copies immediately and prune only stale GUID-owned read-copy workspaces. |
| Backups still rebuilt an unbound activity chart on summary refreshes and window resizes. | Remove the obsolete chart state, computation, and resize handler from the live page. |
| A disconnected destination-scanning pipeline retained automatic-import code and idle coordination state. | Remove the unreachable scanner and its private helper chain instead of preserving a second import path. |
| Startup, responsive History/Recovery layout, and What's New parsing retained dense control flow. | Split the flows around lifecycle, layout region, file discovery, and parsing responsibilities. |
| A cancelled update check could dispose a newer check's cancellation source and restore logging early. | Give each check explicit source ownership and let only the current owner restore shared state. |
| CLI restore read snapshot metadata but copied the current live project and could clean through linked paths. | Resolve the recorded folder backup, preflight all paths, and confine cleanup before changing the target. |
| An empty backup payload path could resolve to a complete destination root during restore or retention. | Reject empty payload identities before resolution or cleanup and preserve their metadata for review. |
| CLI preset names and index entries could resolve outside their intended roots. | Treat identifiers as names and apply containment and link checks to every preset read. |
| CLI path arguments replaced every tilde, including literal characters inside valid names. | Expand only a leading home marker through one shared path helper. |
| Backup preflight recursively enumerated linked directories before rejecting yielded files. | Walk regular source directories explicitly and never descend into filesystem links. |
| The destructive CLI prune cutoff used the machine culture despite documenting an ISO date. | Require exact invariant `yyyy-MM-dd` input and use the parsed UTC date for execution. |
| CLI snapshot pruning could cascade away backup records while leaving their payload folders orphaned. | Exclude backup-referenced and History-protected snapshots before applying count or date pruning. |
| CLI folder restore trusted present backup files without checking their recorded size or hash. | Verify the complete source plan against snapshot metadata before cleanup or target writes. |
| A tray data-shaping exception left the refresh gate permanently occupied. | Release the gate after background failures so future tray updates can recover. |
| Unix single-instance locks could fall back to an environment-selected temporary directory. | Store coordination locks only in the private per-user application-data tree. |
| CLI and direct sync always passed a redundant option rejected by the system rsync shipped with macOS. | Leave compression disabled by default without passing the unsupported option. |
| Backups forced three summary columns and Settings retained two content columns after the window became too narrow for readable cards. | Reflow Backups through three-, two-, and one-column summaries and stack Settings at compact content widths. |

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

- Full .NET suite: **919 passed, 0 failed, 0 skipped** on macOS, including the
  Avalonia application build.
- New regression coverage: insertion without replacing survivors, reorder and
  removal without Reset, expanded and collapsed group refreshes, loaded-page
  preservation after deletion, selected project-row identity and aggregate
  updates, empty-selection continuity, summary updates, removal of an empty group,
  restoring CLI data from the recorded backup rather than the live source, and
  stable/prerelease version-ordering boundaries, protected snapshot pruning,
  preset-index fallbacks, private default instance-lock placement, and tray
  refresh-gate release.
- Existing backup comparison, paging, and theme tests pass.
- The isolated CLI end-to-end smoke test completes snapshot creation, real
  macOS system-rsync transfer, and full-byte verification.
- Live macOS walkthroughs at 900×700 and 700×700 confirm that Dashboard,
  Projects, Backups, Schedule, History, Recovery, Guide, and Settings remain
  vertically navigable without a page-level horizontal scrollbar. Backups and
  Settings compact cards remain readable without the observed overlap.
- A disposable populated macOS profile with three projects and 75 backups
  confirms that loading Alpha from 20 to 25 rows, expanding non-first project
  Beta, and deleting Beta's newest backup preserves the exact history viewport
  offset (9069), keeps Beta expanded, leaves Alpha's loaded depth intact, and
  updates the database, destination folder, and Beta total from 25 to 24.
- Release preparation: **97 Python script tests passed**; canonical/public
  metadata consumer validation and `git diff --check` passed.
- Hosted Windows, Linux, and macOS build/test jobs, CodeQL, YAML, Store
  metadata, and dependency submission are tracked on
  [PR #634](https://github.com/ATAC-Helicopter/VaultSync/pull/634). Final-head
  Sonar analysis passed on promotion PR #660 with **84.5% new-code coverage**
  and zero open findings; Stable integration completed via its merge commit.
- Updater qualification on the exact release head passes **115 focused .NET
  tests** and **19 patch-builder/release-manifest tests**. The hosted Windows
  and Linux jobs each pass all **919 tests**, including platform asset selection,
  verified download metadata, multi-base eligibility, transactional rollback,
  protected-install fallback, temporary-download cleanup, and the Linux rule
  that cancellation or failed administrator authentication must keep VaultSync
  running.
- The live `v1.8.8` canonical manifest matches all **9 published GitHub assets**.
  That release contains no patch archive or patch manifest, so it correctly
  exercises installer fallback only when discovering that published release.
- [Candidate run 35071592180](https://github.com/ATAC-Helicopter/VaultSync/actions/runs/35071592180)
  built source `038f45db1c64caa941d25d14d3a0d6e7135cb4f0` and passed real
  `1.8.8 -> 1.8.9` patch application using the released 1.8.8 executable helper
  on Windows x64, Linux x64/ARM64, and macOS Apple Silicon/Intel. Each host
  rejected corrupt archives and unlisted bases without mutation, waited for
  parent exit, verified every installed file, preserved an external user-data
  sentinel, and started the updated application. Windows unattended installer
  upgrades and both Linux Debian package upgrades also passed.
- All eight direct packages, five patch archives and their five manifests, and
  one Store upload passed canonical verification. Nine package SBOMs and
  provenance attestations were generated; online/offline installer provenance
  verification passed. Evidence remains in that Actions run, not as extra
  release assets outside the canonical contract.
- This execution invokes the real released helper with candidate files; it is
  not an interactive Settings-to-update walkthrough. The public updater cannot
  discover an unpublished draft. Interactive permission prompts and Store
  packaged runtime/certification remain separate gates.
- [Draft staging run 35072851550](https://github.com/ATAC-Helicopter/VaultSync/actions/runs/35072851550)
  passed source-commit identity, unchanged application/package sources, Store
  archive inspection, and exact name/size/SHA-256 reconciliation of all **20
  uploaded files**. The draft targets the qualified build commit and remains
  unpublished. These pre-Stable assets must not be promoted: final packages,
  patches, Store upload, canonical manifest, and supply-chain proof must be
  regenerated from the Stable promotion commit. SBOMs and test logs stay linked
  in Actions; the Store upload has not been submitted or certified.

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

Empty-profile macOS narrow-window verification and populated newest-backup
deletion continuity are recorded. Cancellation, last-item/group deletion,
keyboard-focus, theme and scaling coverage, Windows verification, and a live
Linux Wayland/Xorg walkthrough are not yet recorded. These are outstanding
qualification steps, not completed test claims. Version stamping is prepared;
publication and interactive updater/Store runtime qualification remain pending;
native executable upgrades and draft upload integrity passed.

## Tracked work

| ID | Work | State |
| --- | --- | --- |
| [`BUG-18155` / #628](https://github.com/ATAC-Helicopter/VaultSync/issues/628) | Preserve backup group expansion and loaded history during refreshes | Integrated into Stable |
| [`BUG-18156` / #629](https://github.com/ATAC-Helicopter/VaultSync/issues/629) | Preserve surviving UI rows during collection refreshes | Integrated into Stable |
| [`BUG-18157` / #630](https://github.com/ATAC-Helicopter/VaultSync/issues/630) | Retain selected project identity after refreshed models are rebuilt | Integrated into Stable |
| [`BUG-18158` / #635](https://github.com/ATAC-Helicopter/VaultSync/issues/635) | Keep Linux updater handoff qualification portable on Windows CI | Integrated into Stable |
| [`BUG-18174` / #658](https://github.com/ATAC-Helicopter/VaultSync/issues/658) | Bind Store upload packages to release metadata and provenance | Integrated into Stable |
| [`BUG-18175` / #659](https://github.com/ATAC-Helicopter/VaultSync/issues/659) | Keep the app open for manually installed Linux update archives | Integrated into Stable |
| [`BUG-18176` / #664](https://github.com/ATAC-Helicopter/VaultSync/issues/664) | Preserve user files during CLI diagnostics and drain tool output safely | Implemented through #665; final asset evidence tracked in VS-1894 |
| [`VS-1892` / #631](https://github.com/ATAC-Helicopter/VaultSync/issues/631) | Polish shared pill alignment and theme readability | Integrated into Stable |
| [`VS-1893` / #632](https://github.com/ATAC-Helicopter/VaultSync/issues/632) | Prepare 1.8.9 release identity and repository tracking | Integrated into Stable |
| [`VS-1894` / #633](https://github.com/ATAC-Helicopter/VaultSync/issues/633) | Qualify 1.8.9 desktop continuity and release artifacts | Deferred checks open |
| [`VS-1895` / #636](https://github.com/ATAC-Helicopter/VaultSync/issues/636) | Service coordinated runtime, rendering, and validation dependencies | Integrated into Stable |
| [`VS-1896` / #637](https://github.com/ATAC-Helicopter/VaultSync/issues/637) | Clear remaining desktop Sonar maintainability findings | Integrated into Stable |
| [`BUG-18159` / #638](https://github.com/ATAC-Helicopter/VaultSync/issues/638) | Prevent page-width bindings from restoring horizontal overflow | Integrated into Stable |
| [`VS-1897` / #639](https://github.com/ATAC-Helicopter/VaultSync/issues/639) | Audit and renew 1.8.9 safety, usability, and repository health | Integrated into Stable |
| [`BUG-18160` / #640](https://github.com/ATAC-Helicopter/VaultSync/issues/640) | Reclaim abandoned metadata read-copy workspaces | Integrated into Stable |
| [`BUG-18161` / #641](https://github.com/ATAC-Helicopter/VaultSync/issues/641) | Isolate overlapping updater cancellation ownership | Integrated into Stable |
| [`BUG-18162` / #642](https://github.com/ATAC-Helicopter/VaultSync/issues/642) | Restore CLI snapshots from recorded backup data safely | Integrated into Stable |
| [`BUG-18163` / #643](https://github.com/ATAC-Helicopter/VaultSync/issues/643) | Reject empty backup payload paths before cleanup | Integrated into Stable |
| [`BUG-18164` / #644](https://github.com/ATAC-Helicopter/VaultSync/issues/644) | Confine CLI preset reads to preset roots | Integrated into Stable |
| [`BUG-18165` / #645](https://github.com/ATAC-Helicopter/VaultSync/issues/645) | Expand only leading home markers in CLI paths | Integrated into Stable |
| [`BUG-18166` / #646](https://github.com/ATAC-Helicopter/VaultSync/issues/646) | Skip linked directories during backup preflight enumeration | Integrated into Stable |
| [`BUG-18167` / #647](https://github.com/ATAC-Helicopter/VaultSync/issues/647) | Parse CLI prune dates invariantly | Integrated into Stable |
| [`BUG-18168` / #648](https://github.com/ATAC-Helicopter/VaultSync/issues/648) | Preserve backed snapshots during CLI prune | Integrated into Stable |
| [`BUG-18169` / #649](https://github.com/ATAC-Helicopter/VaultSync/issues/649) | Verify recorded backup bytes before CLI restore | Integrated into Stable |
| [`BUG-18170` / #650](https://github.com/ATAC-Helicopter/VaultSync/issues/650) | Recover tray refresh after background failures | Integrated into Stable |
| [`BUG-18171` / #651](https://github.com/ATAC-Helicopter/VaultSync/issues/651) | Keep Unix instance locks in private app data | Integrated into Stable |
| [`BUG-18172` / #656](https://github.com/ATAC-Helicopter/VaultSync/issues/656) | Support the macOS system rsync in CLI and direct sync | Integrated into Stable |
| [`BUG-18173` / #657](https://github.com/ATAC-Helicopter/VaultSync/issues/657) | Reflow dense Backups and Settings cards at compact widths | Integrated into Stable |

Canonical scope: [ROADMAP.md](../ROADMAP.md#189--bug-fixes-and-everyday-polish).
Implementation integrated through #634 and #660 is tracked separately from
public release. `VS-1894` remains open for explicitly deferred interactive and
Store-runtime evidence under the owner-approved promotion exception.

- [Milestone 1.8.9](https://github.com/ATAC-Helicopter/VaultSync/milestone/16)
- [Delivery Project](https://github.com/users/ATAC-Helicopter/projects/7)
