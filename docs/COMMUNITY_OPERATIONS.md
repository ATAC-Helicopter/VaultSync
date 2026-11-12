# VaultSync Community: 90-day operating plan

**Owner:** VaultSync maintainer (solo) · **Start:** 2026-11-12 · **Review dates:** 2026-12-12, 2027-01-11, 2027-02-10

This is a community operations plan, **not a product release commitment**. The source of truth for software scope remains [ROADMAP.md](../ROADMAP.md); changes to system-recovery capabilities require their own release qualification.

## Objective

Increase the number of people who safely use, test, document and improve VaultSync, rather than maximizing member count or encouraging artificial chat activity. Prioritize useful interactions and independently repeatable recovery evidence.

## Week 1: remove onboarding friction

- [ ] In Discord Community Onboarding / Channels & Roles, make `#welcome`, `#resources`, `#announcements`, `#general`, `#help-and-support`, and `#open-a-ticket` easy to discover.
- [ ] Keep `#bug-reports` Forum, `#ideas-and-feedback`, `#beta-discussion`, `#development` and `#dev-updates` discoverable through voluntary interests. Keep voice, supporter and game/activity channels opt-in.
- [ ] Pin a short `#resources` directory that links to the stable downloads, Quick Start, Recovery Lab, GitHub Issues, Discord Forums and private ticket panel.
- [ ] Confirm Discord roles selected during onboarding do not grant moderation, staff status or privileged channel access.
- [ ] Check that new members can find assistance without completing a survey and that support Forums remain public and searchable within Discord.
- [ ] Invite one or two existing users to try onboarding and record where they get stuck, without requiring screenshots of their personal account.

## Weeks 2–4: first public Recovery Lab

- [ ] Publish [Recovery Lab v1](wiki/Recovery-Lab.md) and ask participants for exact build, platform and observable result.
- [ ] Share one concise technical case report per week or less: **problem → synthetic test → observed outcome → limitation → documentation link**.
- [ ] Ask one specific question weekly on existing community channels. Do not cross-post the same prompt to every forum.
- [ ] Publish useful, sanitized answers in the wiki rather than trapping them in private ticket conversations.
- [ ] For suspected defects, link to the exact GitHub issue. An open ticket is not automatically a confirmed issue.
- [ ] Collect feedback by build/profile; no claim that 1.9 disk recovery works on an unqualified platform.

## Days 31–60: learn, not inflate

- [ ] Aggregate Recovery Lab results by published build, OS, test step and outcome, distinguishing **read-only check**, **preview**, **real restore**, and **not performed**.
- [ ] Find the three most frequent *public* support problems and write short FAQ/troubleshooting entries.
- [ ] Invite testers to verify a small fix or a documentation correction. Offer contributions in docs, localization and validation, not only code.
- [ ] Review whether Discord onboarding options actually help users find the right channel.
- [ ] Stop or archive low-value events that require more organizer time than they return.

## Days 61–90: decide what to scale

- [ ] Review experiment results and first-response patterns, avoiding any guaranteed support SLA.
- [ ] Retain activities that produce reproducible tests, meaningful fixes, successful support or external contributions.
- [ ] Trim low-use channels from default onboarding; don't destroy channel history just for a cleaner sidebar.
- [ ] Update the community roadmap with outcomes, uncertainties, and owner workload before starting a second cycle.

## Metrics and definitions

| Metric | Definition | Source | Limitation |
| --- | --- | --- | --- |
| GitHub attention | Stars, forks, unique external contributors (report separately) | GitHub | Stars do not imply installation |
| Discord joins | New joins by UTC day | VaultSync Community Bot aggregates | Not equivalent to active users |
| Source self-report | Optional new-member survey responses | Bot aggregate | Non-response bias; not independently verified |
| Public support solved | Public posts whose author/staff explicitly confirms a solution | Manual review of Forum threads | Never equate 'closed' with solved |
| Private support handled | Tickets closed vs created, counts only | Private bot SQLite aggregate, if added later | Closure is not proof of satisfaction |
| Recovery Lab participation | Unique **submitted reports** with exact version and test results | GitHub issues or voluntary public submissions | One report may represent multiple tests |
| Read-only/preview/real-restore outcomes | Distinct measured categories, per OS/build | Lab result form | Small, self-selected sample |
| Documentation improvement | Published, verified help entries that answer repeat questions | GitHub PRs | Quantity without usefulness is not a goal |
| 7-day return | Members with consent-appropriate activity 7 days after joining | **Not yet implemented** | Do not infer from joins or collect raw message bodies |
| Website to community | Invite-link clicks | FG Labs website analytics | A click is not a Discord join |

**Privacy constraints:** no message-content analytics, private transcript ingestion, personal user directories, credentials or recovered files. Bot-local aggregates and short-lived signed snapshots are preferable to persistent raw events. Any new retention or tracking must have a separate explicit privacy review.

## Experimental 90-day outcome targets

These are **targets, not forecasts**:
- 20 voluntary participants completing a *documented* safe test.
- 5 independently reproducible recovery checks or negative findings with exact build identifiers.
- 2 useful contributions from outside the core maintainer account (docs, translations, tests or code).

If those targets do not materialize, publish the results rather than manufacturing engagement.

## Suggested communication rhythm

- One short technical update per week at most, when evidence exists.
- One question or community discussion on Sunday **only when there is a specific learning goal**.
- Releases and security notices in `#announcements` when confirmed.
- No repeated `@everyone` pings for routine work.
- Review a single small dashboard weekly; adjust effort to the maintainer's capacity.

## Delivery boundaries

- Discord channel visibility and permission changes require a server administrator; a GitHub PR cannot configure them.
- Publish documentation links on the website/Stable only after checking the resulting canonical URLs.
- Beta issue templates should accept published 1.9 builds *when they exist*, not advertise non-existent downloads.
- Do not add another forum, analytics system or loyalty/reward bot before assessing whether the existing tools answer community needs.
