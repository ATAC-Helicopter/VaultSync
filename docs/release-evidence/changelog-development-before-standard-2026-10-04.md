# Development notes before changelog standardization

Historical development record, not shipped release history or current qualification.
The inherited 1.9.0 notes do not represent this release’s own delivered features.

# Changelog

## [1.9.1] - Unreleased
### Added in development — 2026-10-03
- [VS-1977] Add branded CLI welcome and task discovery with generated shell completion, advertising only the supported flat commands owned by this release.
- [VS-1978] Bundle the handbook, task guides and command reference with package-aware documentation links, preserving generated reference consistency against actual executable help.
### Fixed in development
- [BUG-19008] Restore both console globals before disposing captured discovery output, preventing Windows test runs from retaining a closed Spectre output writer.
### Maintenance
- [VS-1977] Adapt earlier discovery work to flat CLI routes and qualify actual locally installed version, help, documentation URLs and Bash completion ownership.
- [VS-1980] Include qualified shared rendering, Spectre API and unused-font dependency maintenance from the foundation without importing future grouped resource CLI features.

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

