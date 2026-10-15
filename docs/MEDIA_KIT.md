# VaultSync media and community kit

This page is for people evaluating, demonstrating, writing about, or recommending VaultSync. It describes the **currently released folder/project backup product**, not future 1.9 plans.

## Product facts

- **Name:** VaultSync
- **Developer:** FG Labs
- **Type:** Free, MIT-licensed, open-source desktop backup and recovery application
- **Platforms:** Windows, Linux, macOS
- **Source and issues:** https://github.com/ATAC-Helicopter/VaultSync
- **Official downloads:** https://fglabs.dev/downloads
- **Latest stable release:** https://github.com/ATAC-Helicopter/VaultSync/releases/latest
- **User discussion:** https://www.reddit.com/r/VaultSync/
- **Documentation:** https://github.com/ATAC-Helicopter/VaultSync/blob/Stable/DOCUMENTATION.md
- **Contact:** https://fglabs.dev/contact

## Short description (under 200 characters)

VaultSync is a free, open-source desktop backup app for selected folders and projects, with local/NAS destinations, snapshots, verification, and guided recovery checks.

## Longer description

VaultSync backs up selected folders, creative workspaces, and software projects to destinations you control, such as local drives, external disks, and supported network or NAS shares. Its desktop interface brings together snapshots, scheduled backups, history, comparison, integrity verification, restore planning, and recovery drills. The aim is to make the distinction between a completed backup, verified data, and an actually tested recovery easier to understand.

The application runs on Windows, macOS, and Linux and includes a CLI for automation. It is free and open source under the MIT license, with no required proprietary cloud-storage account.

## What to demonstrate

A realistic demo should use disposable sample data rather than personal files:

1. Choose a project folder containing a few files, then select a destination.
2. Review the project's exclusion rules, especially when using a development preset.
3. Run a backup and inspect its recorded recovery point.
4. Modify one file, back up again, and show the history or comparison view.
5. Verify supported stored data and explain precisely what the operation checked.
6. Simulate a restore or run an isolated recovery drill where supported.
7. Open the recovered content in its actual application, noting any dependencies not covered.

For repeatable recording guidance, see [the video recording guide](video/recording-guide.md). For interface visuals, use actual screenshots from [docs/images](images) and the [current product page](https://fglabs.dev/vaultsync). Do not present development mockups as released functionality.

## Appropriate audiences

- **Software developers and creative professionals:** protecting selected project folders while reviewing exclusion presets for generated output.
- **NAS and homelab users:** making local/network backups more inspectable and testing recovery from supported mounted destinations.
- **Desktop users:** scheduled copies of important folders to external or network storage without setting up a proprietary hosted backup service.

Practical guides:
- [Development project backups](https://fglabs.dev/vaultsync/use-cases/backup-development-projects)
- [Backups to NAS](https://fglabs.dev/vaultsync/use-cases/backup-to-nas)
- [Verification and restore tests](https://fglabs.dev/vaultsync/use-cases/verify-backups-and-test-restores)

## Scope and caveats (essential when reviewing)

- VaultSync is **not** currently a full-disk imaging, bootable rescue-media, or bare-metal recovery product.
- A completed backup does not automatically prove recoverability. Hash checks, simulations, and isolated drills produce different levels of evidence; their scope should be described accurately.
- The default development presets intentionally exclude certain generated and machine-local files. Review them before depending on a backup. Live `.git` internals remain excluded by development presets.
- Network storage depends on mounts, permissions, and connectivity. The NAS itself is not automatically an offsite or immutable copy.
- Encrypted archives are password-gated and restorable, but Snapshot Explorer does not browse encrypted archives in its current version.
- Direct Windows and macOS installers are unsigned and may trigger platform warnings. Use the official release assets and published digests. Windows users can also use the Microsoft Store.
- The project is actively evolving. Features described in 1.9 development logs must **not** be presented as released until they ship.

For exact current behavior, use the [README](../README.md), [documentation](../DOCUMENTATION.md), [changelog](../CHANGELOG.md), and most recent stable [release notes](https://github.com/ATAC-Helicopter/VaultSync/releases/latest).

## Suggested outreach copy

### Technical community introduction

> I build VaultSync, a free and open-source desktop backup app for selected folders and project workspaces. One thing I want it to make clear is that finishing a copy, verifying stored bytes, and successfully rehearsing a restore are different claims. It's available for Windows, macOS and Linux, with local and NAS workflows. I'd value technical feedback on the verification and recovery model, particularly from people who manage development projects or home NAS backups. Source: https://github.com/ATAC-Helicopter/VaultSync

### Reviewer / creator introduction

> Hi, I'm the developer of VaultSync, a free, MIT-licensed desktop backup application. It supports Windows, Linux and macOS, with project-folder backups to external disks and supported NAS shares. It also has history, verification and guided recovery checks. If you cover backup software or homelabs, I can share a reproducible demo workflow and explain its current limitations. There is no sponsorship or review condition attached: https://fglabs.dev/vaultsync

Use a tailored note rather than bulk-sending this verbatim. Do not request positive reviews, votes, or endorsements.

## Existing listings and community links

- [Product Hunt listing](https://www.producthunt.com/products/vaultsync) (already exists; do not create a duplicate)
- [AlternativeTo listing](https://alternativeto.net/software/vaultsync/) (already exists; check whether the description/screenshots need updating)
- [Reddit community](https://www.reddit.com/r/VaultSync/)

When posting, follow the destination community's self-promotion rules and disclose that you are the developer.

## Accuracy and privacy rules

- Do not claim independent security certification or external audit unless documented.
- Do not quote download counters as active users or unique installations. Download statistics may include repeated downloads and may omit Store activity.
- Prefer **real, annotated screenshots** and meaningful test-restore footage over conceptual advertising art.
- Do not send unsolicited bulk outreach. Make creator contact personal and relevant.
- If publishing a new listing, link to the official product or release page and ensure the current version, platforms, and pricing are correct.

Last editorial review: 2026-10-14.
