# Optional technical diagnostics

Owner: VS-1998 / #805; 1.9.0 preparation PR #736. Receiver: companion
[fglabs-site issue #49](https://github.com/ATAC-Helicopter/fglabs-site/issues/49).
This is a separate collector, not an uploader for existing logs, support bundles,
legacy hashed telemetry, text crash reports or memory dumps.

## User choice and behavior

At first desktop startup, a translated disclosure offers automatic sending or
keeping reports on this device. Closing the window does not grant consent.
Both choices leave the application usable. Settings > Advanced provides the
same separately persisted switch, a privacy-notice link and complete local
technical-record review with deletion of local/queued reports.

Sending is off by default. The current version-1 decision is the single byte
`0` or `1` in the private `product-diagnostics/consent-v1` file. Any missing,
malformed, inaccessible or future decision fails closed. It is deliberately
outside exported application configuration, its backup and destination metadata:
importing settings or another machine's projects cannot authorize transmission.
A material policy/schema change requires another reviewed consent version.
Enabling never backfills previously collected local history. Only future reports
enter the upload queue. Turning off immediately cancels active client requests
and deletes pending records; already received/in-flight server processing or
operator notices cannot be recalled by a local switch. A failed decision write
is shown to the user and disables sending in the current process; filesystem
permissions must be repaired to persist a choice across future launches.

## Exact payload schema 1

Every field is required; `sourceCommit` may be null. Unknown/duplicate members,
unknown enum codes, invalid UTF-8, excessive nesting, oversize messages and
non-random UUID versions are refused. No free-form user string is accepted.

| Field | Meaning / allowed values |
| --- | --- |
| schemaVersion, consentVersion | 1, 1 |
| reportId | Fresh UUID v4 for this report; never an installation/session ID |
| appVersion, runtimeVersion | Numeric versions, not arbitrary build descriptions |
| sourceCommit | Lowercase 40/64-digit commit, or null |
| releaseChannel | development, candidate, beta, stable |
| os, osMajor | windows/macos/linux/other and coarse major version |
| architecture | x64/arm64/x86/arm/other |
| operation | application, userInterface, backgroundTask, snapshot, backup, verification, restore, update, diskInventory |
| outcome | started, completed, failed, cancelled, crash, hang |
| phase | none, initialize, enumerate, read, compress, write, verify, commit, cleanup, download, apply |
| mode | none, folder, zip, disk |
| failure | Fixed classifications in ProductDiagnosticReport; no exception messages or custom type names |
| durationBucketMs | 0, 100, 1000, 5000, 15000, 60000, 300000, 1800000, 3600000, 86400000 |
| quantityBucket | 0, 1, 10, 100, 1000, 10000, 100000, 1000000, 2147483647 |
| codeSites | At most eight {module: core/ui/cli, token: MethodDef integer} pairs from product assemblies |

Durations/counts are bucket upper bounds, not exact measurements or file sizes.
Absence of a measurement uses zero. Code-site tokens are compiler metadata, not
addresses, process IDs, filenames or source line paths. Given the exact matching
assembly, a developer resolves a token with `assembly.ManifestModule.ResolveMethod(token)`.
Retain the correct platform/build binaries; a claimed commit in an untrusted or
modified local build is not authenticity proof. No exception message, exception
Data, configuration, machine/user name, path, content/hash of a file, credential,
network address, URL, hardware serial or persistent user/device/session ID is read
into the report. No raw log or memory dump is attached. OS/runtime/build metadata
and operation codes can still provide context; do not call the system universally
anonymous or claim that transport infrastructure processes no personal data.

## Local retention and transport

The desktop collector stores up to 100 local and 100 pending JSON records, at
most 8 KiB each, with seven-day expiry checked at startup, collection, review
and periodic flush. Directories/files are restricted where supported; managed
symlinks are not followed. Corrupt, oversized, tampered or unsupported queued
records are not uploaded. The queue is revalidated and reserialized before sending.

The fixed endpoint is `https://fglabs.dev/api/vaultsync/diagnostics`. There is no
embedded secret, configurable arbitrary recipient, cookie jar or HTTP redirect
following. A batch sends at most ten records. Requests including bounded receipt
reads have a ten-second deadline. A 202 response must acknowledge the exact UUID;
partial, mismatched and oversized responses do not claim delivery. Failed sends
remain queued with exponential backoff, bounded Retry-After and no backup failure.
The desktop timer first runs after 15 seconds and then once a minute; concurrent
flushes do not double-send. Fatal crashes can leave records for a subsequent launch.
CLI commands retain their existing local/manual diagnostics; this increment does
not add CLI consent commands or hidden CLI uploads.

Current hooks cover desktop startup, UI/application/background exceptions, hang
signals, snapshot completion/hashing, backup completion/failure, verification
results including integrity mismatches, restore completion/failure and patch
failure. Only executed hooks produce events; this is not universal exception or
performance tracing and cannot prevent every bug. Diagnostics are best effort,
independent of successful protection operations. Uninitialized collectors do no work.

## Receiver, privacy and operator delivery

The companion receiver validates the same schema and caps request bodies. Redis
atomically deduplicates report UUIDs, stores expiring records, increments daily
report counters and reserves error-group notifications. It currently caps new
records per UTC day per deployment scope at 1,000 failures/crashes/hangs and
4,000 ordinary operation reports, keeping an independent allowance for errors. Counts are reports, not people,
installations, backup totals or authenticated recovery evidence. The endpoint is
public and clients can fabricate events; configure the hosting firewall against
abuse before wider testing. No automatic security action trusts a report.

Records and error-group state expire after 30 days. The protected
`/internal/vaultsync-diagnostics` page offers retained-day selection, 100-record pages, counters and
notification states, with private page export and removal of a report. Removal
also strips its retained notification payload and cancels its queued alert;
already dispatched mail cannot be recalled. Non-identifying aggregate counts
remain. The administrator must authenticate; deletion also requires same-origin
credentials. Preview/development scopes never use the production namespace by default.

Only error/crash/hang groups notify the configured operator, once per retained
group, with a summary and private link rather than raw logs or attachments.
A daily cron sends the previous UTC day's aggregate summary. Durable outbox
state and stable provider idempotency keys protect retries/lost acknowledgements.
Uncertain delivery beyond the provider's 24-hour idempotency window is marked
needs-review instead of blindly resending. Accepted means provider acceptance,
not verified inbox delivery. Email notices have their provider/mailbox retention,
which is separate from the diagnostic-store expiry.

The application stores no request IP in diagnostic records, but an HTTPS request
necessarily exposes a network address to hosting/network providers. Hosting
access/security logs, subprocessors, regions, agreements and their retention
must be disclosed and reviewed in the website privacy notice. FG Labs operates
the receiver; Redis/Upstash and Resend are server-side providers. No app SMTP/API
credential is shipped. The application does not make a blanket GDPR exemption
or anonymity claim. The server defaults to disabled until the operator enables it.

## Maintainer setup and qualification

See the companion site's `docs/vaultsync-diagnostics.md`. Existing production
Redis/Resend variables are present, but Vercel marks them Secret: a CLI pull
returns placeholders, not usable credentials. Resend has no Preview binding;
future Preview tests need a branch-scoped sender. Following the maintainer's
main-integration request on October 19, Production reused its existing sender,
configured the private operator mailbox and enabled receipt. A synthetic hang
alert reached the operator's inbox. No further client-side credential setup is
needed; do not paste server secrets into issues, reports or chat.

Set an operator-only `DIAGNOSTICS_TO_EMAIL` (or existing `REPORT_TO_EMAIL`), enable
`VAULTSYNC_DIAGNOSTICS_ENABLED=true` in the intended environment, retain the
protected admin settings, and configure `CRON_SECRET` for the daily route. Preview
uses its own scope and must be tested before reviewed production deployment.
This change does not authorize merging the website or publishing a VaultSync beta.
[Current evidence and remaining checks](release-evidence/1.9-product-diagnostics-2026-10-19.md).
