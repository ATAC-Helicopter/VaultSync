# What's New

## [1.7.4-Beta.1]

Current `1.7.4-Beta.1` highlights focus on the .NET 10 migration, safer destination cleanup, quieter diagnostics, and release-readiness polish across the 1.7 train.

### Platform and release updates
- Core, CLI, UI, and tests now target .NET 10.
- Release workflows and documentation now use the .NET 10 SDK and publish paths.
- The Windows installer metadata now points at the .NET 10 Windows publish output.
- CLI packaging now includes the repository README from the correct path.
- Release examples, issue templates, and release notes now target the prepared `1.7.4` release.

### Backup and destination reliability
- Backups now prune stale database entries when recorded backup folders are missing from reachable prepared destinations.
- Offline or unresolved destinations are left untouched, so disconnected drives are not treated as deleted backups.
- Passive Backups refreshes no longer wake destinations just to update reachability.
- Backup probes now share in-flight archive buffer tuning work per destination.
- Backup path-containment checks now share one hardened implementation across delete, restore, tray, and open-folder flows.
- Imported destination history rebuilt from legacy backup folders now keeps real backup sizes instead of showing `0 B`, and existing imported `0 B` rows are repaired when their folders are still present.

### Diagnostics and app polish
- The log console copy button now uses the active console window clipboard.
- Normal diagnostics no longer show caught SQLite/WinRT first-chance probes unless first-chance diagnostics are explicitly enabled.
- The in-app What's New dialog now reads only the current release slice and presents it as a cleaner release digest.
- Repeated UI byte-size formatting and detached async-command wrappers now use shared helpers.
- Changing language in Settings now preserves the active theme and keeps the page near the same scroll position through the relayout.

### Presets and generated output
- Development and creative presets now exclude nested generated outputs such as build, cache, import, and render folders.
- Filter coverage now includes nested `**/bin/**`, `**/Intermediate/**`, `.import`, and render-cache style folders.
- Source-code presets now keep useful repository metadata such as `.github` workflows and Git config files while still excluding `.git` internals and generated build outputs.

## [1.7.3]

Current `1.7.3` highlights focus on Linux reliability, release asset coverage, safer startup/config recovery, and the final backup and metadata fixes from the 1.7 stabilization cycle.

### Linux and release assets
- Release asset builds now produce Linux `tar.gz` and `.deb` downloads for `x64` and `arm64`.
- Release asset builds also produce a desktop-friendly `linux-x64` AppImage for direct Linux installs.
- Linux `tar.gz` downloads include a rootless `install.sh` that adds VaultSync to the user app menu and creates a `vaultsync` terminal command across distro families.
- Linux update discovery now prefers architecture-specific installer and patch names before falling back to generic Linux assets.

### Linux reliability fixes
- Tray panel screen detection, reopen behavior, and Linux/Wayland positioning are more resilient on Hyprland-class environments.
- Tooltip flicker and focus issues on Linux/Wayland were fixed by enabling overlay popups.
- A fatal Linux x64 `AccessViolationException` during backup was fixed.
- Linux password saving no longer stores `null`, and password operation timeouts are longer.

### Settings and startup recovery
- Settings now refreshes persisted values correctly after config reloads, so fields such as Projects root no longer appear blank when the saved config is intact.
- Startup can repair blank project root paths from the configured Projects root when the matching folder still exists on disk.
- Background settings saves preserve existing project roots, backup roots, and advanced destinations when the UI is still loading transient blank values.
- Command state refreshes now marshal back to Avalonia's UI thread, preventing startup/background checks from crashing command validation.

### Backup and metadata reliability
- Backup All and auto-backup no-change runs now create real first backup artifacts instead of empty destination folders.
- Individual project backup buttons resolve destinations from the latest saved config and refresh destination choices after backup destination settings change.
- Metadata imports compare restore-needed state against the pre-import local backup baseline so newly imported backups no longer suppress their own restore prompt.
- Project auto-backup settings export through metadata before the first backup, so toggles travel across machines earlier.

### Usability
- The in-app log console now has an explicit Auto-scroll toggle.
- The in-app log console can copy the selected log line with a button or the usual platform copy shortcut.

## [1.7.0]

Current `1.7` release-train highlights, including repair tooling, safer updates, transfer resilience, dashboard clarity, and in-context appearance customization.

### Integrity and repair
- Added startup backup-index consistency checks so VaultSync can detect metadata drift early without blocking launch.
- Added deterministic backup-index repair planning plus a Doctor workflow for dry-run and exact fix-now actions.
- Added retention chain preflight and safer retention delete planning so cleanup does not remove the last metadata-valid restore point.
- Added cross-machine metadata conflict capture and resolution for project destination, restore mode, verification policy, and tags.

### Transfer resilience and storage
- Added destination quotas and cleanup suggestions in Backups.
- Added checkpointed archive retry so interrupted archive uploads can resume from validated checkpoints instead of always restarting.
- Added retention simulation preview in Settings.
- Added restore-readiness scorecards in Dashboard and Backups.

### Updates and serviceability
- Added updater release-target diagnostics, patch preflight diagnostics, and richer support-bundle telemetry.
- Added a release-readiness gate script for pre-publish and post-publish verification.
- Added strict multi-base patch manifest support so one patch manifest can safely allow multiple exact tested base versions.

### UI and workflow improvements
- Redesigned the Dashboard information layout for clearer KPI, activity, storage, and readiness scanning.
- Added app-wide tag color styling and in-Projects tag color editing.
- Added custom theme presets, quick palettes, and slot-based theme editing in Settings > Appearance.
- Improved Projects empty-state and no-selection behavior instead of rendering broken blank detail panes.

### Fixes
- Fixed Projects root persistence across restart/config race conditions.
- Fixed Doctor workflow command-state updates crossing onto invalid threads.
- Fixed noisy backup/restore/dashboard trace chatter so normal runs stay clean unless verbose logging is enabled.
- Fixed theme saves so they no longer overwrite tag colors managed from Projects.
- Restored corrupted bundled font assets used by the UI.

## [1.6.0]

### Restore and backup workflow
- Added per-project restore mode (`Direct`, `Sandbox`) with a restore-time override.
- Added sandbox completion actions (`Keep`, `Open sandbox`, `Apply to project`) and apply preflight summary/confirmation.
- Added plain-backup restore preview and selective top-level restore targets.
- Added restore-point timeline compare (`A`/`B`) with range/size/net-diff summary.

### Projects and presets
- Added project tags persistence, pill editing, reusable tag suggestions, and smart groups (`Work`, `Games`, `Media`, `Critical`, `Archive`).
- Added group actions in Projects (snapshot, backup, auto-backup toggles, apply/remove by tag).
- Added preset recommendation engine for common stacks and improved confidence gating.
- Added in-app preset rules editor with reload/test/save plus clone/import/export actions.

### Reliability, diagnostics, and storage insights
- Added support bundle export (`Settings > Advanced`) with redacted config, diagnostics, and telemetry summaries.
- Added per-destination retry policy settings and destination-scoped retry execution with backoff/telemetry.
- Added per-project verification policy (`always`, `scheduled`, `manual`).
- Added backup storage deltas and top-storage-consumer insights in Backups/Dashboard.

### Fixes and hardening
- Fixed major windowed-mode layout/overflow issues across Backups, Projects, Dashboard, and Settings.
- Fixed backup path-containment validation across delete/retention/restore/open-folder flows.
- Hardened elevated patch validation (request path checks + payload/archive integrity checks).
- Fixed project tag input command startup binding noise in diagnostics logs.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
