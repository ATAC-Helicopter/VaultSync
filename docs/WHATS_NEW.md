# What's New

## [1.5.1]

### Stability and startup
- Fixed backup project cards so destination and encryption selectors keep the saved value on startup.
- Fixed startup fallback behavior so empty selector states now default to `Auto (active destinations)` and `Inherit global`.
- Improved chart refresh timing and reduced first-load UI state issues.

### Backup flow fixes
- Fixed stale disabled states on project action buttons such as `Open folder` and `Remove from VaultSync`.
- Hardened backup delete and diagnostics paths to reduce noisy exceptions on network shares and missing external tools.
- Improved UNC/network path handling and config-read resilience during backup startup and metadata work.

### Transfer policy and settings polish
- Refined bandwidth and quiet-hours settings copy and localization coverage.
- Improved lock-timeout and `Lock now` localization bindings.
- Removed the obsolete roadmap-sync GitHub workflow that was failing against the old project location.

### Presets and localization
- Added consumer-friendly presets for Photos, Documents, Steam Mods, and Creative Suites.
- Added preset description/example guidance in the Projects page.
- Refreshed localization reports and normalized non-English locale key ordering.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
