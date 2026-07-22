# VaultSync Documentation Map

Use this page as the primary index for all project documentation.

## Start Here
- Product overview: `README.md`
- Documentation hub: `DOCUMENTATION.md`
- Wiki home: `docs/wiki/Home.md`
- Help page (in-app target): `docs/HELP.md`

## Planning and Release
- Roadmap: `ROADMAP.md`
- Changelog: `CHANGELOG.md`
- Current release highlights: `docs/WHATS_NEW.md`
- Release process: `docs/RELEASING.md`
- Updater and patch assets: `docs/UPDATER.md`
- Microsoft Store planning and packaging notes: `docs/MICROSOFT_STORE.md`
- Microsoft Store submission checklist: `docs/MICROSOFT_STORE_SUBMISSION_CHECKLIST.md`
- Download stats snapshots: `docs/DOWNLOAD_STATS.md`

## Contribution and Governance
- Contributing: `CONTRIBUTING.md`
- Security policy: `SECURITY.md`
- Privacy overview: `docs/PRIVACY.md`
- Crash reporting and user control: `docs/CRASH_REPORTING.md`
- Disaster recovery drills and 3-2-1 advisor: `docs/DISASTER_RECOVERY.md`
- Code of Conduct: `CODE_OF_CONDUCT.md`
- SonarQube Cloud setup: `docs/SONARQUBE.md`

## User Guides (Wiki)
- Quick start: `docs/wiki/Quick-Start.md`
- Installation: `docs/wiki/Installation.md`
- Backups overview: `docs/wiki/Backups.md`
- Backup pipeline: `docs/wiki/Backup-Pipeline.md`
- Backup encryption: `docs/wiki/Encryption.md`
- Destinations: `docs/wiki/Destinations.md`
- Metadata sync: `docs/wiki/Metadata-Sync.md`
- Network shares: `docs/wiki/Network-Shares.md`
- Snapshots: `docs/wiki/Snapshots.md`
- Configuration: `docs/wiki/Configuration.md`
- Tray: `docs/wiki/Tray.md`
- Updates: `docs/wiki/Updates.md`
- Troubleshooting: `docs/wiki/Troubleshooting.md`
- FAQ: `docs/wiki/FAQ.md`
- Reporting bugs: `docs/wiki/Reporting-Bugs.md`

## Metadata Sync Scope
- Carries portable metadata such as project settings, snapshot summaries, backup history fields, tombstones, and non-secret encryption descriptors.
- Does not carry backup payload contents, plaintext secrets, or the full local app configuration.
- Contract details and regression coverage live in `DOCUMENTATION.md` and `tests/VaultSync.Core.Tests/MetadataSyncTests.cs`.

## Localization Operations
- UI strings live in `Localization/strings.en.json`.
- Missing-key checks and translator context should be regenerated before localization sweeps.
- When UI text changes, update:
  - `Localization/strings.en.json`
  - user-facing docs that mention the label or feature
  - `CHANGELOG.md` and `docs/WHATS_NEW.md` when behavior changed

## Maintenance Notes
- When behavior changes, update both:
  - feature and usage docs (`docs/wiki/*`, `docs/HELP.md`)
  - release notes (`CHANGELOG.md`, `docs/WHATS_NEW.md`)
- Keep IDs and naming aligned with `CONTRIBUTING.md`.
