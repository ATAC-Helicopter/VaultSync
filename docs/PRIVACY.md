# VaultSync privacy overview

VaultSync is designed to keep backup data, diagnostics, and operational information under the user's control.

## Backup and recovery data

Project configuration, history, recovery-drill evidence, logs, and generated reports are stored locally or on destinations the user configures. Recovery drills inspect reachable backup content locally and do not upload file contents, hashes, paths, or results to FGLabs.

Portable metadata under a configured destination's `.vaultsync/meta/` contains project and backup history needed for cross-machine sync. It does not contain backup payload contents or plaintext credentials. See [the documentation contract](../DOCUMENTATION.md#8-metadata-sync-contract) for the maintained field-level summary.

## Diagnostics

VaultSync has no background analytics or crash-upload service. Diagnostic logs
and anonymized operational event files, when created, remain on the device.
Support bundles are generated from an allowlist, pseudonymize known paths and
display identities, scrub configured and structured secrets, bound each input
and the complete bundle, and provide a pre-export review where optional
diagnostics or telemetry can be removed. These exports remain local files;
VaultSync shares them only if the user deliberately attaches or uploads them
through a separate application. Because no automatic redactor can understand
every free-form string, users should still inspect the reviewed ZIP before
sharing it.

## Crash reports

VaultSync does not send crash reports automatically. Optional crash-report assistance creates a minimal, strictly allowlisted report locally and displays its complete contents in a read-only view before offering to prepare an email draft. Its random per-report ID, OS family, crash category, and exception-type reason cannot be changed in VaultSync; users can add optional context in their email application.

VaultSync asks the local mail application to attach the reviewed report to a visible draft. The user must still inspect the attachment and press **Send**. VaultSync has no SMTP credentials, crash-reporting SDK, upload API, or hosted crash database.

The feature can be disabled completely in **Settings > Advanced > Crash report assistance**. When disabled, VaultSync does not create crash reports or offer email preparation.

See [Crash reporting and user control](CRASH_REPORTING.md) for exact fields, exclusions, retention, code locations, limitations, and verification.

## Email providers

If a user chooses to email a report, the message is processed by the email providers selected by the user and FGLabs. VaultSync does not add another intermediary and cannot control provider retention.

## No hidden consent

Opening a preview, copying or saving a report, or preparing a draft does not authorize future reports. Each report requires a separate action in the user's email application.
