# VaultSync privacy overview

VaultSync is designed to keep backup data, diagnostics, and operational information under the user's control.

## Backup and recovery data

Project configuration, history, recovery-drill evidence, logs, and generated reports are stored locally or on destinations the user configures. Recovery drills inspect reachable backup content locally and do not upload file contents, hashes, paths, or results to FGLabs.

Portable metadata under a configured destination's `.vaultsync/meta/` contains project and backup history needed for cross-machine sync. It does not contain backup payload contents or plaintext credentials. See [the documentation contract](../DOCUMENTATION.md#8-metadata-sync-contract) for the maintained field-level summary.

## Diagnostics

Existing diagnostic logs and legacy hashed operational event files remain on the device.
VaultSync 1.9.0 adds a separate optional technical diagnostic collector: local
records are bounded, and automatic delivery is off until the user explicitly
opts in. It never uploads those existing logs or event files. See
[the exact fields, consent, retention and reception contract](PRODUCT_DIAGNOSTICS.md).
Support bundles are generated from an allowlist, pseudonymize known paths and
display identities, scrub configured and structured secrets, bound each input
and the complete bundle, and provide a pre-export review where optional
diagnostics or telemetry can be removed. These exports remain local files;
VaultSync shares them only if the user deliberately attaches or uploads them
through a separate application. Because no automatic redactor can understand
every free-form string, users should still inspect the reviewed ZIP before
sharing it.

## Crash reports

Text crash reports are never automatically uploaded. A separately consented
technical event can describe the crash using fixed codes and numeric product
call sites, without its text report or exception message. Optional crash-report assistance creates a minimal, strictly allowlisted report locally and displays its complete contents in a read-only view before offering to prepare an email draft. Its random per-report ID, OS family, crash category, and exception-type reason cannot be changed in VaultSync; users can add optional context in their email application.

VaultSync asks the local mail application to attach the reviewed report to a visible draft. The user must still inspect the attachment and press **Send**. This text-report workflow uses no SMTP credential or upload API. The separate
optional technical collector uses the FG Labs HTTPS receiver described above.

The feature can be disabled completely in **Settings > Advanced > Crash report assistance**. When disabled, VaultSync does not create crash reports or offer email preparation.

See [Crash reporting and user control](CRASH_REPORTING.md) for exact fields, exclusions, retention, code locations, limitations, and verification.

## Email providers

If a user chooses to email a report, the message is processed by the email providers selected by the user and FGLabs. Manual email uses those providers. Optional technical diagnostics additionally
uses the disclosed hosting/storage/mail infrastructure; provider and mailbox
retention differs from the 30-day diagnostic-store expiry.

## No hidden consent

Opening a preview, copying or saving a report, or preparing a draft does not authorize future reports. Each text report requires a separate action in the user's email application.
Automatic structured reports require their separate, device-local opt-in;
turning it off cancels pending client sends. Already received reports/notices
cannot be recalled by that switch. Request IPs are not application diagnostic
fields, but network/hosting providers necessarily process connection metadata.
