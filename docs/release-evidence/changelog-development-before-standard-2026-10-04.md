# Development notes before changelog standardization

Historical development record, not shipped release history or current qualification.
The inherited 1.9.0 notes do not represent this release’s own delivered features.

# Changelog

## [1.9.5] - Unreleased
### Added in development — 2026-10-03
- [VS-1973] Preserve grouped resource routes, source discovery, live mirroring and bounded project or snapshot inspection while assigning automation to its owning release.
- [VS-1974] Preserve versioned JSON inspection, snapshots, mirror, verify, doctor and restore output with private destination summaries and valid empty legacy results.
- [VS-1975] Preserve recorded folder backup creation and inspection, individual or batch verification, selective restore and bounded snapshot pruning with preview/result counts.
- [VS-1974] Preserve task discovery and generated shell completion for grouped commands, with documentation topics reflecting the actual resource CLI command surface.
- [VS-1972] Preserve packaged handbook, task guides and generated reference, correcting install versions and contract links to this release’s own package identity.
### Fixed in development
- [BUG-19002] Drain active watcher work on cancellation before shutdown, preserving cancellation propagation and avoiding abandoned snapshot or transfer operations during exit.
- [BUG-19003] Reject snapshot IDs belonging to another project before constructing a history diff, keeping inspection scoped to the selected registered project.
- [BUG-19004] Recheck staged restore bytes before target replacement, retaining corruption rejection before any destructive change to the user’s existing destination files.
- [BUG-19005] Keep live snapshot prune JSON parseable after deletion, preserving versioned preview and result counts for unattended consumers and automation scripts.
- [BUG-19006] Observe watcher startup changes and stop on failed snapshot, mirror, verification or filesystem cycles, reporting failures explicitly instead of silent continuation.
- [BUG-19007] Keep watcher dry runs finite and read-only, producing versioned plans without snapshots, transfers, verification, watchers or configuration-default writes during preview.
### Changed in development
- [VS-1974] Remove raw argument logging, keep service logs private and inspection lookups read-only, and reject live JSON watch until streaming is qualified.
- [VS-1975] Keep Windows mirror previews from creating targets and label source and destination paths clearly before any authorized live transfer begins.
- [VS-1980] Include qualified shared rendering, Spectre API and unused-font dependency maintenance while preserving all resource command signatures and recorded-backup safety regression coverage.

Current stable remains 1.8.9. Product release and predecessor qualification are pending.
See [the complete daily ledger](docs/release-evidence/1.9-work-2026-10-03.md).

## [1.9.0] - Unreleased
### Planning and tracking — 2026-10-03
- [VS-1980] Import the complete Recovery Horizon roadmap, preserve existing work IDs, and validate release ownership against its immutable BSC crosswalk.
- [VS-1980] Prepare nine release contracts, version identities and unscheduled future metadata, while preserving the published stable version and historical milestones.
- [VS-1980] Separate discovery into 1.9.1 and resource automation into 1.9.5, preserving the full earlier implementation in a published archive branch.
- [VS-1980] Link the GitHub Project, reconcile issue fields and wiki, and consolidate preparation and corrections into one draft PR per release.
- [VS-1980] Make project date audits work with installed GitHub CLI pagination and keep candidate publication dates mandatory during planned-release validation.
- [VS-1910] Draft typed recovery routes and adaptive UI architecture, retaining explicit approval and complete disk capture, boot and restore qualification gates.
### Fixed in development
- [BUG-19001] Require explicit authorization before unattended project removal touches configuration or registration, preserving interactive confirmation and existing source and backup files.
- [VS-1980] Preserve finite read-only legacy watcher previews, database selection, cancellation draining and failed-cycle reporting independently of future resource CLI command ownership.
- [BUG-19009] Run required platform and analysis workflows for documentation changes because bundled CLI guides affect artifacts and protected PR check contexts.
- [BUG-19010] Align all managed and native rendering packages together, correcting incomplete Dependabot updates before their compatibility qualification and eventual release integration.
- [BUG-19011] Upgrade Spectre command parsing atomically with public async overrides, preserving existing cancellation signatures and flat CLI behavior across the API change.
- [VS-1980] Accept exact FG Labs updater release URLs after the organization transfer and handle root-owned AppImage extraction outputs during application packaging.
### Maintenance
- [VS-1980] Remove unused Avalonia Inter font packaging after confirming no registration or resource consumers, preserving existing bundled fonts and system fallback behavior.
- [VS-1980] Integrate the verified Dependabot test SDK patch and preserve coordinated rendering, CLI and CodeQL update commits for reviewed release maintenance.
- [VS-1980] Correct the authorized ruleset update prohibition while retaining required review, strict platform checks, merge history protection and deletion restrictions throughout integration.

1.9.0 has not shipped. Disk capture, independent boot and disk restore remain
unimplemented/unqualified. Discovery fixes belong to 1.9.1; resource CLI and its
regressions belong to 1.9.5. Every change, PR, package audit and remaining gate
from today is recorded in [the daily ledger](docs/release-evidence/1.9-work-2026-10-03.md).

