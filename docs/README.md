# VaultSync Documentation Map

Use this page as the primary index for all project documentation.

## Start Here
- Product overview: [README](../README.md)
- Documentation hub: [DOCUMENTATION](../DOCUMENTATION.md)
- Wiki home: [VaultSync Wiki](wiki/Home.md)
- Help page (in-app target): [Help](HELP.md)

## Planning and Release
- Roadmap: [ROADMAP](../ROADMAP.md)
- Changelog: [CHANGELOG](../CHANGELOG.md)
- Current release highlights: [What's New](WHATS_NEW.md)
- Release process: [Releasing](RELEASING.md)
- Updater and patch assets: [Updater](UPDATER.md)
- Microsoft Store planning and packaging notes: [Microsoft Store](MICROSOFT_STORE.md)
- Microsoft Store submission checklist: [Store submission checklist](MICROSOFT_STORE_SUBMISSION_CHECKLIST.md)
- Download stats snapshots: [Download stats](DOWNLOAD_STATS.md)

## Contribution and Governance
- Contributing: [CONTRIBUTING](../CONTRIBUTING.md)
- Security policy: [SECURITY](../SECURITY.md)
- Privacy overview: [Privacy](PRIVACY.md)
- Crash reporting and user control: [Crash reporting](CRASH_REPORTING.md)
- Disaster recovery drills and 3-2-1 advisor: [Disaster recovery](DISASTER_RECOVERY.md)
- Native recoverability engine and ProofRestore provenance: [Recoverability engine](RECOVERABILITY_ENGINE.md)
- Code of Conduct: [CODE_OF_CONDUCT](../CODE_OF_CONDUCT.md)
- SonarQube Cloud setup: [SonarQube](SONARQUBE.md)

## User Guides (Wiki)
- [Quick start](wiki/Quick-Start.md)
- [Installation](wiki/Installation.md)
- [Backups overview](wiki/Backups.md)
- [Backup pipeline](wiki/Backup-Pipeline.md)
- [Backup encryption](wiki/Encryption.md)
- [Destinations](wiki/Destinations.md)
- [Metadata sync](wiki/Metadata-Sync.md)
- [Network shares](wiki/Network-Shares.md)
- [Snapshots](wiki/Snapshots.md)
- [Recovery](wiki/Recovery.md)
- [Configuration](wiki/Configuration.md)
- [Tray](wiki/Tray.md)
- [Updates](wiki/Updates.md)
- [Troubleshooting](wiki/Troubleshooting.md)
- [FAQ](wiki/FAQ.md)
- [Reporting bugs](wiki/Reporting-Bugs.md)

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
