# Changelog

All notable changes to VaultSync are documented in this file.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/).
Historical technical detail is retained in the [detail archive](docs/release-evidence/changelog-history-before-standard-2026-10-04.md).

## [Unreleased]

**Target version:** `1.9.7`. Development work remains subject to its release gates.

### Changed

- [VS-1980] Standardize release history and ongoing notes on Keep a Changelog, preserving original detail and enforcing concise entries automatically.

Current stable remains 1.8.9. This release is unscheduled; predecessor and product qualification remain open.
See [the original development notes](docs/release-evidence/changelog-development-before-standard-2026-10-04.md) and [the release contract](docs/RELEASE_1.9.7.md).

## [1.8.9] - 2026-09-16

### Changed

- [VS-1897] Audit 1.8.9 safety, edge cases, usability, dependencies, and repository health.
- [VS-1893] Prepare release identity, tracking, documentation, and packaging for 1.8.9.
- [VS-1893] Require final release assets to come from the Stable promotion build.
- [VS-1896] Simplify backup health refreshes and avoid startup probe cooldown loss.
- [VS-1896] Consolidate repeated desktop labels and transient console capture state.
- [VS-1895] Refresh coordinated rendering, test-platform, and analysis dependencies.
- [VS-1897] Keep labeled navigation at the default window size and clarify the Projects purpose.
- [VS-1897] Stop rebuilding an obsolete hidden backup activity chart during refresh and resize.
- [VS-1896] Clear desktop analysis findings in startup, version ordering, and OS notifications.
- [VS-1896] Simplify updater, power, logging, and encryption-enrollment paths.
- [VS-1892] Center icon-and-label stacks inside shared status pills and backup tags.
- [VS-1892] Improve muted text contrast, light-theme status colors, and dark-theme accent labels.
- [VS-1892] Keep custom-theme muted text readable against the configured surfaces.

### Removed

- [VS-1897] Remove obsolete placeholder navigation and reflection paths from the live shell.
- [VS-1897] Remove the obsolete untracked-destination scanner and its dormant state.

### Fixed

- [VS-1896] Clear static-analysis findings and restore the Sonar coverage gate.
- [BUG-18176] Preserve existing files during CLI diagnostics and prevent sync-tool output hangs.
- [BUG-18155] Preserve backup project expansion and loaded history depth during refreshes.
- [BUG-18156] Preserve surviving rows and avoid full resets across shared UI lists.
- [BUG-18157] Keep the selected backup project open while deletion refreshes its summary.
- [BUG-18158] Keep Linux updater handoff qualification portable on Windows CI.
- [BUG-18159] Prevent padded pages from requesting extra width and overflowing horizontally.
- [BUG-18160] Remove abandoned metadata read copies without touching unrelated temporary data.
- [BUG-18161] Prevent cancelled update checks from disposing or resetting their replacements.
- [BUG-18162] Restore CLI snapshots from recorded backup data with confined cleanup.
- [BUG-18163] Reject empty backup paths before restore or retention cleanup.
- [BUG-18164] Confine CLI preset reads to their user or bundled preset root.
- [BUG-18165] Preserve literal tildes while expanding leading CLI home paths.
- [BUG-18166] Keep backup preflight enumeration out of linked source trees.
- [BUG-18167] Make destructive CLI prune dates exact and locale-independent.
- [BUG-18168] Preserve backed and protected snapshots during CLI pruning.
- [BUG-18169] Verify recorded backup bytes before CLI restore changes its target.
- [BUG-18170] Let tray refreshes recover after background data-shaping failures.
- [BUG-18171] Keep Unix single-instance locks in private application data.
- [BUG-18172] Support the system rsync shipped with macOS.
- [BUG-18173] Reflow dense Backups and Settings cards at compact widths.
- [BUG-18174] Include Store uploads in verified release manifests and provenance.
- [BUG-18175] Keep VaultSync open while manual Linux update archives are installed.

## [1.8.8] - 2026-09-02

### Added

- [VS-1823] Added repeatable large-history and high-file-count performance budgets.
- [VS-1882] Added deterministic interruption and recovery qualification for plain and encrypted archives.
- [VS-1890] Added native Wayland with automatic X11 fallback and opaque Linux windows.

### Changed

- [VS-1885] Cleared Sonar findings in release automation, onboarding, and storage hygiene.
- [VS-1886] Reduced archive and support-package memory use with buffer reuse and streaming hashes.
- [VS-1887] Made guided setup actionable and moved Guide and Schedule queries off the UI thread.
- [VS-1884] Qualified exact 1.8.7 state and updated pinned workflow security actions.
- [VS-1888] Cleared remaining core Sonar annotations.
- [VS-1889] Qualified safe multi-version patch updates with installer fallback across Windows, macOS, and Linux.
- [VS-1891] Made platform patch assets opt in for installer-only release builds.
- [VS-1821] Isolated snapshot creation, deferred hashing, checkpoint telemetry, and native-copy orchestration.

### Fixed

- [BUG-18116] Removed unintended horizontal page and dialog scrolling.
- [BUG-18117] Confined retention cleanup across filesystem links.
- [BUG-18118] Failed backups when required snapshotted source files become unavailable.
- [BUG-18119] Isolated decrypted-open workspaces between app processes.
- [BUG-18120] Prevented disposable cleanup from traversing linked children.
- [BUG-18121] Removed abandoned verified release-cache writes safely.
- [BUG-18122] Removed abandoned identity and credential-index writes safely.
- [BUG-18123] Removed abandoned support-bundle staging safely.
- [BUG-18124] Limited recognized temporary telemetry exports to 30 days and 100 MB.
- [BUG-18125] Prevented predictable encrypted-restore staging collisions.
- [BUG-18126] Confined decrypted-workspace cleanup to exact OS temporary children.
- [BUG-18127] Isolated cancellation ownership between overlapping backup runs.
- [BUG-18128] Prevented duplicate and unbounded release-branch Sonar runs.
- [BUG-18129] Published scan-cache state only after snapshot persistence succeeds.
- [BUG-18130] Deduplicated and bounded release-branch CI and analysis runs.
- [BUG-18131] Prevented snapshot scans from following linked source paths.
- [BUG-18132] Propagated verification cancellation instead of reporting a mismatch.
- [BUG-18133] Rolled back interrupted restores instead of leaving partial target changes.
- [BUG-18134] Rolled back interrupted metadata imports and exports.
- [BUG-18135] Preserved completed backups when cancellation arrives after commit.
- [BUG-18136] Rejected scans cancelled while processing the final entry.
- [BUG-18137] Rejected vanished backup data before publishing metadata.
- [BUG-18138] Preserved rollback evidence when restore destinations disappear.
- [BUG-18139] Kept retention metadata while backup destinations are unavailable.
- [BUG-18140] Preserved deferred metadata when destinations disappear during replay.
- [BUG-18141] Isolated metadata-conflict and backup-repair busy state.
- [BUG-18142] Removed failed installer downloads immediately.
- [BUG-18143] Made cancellation performance checks deterministic on small runners.
- [BUG-18144] Kept isolated profiles from opening the normal database.
- [BUG-18145] Kept metadata-conflict reviews readable in narrow windows.
- [BUG-18146] Loaded registered projects into tray menus before opening a page.
- [BUG-18147] Kept Linux open until privileged update authentication succeeds.
- [BUG-18148] Kept script validation compatible with macOS system Python.
- [BUG-18149] Cleared updater security and maintainability quality-gate findings.
- [BUG-18150] Rejected mismatched or duplicate patch-base inventories.
- [BUG-18151] Corrected stale multi-version updater guidance.
- [BUG-18152] Forced protected Windows/Linux installs and macOS app bundles to use installer fallback instead of patch handoff.
- [BUG-18153] Kept VaultSync open after launching macOS DMG update media because DMGs require a manual app replacement.
- [BUG-18154] Relaunched Linux package/AppImage updates only after the old app exits, and kept VaultSync open when automatic relaunch cannot be scheduled.

## [1.8.7] - 2026-08-21

### Added

- [VS-1874] Added portable Recovery Evidence Packages with redacted identities, integrity manifests, inspectable freshness, and validation against tampering or unsupported schemas.
- [VS-1871] Added consistent build identity across Settings, diagnostics, exports, and CLI JSON version output.
- [VS-1873] Added per-package SPDX SBOMs, release provenance attestations, and online or offline candidate verification.
- [VS-1875] Added reviewable support bundles with optional sanitized diagnostics, secret redaction, bounded size, and integrity manifests.
- [VS-1878] Added canonical release metadata and deterministic public rendering with CI drift checks and live website release refresh.
- [VS-1872] Added canonical release manifests binding official downloads to exact sizes, SHA-256 digests, platform requirements, and fail-closed updater validation.
- [VS-1877] Added a durable, owner-private installation identity for cross-machine coordination without treating mutable host names or telemetry identifiers as writer identity.
- [VS-1877] Added repository-scoped writer leases with atomic acquisition, heartbeat and expiry, read-only busy inspection, nonce-bound release, explicit stale takeover, and retained takeover evidence.
- [VS-1877] Added destination writer inspection and explicit stale-lease takeover review with preserved ownership evidence.
- [VS-1879] Added durable merge bases and field-level metadata planning that merges independent edits while retaining overlapping changes for review.
- [VS-1879] Added Base/local/remote conflict details and durable undo until the next portable repository write.

### Changed

- [VS-1811] Rebuilt project folders as collapsed summaries with searchable membership and batch controls, preserving the familiar ungrouped project list.
- [#561] Standardized macOS downloads around VaultSync.app, a drag-to-Applications layout, and consistent application identity.
- [BUG-18099] Serviced .NET and coordinated Microsoft packages, with runtime audits of self-contained publishes and release artifacts.
- [VS-1877] Protected portable metadata writers with repository leases while preserving read-only imports and previews during another writer's activity.
- [VS-1877] Preserved existing destination metadata for merge review; queued metadata initializes empty destinations only once.
- [VS-1879] Made conflict decisions preserve non-overlapping remote edits, record source and base revisions, and advance the durable merge base after either resolution.
- [VS-1879] Added guarded portable-project revisions and schema-version-3 provenance for base identity, field changes, and reviewed resolutions.
- [VS-1879] Unified metadata conflict resolution and bounded undo, qualifying two-installation convergence, restart persistence, repeat imports, and durable decisions.
- [VS-1880] Consolidated metadata export orchestration, SMB mount parsing, mounted-share validation, theme color normalization, and contrast calculations behind focused shared primitives with regression coverage.
- [VS-1880] Unified Windows Robocopy exclusions with the shared preset resolver.
- [VS-1876] Documented repository formats, cross-machine safety, encryption boundaries, emergency recovery, release integrity, and unsigned-package limitations against executable tests.
- [BUG-18103] Modernized Snapshot Explorer, metadata-import review, and updater windows around the current compact, theme-aware app layout.
- [BUG-18104] Updated development presets to preserve Git control files and shared IDE settings while excluding live internals and disposable caches.

### Fixed

- [BUG-18115] Confined SBOM files to approved workspaces and corrected cleanup after backup deletion retries.
- [#559] Corrected launch-on-login registration to use `~/Library/LaunchAgents`, migrates the erroneous `~/Documents/Library/LaunchAgents` entry, and no longer kickstarts a duplicate process during launch synchronization.
- [#560] Added a verified macOS bridge from legacy 1.8.6 bundles to signed VaultSync.app, retaining the old bundle until replacement succeeds.
- [BUG-18112] Prevented passive macOS probes from unlocking credentials or mounting shares; development launches cannot replace the installed login item.
- [BUG-18110] Localized recovery, evidence, writer, history, picker, verification, and restore controls; made Inspector rows adapt to translated text.
- [BUG-18111] Kept recovery exports and manual cache deletion in private application roots, preserving mounted-destination cleanup during credential retries.
- [BUG-18098] Rebuilt roadmap description synchronization around tested wrapped-title parsing, ownership-aware body preservation, repository-contained inputs, validated GitHub identifiers, and an exact write-free dry-run report.
- [BUG-18100] Restored Dev as the permanent integration branch and disabled automatic deletion after Stable promotion.
- [BUG-18102] Prevented deferred metadata replay from overwriting repository metadata changed on another machine or replaying repeatedly after a successful flush.
- [BUG-18102] Disabled connection pooling for the repository coordination database so disposed writer leases release their file handles predictably on Windows.
- [BUG-18104] Corrected the Python pytest-cache rule and removed unsupported VS Code negation rules that previously excluded intended shared configuration.
- [BUG-18105] Prevented metadata-import previews from double-counting projects and backups that are represented by both portable metadata and legacy repository folders.
- [BUG-18106] Normalized macOS SMB mount diagnostics to remove the complete credential-bearing share identity before masking any remaining raw or escaped password text.
- [BUG-18107] Stopped metadata import from exporting deletion tombstones for snapshots that were preserved because they still have local backups or never existed locally.
- [BUG-18108] Stopped repeated background downloads of immutable release and platform patch manifests by persisting digest-verified cache entries across application restarts.
- [BUG-18109] Bounded disposable application data and prevented backups from writing into unmounted macOS destination directories.
- [BUG-18101] Made cross-machine settings conflicts durable, preserving local secrets and destinations and requiring review before destructive tombstone imports.

## [1.8.6] - 2026-08-10

### Added

- [VS-1861] Replaced first-run overlays with a compact, resumable task sequence driven by real source, destination, project, schedule, restore-point, and passed recovery-drill state.
- [VS-1865] Added per-project protection settings and removal previews distinguishing local registration from source files and stored backups.
- [VS-1866] Added a localized Guide with consistent definitions for backup, snapshot, restore point, verification, known good, protected, and recovery drill.
- [VS-1867] Added screen-reader names and help across primary workflows and confirmation previews for reset, cache, project-index, credential, and encryption-password removal.
- [VS-1811] Added persistent project folders with explicit membership, aggregate health, safe deletion, and folder-scoped protection actions.

### Changed

- [VS-1862] Rebuilt Schedule around readiness, upcoming runs, project coverage, constraints, shared policy edits, and actionable delay explanations.
- [VS-1863] Reorganized Dashboard around protection status, required actions, upcoming runs, recent activity, and known-good recovery points.
- [VS-1864] Unified manual and automatic backup progress with explicit typed stages, including waiting, retries, cancellation, failure, and completion.
- [VS-1865] Clarified that desktop and CLI project removal preserves source files and stored backup payloads.
- [VS-1811] Carried project folder identity into Schedule, Backups, Recovery, and History, including folder-aware search and grouped schedule coverage.
- [VS-1868] Refreshed the open-vault identity across application, tray, platform packages, Store, website, and social assets.

### Fixed

- [BUG-18089] Aligned release-candidate validation with the `release/<version>` branch convention and made the direct-to-stable, no-beta delivery path for 1.8.6 explicit.
- [BUG-18084] Restored the missing Schedule page content and added responsive layouts for wide and narrow windows.
- [BUG-18085] Made Dashboard actions, upcoming runs, and known-good recovery evidence readable without duplicate or misleading cards.
- [BUG-18086] Corrected onboarding order and required a passed recovery drill before reporting recovery proof complete.
- [BUG-18087] Fixed project-removal confirmation enablement and refreshed the project list only after repository removal completes.
- [BUG-18088] Corrected the Clear local cache label and added reviewable previews for every destructive Settings action.
- [BUG-18090] Restricted rich-text links to approved external URI schemes so file, script, data, and shell links cannot be launched from rendered content.
- [BUG-18091] Included project tags, external identity, and folder membership in Backups refresh signatures so metadata edits no longer leave stale project cards.
- [BUG-18092] Added diagnostics for scan-cache writes, NAS staging checks, and deferred-backup migration failures that were previously silent.
- [BUG-18093] Restored ungrouped project cards and compact folder controls with contextual management and batch actions.
- [BUG-18094] Cleared brand-build, schedule, and folder static-analysis findings with bounded paths and icon-builder coverage.
- [BUG-18095] Made folder moves explicit and previewable, preserving hierarchy state and clarifying membership and protection summaries.
- [BUG-18096] Aligned folder headers, badges, batch controls, and menus with compact geometry across dark, light, and custom themes.
- [BUG-18097] Completed the optional-folder workflow translations across all maintained locales, including membership, move, health-summary, snapshot, and backup actions.

## [1.8.5] - 2026-08-02

### Added

- [VS-1851] Added recovery-confidence states distinguishing measured, simulated, inferred, confirmed, stale, missing, failed, and unsupported evidence.
- [VS-1852] Added a project-level Recovery Inspector with the decisive state, evidence basis and freshness, latest drill details, limitations, and next useful action.
- [VS-1853] Added a recovery checklist for points, destinations, credentials, integrity, plans, drills, and offsite evidence with direct proof actions.
- [VS-1854] Added isolated restore tests that verify representative files, retain test folders for inspection, and record explicit recovery evidence.
- [VS-1855] Added recovery proof, isolated restore, protection, and report-export evidence to History with timestamps and source identities.
- [VS-1856] Expanded recovery reports with source identity, protection and drill details, redacted proof, deterministic IDs, and checksums.
- [VS-1857] Extended first-run setup beyond backup completion to a separately tracked recovery proof and passed-drill baseline, while keeping Continue later and restart paths.
- [VS-1859] Added illustrated application guidance, a Settings reference, refreshed screenshots, and a maintainable captioned walkthrough workflow.

### Changed

- [VS-1858] Serviced SQLite, rendering, and workflow dependencies while retaining security pins and required macOS runtime capabilities.
- [VS-1860] Bound updates to trusted URLs, exact sizes, and SHA-256 digests; hardened extraction and restricted Unix application data permissions.

### Fixed

- [BUG-18078] Made onboarding compact and interactive with Back, Continue later, and primary actions that leave target pages usable.
- [BUG-18079] Restored the missing snapshot-count placeholder in the Portuguese Dashboard translation.
- [BUG-18080] Replaced the ABI-sensitive macOS disk-space probe with the runtime filesystem API and rejected impossible capacity readings before enforcing backup thresholds.
- [BUG-18081] Removed startup and virtualized-list binding errors by using stable named view roots and a null-safe selected-diff path.
- [BUG-18082] Isolated the CLI end-to-end self-test from the production database by default and guaranteed cleanup on success, failure, or cancellation.
- [BUG-18083] Kept Settings, Projects, Backups, and Recovery interactive behind first-run guidance, including at narrow supported window sizes.

## [1.8.4] - 2026-07-24

### Added

- [VS-1847] Added strictly redacted, user-reviewed crash reports that only the user's email application can send.
- [VS-1810] Added recovery drills, reachable 3-2-1 guidance, explicit offsite confirmation, and protected-point recommendations.
- [VS-1848] Added local byte-level recovery proofs, restore-plan simulation, exportable evidence, and a verified-point retention safety floor.
- [VS-1850] Added four curated themes, including dark and light glass treatments with layered reflections and translucent surfaces.

### Changed

- [VS-1849] Refreshed supported servicing dependencies while preserving the validated cross-platform rendering stack.
- [VS-1850] Rebuilt Appearance as a compact theme studio with previews, advanced tuning, distinct glass materials, and opaque platform fallbacks.

### Fixed

- [BUG-18066] Snapshot Explorer now blocks linked source paths and ambiguous duplicate ZIP entries.
- [BUG-18067] Recovery guidance now counts only reachable payloads at their recorded destinations.
- [BUG-18068] Archive uploads now stop promptly, retry detected stalls, and reject incomplete chunks.
- [BUG-18069] Separate recoverable UI failures can each open crash-report review during one session.
- [BUG-18070] Absolute backup records can no longer escape their recorded destination identity through linked content.
- [BUG-18071] Verification now rejects absolute, traversing, and linked paths outside the recovery root.
- [BUG-18072] Snapshot creation now preserves case-distinct file identities on case-sensitive filesystems.
- [BUG-18073] Backup creation now rejects untrusted snapshot-source paths before reading or writing files.
- [BUG-18074] Restore previews and selective restores now preserve case-distinct paths.
- [BUG-18075] Corrected theme contrast, responsive recovery layouts, navigation feedback, and persistent pointer outlines while preserving visible keyboard focus.
- [BUG-18076] Recovery refresh, export, drill, and protected-point work now stops with the page lifetime instead of continuing after the user leaves Recovery.
- [BUG-18077] Backup deletion, exploration, sandbox apply, and restore tasks now declare their independent cancellation lifetime instead of inheriting the unrelated update-check lifecycle.

## [1.8.3] - 2026-07-16

### Added

- [VS-1809] Added asynchronous same-project snapshot comparison with file-change counts, path hotspots, and deletion, growth, or churn signals.
- [VS-1809] Added searchable snapshot file comparisons with bounded text diffs and explicit fallbacks for encrypted, binary, offline, or unsupported content.
- [VS-1839] Migrated the desktop UI to Avalonia 12.1 with its .NET 10 rendering, accessibility, focus, selection, and compiled-binding improvements.
- [VS-1845] Added Indonesian, Japanese, Korean, Dutch, Polish, Turkish, Ukrainian, and Vietnamese application localizations with full key, duplicate, value, registration, and format-placeholder validation.

### Changed

- [BUG-18063] Improved text comparisons with compact hunks, line numbers, semantic markers, localized states, and unchanged results for line-ending-only differences.
- [VS-1809] Expanded snapshot comparison into dedicated hotspot, changed-file, and text-diff panes so file changes can be inspected without crowding the summary.
- [VS-1809] Improved snapshot comparisons with same-project suggestions, selection guidance, cancellation, filtered counts, and accurate empty or capped states.
- [VS-1809] Cancelled superseded text-diff computations when users move quickly between changed files, avoiding unnecessary quadratic diff work for stale selections.
- [VS-1809] Added previous/next changed-file navigation and one-click filter clearing to make large snapshot comparisons faster to review.
- [VS-1809] Redesigned comparisons with compact summaries and conditional detail workspaces; zero examined files no longer imply unchanged content.
- [VS-1809] Clarified earlier/later selections and comparison status with a two-pane workspace and accessible navigation controls.
- [VS-1809] Added an expandable changed-file folder tree with recognizable file-type icons and a compact comparison header.
- [VS-1843] Simplified comparison hierarchy and hotspot presentation, replacing colored extension boxes with monochrome file icons.
- [VS-1844] Compacted changed-file navigation with quieter selection, tighter rows, abbreviated counts, and hidden zero-byte deltas.
- [VS-1846] Densified snapshot comparison with compact trees, diff gutters, centered filtering, and unobtrusive added/deleted totals.
- [VS-1842] Completed Snapshot Compare, Explorer, onboarding, destination, and shared-control localization while preserving format placeholders.
- [VS-1838] Refreshed the maintained Avalonia 11, HarfBuzzSharp, and LiveCharts patch lines as the validated baseline for the separately tracked Avalonia 12 migration.
- [VS-1839] Aligned rendering dependencies with Avalonia 12, removed retired diagnostics, and adopted supported placeholder and window-decoration APIs.
- [VS-1839] Made Dashboard activity and storage adapt at narrow widths with stacked cards and a responsive donut chart.
- [VS-1840] Hardened snapshot comparisons by excluding links, preserving case-distinct paths, and clearing temporary encryption key material.
- [VS-1840] Replaced re-entrant `async void` UI commands with observed, single-flight async commands and added cancellable Recovery page lifecycle refreshes.
- [VS-1840] Removed the global nullable-warning suppression and corrected every exposed nullability contract so warning-as-error builds enforce the full baseline again.
- [VS-1840] Moved macOS credentials to native Security.framework calls, made credential indexes atomic and corruption-safe, and enforced real Linux secret-tool timeouts with process-tree termination.
- [VS-1840] Added macOS CI and mandatory release build/test/vulnerability gates, checksum verification for pinned AppImageKit tooling, and fail-hard required artifact uploads.
- [VS-1840] Added incremental backup-history paging, compiled core-view bindings, and accessible comparison controls.
- [VS-1840] Scoped metadata coordination per store, removed mutable presentation callbacks, and made required migration failures explicit.
- [VS-1831] Confined patch-building and download-stat outputs to validated roots before writing files.
- [VS-1832] Split Snapshot Explorer browsing, archive traversal, text preview, and code-preview helpers into focused operations while preserving existing browsing and restore behavior.
- [VS-1833] Split snapshot creation, manifest construction, hashing, and persistence into focused stages to reduce complexity while preserving snapshot compatibility.
- [VS-1834] Refactored CLI destination, doctor, snapshot, and watch command flows into smaller validation and execution helpers without changing their command-line contracts.
- [VS-1835] Refactored Projects and Settings workflows, project snapshot commands, and rich-text rendering into focused helpers to reduce UI complexity and improve maintainability.
- [VS-1836] Addressed Sonar analyzer findings and consolidated repeated literals across backup, navigation, update, credential, telemetry, verification, network-mount, and encryption services.
- [VS-1839] Limited floating-window dragging to titles, restored Log Console selection, and corrected Avalonia 12 folder-picker item resolution.

### Fixed

- [BUG-18065] Preserved text edits beyond 800 lines, collapsed unchanged hunks, and stabilized comparison dialogs while switching selected files.
- [BUG-18064] Corrected macOS folder-picker start locations using valid destinations or home/Documents fallbacks, restoring New Folder navigation.
- [BUG-18061] Restored backup change details from earlier project points, with bounded folder/ZIP inspection and accurate offline, encrypted, or capped totals.
- [BUG-18062] Prevented repeated macOS credential prompts by caching session reads and deferring credentials until a new mount needs them.
- [BUG-18060] Confined ZIP restores to their selected destination and rejected linked paths that could redirect writes outside it.
- [BUG-18060] Applied destination containment and linked-path rejection to ordinary folder restores.
- [BUG-18059] Bounded diagnostics retention to two hang dumps within 1 GiB and stopped timed-out collection before partial files accumulate.

## [1.8.2] - 2026-07-04

### Added

- [VS-1808] Added Snapshot Explorer v1 for backup folder/archive browsing, text preview, search, and selected-item restore.

### Changed

- [VS-1808] Made Snapshot Explorer browsing, previews, and selective restores asynchronous and localized, with explicit encrypted-content limitations.
- [VS-1808] Snapshot Explorer preview now handles text-like source/config files instead of only a small extension allow-list, while rejecting binary content safely.
- [VS-1808] Improved Snapshot Explorer navigation and readability with click-to-open folders, line-numbered code previews, horizontal scrolling, and lightweight syntax coloring for code/config files.
- [VS-1808] Changed Snapshot Explorer folder browsing to expand/collapse folders inline so users can browse nested files without losing the surrounding folder context.
- [VS-1829] Rebuilt onboarding as interactive setup for source root, destination, first project, backup, and restore-point review.
- [VS-1829] Simplified the onboarding card by removing the progress bar, status pill, and completed-state block while keeping focused step instructions and Back/Skip/Continue controls.
- [VS-1821] Hardened Sonar-flagged script path writes and kept encrypted archive IV handling analyzer-visible while preserving random IV generation.
- [VS-1821] Reduced duplicated service literals for telemetry storage, drive-health probing, patch helper staging, support bundle redaction, and SQLite schema checks.
- [VS-1822] Reduced duplicated backup UI literals for progress stages, backup filters, restore/delete dialogs, telemetry fields, and repeated action styling.
- [VS-1822] Reduced duplicated Settings, shell, onboarding, metadata-sync, and rsync lookup literals flagged by Sonar.
- [VS-1828] Refreshed setup-python, Spectre.Console, Microsoft.NET.Test.Sdk, SkiaSharp, and HarfBuzzSharp dependencies for the 1.8.2 release train.
- [VS-1830] Release readiness now warns when a changelog section reuses IDs so scopes can be checked before publishing.
- [VS-1808] Added automatic text previews and clearer selection while preventing duplicate Snapshot Explorer windows.
- [VS-1808] Snapshot Explorer action buttons no longer stay disabled after switching from an in-flight file preview to a folder selection.
- [VS-1821] Download stats path guards now normalize macOS-resolved roots before child-path validation, avoiding false escape failures while preserving workspace confinement.

### Fixed

- [BUG-18058] macOS now uses an exclusive per-user lock file for single-instance startup, reducing intermittent duplicate app launches.

## [1.8.1] - 2026-06-25

### Added

- [VS-1824] Recovery assessments can now be exported as portable Markdown reports with coverage and project readiness details.

### Changed

- [BUG-18056] Linux `.deb` fallback installs now request an explicit APT reinstall of the downloaded package.
- [VS-1825] Encrypted backups are now encrypted locally before their final artifact is uploaded to the destination.
- [VS-1825] Published a backup encryption guide covering setup, format, credential storage, password changes, opening, and restore.
- [VS-1826] Recovery project triage now supports search and focused ready or needs-attention filters.
- [VS-1827] Release-facing issue and PR templates now default to the 1.8.1 release train and show the milestone-scoped release gate command.
- [VS-1825] Linux credential references now store, find, and delete the correct Secret Service entry.
- [VS-1825] Encrypted archive readers now reject unsupported formats and excessive embedded KDF parameters before derivation.
- [VS-1827] Release readiness checks now scope Project completion to the target milestone instead of later train work.

### Fixed

- [BUG-18057] Added a private per-user Linux lifetime lock that prevents duplicate instances while preserving window activation.
- [BUG-18056] Made metadata exports skip concurrently removed backups and commit shared SQLite updates atomically.
- [BUG-18056] Protected Linux patch installs now run the elevated helper headlessly instead of losing the desktop session after `pkexec`.

## [1.8.0] - 2026-06-20

### Added

- [VS-1814] Added guarded SonarQube Cloud analysis workflow and setup notes for the public OSS repository.
- [VS-1815] Added SonarQube analysis exclusions for vendored rsync tooling and binary/build artifacts.
- [VS-1802] Added 1.8 history metadata tables for snapshot labels, notes, tags, protected, known-good, and restore events.
- [VS-1803] Added first-class History and Recovery navigation with data-backed pages for the 1.8 project-history release.
- [VS-1804] Added selectable project history with reversible filters, paging, event details, and origin-aware snapshot, import, and restore relationships.
- [VS-1805] Added protected and known-good snapshot markers, with retention honoring protected snapshot metadata.
- [VS-1807] Added Recovery readiness and coverage previews using current project, backup, and destination health data.
- [VS-1806] Added Dashboard workflow links into History, Recovery, and Backups, with restore milestones included in recent activity.
- [VS-1804] Redesigned History as a connected Git/subway-style timeline workspace with modular expandable event stations and a persistent event inspector.

### Changed

- [VS-1804] Rebuilt History as a searchable recovery timeline with compact events, selected details, metrics, and snapshot actions.
- [VS-1804] Connected History details to backup contents, Recovery, and snapshot changes with accessible actions and markers.
- [VS-1805] Completed History editing for snapshot labels, notes, tags, protected markers, and known-good markers.
- [VS-1805] Unified History snapshot protection with Backups “Keep” state so both pages and retention use the same protection marker.
- [VS-1812] Localized the completed History workspace and replaced stale “coming next” metadata copy with current 1.8 behavior.
- [VS-1818] Closed the 1.8 Sonar triage pass; remaining complexity hotspots are tracked as follow-up maintenance rather than release feature work.
- [VS-1819] Refactored History timeline data shaping and removed unreachable/dead helper code flagged by SonarQube in the 1.8 PR.
- [VS-1813] Added History activity, project, lane, and time filters while clarifying Backups as the restore-point snapshot inventory.
- [VS-1806] Recovery readiness now accounts for protected and known-good snapshot markers when metadata is available.
- [VS-1816] Hardened release asset workflow input handling so manually supplied versions are passed through quoted environment variables instead of direct shell interpolation.
- [VS-1812] Aligned 1.8.0 development metadata, release notes, roadmap links, and localized History/Recovery guidance.
- [VS-1815] Tuned SonarQube quality scope and dispositions for vendored rsync documentation and credential-vault identifiers.
- [VS-1816] Hardened release asset workflow inputs and PR YAML lint installs against SonarQube security findings.
- [VS-1817] Kept backup archive IV generation analyzer-visible while preserving the encrypted archive envelope format.
- [VS-1818] Extended CI, PR quality gates, and CodeQL triggers to the active `release/v1.8` branch.

### Fixed

- [BUG-18054] Patched .NET 10 runtime-pack servicing alerts by pinning ASP.NET Core runtime downloads and Microsoft platform packages to `10.0.9`.
- [BUG-18053] Restore target folders and sandbox apply paths now stay under their intended roots when project names or relative paths contain unsafe segments.
- [BUG-18052] Disposed Sonar-flagged cancellation resources in backup runs, diagnostics logging, and onboarding tour scrolling.
- [BUG-18045] macOS tray shutdown no longer crashes when quitting from the native tray menu during menu teardown.
- [BUG-18047] Linux patch updates can now elevate through `pkexec` for protected installs instead of hiding behind the `.deb` installer fallback.
- [BUG-18048] Projects now reload visible content on page attach and recover stale visible lists safely.
- [BUG-18049] macOS mounted `/Volumes` destinations are no longer treated as creatable local folders when access is denied.
- [BUG-18050] Projects and Backups navigation no longer blocks the UI while cached data and history groups load.
- [BUG-18051] Kept preset reads, redundant persistence, and timeline shaping out of project selection and History paging interactions.
- [BUG-18052] Backup, diagnostics, and onboarding scroll cancellation resources are now disposed on completion or teardown.
- [BUG-18055] History and Backups no longer show conflicting protection state for the same snapshot.

## [1.7.5] - 2026-05-31

### Changed

- [VS-1745] Centralized NuGet package versions in `Directory.Packages.props` and removed unused package references.
- [VS-1746] Unified configuration access across core, CLI, and UI through a shared store adapter.
- [VS-1747] Added shared runtime logging and hash-formatting helpers to reduce duplicated service plumbing.
- [VS-1748] Standardized repeated view-model property updates on existing shared base helpers.
- [VS-1749] Consolidated core test fixtures for temporary directories, config isolation, and repository/project setup.
- [VS-1750] Reused destination path normalization across identity and quota planning code.
- [VS-1751] Added a shared dependent-property notification helper for UI view models.
- [VS-1752] Added verbose-only runtime timing scopes around startup background work, deferred metadata imports, update checks, and Dashboard refresh phases.
- [VS-1753] Extracted Dashboard recent-activity projection so refreshes reuse a project lookup instead of scanning projects per activity row.
- [VS-1754] Added reusable test builders for projects, backups, and backup destinations.
- [VS-1755] Centralized byte and signed-byte formatting for UI and CLI callers.
- [VS-1756] Consolidated repeated Dashboard, Backups, Projects, and Settings dependent-property notifications.
- [VS-1757] Routed NetworkMount diagnostics through one local logging helper instead of repeating log prefixes inline.
- [VS-1758] Added a durable unchanged-source cache for background metadata auto-imports and phase timings for metadata import internals.
- [VS-1759] Hardened the metadata unchanged-source cache so skipped background imports still re-run when local repository coverage is incomplete.
- [VS-1760] Reduced SQLite lock contention with escaped connection strings, bounded busy waits, disabled stale pooling, and one-time WAL negotiation.
- [VS-1761] Split SQLite schema setup into schema, migration, index, and path-normalization helpers while reusing one connection for column migrations.
- [VS-1762] Metadata unchanged-source cache coverage now tracks source external IDs so unrelated local rows cannot mask recreated or incomplete repositories.
- [VS-1763] Release-facing Windows, Store, issue-template, and patch-base metadata now target `1.7.5`.
- [VS-1764] UI repository callers now resolve database path fallback through the shared config-store helper.
- [VS-1765] UI repository creation and selected fire-and-forget work now use shared factory and detached-task helpers.
- [VS-1766] Projects and Settings helper view models now live in focused files, with shared export-folder opening.
- [VS-1767] Metadata tombstone exports and Backups option setters now reuse focused helper plumbing.
- [VS-1768] Backups helper view models now live in a focused models file to shrink the main Backups view model.
- [VS-1769] GitHub issue and pull request templates now use structured forms for bugs, crashes, beta feedback, backup/restore problems, updates, and feature requests.
- [VS-1770] Settings reload notifications and backup archive test setup now reuse focused helper methods.
- [VS-1771] Release templates, Store validation docs, and remaining metadata/snapshot temp-directory test fixtures were tightened for the stable build.
- [VS-1772] Shared action button styles now cover small and primary buttons so shell, widget, Backups, Projects, and Settings controls align consistently.
- [VS-1773] Unused exception-variable warnings are no longer globally suppressed after cleaning up low-risk catch blocks.

### Fixed

- [BUG-18038] Windows notification failures no longer assign an invalid empty toast tag and repeated OS-toast failures are suppressed after the first diagnostic entry.
- [BUG-18039] Manual storage-health rechecks now marshal notification and tray updates back to the UI thread after background drive probing.
- [BUG-18040] Config load fallback now records primary, backup, and last-known-good recovery diagnostics instead of silently falling back to defaults.
- [BUG-18041] Made Linux Debian installer fallback request OS elevation before falling back to opening the downloaded package.
- [BUG-18042] Linux OS shutdown and logout requests now bypass run-in-background close interception so VaultSync no longer hides to tray and blocks power-off.
- [BUG-18043] Prevented duplicate Linux tray indicators by clearing native menus, spacing recreation, and honoring the current visibility setting.
- [BUG-18044] Cross-machine imported backup history no longer drives local snapshot diff baselines or project storage deltas, preventing large misleading size swings between OSes.
- [BUG-18045] macOS tray-menu Quit now tears down the native tray icon without throwing Avalonia's menu-mismatch exception.
- [BUG-18046] Settings credential profile cards no longer clip the password visibility control in windowed layouts.

## [1.7.4] - 2026-05-20

### Added

- [VS-1732] Release publish restore now declares Windows, Linux, and macOS RIDs.
- [VS-1737] Added VS Code Linux debug configs for UI, CLI, and tests.
- [VS-1738] Linux tarballs now include rootless install/uninstall scripts with launcher, icon, and `vaultsync` command setup.
- [VS-1739] CLI JSON output now uses a shared indented serializer.

### Changed

- [BUG-17119] Startup metadata import now checks reachable backup destinations.
- [BUG-17119] UI metadata imports now treat source stores as read-only.
- [BUG-17120] Core tests now isolate config writes from real app settings.
- [VS-1724] Patch manifest compatibility tests now cover key edge cases.
- [VS-1739] CLI commands were updated for the current Spectre.Console.Cli override model.
- [VS-1740] Refreshed the safe dependency set for 1.7.4.
- [VS-1741] Release-facing docs, examples, and What's New content now target the prepared `1.7.4` release.
- [VS-1741] The in-app What's New dialog now uses a cleaner release-digest layout with section dividers instead of nested cards.
- [VS-1742] Shared UI formatting and detached async helpers reduce duplicated view-model code.
- [VS-1743] Source-code presets now keep repository metadata such as `.github`, `.gitignore`, `.gitattributes`, and `.gitmodules` while still excluding generated build outputs.
- [VS-1744] Release asset builds can now generate Microsoft Store Partner Center upload packages from the Store packaging project.

### Fixed

- [BUG-17116] Notification cleanup now avoids disposed-token races.
- [BUG-17118] Log console rows now show readable time, source, and message fields.
- [BUG-17108] In-app logs now capture runtime errors without verbose logging.
- [BUG-17115] Windows/Linux tray menus now open the richer tray panel reliably.
- [BUG-17117] Tray panel action labels now wrap in localized layouts.
- [BUG-17119] Linux metadata imports now remap rooted backup paths to the active destination.
- [BUG-17122] Linux debug builds no longer silently exit or crash dashboard charts.
- [BUG-17123] Destination metadata imports now recover SQLite sidecar journals through a temporary copy.
- [BUG-17124] Destination imports now rebuild missing backup history from timestamped folders.
- [BUG-17125] Destination reachability probes now run during deferred startup.
- [BUG-17125] Locked tombstone exports now defer locally for retry instead of being dropped.
- [BUG-17126] Crash dialog chrome now uses Avalonia 11 `SystemDecorations`.
- [BUG-17127] Onboarding’s manual test hook no longer causes unreachable-code diagnostics.
- [BUG-17128] System notification fallback exceptions are now logged.
- [BUG-17129] Drive health checks now prefer `smartctl` and skip unavailable network-drive SMART data.
- [BUG-17130] Metadata tombstone exports now avoid WAL locks on Linux-mounted backup destinations.
- [BUG-18002] Log console diagnostics now display readable, color-coded rows while preserving raw lines.
- [BUG-18003] Backup safety now blocks source/destination overlap and recursive backup growth. Refs #281.
- [BUG-18004] Backup delete cards now stay visible and show deletion progress details. Refs #279.
- [BUG-18005] Manual metadata refresh no longer hangs the UI or maps temporary roots. Refs #280.
- [BUG-18006] Backups now prune stale database entries when recorded backup folders are missing from reachable destinations. Refs #284.
- [BUG-18007] Backup probes now share in-flight buffer tuning and dev presets skip nested generated outputs. Refs #285.
- [BUG-18008] Passive Backups refreshes no longer wake destinations just to update reachability. Refs #286.
- [BUG-18009] The log console copy button now uses the console window clipboard instead of relying only on the main window.
- [BUG-18010] Normal app runs no longer show caught first-chance SQLite/WinRT probes in diagnostics unless first-chance diagnostics are explicitly enabled.
- [BUG-18011] The What's New parser now displays the current release notes slice instead of carrying older release sections into the dialog.
- [BUG-18012] Imported destination history rebuilt from legacy backup folders now records and repairs real backup sizes instead of showing `0 B`. Refs #298.
- [BUG-18013] Changing language no longer resets the selected theme or jumps Settings back to its initial scroll position. Refs #297.
- [BUG-18014] Linux auto backups no longer pause because of device-scoped batteries such as wireless controllers, and auto-backup timer decisions are logged for diagnostics.
- [BUG-18015] Backup summary cards plus action and filter buttons now constrain long localized labels in windowed layouts instead of overflowing their bounds.
- [BUG-18016] Auto backups now warm up active destinations and retry preparation after a short cooldown so sleeping drives can wake before backup starts.
- [BUG-18017] Linux log console now suppresses expected DBus/IBus desktop-integration noise and supports multi-row selection while keeping the readable styled console layout.
- [BUG-18018] The What's New dialog now opens centered over the main app window instead of drifting to another monitor.
- [BUG-18019] Linux SMART probe errors such as permission, unsupported-device, or read failures no longer masquerade as failing disks and block backups.
- [BUG-18020] Linux `.deb` packages now include AppStream metadata, a richer package description, homepage data, and matching desktop/icon IDs for better software-center previews.
- [BUG-18021] Corrected Linux window icons and launcher identity so taskbars associate VaultSync with its application icon.
- [BUG-18022] Linux tray refreshes now reuse the existing native menu object to avoid duplicate tray indicators on AppIndicator hosts.
- [BUG-18023] Linux packages now install hidden desktop identity fallbacks so taskbars can match the `VaultSync.UI` runtime window to the VaultSync icon.
- [BUG-18027] Remapped imported cross-platform project roots to local folders, handling Windows leaf names and Linux case differences.
- [BUG-18028] Pinned patched SkiaSharp and HarfBuzzSharp runtime packages so dependency graph alerts no longer resolve Avalonia's older transitive graphics stack.
- [BUG-18029] Made protected Linux installations use installer fallback and allowed releases to omit unusable patch assets.
- [BUG-18030] Linux updater fallback now prefers `.deb` installers on Debian-family systems and marks downloaded AppImages executable before launch.
- [BUG-18031] Linux startup now keeps Avalonia's compatible DBus protocol dependency instead of overriding it with an incompatible newer package.
- [BUG-18032] Linux packages now use one AppStream/desktop/window identity to improve software-center previews and avoid duplicate taskbar grouping. Refs #315.
- [BUG-18033] Project preset changes now persist immediately for registered projects instead of reverting after refresh. Refs #316.
- [BUG-18034] Projects now label and display latest snapshot size explicitly, and show unavailable sizes instead of misleading `0 MB` values. Refs #317.
- [BUG-18035] Replaced the sidebar-collapse font glyph with vector geometry to prevent missing-glyph rectangles on Linux.
- [BUG-18036] Projects list scrolling now uses the ListBox's own virtualized scroll host and sidebar navigation labels align vertically with their icons. Refs #319.
- [BUG-18037] Preserved snapshot presets using detected recommendations and a generic fallback when no specific preset applies.

### Security

- [BUG-18026] Refreshed vulnerable/dependency-alert package versions across core, UI, and tests while staying on the Avalonia 11 line.

## [1.7.3] - 2026-04-23

### Added

- [VS-17106] Added explicit Log Console auto-scroll and selected-line copying through a button or platform shortcut.
- [VS-1732] Added Linux x64/arm64 archives and an x64 AppImage for broader direct-install coverage.

### Changed

- [VS-1732] Linux update discovery now prefers architecture-specific installers and patch assets (`linux-x64` / `linux-arm64`) before falling back to generic Linux naming.

### Fixed

- [BUG-17100] Tray panel screen detection, reopen behavior, and Linux/Wayland positioning are now resilient on Hyprland-class environments. Contributed by @JustH8Me in PR #216.
- [BUG-17101] Fixed tooltip flickering and focus issues on Linux/Wayland by enabling overlay popups PR #217 (thanks @JustH8Me)
- [BUG-17102] Fixed fatal AccessViolationException on Linux x64 during backup (thanks @JustH8Me, refs #218)
- [BUG-17103] Fixed passwords being saved as 'null' on Linux and increased timeouts passwords (thanks @JustH8Me, refs #220)
- [BUG-17104] Refreshed Settings from persisted values after reload so saved project roots no longer appear blank.
- [BUG-17105] Repaired blank project root paths at startup when matching folders exist under the configured root.
- [BUG-17107] Fixed false "Reachable" status for Read-Only directories on Linux and eliminated UI list flickering (thanks @JustH8Me, refs #230)
- [BUG-17109] Backup All and auto-backup no-change runs now create real first backup artifacts instead of empty destination folders.
- [BUG-17110] Compared imported restore-needed state with the pre-import backup baseline so imported backups retain their restore prompt.
- [BUG-17111] Background settings saves now preserve existing project roots, backup roots, and advanced destinations when transient UI snapshots are blank.
- [BUG-17112] Command state refreshes now marshal back to Avalonia's UI thread, preventing background startup checks from crashing button command validation.
- [BUG-17113] Individual project backup buttons now resolve destinations from the latest saved config and refresh destination choices after backup destination settings change.
- [BUG-17114] Project auto-backup settings now export through metadata before the first backup, so toggles travel across machines earlier.

## [1.7.2] - 2026-04-09

### Added

- [VS-1727] Added an initial Microsoft Store packaging scaffold with the reserved Partner Center identity values, separate from the Direct installer path.
- [VS-1726] Added runtime distribution-channel detection so VaultSync can distinguish Direct installs from the reserved Microsoft Store package identity.
- [VS-1727] Added a dedicated GitHub Actions workflow for building Microsoft Store package artifacts separately from the Direct release pipeline.

### Changed

- [VS-1729] Microsoft Store builds now disable the GitHub self-updater, offer an `Open Microsoft Store` action, and report Store-managed updates in Settings/support bundles.
- [VS-1729] Help, installation, and update docs now explain the Direct-vs-Store Windows split instead of assuming every Windows install uses GitHub updater flows.
- [VS-1730] Added a Microsoft Store submission checklist that tracks Partner Center fields, restricted capability review notes, and remaining packaging validation gaps.
- [BUG-17090] Development metadata and What's New now target `1.7.2`, and Settings diagnostics use multiline summaries instead of unreadable single-line dumps.
- [BUG-17092] Settings now bounds the diagnostics panel with its own scroll area so lower controls remain reachable on smaller screens and higher scaling.
- [BUG-17093] Backup fingerprinting now skips files that disappear mid-scan, and project preset probing no longer throws noisy missing-path exceptions.
- [BUG-17094] Backups now shows an explicit drive-health unavailable message for network or mapped destinations instead of hiding the health line entirely.
- [BUG-17096] Metadata sync now round-trips per-project auto-backup state, tags, and preferred destination changes more reliably so project-specific settings stop drifting between machines.
- [BUG-17097] Removing a project now exports a project tombstone and clears stale auto-backup disables, preventing deleted projects from reappearing from destination metadata.
- [BUG-17098] Archive compression now reports chunk-level progress while reading large files, so compression no longer appears frozen on long-running entries.
- [BUG-17099] Checkpointed retry can now resume parallel archive uploads chunk-by-chunk instead of silently disabling the parallel upload path when both features are enabled.

### Fixed

- [BUG-17089] In-app toasts now dedupe repeated alerts, cap the visible stack, keep actions clickable, and present clearer severity-first cards.
- [BUG-17091] Settings now uses a single-column responsive layout so high scaling and shorter screens can still reach lower destructive/advanced controls without clipping.

### Security

- [BUG-17095] Windows builds now pin `Tmds.DBus.Protocol` `0.21.3` explicitly to override the vulnerable transitive `0.21.2` pulled by `Avalonia.Desktop 11.3.13`.

## [1.7.1] - 2026-04-04

### Added

- [VS-1725] Added scheduled GitHub release download snapshots with a dedicated public stats branch and JSON history.

### Changed

- [VS-1725] Download stats now generate both a readable HTML/Markdown summary and raw release-asset history from the same workflow.
- [VS-1725] README now uses repo-owned visuals and links to the download-stats branch instead of relying on a stale third-party repo card.

### Fixed

- [BUG-17085] Grouped repeated backup advisories and OS notifications instead of stacking one alert per project.
- [BUG-17086] Removed a broken project-details binding and made tag addition, normalization, and deduplication independent of bulk input.
- [BUG-17087] Made bulk tags separate chips and recorded underlying refresh errors in diagnostics.
- [BUG-17088] Restored English key ordering and complete key parity across all shipped localizations.
- [BUG-17015] Kept Dashboard heading and summary alignment consistent with the rest of the page.
- [BUG-17016] Dashboard backup-storage card now uses a ranked top-consumers list instead of an oversized pill cloud, making the lower-right space useful and readable.
- [BUG-17017] Dashboard, Projects, and Settings now route the remaining hardcoded 1.7.x UI copy through English localization keys instead of shipping raw literals.
- [BUG-17017] Theme option labels and settings log/export status text now also use English localization keys instead of hardcoded literals.
- [BUG-17017] Crash dialog, placeholder fallback, and missing-view fallback text now also resolve through English localization keys instead of raw literals.
- [BUG-17017] Shell navigation titles and header fallback copy now also resolve through English localization keys instead of raw literals.
- [BUG-17018] Dashboard backup storage now shows a bounded top-consumers list instead of mixing project rows with the generic 'Other' storage segment.
- [BUG-17019] Diagnostics now suppress expected first-chance missing-path and retention permission exceptions so verbose logs stay focused on actionable faults.
- [BUG-17020] Added regression tests for the download-stats snapshot script so release totals, deltas, highlights, and history output stay stable.
- [BUG-17021] Bounded download-stat history using recent daily snapshots and monthly checkpoints.
- [BUG-17022] The download-stats workflow now runs its regression tests before publishing snapshots, so broken report logic fails fast instead of pushing bad history.
- [BUG-17023] Tray panel header and tooltip now use localized shell copy instead of raw English fallbacks.
- [BUG-17024] Read-only dashboard, backups, and tag-color UI paths now use cached config snapshots instead of reloading config from disk each time.
- [BUG-17025] Settings diagnostics refresh and tag-chip appearance reads now also use cached config snapshots instead of hitting config storage for every read-only refresh.
- [BUG-17026] Used cached configuration for read-only Dashboard and Backups refreshes instead of repeated disk reloads.
- [BUG-17027] Support bundle export now reads the cached config snapshot for report generation instead of reloading config from disk during a read-only export.
- [BUG-17028] Used cached configuration for startup localization, updater theme, tray visibility, and restored navigation.
- [BUG-17029] Used cached configuration for release guidance, onboarding, tray, close behavior, drive-health timing, and theme reads.
- [BUG-17030] Used cached configuration for deferred startup checks and background destination probes.
- [BUG-17031] Used cached configuration in tray health and menu fallbacks when the shared view-model snapshot is unavailable.
- [BUG-17032] Onboarding step refreshes now use cached config snapshots instead of reloading config during read-only tour-state checks.
- [BUG-17033] Opening backup folders from tray actions now uses cached config snapshots instead of reloading config during read-only destination resolution.
- [BUG-17034] Used cached configuration for project refresh, snapshot history, groups, localization, and read-only display updates.
- [BUG-17035] Bounded Dashboard storage consumers to a ranked list while preserving aggregate counts for remaining projects.
- [BUG-17036] Used cached configuration during metadata retention, NAS monitoring, backup preparation, deletion, and restore-password resolution.
- [BUG-17037] Projects search now supports multi-term matching across project names, paths, and tags so narrower searches are easier without changing views.
- [BUG-17038] Backup-all preparation now uses the cached config snapshot instead of reloading config during read-only orchestration setup.
- [BUG-17039] Projects search term matching now routes through a dedicated helper, reducing inline filter complexity without changing behavior.
- [BUG-17040] Deferred project discovery until the Projects page opens to reduce cold-start work.
- [BUG-17041] Delayed Dashboard and Backups warm loads so they no longer compete with initial shell creation.
- [BUG-17042] Removed duplicate Dashboard overview content while retaining detailed protection sections.
- [BUG-17043] Deferred startup now refreshes projects discovery shortly after launch, so Dashboard content repopulates without bringing back the old eager startup hit.
- [BUG-17044] Dashboard refresh now applies chart and collection updates on the UI thread, fixing the empty post-startup dashboard caused by invalid-thread refresh failures.
- [BUG-17045] Defaulted storage legends to largest-first ordering with a compact alphabetical sort option.
- [BUG-17046] Backup storage now raises top-consumer property updates reliably after dashboard refreshes, fixing the empty right-hand consumer list despite valid usage data.
- [BUG-17047] Used Backups summary space for latest project, type, destination, and storage/security context.
- [BUG-17048] Aligned licensing across the repository and CLI package, removing placeholders and broken encoding.
- [BUG-17049] Added a top-level third-party notices index so bundled rsync helper licenses are easier to audit before release.
- [BUG-17050] Installer-based updates now shut down VaultSync automatically after the installer is launched, so Windows setup can continue without a manual close step.
- [BUG-17051] Release-facing docs and metadata now target `1.7.1`, including app versioning, What's New, updater/releasing docs, and issue-template examples.
- [BUG-17052] Backed off failed destination probes and avoided immediate rescans of offline targets during startup or Backups refresh.
- [BUG-17053] Embedded color pickers now fully offset the stock tab strip height, fixing the visible clipped header chrome in Settings and Projects.
- [BUG-17081] Replaced the production `.ico`, tray PNG, and macOS `.icns` assets with renders of `docs/branding/vaultsync-logo-icon.svg` while preserving the previous assets under `src/VaultSync.UI/Assets/backup/2026-03-31-icon-refresh/`.
- [BUG-17083] Rebalanced the new production SVG icon so the safe composition sits centered within the icon tile instead of reading top-left heavy.
- [BUG-17084] Serviced the .NET and Avalonia dependency baseline, including coordinated SQLite, cryptography, JSON, reflection, and test packages.

## [1.7.0] - 2026-03-20

### Added

- [VS-1706] Added a non-blocking startup backup-index consistency scan.
- [VS-1706] Added persisted integrity findings for support bundles and Doctor workflows.
- [VS-1711] Added retention preflight to protect the last valid restore point.
- [VS-1701] Added deterministic orphan-backup repair planning.
- [VS-1702] Added backup-index repair tools in Settings > Advanced.
- [VS-1712] Added stable destination fingerprinting for rename and re-add scenarios.
- [VS-1705] Added ordered fallback deletion for retention cleanup.
- [VS-1714] Added an initial Doctor workflow surface in Settings > Advanced.
- [VS-1717] Added cross-machine project metadata conflict tracking and resolution.
- [VS-1710] Added an optional maintenance window for scheduled health tasks.
- [VS-1703] Added per-destination soft quotas and cleanup suggestions.
- [VS-1721] Added app-wide tag colors with visual editing from Projects.

### Changed

- [VS-1719] Dashboard KPI cards now use stable-width wrapping so fullscreen layouts avoid oversized dead space while narrower windows keep a predictable card rhythm.
- [VS-1719] The 1.7 dashboard pass is now complete with a stable responsive KPI row, dedicated restore-readiness review section, and explicit backup-storage risk explanations.
- [VS-1708] Prerelease builds now identify with an explicit suffix, while Stable uses `1.7.0`.
- [VS-1707] Settings and support bundles now include updater release-target diagnostics.
- [VS-1708] Patch updates now run explicit `current -> target` preflight checks before offering the patch path.
- [VS-1708] Patch preflight now keeps prerelease labels distinct, so beta `1.7.0-*` builds do not collapse into stable `1.7.0`.
- [VS-1708] Release asset builds now validate beta/stable branch and prerelease rules before generating patch files.
- [VS-1709] Support bundles now include update, repair, and metadata-conflict telemetry.
- [VS-1720] Archive transfers can now resume from verified checkpoints instead of restarting.
- [VS-1720] Settings diagnostics and support bundles now record checkpoint resume, discard, cleanup-preserve, and fallback outcomes for interrupted archive uploads.
- [VS-1720] Preserved restartable rsync/Robocopy transfers so native backup retries do not restart the entire data set.
- [VS-1724] Patch manifests can now declare multiple exact allowed base versions for one target release.
- [VS-1716] Settings > Backups now includes a retention simulation preview.
- [VS-1718] Added a scripted release-readiness gate with human and JSON output.
- [VS-1718] Release gate now separates `PrePublish` warnings from `PostPublish` failures.
- [VS-1715] Diagnostics now include a non-blocking startup timeline summary.
- [VS-1721] Tag-color editing now lives primarily in Projects instead of a duplicate Settings workflow.
- [VS-1721] Onboarding now points new users to Projects for tag-color editing, and Settings no longer shows a duplicate reminder panel.
- [VS-1721] Tag-color editing now uses visible quick swatches and a wrap-friendly layout for smaller windows.
- [VS-1721] Tag quick colors now use a standard hard-coded palette instead of sparse placeholder swatches.
- [VS-1721] Tag quick colors now stay focused on chip-friendly accents instead of generic theme colors.
- [VS-1721] Tag quick colors now use higher-contrast preset tiles so light and dark choices stay readable at a glance.
- [VS-1722] Custom themes now include OLED Black, Deep Blue, an editor-style palette, and side-panel advanced sliders.
- [VS-1722] Appearance onboarding now calls out custom theme palettes instead of only basic layout options.
- [BUG-17005] Simplified the custom theme editor with visible swatches, responsive wrapping, and a cleaner spectrum-only picker.
- [BUG-17005] Theme quick colors now use explicit brush-backed swatches so the default palette reads correctly at a glance.
- [VS-1722] Theme quick colors now adapt to the selected slot so backgrounds, text, accents, and status colors get usable presets.
- [VS-1722] Theme quick colors now use standard picker-style presets and stronger contrast for light surface colors.
- [VS-1722] Theme quick colors now visibly track and apply to the active custom-theme slot.
- [VS-1722] Theme quick colors now use the same chip-style swatches as tag colors while keeping the theme slot selector.
- [VS-1722] The theme default palette now matches the tag picker presets instead of using a separate neutral-only row.
- [VS-1722] The custom-theme palette block now uses the same layout and hint pattern as the Projects tag picker.
- [VS-1722] Theme and tag palette swatches now use the same border rendering instead of separate selected-state outlines.
- [VS-1722] Theme quick colors now use the same full quick palette as the Projects tag editor.
- [VS-1722] Theme quick colors now always apply to the actively selected theme section.
- [VS-1722] Theme section chips now use an explicit selection path before palette colors are applied.
- [VS-1722] Theme quick colors now update the selected section immediately instead of relying on indirect refresh side effects.
- [VS-1723] Settings theme-editor logic now lives in a dedicated partial viewmodel file to reduce risk before the macOS work.
- [BUG-17006] Restore-readiness cards now offer a one-click review list with project names and reasons.
- [BUG-17006] English summaries no longer show corrupted separator glyphs across Dashboard, Backups, and Projects.
- [BUG-17006] Tightened the collapsed sidebar, shared toggle/checkbox styling, Backups spacing, and the Projects tag-color editor.
- [VS-1713] Backups and Dashboard now show a restore-readiness scorecard.
- [VS-1719] Dashboard now uses a more coherent wrap-based information layout.
- [VS-1719] Dashboard sections were rebalanced for clearer fullscreen and windowed layouts.
- [VS-1719] Dashboard summary cards now use a cleaner accent-strip hierarchy with less duplicated header content.
- [VS-1719] Restore-readiness review moved out of the KPI row, and backup storage cards now explain why free-space capacity is currently at risk.
- [VS-1713] Restore-readiness summaries and dashboard pills now use localized copy.

### Fixed

- [BUG-17006] Dashboard restore-readiness review now links directly to Backups, and the corrupted English notification dismiss glyph was replaced with an ASCII-safe fallback.
- [BUG-17002] Restored corrupted bundled Noto Sans font assets.
- [BUG-17003] Projects now fall back to registered entries when discovery is empty or partial.
- [BUG-17003] Projects now show explicit empty and no-selection placeholders.
- [BUG-17004] Projects root now survives startup config read/write races.
- [BUG-17008] Backup, restore, and dashboard trace chatter now stays behind explicit verbose logging, with `VAULTSYNC_FORCE_VERBOSE` available as a developer override.
- [BUG-17009] Diagnostics logging now batches session-log writes through a single background writer instead of spawning one task per log line.
- [BUG-17010] Projects group auto-backup actions no longer re-read app config during command-state evaluation and now use refreshed cached preferences instead.
- [BUG-17011] Dashboard KPI cards now align to the left instead of centering within the available row width.
- [BUG-17012] Saving a custom theme no longer overwrites tag-color mappings that are now managed from Projects.
- [BUG-17013] The Projects destructive action button now uses centered text and a cleaner danger outline/fill treatment.
- [BUG-17014] The macOS release-assets workflow now builds patch manifests without Bash `mapfile`, so the hosted runner can finish patch packaging.
- [BUG-17007] Preserved imported root hints and repaired blank project paths instead of creating metadata projects without usable roots.
- [BUG-17001] Doctor repair workflows now marshal state updates onto the UI thread.
- [BUG-16023] Restore status no longer falls back to raw localization keys.
- [BUG-16024] Restore-mode dropdowns no longer render fallback view text.

## [1.6.0] - 2026-03-09

### Added

- [VS-1607] Added per-project restore mode settings in Backups (Direct, Sandbox) and persisted restore_mode in project schema/model with migration-safe default direct.
- [VS-1608] Added preset recommendation detection for common project types (`Unity`, `Godot`, `Unreal`, `.NET`, `Node`, `Python`, `Rust`, `Avalonia`, `Blender`, `Video`) with cached per-path evaluation.
- [VS-1609] Added per-project tag persistence (`projects.tags`) and editable tag field in Projects details (`comma-separated`).
- [VS-1606] Added exportable support bundle generation (redacted config + metadata summaries + diagnostics + telemetry) under `Documents/VaultSync/Exports/Support`.
- [VS-1610] Added per-destination retry policy settings (`attempts`, `base backoff seconds`) with persistence in advanced backup destinations.
- [VS-1611] Added per-project verification policy persistence (`always`, `scheduled`, `manual`) with metadata sync import/export coverage.

### Changed

- [VS-1607] Restore flow now honors project restore mode: sandbox restores target an isolated preview folder while direct restores keep current project-path behavior.
- [VS-1607] Restore confirmation now includes a per-run restore-mode override selector so users can switch between direct and sandbox restore at execution time.
- [VS-1607] Sandbox restore completion now offers post-restore actions (keep, open sandbox, apply to project) and optional sandbox cleanup after apply.
- [VS-1607] Sandbox apply now includes a pre-apply summary (total/new/overwrite files and bytes) plus explicit confirmation before writing into the project path.
- [VS-1601] Added plain-backup restore previews showing new files, overwrites, conflicts, retained project-only files, and total bytes.
- [VS-1601] Restore confirmation now supports selective top-level restore targets, and restore execution applies only selected targets for plain backups/archives.
- [VS-1602] Added timeline restore-point comparisons covering elapsed time, size changes, net differences, and latest-point statistics.
- [VS-1602] Restore-point compare copy now explains selection order (`older` on the left, `newer` on the right) directly in the UI.
- [VS-1608] Projects preset card now shows a localized recommendation reason and an `Apply recommendation` action while keeping manual preset selection fully available.
- [VS-1608] Preset recommendation confidence gating now requires corroborating markers for generic stacks (`Node`, `Python`, `.NET`) to reduce noisy/low-confidence suggestions.
- [VS-1604] Added an in-app preset editor with reload/save actions and include/exclude previews against the selected project.
- [VS-1604] Preset editor now supports clone/import/export flows (`Clone` to a new preset id, `Import` from file path, `Export` to `Documents/VaultSync/Exports/Presets`).
- [VS-1604] Projects preset editor copy/layout was refined for clarity (clearer action labels, usage guidance, concise preset file display with full-path tooltip).
- [VS-1609] Projects list now includes smart-group filtering (`All`, `Work`, `Games`, `Media`, `Critical`, `Archive`) using project tags and lightweight preset/health signals.
- [VS-1609] Projects smart-group selector now includes a bulk `Snapshot group` action for registered projects in the active group filter.
- [VS-1609] Projects smart-group controls now include `Back up group` and group-wide auto-backup toggles (`Disable auto`, `Enable auto`) to support pause/backup workflows by tag/group.
- [VS-1609] Made tags explicit editable pills so partial typing no longer immediately overwrites saved project tags.
- [VS-1609] Added colored tags, reusable suggestions, and filtered-project bulk tag actions.
- [VS-1609] Projects now pre-seed reusable tags (`Work`, `Games`, `Media`, `Critical`, `Archive`) so group tagging works immediately on first use.
- [VS-1609] Project tags are now visible in Backups per-project cards, Backups history group headers, and Dashboard recent activity entries.
- [VS-1609] Backups per-project sorting now includes a `Tags` mode.
- [VS-1609] Metadata sync now round-trips project tags, preferred destination routing, and restore mode so per-project behavior stays aligned across machines.
- [VS-1603] Backups per-project cards now show storage delta (`?`) versus the previous backup size to surface per-project growth/shrink at a glance.
- [VS-1603] Backups summary now surfaces top local storage consumers (top projects by backup storage share) for faster capacity triage.
- [VS-1605] Backups summary now includes a health center mix (healthy/aging/stale/no-backup projects) based on per-project backup freshness.
- [VS-1610] Manual and auto-backup destination execution now uses destination-scoped retry loops with exponential backoff and retry telemetry/status feedback.
- [VS-1611] Post-backup verification flow now follows per-project verification policy (`always` verifies every run, `scheduled` verifies auto-runs, `manual` skips automatic verification).
- [VS-1606] Settings > Advanced now exposes an `Export support bundle` action that writes a redacted support zip and opens the export folder.
- [ISS-16001] Quiet-hours editor now uses a compact centered start/end layout with consistent control widths in windowed mode.
- [ISS-16002] App config load retry now uses async backoff/read operations instead of blocking sleep loops during transient file-lock contention.

### Fixed

- [BUG-16001] Backups history cards in windowed mode no longer overlap snapshot chips, retention text, and action controls.
- [BUG-16002] History snapshot size pill now keeps a stable adaptive shape instead of collapsing into a circular badge on narrow widths.
- [BUG-16001] Windowed history chips now trim long mode/import/encryption labels to keep spacing stable next to the size pill.
- [BUG-16003] Restore active backup cards now report restore/decrypt stages (not generic backup stage labels) with live throughput and restored-bytes progress detail.
- [BUG-16004] Diff imported-type chip no longer shows raw key text; English localization now includes `Backups.Section.TypeImported`.
- [BUG-16005] Confined elevated patch requests and manifest targets to validated staging and installation roots.
- [BUG-16006] Backup delete now enforces destination-root path containment and uses a manual fallback delete pass with explicit permission guidance for protected SMB/NAS files.
- [BUG-16007] Elevated patch mode now binds request payload integrity via launcher-provided SHA-256 and re-validates patch archive hash/size in helper before extraction.
- [BUG-16008] Retention cleanup, restore preparation, and tray backup-folder open flow now enforce destination-root path containment to reject out-of-root/traversal backup paths.
- [BUG-16009] Backups page windowed layout now reflows the per-project and history panels (including per-project destination/encryption/restore controls) to prevent narrow-width collapse and overlap.
- [BUG-16010] Projects group selector now renders readable option labels in the dropdown instead of fallback "View not found for ProjectGroupOption" text.
- [BUG-16011] Group auto-backup actions now immediately sync per-project toggle state on the Backups page by refreshing from the same `AutoBackupDisabledProjects` setting source.
- [BUG-16012] Backups page left per-project panel no longer uses a hard list-height cap, avoiding visibly shorter column height versus the right history panel.
- [BUG-16013] Top storage consumers now aggregate total backup storage (including imported history) so names/sizes are consistent with total-storage summaries.
- [BUG-16014] Dashboard storage legend and backups top-consumer rows now use centered vertical alignment for cleaner name/dot/value layout.
- [BUG-16015] Projects list cards now show project tags in the All projects panel (`TagsDisplay`) to match tagging visibility across the app.
- [BUG-16016] Dashboard weekly backups-per-day buckets now use local-day window boundaries (converted to UTC for query) to reduce day-label/count drift.
- [BUG-16017] Improved popup readability and selection styling across the application and project/backup lists.
- [BUG-16018] Made core pages fill available window width while retaining readability limits.
- [BUG-16019] Backups page now removes hard per-panel list height caps and rebalances per-project/history columns to better use available windowed space without collapse.
- [BUG-16020] Projects details and Settings Advanced controls now reflow/wrap in windowed mode, and near-zero per-project storage deltas render as neutral `? ~0 B`.
- [BUG-16021] Sized the Backups activity chart to content to remove excessive empty space in windowed layouts.
- [BUG-16022] Projects tag input Enter shortcut now runs through a guarded key handler, removing startup/null `CommitProjectTagInputCommand` binding trace noise in diagnostics logs.

## [1.5.1] - 2026-02-28

### Added

- [VS-15001] Added Backups-page per-project sort control (Latest backup, Name, Total size, Backup count) to improve project list navigation.
- [VS-15002] Added new localization keys for transfer policy, encryption open-timeout/lock labels, validation copy, and backup sort labels.
- [VS-15003] Added startup-safe dashboard donut refresh hooks to reduce first-load no-render states.
- [VS-1572] Added consumer-friendly preset coverage (`Photos`, `Documents`, `Steam mods`, `Creative suites`) with Projects-page preset description/example hints and index-safe preset file mapping.

### Changed

- [ISS-15004] Settings transfer policy section was redesigned for clearer Bandwidth limit and Quiet hours editing with compact window preview and better field grouping.
- [ISS-15005] Open help action now attempts local docs first, then online docs, and reports success/failure in-app instead of failing silently.
- [ISS-15006] Dashboard Recent activity rendering moved to a simpler item layout to avoid list selection/highlight artifacts.
- [ISS-15007] Pill/text alignment and project-search input styling were adjusted for more consistent visual centering and contrast.
- [ISS-15008] Backups per-project header and history-card spacing were tightened to reduce overlap/clipping in windowed layouts.
- [ISS-15009] Backups history grouping now reuses a cached project lookup map to reduce refresh-time allocations during filter/group rebuild.
- [ISS-15010] Repository async read paths now use true Dapper async queries (projects/snapshots/files/backups) instead of `Task.Run` wrappers.
- [ISS-15011] Backups history snapshot lookup now queries only referenced snapshot IDs instead of scanning/loading all snapshots.
- [ISS-15012] Metadata sync import/preview/export paths now expose async APIs and use cancellation-aware retry backoff instead of blocking sleep loops.
- [ISS-15013] Adapted archive compression to file content without changing backup format or restore compatibility.
- [ISS-15014] Added asynchronous configuration saves with cancellation-aware retries to reduce Settings blocking.
- [ISS-15015] Main README was refreshed for the current 1.5.x feature set and outdated wording was cleaned up.
- [ISS-15016] README now includes dedicated app screenshot placeholders (`Dashboard`, `Projects`, `Backups`, `Settings`) under `docs/images/placeholders/`.
- [VS-1573] Refreshed project action availability on selection changes and handled expected notification-dismissal cancellation.

### Fixed

- [BUG-15001] Pie/donut chart now re-renders more reliably after async startup data load and late layout passes.
- [BUG-15002] Lock now and encrypted open-timeout labels now bind through localization keys instead of hardcoded literals.
- [BUG-15003] Bandwidth limit, Max Mbps labeling, and quiet-hours validation copy now use localization-key-backed text.
- [BUG-15004] Backups-page project search no longer shows the incorrect dark background artifact.
- [BUG-15005] Recent-activity card no longer shows unintended selected-row highlight styling.
- [BUG-15006] macOS system notifications now prefer `terminal-notifier` with VaultSync icon wiring (with AppleScript fallback when unavailable).
- [BUG-15007] Backup/history/runtime async entry points no longer rely on `async void`; handlers now run as `Task` flows with centralized detached-operation exception logging.
- [BUG-15008] Tray encrypted-open lock/open handlers and project destination/encryption change handlers now use detached `Task` wrappers instead of `async void`.
- [BUG-15009] Notification auto-dismiss, project snapshot action, settings browse/test commands, and tray refresh no longer use `async void` handlers.
- [BUG-15011] Metadata schema migration now checks column presence with `PRAGMA table_info(...)` before running `ALTER TABLE`, preventing duplicate-column SQLite exceptions (`origin_machine_name`) on already-migrated stores.
- [BUG-15017] Handled missing Windows drive-health tools without process-launch exceptions, reporting unknown health explicitly.
- [BUG-15018] Classified SMB/UNC destinations without constructing invalid drive roots during backup preflight.
- [BUG-15019] Handled archive-upload tuning timeouts without exception noise while preserving explicit backup cancellation.
- [BUG-15020] Retried shared configuration reads during transient save/export file locks.
- [BUG-15021] Handled transient backup-delete failures, unavailable dump tools, and null destination selections while preserving defaults.
- [BUG-15022] Used serialized elevated Windows patch requests to preserve installation paths and restart/wait arguments.

## [1.5.0] - 2026-02-19

### Added

- [VS-1501] Versioned backup crypto descriptor contract for metadata (`formatVersion`, `algorithm`, `kdfProfile`, `kdfParamRef`).
- [VS-1504] `BackupEncryptionSecretService` with secure-store writes and explicit session-memory fallback workflow.
- [VS-1504] Global backup encryption config contract with non-secret key reference fields (`KeyRef`, algorithm/KDF parameters).
- [VS-1502] `BackupArchiveCryptoService` for encrypted archive artifacts (`data.vse`) with per-backup salt/IV envelope metadata.
- [VS-1530] Global backup encryption settings UI with secure password enrollment and explicit clear/reset action.
- [VS-1531] Per-project encryption policy selector (`inherit global`, `encrypted`, `plain`) in Projects view with effective-state display.
- [VS-1532] Per-project encryption key reference persistence (`encryption_key_ref`) with migration-safe defaults in the projects schema.
- [VS-1533] `BackupEncryptionPolicyResolver` to compute effective encryption mode/key source per project at backup runtime.
- [VS-1535] Explorer/open-file activation for `.vse` encrypted archives now routes into VaultSync with a password prompt flow.
- [VS-1536] `BackupKeyRotationService` with explicit user-triggered rotation flow for existing encrypted backups (global scope or single-project filter).
- [VS-1537] Per-project encryption password management action is now available in both Projects and Backups pages.
- [VS-1539] Settings > Encryption now includes a proactive "Set password (Projects)" flow to enroll project passwords on a new machine before restore/open.
- [VS-1539] Settings > Encryption now includes a `Lock now` action to immediately close/decrypt-open temp workspaces.
- [VS-1510] Settings now includes backup bandwidth cap and quiet-hours controls with persisted config fields.
- [VS-1511] Added shared transfer policy helper and automated unit tests for bandwidth conversion/throttling math.
- [VS-1512] Added shared quiet-hours policy helper and automated unit tests for overnight/daytime schedule evaluation.
- [VS-1513] Active backup cards now show runtime transfer-policy chips (`Throttled`, `Quiet hours`) in both the Backups page and backup widget.
- [VS-1513] Tray native menu and tray panel summary now show the active transfer-policy state when applicable.
- [VS-1520] Added persisted backup mode metadata (`full`/`incremental`) on backups and metadata sync records.
- [VS-1521] Added retention outcome line metadata on backup history card items.
- [VS-1522] Added restore confirmation guidance block support (type-aware and encryption-aware).
- [VS-1523] Updated README and docs terminology to document `Full` / `Incremental` / `Imported` backup types and restore guidance behavior.
- [VS-1523] Backups summary cards now include compact utility meters (run mix, backup freshness, storage composition) to use empty card space with actionable context.
- [VS-1540] Added snapshot diff-summary persistence fields (`added`, `modified`, `deleted`, `net size delta`, and `top changed paths`) to local and metadata snapshot schemas.
- [VS-1542] Backups history now includes per-snapshot diff export actions (`text` and `JSON`) plus an in-app git-style diff preview dialog.
- [VS-15013] release execution backlog with `VS-xxxx` work-item IDs, dependency links, and acceptance criteria in the roadmap.
- [VS-15014] phase plan (`A` security backbone, `B` controls, `C` UX/insights, `D` stabilization) with explicit release-gate policy.
- [VS-15015] Backup history cards now show an explicit encryption status tag (`Encrypted` / `Plain`).
- [VS-15016] Project cards now show an explicit encryption status tag (`Encrypted` / `Plain`) for quick visibility.

### Changed

- [VS-1501] Metadata backup writes now normalize crypto descriptor payloads and persist only non-secret fields.
- [VS-1501] Metadata sync export paths now use the shared plain-descriptor contract value for backward-safe plain backups.
- [VS-1504] Encryption secret fallback now requires explicit confirmation before keeping secrets in session memory.
- [VS-1502] Backup runs now support encrypted archive write mode and persist encrypted descriptor metadata in backup records.
- [VS-1502] Metadata sync import/export now preserves encryption flags and descriptor payloads for encrypted backups.
- [VS-1503] Restore flow now prompts for an encryption password only for encrypted backups and uses staged decrypt/extract before applying files.
- [VS-1505] Metadata sync import/export now stays compatible with mixed encrypted/plain history across legacy metadata-store schemas.
- [VS-1530] Settings persistence now stores only non-secret backup-encryption refs (`Enabled`, `KeyRef`, fallback policy) while password material stays in secure storage/session memory.
- [VS-1531] Backup runtime now resolves encryption mode per project using policy precedence (`project override` > `global`) before deciding archive encryption.
- [VS-1531] Projects repository schema now persists per-project encryption policy with migration-safe default `inherit`.
- [VS-1532] Metadata sync project settings import/export now preserves non-secret encryption fields (`encryptionPolicy`, `encryptionKeyRef`) across devices.
- [VS-1533] Backup encryption now resolves key source in order: project key reference first, then global key reference, while honoring project policy overrides.
- [VS-1533] Backup runs now fail fast with explicit errors when encryption is required but no key reference/secret is available.
- [VS-1534] Encrypted restore now attempts secure-store keys in order (project key reference first, then global key reference) before prompting for manual password entry.
- [VS-1534] Restore key fallback now prompts only when no stored key succeeds, preserving plain-backup restore behavior unchanged.
- [VS-1535] Single-instance activation now forwards file-open payloads so opening `.vse` while VaultSync is running reuses the current app session.
- [VS-1536] Added password rotation prompts targeting all projects or a selected project.
- [VS-1536] Backup records now update encrypted descriptor metadata/size after successful key rotation.
- [VS-1537] Projects and Backups per-project cards now share one password-edit flow, using a single app-level handler to prevent cross-page mismatch.
- [ISS-15017] Projects and Backups encryption sections now show a dedicated status pill (`Encrypted`, `Not protected`, or missing-password warning).
- [VS-1538] Opened encrypted backup content in temporary workspaces after resolving or prompting for the password.
- [VS-1539] Encrypted open-folder auto-lock timeout is now configurable in Settings and shared by in-app and external `.vse` open flows.
- [VS-1543] Encrypted open-folder now reuses a per-project in-memory session unlock within the configured timeout, then re-prompts after expiry.
- [VS-1511] Native backup copy path now enforces configured bandwidth caps in `rsync` (`--bwlimit`) and robocopy (`/IPG`).
- [VS-1512] Auto-backup timer now defers backup starts during configured quiet-hours windows with deterministic resume timing.
- [VS-1512] Quiet-hours policy is applied to new auto-backup starts only; active in-flight backups are allowed to complete.
- [VS-1513] Backup policy transitions are now emitted as informational `[Policy]` log entries (no warning/error noise) and trigger tray status refresh when state changes.
- [VS-1520] Backups history type chips now use `Full`/`Incremental`/`Imported` terminology from per-backup mode metadata.
- [VS-1521] Backup history cards now show retention outcome text (`eligible`, `protected`, `imported history`) and refresh it when Keep toggles.
- [VS-1522] Restore requests now open a confirmation dialog with a "What happens next" block before starting restore.
- [VS-1540] Snapshot creation now computes and stores diff summaries per snapshot, and metadata sync import/export now preserves those summary fields across devices.
- [VS-1541] Added snapshot change summaries, path previews, and explicit fallback states to project and backup history.
- [VS-1542] Diff-summary export writes now use collision-safe filenames under `Documents/VaultSync/Exports/SnapshotDiff` and report actionable export success/failure notifications.
- [ISS-15018] Settings quiet-hours inputs now use explicit side-by-side Start/End field groups for clearer overnight scheduling setup.
- [ISS-15019] Backup history chips now separate mode and encryption context (`Mode: ...`, `Encryption: ...`) for faster scanning.
- [ISS-15020] New backup summary/mode/encryption chip strings are now fully localization-key based (no hardcoded UI literals), and keys were added to all language packs.
- [ISS-15021] Backup freshness summary now shows localized state + relative age, with a localized threshold tooltip and state-based color coding.
- [ISS-15022] Applied semantic pill styles and safe text truncation with tooltips in Backups.
- [ISS-15023] Pill styling is now centralized in app-wide styles so Backups and Dashboard share the same visual behavior.
- [ISS-15024] Backups history type filters now use active-state toggle pills for clearer selected filter feedback.
- [ISS-15025] Quiet hours settings UI was redesigned with a compact window preview card and clearer start/end time inputs.
- [ISS-15026] Dashboard weekly activity graph now avoids stretch-to-row behavior, with thicker bars and adaptive chart height so low-activity weeks don't look sparse.
- [ISS-15027] Backups activity mini-chart now uses adaptive height and thicker bar segments for better readability at low counts.
- [ISS-15028] Backups per-project cards were reworked into a denser two-column layout (stats, destination, encryption, and actions grouped more cleanly).
- [ISS-15029] Dashboard weekly chart header now separates legend and summary rows to prevent overlap/clutter in windowed layouts.
- [ISS-15030] Project health warning strip now uses higher-contrast foreground text, and out-of-date copy is clearer.
- [ISS-15031] Projects details panel now uses a denser layout with key snapshot/size info pulled into the header and reduced empty middle spacing.
- [ISS-15032] Projects detail controls now use a structured 4-column grid to improve alignment of preset/destination/encryption/health sections.
- [ISS-15033] Settings destinations cards were reflowed with cleaner grouping (header, path actions, credentials, and two-column options) for better windowed readability.
- [ISS-15034] Settings quiet-hours window card was compacted with centered start/end controls and reduced horizontal dead space.
- [ISS-15035] Backups per-project cards were further tightened to prevent overlap between toggle/stat pills/actions on narrower widths.
- [ISS-15036] Dashboard and Backups weekly activity bars now use fixed centered segment widths to prevent stretch/overlap artifacts at low activity.
- [VS-1539] Project encryption enrollment/edit dialogs were extracted from `AppViewModel` into a dedicated `ProjectEncryptionEnrollmentService` while preserving existing metadata export + UI refresh behavior.
- [ISS-15037] Separate backup preparation and aggregate progress helpers from the main view model while preserving behavior.
- [ISS-15038] Separate manual and batch backup handlers from unrelated UI and update logic while preserving behavior.
- [ISS-15039] Separate backup history deletion, restore and encrypted-folder workflows from startup and navigation logic.
- [ISS-15040] Separate NAS monitoring, destination health, metadata synchronization and encryption rotation from the main view model.
- [ISS-15041] Separate tray backup, snapshot, recent-backup and encrypted-folder actions into a dedicated workflow.
- [ISS-15042] Separate update checks, retries, downloads and installer launches from the main view model.
- [ISS-15043] Added repository-level Prettier configuration (`.prettierrc.json`) and ignore rules (`.prettierignore`) for consistent formatting of supported text assets.
- [ISS-15044] Added repository-level `.editorconfig` with C# and text formatting defaults so .NET/C# formatters apply consistent style in IDE and CLI.
- [ISS-15045] Separate backup hashing, verification, drive-health notifications and restore advisory helpers into their own workflow.
- [ISS-15046] Separate navigation and view state from the main view model to isolate routing responsibilities.
- [ISS-15047] Separate startup service wiring and lazy backup view-model initialization while preserving startup behavior.
- [ISS-15048] Separate shared progress, localization, download-status and backup-notification helpers from the main view model.
- [ISS-15049] `CONTRIBUTING.md` fully restructured with the default `VS-xxxx` planning model and contribution flow.
- [ISS-15050] Core test suite rewritten to match current metadata-sync and destination behavior contracts.
- [ISS-15051] Redesigned weekly analytics with a separate insight panel, lighter surfaces, and a capsule-style activity graph.
- [ISS-15052] Kept storage cards visible at wide layouts with side-by-side usage charts, legends, and capacity details.
- [ISS-15053] Made project detail actions wrap without clipping or overlapping in windowed layouts.
- [ISS-15054] Dashboard storage donut now uses explicit visibility toggling against `HasStorageSeries` to avoid stale empty-chart presentation when data arrives after initial layout.
- [ISS-15055] Added `1.4` <-> `1.5` compatibility matrix runbook (`CM-1501`..`CM-1508`) to drive `VS-1591` release-gate validation.
- [VS-1501] Legacy plain backup crypto metadata (`{}`) now parses through the typed descriptor compatibility path.
- [VS-1504] Secure-store failures no longer require plaintext secret persistence in config as fallback path.
- [VS-1502] Destination scans and backup-size probes now recognize encrypted archive artifacts alongside plain archives.
- [VS-1503] Encrypted restore now fails with an explicit invalid-password/corruption error and leaves no partial restored output on wrong-password attempts.
- [VS-1503] `NeedsRestore` flags are now cleared only after a successful restore completion.
- [VS-1505] Import/preview from older metadata stores (missing `origin_machine_name` and encryption columns) no longer fails and defaults backups to plain compatibility values.
- [VS-1536] Rotation failures now preserve original encrypted backup artifacts via rollback-safe swap logic (no corruption on failure/interruption).
- [VS-1538] In-app `Open folder` no longer sends encrypted backups to the raw backup folder path that could trigger OS "Open with" on `.vse`.

### Fixed

- [BUG-15009] Dashboard storage donut now force-invalidates measure/visual on `StorageSeries` updates so the pie reliably appears after async data refreshes.
- [BUG-15010] Windows startup/debug runs no longer attempt to execute `/bin/ps` for parent-process info logging.
- [BUG-15011] Metadata sync tests now reflect current import rules for existing/missing backup paths.
- [BUG-15012] Windows installer now registers `.vse` file association so encrypted backup files open directly in VaultSync.
- [BUG-15013] Build no longer picks up generated `artifacts/tmpobj` sources as compile inputs, fixing duplicate assembly attribute errors (`CS0579`) in local builds.
- [BUG-15014] Dashboard weekly summary labels now compute after day-series arrays are populated, so summary text matches the rendered weekly chart.
- [BUG-15015] Dashboard activity summary now includes imported-run counts for parity with the weekly graph breakdown.
- [BUG-15016] Dashboard storage donut hover labels now truncate long project names to prevent tooltip overlay overflow in windowed layouts.

## [1.4.1] - 2026-02-06

### Added

- [VS-14001] Startup load deferral safeguards to reduce early UI stalls.

### Changed

- [ISS-14002] Dashboard/Backups now load immediately on first launch to avoid blank shells.
- [ISS-14003] Backup drive health probes now apply a cooldown to avoid repeated disk checks.
- [ISS-14004] Log console UI updates now use smaller batches when the console is open for smoother scrolling.
- [ISS-14005] Tray menu refreshes now skip rebuilds when data hasn't changed.
- [ISS-14006] Metadata auto-import now backs off longer after failures to reduce repeated I/O.
- [ISS-14007] Update checks now enforce a minimum interval to avoid duplicate startup fetches.
- [ISS-14008] Verbose log file writes now buffer more and flush less often to reduce I/O spikes.
- [ISS-14009] Patch and installer downloads now report progress and use longer timeouts for slow connections.
- [ISS-14010] Backups charts/summary now refresh only when the Backups view is active.
- [ISS-14011] Backup progress UI updates are now throttled to reduce UI churn.
- [ISS-14012] Tray storage health now stays hidden for network paths that can't report SMART.
- [ISS-14013] Auto-import notifications now only appear when new metadata is actually imported.
- [ISS-14014] Dashboard data now reuses a short cache window to reduce repeat DB reads.
- [ISS-14015] Log export now runs on a background thread to avoid UI stalls.
- [ISS-14016] Drive health probes are delayed briefly after startup to reduce early I/O spikes.
- [ISS-14017] Initial destination probes are delayed briefly after startup to reduce early network load.
- [ISS-14018] Backup history scans now skip destination sweeps when there are no backups.
- [ISS-14019] Log snapshots keep fewer lines when verbose logging is disabled.
- [ISS-14020] Active backup card updates are now batched to reduce UI thread churn.
- [ISS-14021] Dashboard refresh now reuses a cached repository when possible.
- [ISS-14022] Destination status overview now uses the in-memory config snapshot to reduce disk reads.
- [ISS-14023] Dashboard view model now initializes lazily when first shown.
- [ISS-14024] Startup retention/cleanup and metadata import now reuse the in-memory config snapshot to avoid extra disk reads.
- [ISS-14025] Backups view model now initializes lazily when first shown.
- [ISS-14026] Backups-related hot paths now use the in-memory config snapshot more consistently.
- [ISS-14027] Tray menu composition now uses helper builders for cleaner, more maintainable code.
- [ISS-14028] Backup progress labeling and backup-all aggregate updates now use shared helpers for clearer flow.
- [ISS-14029] Snapshot scan cache decisions now use a shared helper for cleaner logic.

### Fixed

- [BUG-14001] macOS: reduced UI freezes by deferring log console UI updates until the console is opened.
- [BUG-14002] macOS: tray menu opening no longer hangs the app.
- [BUG-14003] Startup now guarantees an initial view is rendered instead of showing a blank shell.
- [BUG-14004] Settings input text now centers vertically on Windows.
- [BUG-14005] Network destinations now hide SMART/drive health status when unavailable to avoid clutter.
- [BUG-14006] Backups destinations card now reflects configured destinations after lazy view model creation.
- [BUG-14007] Verbose log file flushes now happen off the UI thread to avoid periodic stalls.
- [BUG-14008] Destinations card now shows configured destinations as pending before probes run.
- [BUG-14009] Verbose log capture now offloads UI-thread log writes to a background queue to reduce stalls.
- [BUG-14010] Destinations overview now initializes as soon as the Backups view model is created.
- [BUG-14011] Destinations overview refresh work now runs off the UI thread to avoid stalls.
- [BUG-14012] Config reloads for settings/destination changes now happen off the UI thread.
- [BUG-14013] Backups reload now snapshots UI state before background work to avoid cross-thread access.
- [BUG-14014] Manual metadata refresh now loads config off the UI thread.
- [BUG-14015] Manual metadata refresh now prepares destinations off the UI thread.
- [BUG-14016] Launch-on-login setup now runs off the UI thread.
- [BUG-14017] Deferred startup tasks now load config off the UI thread.
- [BUG-14018] Resume-last-session view selection now loads config off the UI thread.
- [BUG-14019] Last-view persistence and update-skip tagging now save config off the UI thread.
- [BUG-14020] Backup verification config lookups now load off the UI thread.
- [BUG-14021] Delete-confirm dialog now loads config off the UI thread.
- [BUG-14022] Retention tombstone export now loads config off the UI thread.
- [BUG-14023] Backup throughput persistence now saves config off the UI thread.
- [BUG-14024] Delete flow now loads destination config off the UI thread.
- [BUG-14025] Metadata import retention config lookups now run off the UI thread.
- [BUG-14026] Force-backfill clearing now saves config off the UI thread.
- [BUG-14027] Destination probes now retry faster after an unreachable result to clear stale error states.
- [BUG-14028] Projects refresh now loads config off the UI thread.
- [BUG-14029] Project removal and snapshot actions now load config off the UI thread.
- [BUG-14030] Settings and destination tests now load/persist config off the UI thread.
- [BUG-14031] Backups and dashboard view models now load config off the UI thread.
- [BUG-14032] Onboarding tour now refreshes cached config off the UI thread.

## [1.4.0] - 2026-02-04

### Added

- [VS-14030] Backup estimate UI now shows size/time previews and capacity warnings.
- [VS-14031] Backup preflight API for size/time estimates.
- [VS-14032] Backups page now lets you pick a destination per project.
- [VS-14033] Settings toggles for scan cache and aggressive mode.
- [VS-14034] Per-project destination selection (including an "All destinations" option).
- [VS-14035] Scan cache support to speed up snapshot file scanning (with aggressive mode).
- [VS-14036] Preferred destination tracking on projects.
- [VS-14037] New localization keys for destination selection, update status, and drive health messaging.
- [VS-14038] Guided onboarding tour now spotlights first-time setup steps.

### Changed

- [ISS-14039] Preflight runs asynchronously so backups start immediately.
- [ISS-14040] Preflight now reuses latest snapshot stats to avoid extra scans.
- [ISS-14041] Preflight capacity checks include a small archive overhead.
- [ISS-14042] ETA calibration now tracks separate archive/copy throughput.
- [ISS-14043] Preflight caching now trims stale entries and reuses project scan stats.
- [ISS-14044] Backup throughput sampling now feeds ETA calibration.
- [ISS-14045] Scan cache cadence now enforces periodic full scans by run count and age.
- [ISS-14046] Backup, snapshot, and verification flows now resolve destinations per project instead of relying on the global backup root.
- [ISS-14047] Snapshot creation can reuse cached scan results when enabled.
- [ISS-14048] Projects list now shows the resolved destination label for each project.
- [ISS-14049] Drive health status messages now respect the active localization.
- [ISS-14050] Compact mode now drives page padding, card density, and list spacing across Projects and Backups.
- [ISS-14051] Compact mode now tightens card padding and typography across Dashboard, Settings, Notification, Log Console, and updater views.
- [ISS-14052] Sidebar destination overview now lists only active destinations when multiple are configured.
- [ISS-14053] Imported backup tags now include the source machine name when available from metadata sync.
- [ISS-14054] Project avatar colors now use external IDs when available for consistent colors across views and metadata sync.
- [ISS-14055] Active backup cards now show the destination label while running.
- [ISS-14056] Tray menu now opens on left click (popover) and right click (native menu) for faster access.
- [ISS-14057] Onboarding now defaults to the system UI language on first run (fallback to English).
- [ISS-14058] Onboarding tour now auto-navigates between pages and centers highlighted sections with smoother scrolls.
- [ISS-14059] App startup now opens in a maximized window instead of fullscreen.

### Fixed

- [BUG-14033] Restore/delete/open now resolve backups across inactive destinations when possible.
- [BUG-14034] Destination selector no longer renders as 'View not found'.
- [BUG-14035] Destination dropdowns now refresh immediately when destinations are added, renamed, or toggled.
- [BUG-14036] Destination dropdowns now default to Auto instead of rendering blank when a saved target is missing.
- [BUG-14037] Backup-all telemetry now uses per-project destination data.
- [BUG-14038] Removed stale backup-all variables that caused compile errors.
- [BUG-14039] macOS auto-start now reloads LaunchAgent entries reliably when toggled on/off.
- [BUG-14040] Imported backup badge no longer clips in history when the window is narrow.
- [BUG-14041] Missing localization keys for destination selector, update status, and drive health strings across all languages.
- [BUG-14042] Destination status overview now probes immediately so active destinations don't stay in Pending.
- [BUG-14043] Destination validation errors no longer spam while editing; they show on explicit save/test instead.
- [BUG-14044] Backup pause notification now includes the project name when imported history is newer.
- [BUG-14045] Onboarding now respects "first run" and no longer appears on every start.
- [BUG-14046] Settings view no longer fails to load due to duplicate control names.
- [BUG-14047] Onboarding highlights now stay aligned with their intended settings sections.

## [1.3.5] - 2026-01-28

### Added

- [VS-13001] Backups summary cards now show mini activity sparklines and extra stats.
- [VS-13002] Dashboard weekly chart now labels auto/manual/imported backups.

### Changed

- [ISS-13003] Backups weekly chart now uses a taller plot area and extra padding so bars/labels don't feel clipped.
- [ISS-13004] Patch manifest downloads now reuse cached results to reduce repeated update checks.
- [ISS-13005] Backups per-project cards now enforce unique accent colors.
- [ISS-13006] Backups summary sparklines and activity chart now scale up to use more space.
- [ISS-13007] Dashboard backups-this-week chart now uses the same compact bar style as the Backups page.
- [ISS-13008] Dashboard now avoids demo backup data when the database is empty.
- [ISS-13009] Total stored cards now show both local and imported totals.
- [ISS-13010] Backups summary cards now use a more compact, left-aligned layout with integrated mini charts.
- [ISS-13011] Backups summary cards now use distinct metric stacks instead of repeated mini charts.
- [ISS-13012] Backups summary cards now reduce repeated stats and emphasize primary values.
- [ISS-13013] Backups summary cards now use full-width stat grids for better fullscreen use.
- [ISS-13014] Backups summary cards now use presentable stat tiles for clearer scanning.
- [ISS-13015] Backups per day chart moved to the summary row for better visibility.
- [ISS-13016] Backups page weekly chart now focuses on the stacked bars only (average guide removed).
- [ISS-13017] Backups weekly charts now use thicker bars and a dynamic average guide line to better fill the card space.
- [ISS-13018] Restore advisories are suppressed when local project changes are newer than imported history.

### Fixed

- [BUG-13001] Imported backups now trigger retention cleanup for their destination path.
- [BUG-13002] Tray menu on MacOS now opens
- [BUG-13003] Hardened metadata sync locks to prevenet write fials on MacoOS
- [BUG-13004] Backups page now renders cached results before destination scanning to avoid empty startup screens.
- [BUG-13005] Backups summary charts now collapse in narrow windows to keep the text readable.
- [BUG-13006] Backups activity legend no longer overlaps the section title in tight layouts.
- [BUG-13007] Total stored values now include imported backups for consistent totals across machines.
- [BUG-13008] Dashboard legend dots now align with their labels.
- [BUG-13009] Filled missing localization keys for backup summary labels across all languages.

## [1.3.4] - 2026-01-25

### Added

- [VS-13019] Sidebar now shows a compact destination status overview for quick reachability checks.
- [VS-13020] Backups page now includes a dedicated destinations card showing reachability status.
- [VS-13021] Destination scan now imports untracked backups from destinations into history.
- [VS-13022] New tray menu

### Changed

- [ISS-13023] Backup destination status row refreshed for clearer hierarchy and status clarity.
- [ISS-13024] Destination status indicator now pulses only during reachability checks.
- [ISS-13025] Destination probes now skip redundant checks within a short window and only notify on state changes.
- [ISS-13026] Active backup stage labels now use color coding per phase.
- [ISS-13027] Backups page destinations card now sits alongside history for easier scanning.
- [ISS-13028] Per-project backup cards now use tighter spacing and centered accent pills/avatars.
- [ISS-13029] Destination cards now use tighter spacing for a more compact layout.
- [ISS-13030] Destination status cards now share a single layout to keep sidebar and backups styling aligned.
- [ISS-13031] Backups destinations card now links to Settings for destination management.
- [ISS-13032] UI status and stage brushes now reuse cached instances to reduce allocations.
- [ISS-13033] Log console now batches UI updates to reduce UI thread churn during verbose logging.
- [ISS-13034] Log console now buffers file writes and snapshots to reduce I/O and avoid UI-thread blocking during exports.
- [ISS-13035] Backups history grouping now reuses cached accent brushes to reduce allocations on refresh.
- [ISS-13036] Keep toggles now record a marker file so protected backups can be rediscovered by scans.
- [ISS-13037] Projects snapshot trend labels now show only when the day changes for cleaner timelines.
- [ISS-13038] Project snapshot history now loads via async repository calls.
- [ISS-13039] Snapshot trend bars now enforce a minimum height for readability.
- [ISS-13040] Backups view stat pills now reuse a shared style for cleaner layout.
- [ISS-13041] Log console file capture now flushes buffered output when disabled.
- [ISS-13042] Clear local cache now removes temporary patch staging data.

### Fixed

- [BUG-13010] Deleting a backup no longer collapses the active project group in history.
- [BUG-13011] Backup deletion now resolves destinations even if they are inactive.
- [BUG-13012] Backup deletion now keeps entries when the destination cannot be removed and reports permission failures.
- [BUG-13013] Backup deletion now mounts destinations with credentials before removing files to avoid NAS permission errors.
- [BUG-13014] Backup delete now retries with destination credentials after a permission error on NAS shares.
- [BUG-13015] Backup delete now allows entering one-time credentials after a permission error when no profile is set.
- [BUG-13016] Backup delete now prompts for credentials when a permission error prevents removing protected backups.
- [BUG-13017] Backup delete now resolves UNC paths for mapped drives when retrying with credentials.
- [BUG-13018] Backup delete now mounts UNC share roots (not subfolders) when retrying with credentials.
- [BUG-13019] Backups no longer write completion markers on network destinations to avoid ownership/permission locks.
- [BUG-13020] Destination status labels now reflect reachability instead of backup activity stages.
- [BUG-13021] Backup destination help now opens reliably from Settings.
- [BUG-13022] SMART/drive health status now refreshes alongside Backups page data.
- [BUG-13023] Metadata import now normalizes backup paths so cross-machine imports do not skip existing backups.
- [BUG-13024] Metadata import now tombstones missing backups on disk to prevent reappearing entries after manual deletions.
- [BUG-13025] Metadata import now cleans orphan snapshots when their backups are missing on disk.
- [BUG-13026] Archive upload auto-tune timeouts no longer cancel backups; fallback buffer is used instead.
- [BUG-13027] Destination status no longer resets to Pending during manual backups when probe data is already available.
- [BUG-13028] Tray menu layout now prioritizes quick actions, status summary, and clean per-backup actions.
- [BUG-13029] Tray icon now opens a modern popover panel with destinations, quick actions, and recent backups.
- [BUG-13030] Tray popover now opens on the left side to avoid edge clipping.
- [BUG-13031] Tray popover destinations restore the vertical status pill indicator.
- [BUG-13032] Backups destinations cards now show the vertical status pill indicator again.
- [BUG-13033] Destination status labels and dots now align correctly within the cards.
- [BUG-13034] Project avatars now render as perfect circles in the backups list.
- [BUG-13035] Destination reachability labels no longer get replaced by backup completion states.
- [BUG-13036] Destination scans now treat read-only backup folders as protected so they can be unprotected in-app.
- [BUG-13037] Per-project backup list no longer stretches when expanding history entries.
- [BUG-13038] "What's new" links now open in the browser instead of rendering as plain text.

## [1.3.3] - 2026-01-21

### Changed

- [ISS-13043] Dashboard refresh now uses aggregated queries for counts and totals to avoid loading full history.
- [ISS-13044] UI view refreshes no longer re-run database schema setup; initialization now happens once at startup.
- [ISS-13045] Archive and fallback copy paths reuse the snapshot file list when available to avoid re-enumerating the full tree.
- [ISS-13046] Backup retention now batches orphan snapshot cleanup to avoid repeated DB scans per deletion.
- [ISS-13047] Metadata sync now preloads external ID maps to reduce per-item DB lookups during import/preview.
- [ISS-13048] Backups history refresh now coalesces repeated filter updates to avoid redundant rebuilds.
- [ISS-13049] Update checks reuse a short in-memory cache to avoid repeated API fetches within a session.
- [ISS-13050] App backup flows now use targeted backup lookups instead of full-history scans.
- [ISS-13051] Project refresh now builds discovery/preset data off the UI thread to reduce stutter on large trees.
- [ISS-13052] Tray recent backups now uses a single batched query instead of per-project scans.
- [ISS-13053] Backups history reload now coalesces repeated requests and avoids UI-thread blocking for open-folder resolution.
- [ISS-13054] Snapshot cleanup now checks for remaining backups with a targeted query instead of loading full project history.
- [ISS-13055] Archive upload auto-tune now scales buffer sizes up on faster links and honors per-destination overrides for SMB.
- [ISS-13056] Archive upload auto-tune now runs on SMB destinations with a longer probe timeout to avoid 0 MB/s results.
- [ISS-13057] Network drive destinations now count as remote so parallel archive uploads can kick in on SMB-mapped paths.
- [ISS-13058] Archive upload auto-tune probe now uses a larger test file and allows higher buffer ceilings on fast links.
- [ISS-13059] Dashboard and backups totals now exclude imported-only backups unless they were created locally.
- [ISS-13060] Parallel archive upload now exits cleanly after completion instead of stalling on the heartbeat task.
- [ISS-13061] Dashboard refresh now coalesces concurrent requests to avoid redundant refresh work.
- [ISS-13062] Backups view reload now reuses cached data when off-page to avoid redundant DB reads.
- [ISS-13063] Projects refresh now reuses cached discovery results unless a manual refresh is requested.
- [ISS-13064] Projects refresh now coalesces concurrent requests to avoid redundant refresh work.
- [ISS-13065] Auto backup now resolves destinations once per run to avoid repeated mount checks per project.
- [ISS-13066] Navigation now skips redundant reloads when switching to the current view and throttles dashboard refreshes.
- [ISS-13067] Metadata sync preview now uses lightweight store queries to reduce load time on large metadata stores.
- [ISS-13068] Startup now defers destination probes, metadata auto-import, and update checks briefly to reduce launch stutter.
- [ISS-13069] Projects page detail panel refreshed with a modern preset control and tightened stat cards.
- [ISS-13070] Projects page preset dropdown and recent snapshots list refreshed for consistency.
- [ISS-13071] Projects page registration checks now run off the UI thread to avoid selection stalls.
- [ISS-13072] Metadata import UI refresh now coalesces repeated updates to avoid redundant reloads.
- [ISS-13073] Archive compression now uses larger stream buffers and sequential scan hints for better throughput.
- [ISS-13074] Dashboard KPI typography now uses heavier weights to reduce the thin look.
- [ISS-13075] Dashboard weekly backups panel layout refreshed with a compact stat column and framed chart.

### Fixed

- [BUG-13039] Windows release publishes default to self-contained `win-x64` to avoid missing runtime prompts.
- [BUG-13040] Startup crash in backup path normalization (Dapper materialization) resolved.
- [BUG-13041] Dashboard backup storage card no longer shows a stale/translucent bar behind the usage segments.
- [BUG-13042] Projects page All Projects panel now uses a dedicated scroll region so the list reaches the end without clipping.
- [BUG-13043] Projects page shows "Not added" for unregistered projects with no snapshots.
- [BUG-13044] Projects page uses latest backup timestamps (including imported) to avoid stale health when snapshots lag behind.
- [BUG-13045] Projects page date labels now use ASCII separators to avoid missing glyphs.
- [BUG-13046] Snapshot history now orders by timestamp to avoid stale "latest" entries.
- [BUG-13047] Metadata import now uses temp copies when WAL files are present to stabilize manual refresh previews.
- [BUG-13048] Metadata import preview/import now ignores backups that are tombstoned in the store to prevent flip-flopping adds/deletes.
- [BUG-13049] Dashboard now refreshes on initial load so the first view shows live data.
- [BUG-13050] Restore now extracts archived backups (`data.zip`) instead of copying the archive file.
- [BUG-13051] Restore now resolves imported backups using destination aliases when original paths are missing.
- [BUG-13052] Restore now uses the configured Projects root when a project path is missing on a new machine.
- [BUG-13053] Backup progress now switches to a dedicated finalizing stage and disables cancel once uploads complete.

## [1.3.2] - 2026-01-18

### Added

- [VS-13076] Cross-machine metadata store (`.vaultsync/meta/`) with portable project/snapshot/backup records and external IDs.
- [VS-13077] Metadata sync controls (global + per-destination), manual refresh, and review dialog.
- [VS-13078] Metadata backfill options with per-destination force-export toggle.
- [VS-13079] macOS rsync bundling (arch-specific) plus Settings hint when rsync is missing/too old.
- [VS-13080] Archive upload auto-tuning per destination (small probe file).
- [VS-13081] Toggle to enable/disable parallel archive uploads.
- [VS-13082] "What's new" popup shown once per version on first launch after updating.
- [VS-13083] Editable `docs/WHATS_NEW.md` content for the "What's new" popup.

### Changed

- [ISS-13084] Auto-imported projects now advise restore only when imported history is newer.
- [ISS-13085] Manual per-project backups can run concurrently (unless backup-all is active).
- [ISS-13086] Drive health probe deferred to reduce startup impact.
- [ISS-13087] Destination probe tracks effective path/read-only status.
- [ISS-13088] Backups page right panel now uses expandable project headers with clearer stats.
- [ISS-13089] Removed sample ?default? projects when no real projects exist. (thanks to King_Hippo for reporting)
- [ISS-13090] Scroll layout now scales more reliably at higher DPI. (thanks to King_Hippo for reporting)
- [ISS-13091] Docs updated to cover new features and macOS release flow.
- [ISS-13092] macOS NFS auto-mount is disabled; pre-mounted paths are required instead.
- [ISS-13093] Archive upload auto-tune now defaults to off, with a fixed buffer fallback.
- [ISS-13094] SMB archive uploads use a smaller buffer and avoid parallel writers by default.

### Fixed

- [BUG-13054] Fixed localization coverage across all languages (including backup progress/status keys).
- [BUG-13055] Arabic UI font fallback now uses bundled Noto Sans + Noto Sans Arabic to avoid missing glyphs.
- [BUG-13056] Metadata import handles locked/missing stores (temp copy with WAL/SHM, schema ensure).
- [BUG-13057] Manual/auto metadata refresh now updates UI lists immediately.
- [BUG-13058] Backup retention and cleanup now respect destination paths and skip unrelated directories; interrupted backups are cleaned safely.
- [BUG-13059] Backup status cards no longer duplicate speed/ETA, support cancelling/deleting states, and avoid auto-scroll jumps.
- [BUG-13060] Dashboard storage totals, per-project segments, and donut tooltips now match actual stored data.
- [BUG-13061] Backups page right panel/history styling cleaned up with clearer hierarchy.
- [BUG-13062] Toast notifications no longer render a duplicated band.
- [BUG-13063] macOS mounts now use a user-writable root, redact SMB passwords, validate SMB/NFS mounts, and report permission errors instead of crashing.
- [BUG-13064] macOS/Linux free-space checks now use statvfs and avoid false readings on unmanaged mounts.
- [BUG-13065] Destination tests use unique probe files to avoid repeated "file exists" warnings.
- [BUG-13066] Archive upload auto-tune now times out quickly and can be disabled in Settings.
- [BUG-13067] Backup storage usage card now preserves the last known usage when the target is temporarily unavailable.
- [BUG-13068] Archive upload progress now stays responsive on slow links and uses longer stall timeouts.
- [BUG-13069] Upload status now shows "Finalizing" after 100% instead of "Waiting for network".
- [BUG-13070] Retention cleanup now normalizes cross-platform backup paths to avoid false "not found" logs.
- [BUG-13071] Backup cancellation now shows a cancelling state and avoids failed notifications after cleanup.
- [BUG-13072] macOS fullscreen now falls back to maximized to avoid a crash during the native fullscreen transition.
- [BUG-13073] macOS SMB auto-mount now respects subfolder paths (e.g., `//host/share/Dev`) for backups and metadata import.

## [1.2.3] - 2026-01-07

### Added

- [VS-12001] Configurable update check interval in Settings -> Advanced.
- [VS-12002] Manual "Check for updates now" action for on-demand update checks.
- [VS-12003] Roadmap outline for upcoming features and priorities.
- [VS-12004] Active backup cards now show explicit stages (preparing, hashing, backing up, compressing, uploading).
- [VS-12005] Snapshot hashing now reports progress and ETA during backups.
- [VS-12006] Active backup detail line now shows the current file name plus files moved/total and speed.
- [VS-12007] Update banner actions to skip a version or close the banner.
- [VS-12008] Persisted skipped update tag to suppress a specific release.
- [VS-12009] Localized copy-stage strings and backup status keys across all languages.
- [VS-12010] Localized "No snapshots yet" and time-since strings on the Projects page.

### Changed

- [ISS-12011] Backup compression now defaults to off for new installs.
- [ISS-12012] Update checks now expose richer diagnostic logging (candidates, decisions, errors).
- [ISS-12013] Settings and log console buttons now use unified action styles.
- [ISS-12014] Log console window now matches app card styling and layout.
- [ISS-12015] Active backup card layout refreshed with clearer status/ETA and staging.
- [ISS-12016] Active backup phases now reset the progress bar between hashing and copy phases.
- [ISS-12017] Copy phase now reports estimated file counts and copy speed in MB/s.
- [ISS-12018] Copy phase now derives progress from destination file sizes for steadier percentages.
- [ISS-12019] Copy progress sampling now batches file checks to reduce stalls on large backups.
- [ISS-12020] Backup snapshots now defer hashing until after data is copied to speed up the copy phase.
- [ISS-12021] Auto-backup runs now parallelize projects for faster completion.
- [ISS-12022] Robocopy thread count now scales with CPU cores for higher throughput.
- [ISS-12023] Active backup cards now show live elapsed time per phase.
- [ISS-12024] Backup/mount steps now emit detailed console logs for destinations and network mounts.
- [ISS-12025] Copy progress now logs periodic file/percent/speed updates in the console.
- [ISS-12026] Robocopy progress now feeds ETA/percent lines to the UI when file-size scanning is slow.
- [ISS-12027] Robocopy output now logs periodic progress/file hints to the console.
- [ISS-12028] Copy phase now surfaces "robocopy" activity even before file sizes start reporting.
- [ISS-12029] Backup ETA helper text now localizes across supported languages.
- [ISS-12030] App settings writes now retry with a temp file to avoid crashes during concurrent saves.
- [ISS-12031] Update checks now use ETag caching and a single release page to reduce rate-limit pressure.
- [ISS-12032] Network share backups now prefer rsync delta when available and tune robocopy for network paths.

### Fixed

- [BUG-12001] Update banner now clears when no newer release is available, preventing stale "update available" states.
- [BUG-12002] Patch installs now shut down cleanly without triggering the "still running" tray notification.
- [BUG-12003] Patch helper relaunch no longer fails due to an invalid app manifest XML header.
- [BUG-12004] Language switching now loads legacy-encoded localization files correctly.
- [BUG-12005] Log console filters noisy Avalonia trace spam for layout/input/render-loop glitches.
- [BUG-12006] Manual update checks now log their progress and outcomes for troubleshooting.
- [BUG-12007] Cleaned mojibake in localized strings so non-ASCII languages render correctly.
- [BUG-12008] Replaced broken localization glyphs (bullet, separator, dismiss) to avoid ? placeholders.
- [BUG-12009] Fixed garbled update language strings across non-English translations.
- [BUG-12010] Settings view no longer jumps to the top when switching language.
- [BUG-12011] Settings descriptions now wrap cleanly instead of clipping in narrow windows.
- [BUG-12012] Active backup progress bars no longer jump to 100% prematurely.
- [BUG-12013] Backup verification now runs off the UI thread to prevent completion freezes.
- [BUG-12014] Missing update status label now added across all translations.
- [BUG-12015] Projects page snapshot and health labels now localize correctly after language changes.
- [BUG-12016] Update-available notification now calls out the active update channel (stable/beta).
- [BUG-12017] Windows uninstaller now removes bundled tools under `tools`.
- [BUG-12018] Post-backup verification and hashing now run asynchronously to avoid blocking the UI.
- [BUG-12019] Update banner layout now matches the app style and groups actions cleanly.
- [BUG-12020] Installer fallback button only appears after a patch install fails.
- [BUG-12021] Project health pills now refresh on language switch.
- [BUG-12022] Projects/Settings labels now wrap instead of truncating.

## [1.2.0] - 2026-01-01

### Added

- [VS-12033] Incremental backup mode (rsync hardlinks) toggle to keep history while only copying changes.
- [VS-12034] New "Delta sync for large files" backup setting, with persisted config and localized UI copy.
- [VS-12035] Bundled cwRsync client + license bundle under tools/rsync, copied into Windows publish output for zero-install delta sync.
- [VS-12036] Crash handler that writes a crash log and shows a crash dialog with copy/open-log actions.
- [VS-12037] In-app log console with live capture, optional disk logging, and export support via Settings -> Advanced.
- [VS-12038] Dedicated updater window that stays visible while a patch installs and the app restarts.

### Changed

- [ISS-12039] Updater now surfaces installer downloads when patches are incompatible, enabling version skipping and beta-to-stable moves.
- [ISS-12040] Incremental backups now disable the delta-sync toggle to avoid slow conflicting modes.
- [ISS-12041] Backups can use rsync delta transfers when enabled; Windows prefers bundled rsync and falls back to PATH/robocopy.
- [ISS-12042] rsync runner now supports custom executable paths and optional whole-file mode.
- [ISS-12043] Refined backup settings descriptions for clarity in the Settings UI.
- [ISS-12044] Updated backup and advanced settings translations across all supported languages.

### Removed

- [ISS-12045] Removed legacy beta notes document.

### Fixed

- [BUG-12023] Backup settings text wrapping improved for delta/incremental descriptions.
- [BUG-12024] Log console auto-scroll no longer triggers layout loop warnings; noisy layout trace messages are filtered.
- [BUG-12025] Log console no longer blocks main window interaction.
- [BUG-12026] rsync on Windows now hides the console window and rewrites paths for bundled cwRsync compatibility.
- [BUG-12027] Suppress update banners and notifications while handling crashes.
- [BUG-12028] ViewLocator now ignores non-view-model data types to avoid mis-instantiating log items.

## [1.1.0] - 2025-12-17

### Added

- [VS-11001] Added responsive layouts that adapt Dashboard, Settings, Projects, and Backups to available width and display scaling.
- [VS-11002] Completed translated UI text across supported languages so changing locale no longer exposes English placeholders.

### Changed

- [ISS-11003] Aligned sidebar branding with shared light/dark theme resources.
- [ISS-11004] Stacked storage metrics and hints vertically with consistent left alignment.
- [ISS-11005] Aligned light-theme branding with shared foreground and primary colors.
- [ISS-11006] Beta update checks now consider stable releases and will upgrade prerelease installs to the matching stable version when available.

### Fixed

- [BUG-11001] Prevented locked-executable build failures by closing the running Windows application before rebuilding.
- [BUG-11002] Limited still-running notifications to tray minimization so quitting no longer triggers them.
- [BUG-11003] Prevented multiple instances of the app from launching and activated the existing window when a second launch is attempted.
- [BUG-11004] Kept Dashboard storage charts and wrapping legends from overlapping adjacent cards.
- [BUG-11005] Settings destinations/credentials layout now aligns controls and action buttons correctly on narrow and wide windows.

## [1.0.0] - 2025-12-07

### Added

- [VS-10001] Added localized housekeeping controls and credential-aware destination tests beside advanced fallback backup settings.
- [VS-10002] Auto backups now compare snapshots before running so they skip when nothing changed and report skips separately in the UI.

### Changed

- [ISS-10003] Dashboard storage/gradient branding, shell tagline localization, and the backup settings layout use theme-aware resources so every element adapts to light/dark variants.

### Fixed

- [BUG-10001] Windows SMB mounts handle error 1219 by disconnecting existing sessions and retrying, and clipboard/mount tooling runs hidden to avoid flickering consoles.

## [0.9.8] - 2025-12-05

### Fixed

- Language selection now persists across restarts/updates and settings clamp numeric fields (snapshots, intervals, free-space) to avoid crashes.
- Updated translations and storage labels for a cleaner UI.

## [0.9.7.3] - 2025-12-05

### Fixed

- Copy the complete installation before elevated patching so dependencies remain available and the application restarts after updating.

## [0.9.7.2] - 2025-12-04

### Fixed

- Patch manifest validation now treats missing revision as zero, allowing 0.9.7 ? 0.9.7.1/2 deltas to apply when the assembly reports 0.9.7.0.
- Rebuilt patch assets for 0.9.7.2.

## [0.9.7.1] - 2025-12-04

### Fixed

- Elevated patch helper launch for Program Files installs and resolved compile warnings; rebuilt patch assets for 0.9.7.1.

## [0.9.7] - 2025-12-04

### Changed

- Bumped Windows metadata to 0.9.7 (installer + assembly) to ship the built-in patch helper flow that self-applies and restarts.
- Regenerated the Windows patch assets to match the latest publish output.

## [0.9.4] - 2025-12-02

### Changed

- Update Windows .NET 8 build and installer metadata to 0.9.4 so the update channel discovers the hotfix.
- Normalize version strings with a shared VersionHelper so 0.9.4 delta manifests validate against older installations.

### Fixed

- Publish 0.9.4 Windows delta manifests and archives so 0.9.3 installations download only changed executable, library and runtime configuration files.

## [0.9.0-beta.1] - 2025-11-16

### Added

- Patch-based updater downloads delta packages per platform from the `stable` GitHub release channel and stages them for the updater helper.
- Cross-platform localization pipeline with JSON resource dictionaries, `LocalizationService`, and Italian translations covering Settings, Dashboard, Backups, Projects, and notifications.
- Language selector in Settings -> Advanced plus docs showing how to add new languages.

### Changed

- Docs now describe the patch updater + localization workflow ahead of the public beta.

## [0.8.1] - 2025-11-09

### Changed

- Updater now surfaces installer downloads when patches are incompatible, enabling version skipping and beta-to-stable moves.
- Backup settings text wrapping improved for delta/incremental descriptions.
- Watch cycles are now atomic ? no overlap possible.
- Improved log readability during watch cycles (Spectre.Console markup formatting).
- Debounce logic refined for high-frequency file systems.

### Fixed

- Resolved SQLite **FOREIGN KEY constraint** errors during heavy file churn in watcher mode.
- Added concurrency protection in `WatchCommand` using `SemaphoreSlim` to serialize snapshot/sync/verify cycles.
- Added graceful handling and clear message for FK19 errors.
- Ensured watcher cancellation safely short-circuits change handlers.
- Verified stability under stress: rename storms, 400+ file bursts, large binary files, read-only destinations.

## [0.8.0] - 2025-11-08

### Changed

- Modularize CLI commands with dedicated files, namespaces and modern Spectre.Console.Cli branch registration.
- Verified compatibility with all existing commands.
- Logging now centralized through `VaultSync.CLI.Utils.Log`.

### Fixed

- Command discovery issues under .NET 8 due to non-generic branch registration.
- CLI startup logging consistency.

## [0.7.0] - 2025-11-03

### Added

- **Spectre.Console.Cli** integration replacing raw `System.CommandLine`.
- Implemented full project lifecycle commands: - `init`, `add-project`, `remove-project`, `list-projects`, `set-path`, `snapshot`, `sync`, `verify`, `restore`, `history`, `diff`, `prune`, `doctor`, `self-test`, `version`.
- Added `config` and `presets` branches with subcommands for configuration inspection.
- Integrated `Log` utility with color-coded console output.

### Fixed

- Early null reference handling when DB not initialized.
- Command help output rendering.

## [0.6.0] - 2025-10-31

### Added

- Core `SqliteRepository` with schema creation for `projects`, `snapshots`, and `files` tables.
- Services: `SnapshotService`, `SyncService`, `VerifyService`, `FilterService`, `ScannerService`, `HashService`, and `RobocopyRunner`/`RsyncRunner`.
- Implemented basic snapshot creation, hash storage, and diff comparison.
- Introduced `DoctorCommand` to validate environment (`rsync`, `robocopy`, DB path).

## [0.5.0] - 2025-10-27

### Added

- Initial CLI project structure: `VaultSync.CLI` under `src/`.
- Basic `Program.cs` entrypoint with hardcoded commands for testing.
- Local SQLite persistence prototype.
- Basic logging utilities.

## [0.4.0] - 2025-10-19

### Added

- Migration of OverSteer network management and RaceManager flow to separate baseline (predecessor to VaultSync CLI project).
- Established shared core for later reuse (config loader, command dispatcher).

## [0.3.0] - 2025-10-17

### Added

- Baseline logic for AI racing system (from related OverSteer project, used as testing ground for VaultSync command orchestration).

## [0.1.0] - 2025-10-15

### Added

- Project initialized, foundational scaffolding set up for CLI + SQLite architecture.

[Unreleased]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.9...work/1.9.7-changelog-protocol
[1.8.9]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.8...v1.8.9
[1.8.8]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.7...v1.8.8
[1.8.7]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.6...v1.8.7
[1.8.6]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.5...v1.8.6
[1.8.5]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.4...v1.8.5
[1.8.4]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.3...v1.8.4
[1.8.3]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.2...v1.8.3
[1.8.2]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.1...v1.8.2
[1.8.1]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.8.0...v1.8.1
[1.8.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.7.5...v1.8.0
[1.7.5]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.7.4...v1.7.5
[1.7.4]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.7.3...v1.7.4
[1.7.3]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.7.2...v1.7.3
[1.7.2]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.7.1...v1.7.2
[1.7.1]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.7.0...v1.7.1
[1.7.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.5.1...v1.6.0
[1.5.1]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.5.0...v1.5.1
[1.5.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.4.1...v1.5.0
[1.4.1]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.3.5...v1.4.0
[1.3.5]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.3.4...v1.3.5
[1.3.4]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.3.3...v1.3.4
[1.3.3]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.3.2...v1.3.3
[1.3.2]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.2.3...v1.3.2
[1.2.3]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.2.0...v1.2.3
[1.2.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ATAC-Helicopter/VaultSync/compare/v0.9.8...v1.0.0
[0.9.8]: https://github.com/ATAC-Helicopter/VaultSync/compare/v0.9.7.3...v0.9.8
[0.9.7.3]: https://github.com/ATAC-Helicopter/VaultSync/compare/v0.9.7.2...v0.9.7.3
[0.9.7.2]: https://github.com/ATAC-Helicopter/VaultSync/compare/v0.9.7.1...v0.9.7.2
[0.9.7.1]: https://github.com/ATAC-Helicopter/VaultSync/compare/v0.9.7...v0.9.7.1
[0.9.7]: https://github.com/ATAC-Helicopter/VaultSync/compare/v0.9.4...v0.9.7
[0.9.4]: https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v0.9.4
[0.9.0-beta.1]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#090-beta1---2025-11-16
[0.8.1]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#081---2025-11-09
[0.8.0]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#080---2025-11-08
[0.7.0]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#070---2025-11-03
[0.6.0]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#060---2025-10-31
[0.5.0]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#050---2025-10-27
[0.4.0]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#040---2025-10-19
[0.3.0]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#030---2025-10-17
[0.1.0]: https://github.com/ATAC-Helicopter/VaultSync/blob/work/1.9-roadmap-realignment/docs/release-evidence/changelog-history-before-standard-2026-10-04.md#010---2025-10-15
