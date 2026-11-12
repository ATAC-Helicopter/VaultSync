# VaultSync Community and Support

VaultSync is open-source backup software for Windows, macOS and Linux.
[Discord](https://discord.gg/sgP8UzeAQt) is the place for quick conversations, optional beta testing and direct help. **Joining Discord is never necessary** to use VaultSync, read the documentation, file a GitHub issue or contribute code.

## New here? Three useful first steps

1. **Try the app:** download the [latest published stable build](https://github.com/ATAC-Helicopter/VaultSync/releases/latest) and follow the [Quick Start](wiki/Quick-Start.md).
2. **Learn how to check a backup:** complete the [Recovery Lab v1](wiki/Recovery-Lab.md) using **disposable synthetic files** and a separate restore destination. A passing read-only drill is not a completed real restore.
3. **Need help or want to contribute?** Use the routing table below. You can offer a tested procedure, improve docs, translate or submit a reproducible bug without being a developer.

## Where should I post?

| Purpose | Best destination |
| --- | --- |
| General troubleshooting or questions other users could benefit from | Discord **`#help-and-support`** Forum |
| Public suspected bugs and reproducible examples | Discord **`#bug-reports`** Forum |
| Private support, an individual case, or a conversation requiring limited access | Discord **`#open-a-ticket`** (private VaultSync ticket) |
| Confirmed defects that need engineering tracking | [GitHub Issues](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose) |
| Feature ideas and design proposals | Discord **`#ideas-and-feedback`** or [GitHub Discussions](https://github.com/ATAC-Helicopter/VaultSync/discussions) |
| Published-build beta reports and safe repeatable checks | Discord **`#beta-discussion`** and the [beta feedback issue form](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose) |
| Implementation, documentation and translation contributions | Discord **`#development`**, GitHub pull requests, [CONTRIBUTING.md](../CONTRIBUTING.md) |
| Release announcements and shipping status | [GitHub Releases](https://github.com/ATAC-Helicopter/VaultSync/releases), Discord **`#announcements`**, and [r/VaultSync](https://www.reddit.com/r/VaultSync/) |
| Security vulnerabilities | [Private security reporting](../SECURITY.md), **never** a public Forum, ticket or issue |

**Public Forum versus private ticket:** use public Forums when the answer could help others. Use tickets for privacy or an individual support exchange. Tickets are accessible to the requester and designated staff; they are **not a secret vault**. Never upload passwords, recovery keys, account tokens, private backups, disk images or confidential files. Staff may later write a *sanitized* public help article, but private ticket content must not be copied publicly without authorization.

Ticket channels are automatically removed after the configured closed-ticket retention window (**7 days by default unless reopened**). Transcript copies may remain in the private staff log and are subject to separate retention. The ticket service does **not** automatically publish conversations to GitHub, Reddit or the FG Labs analytics site.

GitHub remains authoritative for code changes, reproduced bugs, roadmap commitments and published releases. A Discord discussion is not a confirmed bug, a support SLA or proof of a tested feature.

## Suggested Discord onboarding (server administrator)

Keep the initial channel list small, especially while the server is young. **Do not delete existing Forum history or community channels.**

**Show by default:**
- `#welcome` and `#resources`: orientation, installation and links.
- `#announcements`: important release/security announcements, not every commit.
- `#general`: a place to ask where to go.
- `#help-and-support`: searchable, public support.
- `#open-a-ticket`: private requests.

**Offer through Channels & Roles or Onboarding as optional:**
- `#bug-reports`, `#ideas-and-feedback`, `#development`, `#beta-discussion`, `#dev-updates`.
- Events, voice, off-topic and community activity channels if the member wants them.

A practical onboarding question: **"What brought you to VaultSync?"** with answers like *I want to use it*, *I want to help test*, *I want to contribute*, and *I'm looking for support*. Map these to **channel visibility**, not trust or staff permissions. Keep the existing `Release Updates`, `Event Updates` and `Community Games` roles **opt-in**. Do not create a false `Contributor` or `Beta Tester` role simply because somebody joins a channel.

Avoid routine `@everyone` mentions. Announce only important releases, security alerts or material service changes; ordinary dev updates belong in `#dev-updates`.

## Getting useful support faster

For a public question, beta report or private ticket include the following **without sensitive details**:

- VaultSync version/build and installation channel.
- Operating system and, for a network issue, destination type (local disk, external drive, pre-mounted SMB/NFS, etc.).
- Exact steps to reproduce; what you expected and what actually happened.
- The status shown in the app (completed, failed, attention), plus safe screenshots or redacted logs.
- Whether you tested with disposable files and whether the outcome was a **read-only drill**, **restore preview**, or **completed real restore**.

For reproducible reports, prefer the [appropriate GitHub issue form](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose), even if the conversation started on Discord. Do not publish private ticket transcripts or private logs to prove a point.

## Beta testing and Recovery Lab

- The **desktop app** remains available for Windows, macOS and Linux independently of when system-disk recovery qualifies on each platform.
- The **1.9 system recovery** roadmap is staged by **qualified platforms and disk profiles**; development discussions are not evidence that a public beta or system recovery is available.
- Test only published, explicitly labeled assets or accurately identify a source/dev build. Never use live disks or production backups for destructive recovery experiments.
- Start with [Recovery Lab v1](wiki/Recovery-Lab.md): three synthetic text files, a local backup, a read-only drill, then a real restore **only if the UI supports an independent empty target**. Report the precise result, including tests that could not safely be completed.

When a recurring issue has a confirmed, non-sensitive solution, maintainers should add it to [Troubleshooting](wiki/Troubleshooting.md), [FAQ](wiki/FAQ.md) or a relevant wiki guide. This ensures answers remain discoverable outside Discord.

## Community expectations

- See the [Code of Conduct](../CODE_OF_CONDUCT.md) and [Privacy](PRIVACY.md).
- Never assume that a channel response is guaranteed. Volunteer/maintainer support has no service-level commitment.
- Server membership, role selections and ticket usage must not be treated as a substitute for product adoption or safe-recovery evidence.

## GitHub-to-Discord release notifications

The optional [release notification workflow](discord-release-notifications.md) announces **newly published** GitHub releases to Discord `#announcements`. It does not announce commits, pull requests or drafts and does not backfill history. It requires integration on the release-publishing branch and a configured `DISCORD_RELEASE_WEBHOOK` Actions secret. Until verified, GitHub Releases remains the authoritative source.
