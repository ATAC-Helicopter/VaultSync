# Crash reporting and user control

VaultSync crash reporting is a local report-preparation feature. It is not an automatic crash uploader and does not use an analytics SDK, crash-reporting vendor, web API, SMTP credential, background transfer, or hosted report database.

## In one sentence

When crash-report assistance is enabled, VaultSync creates a minimal report locally, shows the complete generated report to the user, and can prepare an email draft with that report attached; only the user can press **Send**.

## Data flow

```text
Unhandled exception
        |
        v
Strict allowlist report builder
        |
        v
Local text file with owner-only permissions where supported
        |
        v
Complete read-only generated report
        |
        +---- Copy report
        +---- Delete report
        +---- Open report folder
        +---- Prepare email draft
                         |
                         v
               User reviews and sends
```

There is no network request between the exception and the email application. Preparing a draft does not send a report.

## User control

The **Settings > Advanced > Crash report assistance** toggle controls the entire feature.

- Enabled: VaultSync may create a local report after a crash and offer the review workflow.
- Disabled: VaultSync does not create a crash report and does not offer email preparation.
- Changing the setting does not transmit anything.
- There is no automatic-send option and no remembered consent to send future reports.
- A report can be inspected, copied, or deleted before the email application is opened.
- The report ID, OS family, crash category, and crash reason are generated fields and cannot be changed in VaultSync. Optional user context belongs in the visible email draft.
- VaultSync attempts to attach the visible text file to the draft. The user must review the attachment and press **Send** in their own email application.

The feature is enabled by default because it performs no transmission by itself. Users who do not want local crash reports can disable it completely.

## Exact report contents

The report builder uses an allowlist. It does not begin with a broad diagnostic log and then attempt to remove selected secrets.

Included:

- A category-prefixed report ID containing a fresh UUID generated for this report only.
- The VaultSync application version.
- Operating-system family: `Windows`, `macOS`, `Linux`, or `Other`.
- A coarse crash category: application domain, user interface, background task, or application.
- A coarse crash reason containing only the outer exception type name, never its message.
- Whether VaultSync must close.
- Exception type names.
- Method names from call sites whose namespace begins with `VaultSync`.
- A visible explanation of the fields deliberately excluded.

The identifier format is `CRASH-<CATEGORY>-<32 HEX CHARACTERS>`. Category is one of `UI`, `APP`, `TASK`, or `GEN`. The final component is a locally generated UUID v4 (122 random bits after the UUID version and variant bits), so no server or shared counter is required. It is not an installation or user identifier and is never reused intentionally. It exists only to correlate the user's email thread with the matching report.

Example:

```text
VaultSync shareable crash report
================================
This report was created and redacted locally.
VaultSync did not send it automatically.

Report ID: CRASH-UI-00112233445566778899AABBCCDDEEFF
Application version: 1.8.4
Operating system family: macOS
Crash category: user-interface
Crash reason: System.InvalidOperationException
Application must close: yes

Exception chain and application call sites
------------------------------------------
Exception 1: System.InvalidOperationException
  at VaultSync.UI.BackupsViewModel.RunBackupAsync()
```

Deliberately excluded:

- Exception messages and file contents.
- Recent diagnostics, verbose logs, memory dumps, and support bundles.
- Project, backup, snapshot, destination, credential, user, and machine names.
- File, folder, database, log, command-line, network-share, URL, and executable paths.
- Email, IP, MAC, host, and account addresses.
- Credentials, tokens, keys, passwords, cookies, environment variables, configuration, and identifiers.
- OS version, locale, architecture, process details, timestamps, source filenames, and source line numbers.

Exception messages are excluded rather than heuristically cleaned because messages commonly embed paths, project names, remote addresses, and values whose meaning cannot be inferred reliably.

## Defense-in-depth sanitization

The allowlist is the primary privacy boundary. Before display, a second pass removes recognizable email addresses, URLs, Windows and Unix paths, IPv4 addresses, and GUIDs if a future change accidentally introduces one.

This secondary pass is not permission to add broad data sources. New fields must be reviewed and documented individually.

## Local storage and retention

Reports are saved below the platform local application-data directory:

```text
VaultSync/crash/shareable/vaultsync-crash-<REPORT-ID>.txt
```

On Unix-like systems VaultSync requests owner read/write permissions only. Filesystems that do not implement Unix mode bits may apply their normal platform permissions instead.

When a new report is saved, VaultSync removes managed reports older than seven days and keeps at most ten. Cleanup is best effort so failure cannot interfere with crash handling. Users can delete the current report immediately from the preview. The deletion action rejects paths outside the managed report directory.

## Email draft preparation

VaultSync asks the platform mail application to create a visible draft addressed to:

```text
crashes@fglabs.dev
```

The generated report is attached before the draft is shown. VaultSync uses Apple Mail automation on macOS, `xdg-email --attach` on Linux, and the local Outlook automation interface on Windows. Arguments are passed separately to the platform process rather than interpolated into shell commands. No report contents are placed in a `mailto:` URI, and no SMTP or API credential is present in VaultSync.

macOS may show its standard consent prompt the first time VaultSync asks Mail to create a draft. Denying that permission leaves the report local and triggers the visible manual-attachment fallback.

If the configured mail application cannot accept an attachment through the supported platform interface, VaultSync does not claim that a usable draft was created. It opens the report folder and clearly asks the user to attach the file manually.

Simplified construction:

```csharp
ProcessStartInfo draft = CreatePlatformDraft(
    recipient: "crashes@fglabs.dev",
    subject: $"[VaultSync crash {report.ReportId}]",
    body: genericInstructions,
    attachmentPath: managedReportPath);
```

The user's email provider and FGLabs receiving mailbox process a report only after the user sends it. The sent message normally remains visible in the user's Sent folder, and support can reply in the same thread.

## What VaultSync cannot claim

VaultSync cannot determine whether a prepared draft was sent, altered, saved, or discarded. It therefore never displays “report sent.” It only reports that a draft and report folder were opened.

VaultSync also cannot claim that only one human or computer processes an emailed report. Email necessarily passes through the providers selected by the sender and recipient. VaultSync itself introduces no additional report processor.

## Threat model

The implementation protects against accidental background transmission, raw-log overcollection, sensitive exception messages, modification of generated identity/classification fields in the app, debug source paths, deletion outside the report directory, unlimited report accumulation, common path/address leakage, and recovery of mail/API credentials from the app because none exist.

It does not protect against a user manually adding sensitive information, malware that can already read local files or email, provider-side email retention, or unusual sensitive information encoded solely in a compiled method/type identifier.

## Maintainer rules

Any report-schema change must:

1. Remain allowlist-based.
2. Avoid exception messages and raw log ingestion.
3. Update this document's included and excluded lists.
4. Add a regression test proving representative sensitive values are absent.
5. Preserve the read-only generated report, preview-before-draft, and explicit user sending.
6. Never add SMTP secrets, third-party SDKs, or automatic uploads.
7. Keep report contents out of command-line text and require an existing managed report file for every attachment.

Relevant implementation:

- `src/VaultSync.UI/Infrastructure/ShareableCrashReport.cs`
- `src/VaultSync.UI/Infrastructure/CrashReportEmailDraft.cs`
- `src/VaultSync.UI/Infrastructure/CrashHandler.cs`
- `src/VaultSync.UI/Infrastructure/SystemFileLauncher.cs`
- `tests/VaultSync.Core.Tests/ShareableCrashReportTests.cs`

## Verification checklist

- Run unit and localization coverage tests.
- Trigger a test exception on every supported platform.
- Confirm the preview contains only documented fields.
- Confirm the report ID, OS family, category, and reason cannot be edited in VaultSync.
- Confirm disabling assistance creates no report and offers no email action.
- Confirm **Prepare email** opens a draft but sends nothing.
- Confirm the generated report is already attached to the visible draft.
- Confirm attachment failure is reported honestly and opens the report folder for manual recovery.
- Confirm **Delete report** cannot delete an unrelated file.
- Confirm permissions and retention on Windows, macOS, and Linux.
- In a development build, inject a deliberate UI exception and confirm the user retains a Sent copy and support can reply in the same thread. Release builds expose no crash-test control.
