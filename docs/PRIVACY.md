# VaultSync privacy overview

VaultSync is designed to keep backup data, diagnostics, and operational information under the user's control.

## Crash reports

VaultSync does not send crash reports automatically. Optional crash-report assistance creates a minimal, strictly allowlisted report locally and displays its complete contents before offering to prepare an email draft.

The user must attach the report and press **Send** in their own email application. VaultSync has no SMTP credentials, crash-reporting SDK, upload API, or hosted crash database.

The feature can be disabled completely in **Settings > Advanced > Crash report assistance**. When disabled, VaultSync does not create crash reports or offer email preparation.

See [Crash reporting and user control](CRASH_REPORTING.md) for exact fields, exclusions, retention, code locations, limitations, and verification.

## Email providers

If a user chooses to email a report, the message is processed by the email providers selected by the user and FGLabs. VaultSync does not add another intermediary and cannot control provider retention.

## No hidden consent

Opening a preview, copying or saving a report, or preparing a draft does not authorize future reports. Each report requires a separate action in the user's email application.
