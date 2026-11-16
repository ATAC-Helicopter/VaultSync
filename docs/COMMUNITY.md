# VaultSync Community and Support

Join the [official VaultSync Discord community](https://discord.gg/sgP8UzeAQt) for informal help, discussions, bug triage, development, and optional community activities. Discord participation is never required to use VaultSync, file bugs, or contribute.

## Choose the right place

| Purpose | Channel |
| --- | --- |
| General questions and troubleshooting | Discord `#help-and-support` Forum |
| Discuss suspected bugs, with sanitized screenshots/logs | Discord `#bug-reports` Forum |
| Track reproducible bugs and engineering changes | [GitHub Issues](https://github.com/ATAC-Helicopter/VaultSync/issues) |
| Feature ideas and general feedback | Discord `#ideas-and-feedback` or [GitHub Discussions](https://github.com/ATAC-Helicopter/VaultSync/discussions) |
| Implementation and contributions | Discord `#development`, GitHub PRs, and [CONTRIBUTING.md](../CONTRIBUTING.md) |
| Pre-release development conversation | Discord `#beta-discussion` |
| Published releases and official announcements | [GitHub Releases](https://github.com/ATAC-Helicopter/VaultSync/releases), Discord `#announcements`, and [r/VaultSync](https://www.reddit.com/r/VaultSync/) |
| Security vulnerabilities | Follow [SECURITY.md](../SECURITY.md) **privately**, not in a public channel |

GitHub remains the source of truth for issues, code review, the roadmap, and published releases. A Discord discussion is not a confirmed bug, a support commitment, or a claim that experimental behavior is supported.

## Discord channels and opt-in roles

- `#welcome`, `#resources`, `#announcements`: getting started, useful links, and official updates.
- `#general`, `#ideas-and-feedback`, `#development`: discussion and collaboration.
- `#help-and-support`, `#bug-reports`: separate support and bug-report Forum threads.
- `#beta-discussion`: upcoming beta discussion; download availability is determined solely by published release assets.
- `#trivia`, `#bot-games`, `Game Night`: optional community activities.

Members can select `Release Updates`, `Event Updates`, and `Community Games` through **Channels & Roles**. These are opt-in notification/activity roles, not proof of contributor, maintainer, moderator, or beta-tester status.

## Private security collaboration

Use the private GitHub Security Advisory for vulnerability evidence, review,
and private patches; use its restricted temporary fork where appropriate.
A private Discord channel is for coordination, not for replacing the advisory
as the technical source of truth. Invite external reporters to advisory
collaboration without granting general repository write/admin privileges.
See [SECURITY.md](../SECURITY.md) and
[engineering workflow](ENGINEERING_WORKFLOW.md).

## Privacy and security

Provide your VaultSync version, OS, reproduction steps, and expected/actual behavior when asking for help. Review and redact all logs, screenshots, and support bundles before posting. Do not share credentials, tokens, password material, encryption or recovery keys, private backup content, or personal information.

The [Code of Conduct](../CODE_OF_CONDUCT.md) applies to the Discord community. Moderation and informal support do not have guaranteed response times.

## GitHub-to-Discord releases

The optional [release-notification workflow](discord-release-notifications.md) is designed to announce **newly published** GitHub releases in Discord `#announcements`. It does not send messages for commits, pull requests, or draft releases and does not backfill historical versions. It requires both deployment to the default `Stable` branch and a configured `DISCORD_RELEASE_WEBHOOK` Actions secret. Until both are in place, GitHub Releases is the authoritative source.

VaultSync 1.9 system recovery is still under development; the current stable application does not provide full-disk bootable recovery.
