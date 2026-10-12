# Development notes before changelog standardization

Historical development record, not shipped release history or current qualification.
The inherited 1.9.0 notes do not represent this release’s own delivered features.

# Changelog

## [1.9.8] - Unreleased

Planning scaffold for 1.9.8; current published stable remains 1.8.9.
This branch is unscheduled and requires qualified predecessor integration.
See [the family plan](docs/RELEASE_FAMILY_1.9.md).

## [1.9.0] - Unreleased
### Planning
- [VS-1980] Align the Recovery Horizon family with the complete supplied roadmap.
- [VS-1910] Draft typed route and adaptive UI architecture; approval pending.
### Fixed in development
- [BUG-19001] Require explicit authorization for unattended project removal.
- Preserve finite read-only watcher previews, cancellation/draining and failed-cycle reporting.
- Accept the exact FG Labs updater release URLs after the organization transfer.
- Handle root-owned AppImage extraction outputs during packaging.

1.9.0 has not shipped. Disk capture, independent boot and disk restore remain
unimplemented/unqualified. The future CLI implementation is preserved in 1.9.5;
its previous development notes are retained in the archived branch.

