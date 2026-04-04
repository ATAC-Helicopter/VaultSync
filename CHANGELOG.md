# Changelog
## [1.7.1] - Unreleased
### Added
- [VS-1725] Added scheduled GitHub release download snapshots with a dedicated public stats branch and JSON history.
### Changed
- [VS-1725] Download stats now generate both a readable HTML/Markdown summary and raw release-asset history from the same workflow.
- [VS-1725] README now uses repo-owned visuals and links to the download-stats branch instead of relying on a stale third-party repo card.
### Fixed
- [BUG-17085] Backup advisories now batch repeated project warnings into grouped notifications, and OS notifications use stable grouping keys instead of stacking one alert per project.
- [BUG-17015] Dashboard header title, subtitle, and summary pills now stay left-aligned so the top overview block reads consistently with the rest of the page.
- [BUG-17016] Dashboard backup-storage card now uses a ranked top-consumers list instead of an oversized pill cloud, making the lower-right space useful and readable.
- [BUG-17017] Dashboard, Projects, and Settings now route the remaining hardcoded 1.7.x UI copy through English localization keys instead of shipping raw literals.
- [BUG-17017] Theme option labels and settings log/export status text now also use English localization keys instead of hardcoded literals.
- [BUG-17017] Crash dialog, placeholder fallback, and missing-view fallback text now also resolve through English localization keys instead of raw literals.
- [BUG-17017] Shell navigation titles and header fallback copy now also resolve through English localization keys instead of raw literals.
- [BUG-17018] Dashboard backup storage now shows a bounded top-consumers list instead of mixing project rows with the generic 'Other' storage segment.
- [BUG-17019] Diagnostics now suppress expected first-chance missing-path and retention permission exceptions so verbose logs stay focused on actionable faults.
- [BUG-17020] Added regression tests for the download-stats snapshot script so release totals, deltas, highlights, and history output stay stable.
- [BUG-17021] Download stats history now prunes older daily snapshots while keeping recent runs and monthly checkpoints, preventing the stats branch from growing without bounds.
- [BUG-17022] The download-stats workflow now runs its regression tests before publishing snapshots, so broken report logic fails fast instead of pushing bad history.
- [BUG-17023] Tray panel header and tooltip now use localized shell copy instead of raw English fallbacks.
- [BUG-17024] Read-only dashboard, backups, and tag-color UI paths now use cached config snapshots instead of reloading config from disk each time.
- [BUG-17025] Settings diagnostics refresh and tag-chip appearance reads now also use cached config snapshots instead of hitting config storage for every read-only refresh.
- [BUG-17026] Dashboard refresh and backup view read-only display refreshes now also use cached config snapshots instead of reloading config from disk during normal UI updates.
- [BUG-17027] Support bundle export now reads the cached config snapshot for report generation instead of reloading config from disk during a read-only export.
- [BUG-17028] Startup localization, updater theme bootstrap, tray visibility checks, and last-view restore now also use cached config snapshots instead of reloading config during read-only startup flows.
- [BUG-17029] WhatsNew checks, onboarding gating, tray-menu reads, close-to-tray behavior, drive-health timing, and theme bootstrap now also use cached config snapshots instead of reloading config during read-only UI flows.
- [BUG-17030] Deferred startup update/metadata checks and background destination probing now also use cached config snapshots instead of reloading config during read-only background refresh flows.
- [BUG-17031] Tray health/menu fallback reads now consistently use cached config snapshots when the shared app view-model snapshot is unavailable, avoiding unnecessary disk reads in shell refresh paths.
- [BUG-17032] Onboarding step refreshes now use cached config snapshots instead of reloading config during read-only tour-state checks.
- [BUG-17033] Opening backup folders from tray actions now uses cached config snapshots instead of reloading config during read-only destination resolution.
- [BUG-17034] Projects page refresh, snapshot history loads, group actions, localization refresh, and read-only project display updates now use cached config snapshots instead of reloading config during normal UI workflows.
- [BUG-17035] Dashboard backup storage now caps the top-consumers card to a small ranked list while preserving the aggregate '+ more' row when additional projects exist.
- [BUG-17036] Metadata post-import retention checks, NAS monitoring, delete/restore preparation, auto-backup preparation, and restore password resolution now use cached config snapshots instead of reloading config during read-only background and backup-history flows.
- [BUG-17037] Projects search now supports multi-term matching across project names, paths, and tags so narrower searches are easier without changing views.
- [BUG-17038] Backup-all preparation now uses the cached config snapshot instead of reloading config during read-only orchestration setup.
- [BUG-17039] Projects search term matching now routes through a dedicated helper, reducing inline filter complexity without changing behavior.
- [BUG-17040] Projects discovery now loads lazily when the Projects view is opened instead of refreshing during app construction, reducing startup work on cold launch.
- [BUG-17041] Dashboard and backups warm-loads now wait briefly after startup instead of competing immediately with shell initialization, reducing perceived startup hangs and startup impact.
- [BUG-17042] Dashboard no longer renders a duplicate top overview block above the KPI row, removing the broken double-summary layout while keeping the detailed sections intact.
- [BUG-17043] Deferred startup now refreshes projects discovery shortly after launch, so Dashboard content repopulates without bringing back the old eager startup hit.
- [BUG-17044] Dashboard refresh now applies chart and collection updates on the UI thread, fixing the empty post-startup dashboard caused by invalid-thread refresh failures.
- [BUG-17045] Storage usage now defaults to a largest-first legend and exposes a compact sort selector so users can switch between size-based and alphabetical ordering.
- [BUG-17046] Backup storage now raises top-consumer property updates reliably after dashboard refreshes, fixing the empty right-hand consumer list despite valid usage data.
- [BUG-17047] Backups now use the center summary card for project, type, destination, and storage/security context from the latest backup so the page wastes less space.
- [BUG-17048] Repository licensing text is now cleaned up and consistent across the root license, README, and CLI package metadata, removing stale placeholders and broken encoding.
- [BUG-17049] Added a top-level third-party notices index so bundled rsync helper licenses are easier to audit before release.
- [BUG-17050] Installer-based updates now shut down VaultSync automatically after the installer is launched, so Windows setup can continue without a manual close step.
- [BUG-17051] Release-facing docs and metadata now target `1.7.1`, including app versioning, What's New, updater/releasing docs, and issue-template examples.
- [BUG-17052] Startup and Backups destination probing now back off after recent failures and avoid immediate rescans of known-offline remote targets, reducing hangs when a NAS/server is unavailable.
- [BUG-17053] Embedded color pickers now fully offset the stock tab strip height, fixing the visible clipped header chrome in Settings and Projects.
- [BUG-17081] Replaced the production `.ico`, tray PNG, and macOS `.icns` assets with renders of `docs/branding/vaultsync-logo-icon.svg` while preserving the previous assets under `src/VaultSync.UI/Assets/backup/2026-03-31-icon-refresh/`.
- [BUG-17083] Rebalanced the new production SVG icon so the safe composition sits centered within the icon tile instead of reading top-left heavy.
- [BUG-17084] Updated the safe .NET and Avalonia dependency set to current patch releases for `1.7.1`, including SQLite, cryptography, JSON, Dapper, Namotion.Reflection, the test JSON package, and the Avalonia 11.3.13 stack.

## [1.7.0] - 20.03.2026
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
- [VS-1720] Native rsync/robocopy backup paths keep their restartable transfer semantics, so non-archive retries do not restart the whole backup set on the next run.
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
- [BUG-17007] Metadata import no longer creates or leaves projects with an empty `RootPath`; imported root hints are preserved and existing blank paths are repaired when metadata provides a usable root.
- [BUG-17001] Doctor repair workflows now marshal state updates onto the UI thread.
- [BUG-16023] Restore status no longer falls back to raw localization keys.
- [BUG-16024] Restore-mode dropdowns no longer render fallback view text.

## [1.6.0] - 09.03.2026
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
- [VS-1601] Restore confirmation now includes a pre-run preview for plain backups (files in backup, new files, overwrite count, potential conflicts, project-only files kept, total bytes).
- [VS-1601] Restore confirmation now supports selective top-level restore targets, and restore execution applies only selected targets for plain backups/archives.
- [VS-1602] Backups history now includes restore-point timeline compare selectors (`A` / `B`) with a compare summary dialog (range, elapsed, size delta, net-diff delta, latest-point diff stats).
- [VS-1602] Restore-point compare copy now explains selection order (`older` on the left, `newer` on the right) directly in the UI.
- [VS-1608] Projects preset card now shows a localized recommendation reason and an `Apply recommendation` action while keeping manual preset selection fully available.
- [VS-1608] Preset recommendation confidence gating now requires corroborating markers for generic stacks (`Node`, `Python`, `.NET`) to reduce noisy/low-confidence suggestions.
- [VS-1604] Projects details now includes an in-app preset rules editor (reload/save + preview include/exclude counts against the selected project path) for faster preset tuning without leaving the app.
- [VS-1604] Preset editor now supports clone/import/export flows (`Clone` to a new preset id, `Import` from file path, `Export` to `Documents/VaultSync/Exports/Presets`).
- [VS-1604] Projects preset editor copy/layout was refined for clarity (clearer action labels, usage guidance, concise preset file display with full-path tooltip).
- [VS-1609] Projects list now includes smart-group filtering (`All`, `Work`, `Games`, `Media`, `Critical`, `Archive`) using project tags and lightweight preset/health signals.
- [VS-1609] Projects smart-group selector now includes a bulk `Snapshot group` action for registered projects in the active group filter.
- [VS-1609] Projects smart-group controls now include `Back up group` and group-wide auto-backup toggles (`Disable auto`, `Enable auto`) to support pause/backup workflows by tag/group.
- [VS-1609] Projects tags now use pill-based editing (Enter/comma to commit, double-click pill to edit, remove button per tag) so partial typing does not immediately overwrite project tags.
- [VS-1609] Tag pills are now color-coded, reusable tag suggestions are clickable, and group actions now support apply/remove by tag for all projects in the active view.
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
- [BUG-16005] Elevated patch helper now validates patch request paths and constrains manifest file targets to the staging/install roots, blocking absolute and traversal paths during verify/copy.
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
- [BUG-16017] Dropdown popups were restyled app-wide for readability (clean hover/selected states, rounded popup panel, consistent item spacing) and Projects/Backups selected rows no longer use harsh filled highlight.
- [BUG-16018] Backups, Dashboard, and Settings pages now stretch to full `ScrollViewer` viewport width in windowed mode (while keeping max-width readability caps) instead of rendering as narrow centered columns.
- [BUG-16019] Backups page now removes hard per-panel list height caps and rebalances per-project/history columns to better use available windowed space without collapse.
- [BUG-16020] Projects details and Settings Advanced controls now reflow/wrap in windowed mode, and near-zero per-project storage deltas render as neutral `? ~0 B`.
- [BUG-16021] Backups summary activity card now sizes to content in windowed mode (top-aligned, auto-height row) so the chart no longer leaves a large empty block under the bars.
- [BUG-16022] Projects tag input Enter shortcut now runs through a guarded key handler, removing startup/null `CommitProjectTagInputCommand` binding trace noise in diagnostics logs.

## [1.5.1] - 28.02.2026
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
- [ISS-15013] Archive backup compression now uses an adaptive per-file strategy (`NoCompression` for already-compressed media/archives, `Optimal` for text/code, `Fastest` fallback) to improve throughput/ratio balance without changing backup format or restore compatibility.
- [ISS-15014] Config persistence now exposes async save with cancellation-aware retry backoff, and Settings save paths now use the async flow to reduce blocking waits.
- [ISS-15015] Main README was refreshed for the current 1.5.x feature set and outdated wording was cleaned up.
- [ISS-15016] README now includes dedicated app screenshot placeholders (`Dashboard`, `Projects`, `Backups`, `Settings`) under `docs/images/placeholders/`.
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
- [VS-1573] Projects action buttons now re-evaluate command state on selection changes so `Open folder` / `Remove from VaultSync` no longer remain incorrectly disabled; notification auto-dismiss cancellation is now handled as expected flow to prevent debug-noise cancellation exceptions.
- [BUG-15011] Metadata schema migration now checks column presence with `PRAGMA table_info(...)` before running `ALTER TABLE`, preventing duplicate-column SQLite exceptions (`origin_machine_name`) on already-migrated stores.
- [BUG-15017] Drive health probing now resolves external tool paths before process launch; when `smartctl` is missing on Windows, manual backup no longer emits first-chance `Win32Exception` and cleanly falls back to `Unknown`.
- [BUG-15018] SMB/UNC path detection no longer constructs `DriveInfo` from invalid UNC roots; manual backup startup checks now avoid `ArgumentException` (`Drive name must be a root directory...`) and classify UNC paths as network directly.
- [BUG-15019] Archive upload auto-tune timeout now uses a non-exception fallback path; timeout-driven probe cancellation no longer raises debug-noise `OperationCanceledException` while explicit backup cancellation remains intact.
- [BUG-15020] App config reads now retry with shared-read file access when `appsettings.json` is briefly locked by concurrent save/export work, reducing transient `IOException` lock failures.
- [BUG-15021] Backup delete robustness now avoids rethrowing marker-file attribute/delete failures on network shares, diagnostics dump collection skips cleanly when `dotnet-dump` is not installed, and project destination dropdowns ignore transient null refresh events so selections stay stable (`Auto`/`Inherit global` defaults preserved).
- [BUG-15022] Windows elevated patch installs now pass a serialized apply-request file to the helper instead of flattening `InstallDir`, `--restart`, and `--waitpid` into one fragile UAC command line, fixing Program Files patch-apply failures.

## [1.5.0] - 19.02.2026
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
- [VS-1536] Settings encryption panel now includes a rotation action that prompts for old/new passwords and can target all projects or one project by name.
- [VS-1536] Backup records now update encrypted descriptor metadata/size after successful key rotation.
- [VS-1537] Projects and Backups per-project cards now share one password-edit flow, using a single app-level handler to prevent cross-page mismatch.
- [ISS-15017] Projects and Backups encryption sections now show a dedicated status pill (`Encrypted`, `Not protected`, or missing-password warning).
- [VS-1538] Backups `Open folder` now detects encrypted backups, prompts for password (stored keys first), decrypts to a temp workspace, and opens decrypted content directly.
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
- [VS-1541] Projects and Backups history cards now surface compact snapshot diff summaries (`+`, `~`, `-`, signed net delta) with top-path previews and fallback states.
- [VS-1542] Diff-summary export writes now use collision-safe filenames under `Documents/VaultSync/Exports/SnapshotDiff` and report actionable export success/failure notifications.
- [ISS-15018] Settings quiet-hours inputs now use explicit side-by-side Start/End field groups for clearer overnight scheduling setup.
- [ISS-15019] Backup history chips now separate mode and encryption context (`Mode: ...`, `Encryption: ...`) for faster scanning.
- [ISS-15020] New backup summary/mode/encryption chip strings are now fully localization-key based (no hardcoded UI literals), and keys were added to all language packs.
- [ISS-15021] Backup freshness summary now shows localized state + relative age, with a localized threshold tooltip and state-based color coding.
- [ISS-15022] Backups page pills now use semantic/size variants (`info/success/warning`, `sm/md`) and long pill text now truncates safely with tooltips to avoid clipping in windowed layouts.
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
- [ISS-15037] Backup orchestration support methods (`destination prep`, `backup-all prep`, aggregate progress update, NAS temp-root migration helpers) were extracted from
  `AppViewModel` into a dedicated partial class file to reduce main view-model complexity without changing runtime behavior.
- [ISS-15038] Manual backup and backup-all handler implementations were extracted from `AppViewModel` into a dedicated partial file to isolate orchestration flow from
  unrelated UI/update logic while keeping behavior unchanged.
- [ISS-15039] Backup history workflows (`delete`, `restore`, encrypted open-folder/decrypt prompts, and related preparation helpers) were extracted from
  `AppViewModel` into a dedicated partial file to keep history operations isolated from startup/navigation/update logic.
- [ISS-15040] Runtime operations (`NAS monitor`, destination probing/status summaries, metadata sync import/export/refresh flow, and encryption-rotation settings workflow)
  were extracted from `AppViewModel` into a dedicated partial file to reduce central view-model coupling.
- [ISS-15041] Tray/menu workflows (backup/snapshot trigger surface, recent-backups tray actions, open-folder-from-tray, and encrypted-open cleanup helpers) were extracted
  from `AppViewModel` into a dedicated partial file for clearer operational boundaries.
- [ISS-15042] Update/startup-check workflow (`manual/auto update checks`, `retry/timer state`, `patch + installer download/launch`, and related UI status methods) was extracted
  from `AppViewModel` into a dedicated partial file to isolate release/update flow.
- [ISS-15043] Added repository-level Prettier configuration (`.prettierrc.json`) and ignore rules (`.prettierignore`) for consistent formatting of supported text assets.
- [ISS-15044] Added repository-level `.editorconfig` with C# and text formatting defaults so .NET/C# formatters apply consistent style in IDE and CLI.
- [ISS-15045] Backup support helpers (`post-hash`, verification, drive-health evaluation/notifications, restore advisories, and project-root fallback checks) were extracted
  from `AppViewModel` into a dedicated partial file for clearer backup-domain boundaries.
- [ISS-15046] Navigation/view-state members (`CurrentView`, header state, initial route guard, and shell navigation commands) were extracted from `AppViewModel`
  into a dedicated partial file to keep routing concerns isolated.
- [ISS-15047] Startup/bootstrap orchestration (constructor service wiring, initial config/runtime setup, and lazy Backups view-model composition) was extracted from
  `AppViewModel` into a dedicated partial file while preserving startup behavior.
- [ISS-15048] Shared helper methods (progress label computation, localization helpers, system-language resolution, download status updates, and backup skip notifications)
  were extracted from `AppViewModel` into a dedicated partial file to reduce core file coupling.
- [ISS-15049] `CONTRIBUTING.md` fully restructured with the default `VS-xxxx` planning model and contribution flow.
- [ISS-15050] Core test suite rewritten to match current metadata-sync and destination behavior contracts.
- [ISS-15051] Dashboard weekly analytics card was fully redesigned with a split insight rail + chart stage, updated lighter surface layering, and a capsule/lollipop activity graph style.
- [ISS-15052] Dashboard charts row was rebalanced so the storage card remains visible at large widths, and storage usage now uses a side-by-side donut + legend layout with a bottom capacity strip.
- [ISS-15053] Projects detail action row now wraps responsively so `Open folder` / `Snapshot now` / `Remove from VaultSync` actions do not clip or overlap in windowed layouts.
- [ISS-15054] Dashboard storage donut now uses explicit visibility toggling against `HasStorageSeries` to avoid stale empty-chart presentation when data arrives after initial layout.
- [ISS-15055] Added `1.4` <-> `1.5` compatibility matrix runbook (`CM-1501`..`CM-1508`) to drive `VS-1591` release-gate validation.
### Fixed
- [VS-1501] Legacy plain backup crypto metadata (`{}`) now parses through the typed descriptor compatibility path.
- [VS-1504] Secure-store failures no longer require plaintext secret persistence in config as fallback path.
- [VS-1502] Destination scans and backup-size probes now recognize encrypted archive artifacts alongside plain archives.
- [VS-1503] Encrypted restore now fails with an explicit invalid-password/corruption error and leaves no partial restored output on wrong-password attempts.
- [VS-1503] `NeedsRestore` flags are now cleared only after a successful restore completion.
- [VS-1505] Import/preview from older metadata stores (missing `origin_machine_name` and encryption columns) no longer fails and defaults backups to plain compatibility values.
- [BUG-15009] Dashboard storage donut now force-invalidates measure/visual on `StorageSeries` updates so the pie reliably appears after async data refreshes.
- [VS-1536] Rotation failures now preserve original encrypted backup artifacts via rollback-safe swap logic (no corruption on failure/interruption).
- [BUG-15010] Windows startup/debug runs no longer attempt to execute `/bin/ps` for parent-process info logging.
- [BUG-15011] Metadata sync tests now reflect current import rules for existing/missing backup paths.
- [BUG-15012] Windows installer now registers `.vse` file association so encrypted backup files open directly in VaultSync.
- [VS-1538] In-app `Open folder` no longer sends encrypted backups to the raw backup folder path that could trigger OS "Open with" on `.vse`.
- [BUG-15013] Build no longer picks up generated `artifacts/tmpobj` sources as compile inputs, fixing duplicate assembly attribute errors (`CS0579`) in local builds.
- [BUG-15014] Dashboard weekly summary labels now compute after day-series arrays are populated, so summary text matches the rendered weekly chart.
- [BUG-15015] Dashboard activity summary now includes imported-run counts for parity with the weekly graph breakdown.
- [BUG-15016] Dashboard storage donut hover labels now truncate long project names to prevent tooltip overlay overflow in windowed layouts.

## [1.4.1] - 06.02.2026
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

## [1.4.0] - 04.02.2026
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

## [1.3.5] - 28.01.2026
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

## [1.3.4] - 25.01.2026
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

## [1.3.3] - 21.01.2026
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

## [1.3.2] - 18.01.2026
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
- [BUG-13058] Backup retention and cleanup now respect destination paths and skip unrelated directories; interrupted backups are
  cleaned safely.
- [BUG-13059] Backup status cards no longer duplicate speed/ETA, support cancelling/deleting states, and avoid auto-scroll 
  jumps.
- [BUG-13060] Dashboard storage totals, per-project segments, and donut tooltips now match actual stored data.
- [BUG-13061] Backups page right panel/history styling cleaned up with clearer hierarchy.
- [BUG-13062] Toast notifications no longer render a duplicated band.
- [BUG-13063] macOS mounts now use a user-writable root, redact SMB passwords, validate SMB/NFS mounts, and report permission
  errors instead of crashing.
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
### Fixed
- [BUG-12023] Backup settings text wrapping improved for delta/incremental descriptions.
- [BUG-12024] Log console auto-scroll no longer triggers layout loop warnings; noisy layout trace messages are filtered.
- [BUG-12025] Log console no longer blocks main window interaction.
- [BUG-12026] rsync on Windows now hides the console window and rewrites paths for bundled cwRsync compatibility.
- [BUG-12027] Suppress update banners and notifications while handling crashes.
- [BUG-12028] ViewLocator now ignores non-view-model data types to avoid mis-instantiating log items.
### Removed
- [ISS-12045] Removed legacy beta notes document.

## [1.1.0] - 2025-12-17
### Added
- [VS-11001] Responsive layout scaffolds for Dashboard, Settings, Projects, and Backups so each view uses a centered, width-capped grid instead of `Viewbox` scaling, letting the UI naturally expand and contract on any resolution or DPI without misaligned cards.
- [VS-11002] Added translations for the remaining UI text (advanced settings beta channel text, health badges, buttons, etc.) across all supported locales so switching languages no longer exposes English placeholders.
### Changed
- [ISS-11003] The sidebar header now honors the shared theme brushes (`ShellBrandStartBrush`, `BorderSoft`, etc.) so the VaultSync title/slogan block matches both light and dark palettes instead of hard-coded gradients.
- [ISS-11004] Reflowed the storage KPI text so the metric and hint stack vertically and align to the left, keeping the description legible on large screens.
- [ISS-11005] Updated the light-theme brand colors (`VsShellBrand*` values) so the shell banner text uses the same primary/foreground tokens as the rest of the app.
- [ISS-11006] Beta update checks now consider stable releases and will upgrade prerelease installs to the matching stable version when available.
### Fixed
- [BUG-11001] Build failures caused by `VaultSync.UI.exe` remaining open are prevented by closing the running app before rebuilding; the shell banner and layout changes now compile cleanly once the process is released.
- [BUG-11002] The "VaultSync is still running" notification now fires only when minimizing to the tray, so quitting the app never triggers the toast unexpectedly.
- [BUG-11003] Prevented multiple instances of the app from launching and activated the existing window when a second launch is attempted.
- [BUG-11004] Dashboard storage pie chart now keeps a consistent size without overlapping the chart card next to it, and the legend list wraps cleanly.
- [BUG-11005] Settings destinations/credentials layout now aligns controls and action buttons correctly on narrow and wide windows.

## [1.0.0] - 2025-12-07
### Added
- [VS-10001] Advanced destination mode now shares the same Housekeeping block close to the fallback backup path, with localized descriptions, localized checklist, and a dedicated ?Test? flow that mounts/unmounts    using credential profiles.
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
- Patch helper now copies the full install directory into its temp folder before elevating, so UAC launches keep dependencies and the app restarts after applying the update.

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
- Bumped the Windows build (Net 8 + `net8.0-windows10.0.19041.0`) to 0.9.4 and republished the installer metadata so the update channel can pick up the hotfix.
- Added a reusable `VersionHelper` so release and patch manifest comparisons normalize strings like `v0.9.2.0`, letting 0.9.4 delta manifests validate cleanly against older installs.
### Fixed
- Created the `patches/v0.9.4/vaultsync-patch-windows.json` manifest/+zip (and published the delta assets) so 0.9.3 installations can download/apply just the changed `.exe`, `.dll`, and runtime config files.

## [0.9.0-beta.1] - 2025-11-16
### Added
- Patch-based updater downloads delta packages per platform from the `stable` GitHub release channel and stages them for the updater helper.
- Cross-platform localization pipeline with JSON resource dictionaries, `LocalizationService`, and Italian translations covering Settings, Dashboard, Backups, Projects, and notifications.
- Language selector in Settings -> Advanced plus docs showing how to add new languages.
### Changed
- Docs now describe the patch updater + localization workflow ahead of the public beta.

---

## [0.8.1] - 2025-11-09
### Fixed
- Resolved SQLite **FOREIGN KEY constraint** errors during heavy file churn in watcher mode.
- Added concurrency protection in `WatchCommand` using `SemaphoreSlim` to serialize snapshot/sync/verify cycles.
- Added graceful handling and clear message for FK19 errors.
- Ensured watcher cancellation safely short-circuits change handlers.
- Verified stability under stress: rename storms, 400+ file bursts, large binary files, read-only destinations.

### Changed
- Updater now surfaces installer downloads when patches are incompatible, enabling version skipping and beta-to-stable moves.
- Backup settings text wrapping improved for delta/incremental descriptions.
- Watch cycles are now atomic ? no overlap possible.
- Improved log readability during watch cycles (Spectre.Console markup formatting).
- Debounce logic refined for high-frequency file systems.

### Planned
- `--no-startup-snapshot` and `--idle-after-ms` flags for watch mode.
- Snapshot rollback on failure to ensure atomic DB writes.
- Configurable symlink policy (`skip` vs `dereference`).

---

## [0.8.0] - 2025-11-08
### Changed
- **Program.cs fully modularized**:
  - Separated each CLI command into its own file under `VaultSync.CLI.Commands`.
  - Added clean namespace structure and modern Spectre.Console.Cli setup.
  - Replaced obsolete `SetApplicationName()` with branch generics `AddBranch<CommandSettings>`.
- Verified compatibility with all existing commands.
- Logging now centralized through `VaultSync.CLI.Utils.Log`.

### Fixed
- Command discovery issues under .NET 8 due to non-generic branch registration.
- CLI startup logging consistency.

---

## [0.7.0] - 2025-11-03
### Added
- **Spectre.Console.Cli** integration replacing raw `System.CommandLine`.
- Implemented full project lifecycle commands:
  - `init`, `add-project`, `remove-project`, `list-projects`, `set-path`, `snapshot`, `sync`, `verify`, `restore`, `history`, `diff`, `prune`, `doctor`, `self-test`, `version`.
- Added `config` and `presets` branches with subcommands for configuration inspection.
- Integrated `Log` utility with color-coded console output.

### Fixed
- Early null reference handling when DB not initialized.
- Command help output rendering.

---

## [0.6.0] - 2025-10-31
### Added
- Core `SqliteRepository` with schema creation for `projects`, `snapshots`, and `files` tables.
- Services: `SnapshotService`, `SyncService`, `VerifyService`, `FilterService`, `ScannerService`, `HashService`, and `RobocopyRunner`/`RsyncRunner`.
- Implemented basic snapshot creation, hash storage, and diff comparison.
- Introduced `DoctorCommand` to validate environment (`rsync`, `robocopy`, DB path).

---

## [0.5.0] - 2025-10-27
### Added
- Initial CLI project structure: `VaultSync.CLI` under `src/`.
- Basic `Program.cs` entrypoint with hardcoded commands for testing.
- Local SQLite persistence prototype.
- Basic logging utilities.

---

## [0.4.0] - 2025-10-19
### Added
- Migration of OverSteer network management and RaceManager flow to separate baseline (predecessor to VaultSync CLI project).
- Established shared core for later reuse (config loader, command dispatcher).

---

## [0.3.0] - 2025-10-17
### Added
- Baseline logic for AI racing system (from related OverSteer project, used as testing ground for VaultSync command orchestration).

---

## [0.1.0] - 2025-10-15
### Added
- Project initialized, foundational scaffolding set up for CLI + SQLite architecture.
