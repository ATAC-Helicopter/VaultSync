# Frequently Asked Questions

Answers to common questions about VaultSync, backups, recovery, updates, and community support.

## General

### Is VaultSync free and open source?

Yes. VaultSync is open-source software licensed under the MIT License. The source code is available in the [official GitHub repository](https://github.com/ATAC-Helicopter/VaultSync).

### Which operating systems are supported?

VaultSync provides desktop builds for Windows, macOS, and Linux.

Available packages and architectures depend on the published release. Check [GitHub Releases](https://github.com/ATAC-Helicopter/VaultSync/releases/latest) and the [Installation Guide](Installation.md).

### Do I need an account or cloud subscription?

No mandatory cloud account is required. VaultSync works with storage that you configure, including local disks, removable storage, and supported network destinations.

## Backups and Storage

### What is the difference between a snapshot and a backup?

A **snapshot** records the state of a project so changes can be identified and compared.

A **backup** stores recoverable project data at a configured destination.

Creating a snapshot alone does not mean an independent, recoverable copy of your files exists.

See [Backups](Backups.md) and [Backup Pipeline](Backup-Pipeline.md).

### Does VaultSync support multiple destinations?

Yes. Enable advanced destinations in Settings to configure multiple local, external, or network targets.

See [Destinations](Destinations.md) for setup and limitations.

### Where are backups stored?

Backups are stored under the configured backup root or destination path, depending on your settings.

See [Destinations](Destinations.md).

### Can VaultSync back up to a NAS?

Yes, using supported network-share workflows, including SMB and pre-mounted storage.

Network permissions, mount availability, and credentials affect whether a destination is usable.

See [Network Shares](Network-Shares.md).

### Is a sync or mirror operation the same as a versioned backup?

No. A mirror synchronizes the current state of files and can propagate deletions or other changes.

It is not a substitute for independent, versioned recovery points. Use VaultSync's backup workflow when you need recoverable history.

### Can I synchronize backup history across machines?

Yes. VaultSync can discover and merge portable metadata stored in `.vaultsync/meta/` on supported destinations.

This is metadata synchronization, not automatic restoration of project files.

See [Metadata Synchronization](Metadata-Sync.md).

## Verification and Recovery

### Does a successful backup prove that every file can be recovered?

No. Backup completion confirms the result of that operation, but it does not prove recovery under every possible failure condition.

Use a recovery drill to check stored-data reachability and bounded verification evidence. Periodically restore important data to an independent test destination.

See [Recovery](Recovery.md) and [Recovery Lab](Recovery-Lab.md).

### What does a recovery drill check?

Depending on the stored recovery point and available metadata, a read-only drill can examine:

- Backup and snapshot linkage.
- Destination and stored-payload reachability.
- Stored-file inventory and bounded byte verification.
- A read-only restore plan and possible conflicts.

A passing drill means the checks that ran passed. It does not guarantee recovery from every hardware, storage, credential, or operating-system failure.

### Why is an encrypted recovery drill limited?

VaultSync can confirm the existence of the encrypted payload and its descriptor, but it does not request or retain the encryption password merely to improve the readiness result.

Open an encrypted recovery point with the appropriate password when you need to inspect or restore its contents.

### What is the 3-2-1 protection advisor?

The advisor helps evaluate whether your protection strategy includes:

- Three copies of the data, including the live source.
- Two distinct storage media.
- One reachable destination that you have explicitly identified as offsite.

VaultSync does not infer physical offsite status from a path or hostname.

### Can VaultSync already recover an entire bootable operating system?

Do not assume so.

The current desktop recovery workflow and read-only drills are different from full-disk imaging, restoring a system partition, and proving that a recovered machine boots successfully.

Additional system-recovery capabilities are development goals with platform-specific qualification requirements.

Consult the [Roadmap](../../ROADMAP.md) and published release notes before relying on such functionality.

## Updates and Maintenance

### Can I skip an update?

Yes. Use **Skip version** in the update banner. The skipped-version setting can be cleared later.

### Why was a patch update not offered?

Patch compatibility uses exact installed versions listed in a published release manifest.

A newer target version does not mean a patch exists for every predecessor. When the installed version is not eligible, use the appropriate installer fallback.

See [Updates](Updates.md).

### What is Doctor?

Doctor is the repair and conflict-review area under **Settings > Advanced**.

It provides diagnostic and follow-up actions, reviewable repair plans, and controls for resolving imported-setting conflicts.

### Does VaultSync send crash reports automatically?

No. When crash-report assistance is enabled, VaultSync prepares a local report for inspection.

The user controls whether to share it. The feature can be disabled in Settings.

Review diagnostics and exported support bundles before sharing them, even when automated redaction is available.

## Community and Support

### Where can I get help?

Use the [VaultSync Discord server](https://discord.gg/sgP8UzeAQt) for community discussion, public support, and optional private support tickets.

You can also use [GitHub Discussions](https://github.com/ATAC-Helicopter/VaultSync/discussions) or [r/VaultSync](https://www.reddit.com/r/VaultSync/).

Discord membership is optional.

### How do private Discord support tickets work?

Where enabled, the Community Bot can create a restricted support channel for a specific request.

Tickets are not secure storage for passwords, encryption keys, access tokens, private backups, or confidential files.

Closed ticket channels are deleted after the configured retention period; authorized staff transcripts may be retained separately.

See [Community and Support](../COMMUNITY.md).

### How can I suggest a feature?

Use GitHub Discussions or the Discord Ideas Forum when enabled.

The Community Bot supports proposal submission, voting, staff review, and optional GitHub integration. Submission or approval does not guarantee a feature has shipped.

Official implementation progress and release status remain tracked through GitHub.

### How do I report a bug?

Use the [GitHub issue forms](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose).

Include the application version, operating system, steps to reproduce, expected behavior, actual behavior, and relevant redacted diagnostics.

See [Reporting Bugs](Reporting-Bugs.md).

### How do I report a security vulnerability?

Do not publish vulnerability details in GitHub Issues, Discord, Reddit, or public support channels.

Follow the [private security reporting policy](../../SECURITY.md).

---

[Quick Start](Quick-Start.md) · [Troubleshooting](Troubleshooting.md) · [Community and Support](../COMMUNITY.md) · [Official Releases](https://github.com/ATAC-Helicopter/VaultSync/releases/latest)
