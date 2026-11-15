# VaultSync Community and Support
VaultSync is an open-source backup and recovery application for Windows, macOS, and Linux. Its community welcomes users, developers, contributors, Linux and homelab enthusiasts, and people interested in technology.

The [VaultSync Discord server](https://discord.gg/sgP8UzeAQt) provides community conversations, public support, project ideas, and optional development participation.

**Discord membership is optional.** You do not need a Discord account to use VaultSync, download releases, read documentation, report a bug on GitHub, or contribute.

## Getting started

1. **Try VaultSync:** download the [latest published stable release](https://github.com/ATAC-Helicopter/VaultSync/releases/latest) and follow the [Quick Start](wiki/Quick-Start.md).
2. **Understand backup verification:** use [Recovery Lab v1](wiki/Recovery-Lab.md) with disposable files and a separate test destination.
3. **Explore the community:** visit the Discord server's welcome and resources channels, then choose the discussions relevant to you.
4. **Contribute if you want:** improve documentation, report reproducible problems, test published builds, suggest features, or contribute code.

A completed backup does not by itself prove that all files can be restored. A read-only recovery drill and a completed test restore are different checks.

## Where to go

| Purpose | Recommended destination |
| --- | --- |
| General conversation and community questions | Discord general discussion |
| Installation, backup, NAS, or restore assistance | Public support Forum linked from Discord resources |
| Individual support requiring restricted access | Discord's private ticket panel |
| Reproducible software defects | [GitHub Issues](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose) |
| Public feature proposals, voting, and discussion | Discord Ideas Forum, when enabled |
| Product design discussions | [GitHub Discussions](https://github.com/ATAC-Helicopter/VaultSync/discussions) |
| Development, documentation, and translations | Discord development channels and [Contributing](../CONTRIBUTING.md) |
| Beta feedback | Discord beta channels and the appropriate GitHub issue form |
| Published release information | [GitHub Releases](https://github.com/ATAC-Helicopter/VaultSync/releases) |
| Security vulnerabilities | [Private security reporting](../SECURITY.md) |

The Discord Server Guide and `#resources` should link to the current channels. Their names and visibility may change as the server evolves.

GitHub remains authoritative for confirmed engineering work, release status, source code, and published artifacts.

## Welcome and optional onboarding survey

The VaultSync Community Bot provides a personalized welcome in the configured public welcome channel.

- A new member receives a public greeting with one member-specific mention, not an unsolicited direct message.
- The greeting includes an optional button to start a private onboarding survey.
- Survey questions appear in Discord interactions visible to the responding member, not as public channel messages.
- The survey asks about discovery, interests, and use of the community, with an optional comment.
- Completing the survey is not required to participate in the server.
- The bot remembers completion to avoid repeated surveys and redundant welcome messages.
- Members can review or request deletion of supported personal records through the bot's privacy commands.

**Private interaction does not mean anonymous storage.** Authorized administrators can access retained survey records under the [Community Bot privacy policy](https://github.com/ATAC-Helicopter/vaultsync-community-bot/blob/main/docs/privacy.md).

Discord's native Rules Screening and Community Onboarding remain responsible for accepting rules, selecting visible channels, and assigning approved opt-in roles. The bot survey does not replace these features or grant privileged roles.

## Resources and community navigation

The bot maintains a concise community guide in the configured `#resources` channel.

It covers essential links, getting help, proposing ideas, contributing, and privacy controls without exposing the full administrative command list to members.

Once an administrator has published the guide, subsequent supported revisions update the tracked bot-owned message in place instead of creating duplicate posts or additional member notifications.

Useful member commands include:

| Command | Purpose |
| --- | --- |
| `/community` | Community orientation |
| `/contribute` | Ways to contribute to VaultSync |
| `/links` | Project and community links |
| `/guide show` | A concise, private command and navigation guide |
| `/survey` | Start or resume the optional private survey |
| `/feedback` | Submit optional feedback |
| `/privacy show` | Review supported stored personal information |
| `/privacy delete` | Request deletion of supported personal records |

The commands operate within the permissions and configuration of the community server. Full administrative documentation belongs in the bot repository, not in public onboarding messages.

## Ideas, voting, and project progress

The optional Ideas Board uses a Discord Forum for public proposals and discussion.

When enabled:

1. A member submits `/idea suggest` and reviews a private preview before publication.
2. Confirming creates a public Forum thread with voting controls.
3. Members can vote once per proposal, change their vote, or remove it.
4. Discussion remains in the native Discord thread.
5. An author can request withdrawal of their proposal, subject to the documented deletion behavior.

An idea being submitted or receiving votes **does not mean it has been approved for development**.

### Review and GitHub integration

Where the optional GitHub bridge is configured, a newly confirmed public idea may also create a GitHub Issue for triage. This happens after publication confirmation, not while the member is drafting.

Authorized staff can review eligible proposals through Approve, Decline, and Reopen review controls in the Forum. These actions are permission-checked; visible buttons do not grant moderation rights to ordinary members.

- **Under Review:** a proposal is awaiting an engineering decision.
- **Planned:** the proposal has been accepted and its mapped development status supports that label.
- **In Progress:** work is recorded as underway in GitHub Project.
- **Completed:** the mapped GitHub work is marked done; this does not necessarily mean it has shipped.
- **Shipped:** the delivered functionality has been explicitly associated with a release.
- **Declined:** the proposal has been rejected. An automatically exported GitHub Issue is closed as `not_planned`, not permanently deleted.

The bot can synchronize supported public GitHub Project #7 statuses back to the relevant Discord proposals. GitHub Project remains the source of truth for engineering progress.

Official roadmap proposals are distinguishable from member-submitted ideas. Neither a roadmap post nor a Discord status establishes that an unpublished beta is available.

The Ideas Board and GitHub export are separately configured features. Do not assume they are enabled on every deployment.

For implementation and moderation details, see the [Ideas Board guide](https://github.com/ATAC-Helicopter/vaultsync-community-bot/blob/main/docs/ideas-board.md) and [GitHub integration guide](https://github.com/ATAC-Helicopter/vaultsync-community-bot/blob/main/docs/github-project-status-sync.md).

## Private support tickets

The bot provides an optional private ticket workflow for cases that are better handled outside public discussion.

Members can select a request category, describe the issue, and open a restricted ticket channel. Staff can claim tickets, add authorized participants, archive a transcript, close a case, or reopen it before cleanup.

Only the requester, authorized participants, configured support staff, the bot, and Discord administrators with applicable access can view the ticket.

**Tickets are private support spaces, not secure storage for secrets.** Never upload passwords, recovery keys, access tokens, unredacted private logs, confidential backups, or disk images.

Closed ticket channels are automatically deleted after the configured retention period, **seven days by default**, unless reopened. Authorized staff transcript copies are retained separately and are not automatically deleted with the channel.

Full message transcripts depend on the server's Message Content Intent configuration; without it, some message bodies may be unavailable.

Ticket contents are not automatically copied to GitHub Issues, public Forum posts, Reddit, or the FG Labs analytics dashboard. Operational ticket counts may be included in restricted aggregate reporting without ticket content.

For transcript and retention details, consult the [Ticket Center documentation](https://github.com/ATAC-Helicopter/vaultsync-community-bot/blob/main/docs/ticketing.md).

## Interactive workspace boards

The Community Bot repository also contains an optional browser-based Workspace Board implementation.

Its planned deployment model separates:

- **Community Ideas:** public proposal visibility and voting linked to the existing Forum.
- **Development:** public GitHub Project information alongside local contributor tasks.
- **Private Workspaces:** invitation-controlled collaboration spaces with viewer or editor permissions.

**The web board is disabled by default and is not an embedded Discord Activity.** Its availability requires an explicitly configured HTTPS deployment, Discord authentication, access-control checks, and operational verification.

The development view does not gain permission to modify GitHub Issues or Project statuses simply by moving local cards.

Do not store credentials, confidential security reports, or undisclosed vulnerabilities in MVP workspace cards.

See the [Workspace Board deployment and limitations](https://github.com/ATAC-Helicopter/vaultsync-community-bot/blob/main/docs/workspace-board.md).

## Reporting problems and receiving support

When asking for help or submitting a reproducible report, provide:

- The VaultSync version, build identity, and distribution channel.
- Operating system and relevant destination type.
- Steps to reproduce the problem, expected behavior, and actual behavior.
- Safe screenshots or redacted diagnostic output.
- Whether the observation came from a production backup, synthetic test, read-only drill, restore preview, or completed test restore.

Review any exported support bundle before uploading it. Automated redaction cannot guarantee removal of every sensitive value.

Public support conversations can help other users find solutions. If a discussion establishes a reproducible software defect, record it on GitHub Issues. If it establishes a reusable non-sensitive fix, maintainers should update the appropriate [Troubleshooting](wiki/Troubleshooting.md) or [FAQ](wiki/FAQ.md) page.

For suspected vulnerabilities, use the [private reporting process](../SECURITY.md), not Discord, a public issue, or a support ticket.

## Beta testing and recovery safety

The VaultSync desktop application continues to target Windows, macOS, and Linux.

The 1.9 system-disk recovery work has separate platform and disk-profile qualification milestones. Development progress does not imply that disk-cloning or bootable recovery is already available in a published build.

- Use explicitly published, correctly identified test builds.
- Prefer synthetic project files and isolated backup destinations.
- Avoid destructive experiments on working disks or valuable backups.
- Report whether tests were read-only, simulated, or involved an actual restore.
- Do not present a successful drill as evidence of a completed bootable system recovery.

Start with [Recovery Lab v1](wiki/Recovery-Lab.md).

## Community conduct, privacy, and expectations

The community follows the [Code of Conduct](../CODE_OF_CONDUCT.md).

Participation is voluntary. There is no guaranteed support response time, and community votes do not determine release commitments automatically.

VaultSync's desktop application and the Discord Community Bot are separate systems with separate data policies:

- [VaultSync application privacy](PRIVACY.md)
- [Community Bot data policy](https://github.com/ATAC-Helicopter/vaultsync-community-bot/blob/main/docs/privacy.md)

The bot does not require access to users' backups, credentials, or private project files. Discord participation and community metrics are not substitutes for application reliability or restore verification.

## Administration and source of truth

Server administrators should keep native Discord onboarding, permissions, and channel navigation aligned with the deployed bot configuration.

The bot provides tools for reviewing welcome-channel settings, correcting configured channel links, repairing supported historical welcome messages in place, previewing changes safely, and managing optional Ideas and ticket features.

Administrative command references, setup instructions, deployment requirements, and privacy boundaries are maintained in the [VaultSync Community Bot repository](https://github.com/ATAC-Helicopter/vaultsync-community-bot).

Published release assets are authoritative on [GitHub Releases](https://github.com/ATAC-Helicopter/VaultSync/releases). The community's announcement channels and [r/VaultSync](https://www.reddit.com/r/VaultSync/) may provide summaries, but do not replace the official release record.
