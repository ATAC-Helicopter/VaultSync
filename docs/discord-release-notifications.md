# Discord release notifications

This integration sends a single formatted Discord message to the VaultSync
`#announcements` channel when a GitHub Release is **published**.

## Setup

1. In Discord, open **#announcements → Edit Channel → Integrations → Webhooks**.
2. Create a webhook named **VaultSync Releases** for that channel.
3. Copy its HTTPS URL. Treat it as a password: never paste it into source code,
   an issue, a PR, chat, or an Actions log.
4. In GitHub, open **Settings → Secrets and variables → Actions → New repository secret**.
5. Name it `DISCORD_RELEASE_WEBHOOK` and paste the webhook URL as its value.
6. Merge the notifier workflow into **Stable** (the repository's default branch).

The workflow at `.github/workflows/discord-release.yml` listens only for
`release.published`. This covers both stable releases and prereleases published
from drafts. It does **not** run on pushes, PRs, edits, or draft preparation.
It does not backfill announcements for older releases.

The notifier checks out the script from `Stable`, even when the published tag
points at a different branch. This keeps the notification logic independent of
the code being released. It needs only `contents: read` permissions and Python's
standard library. It never prints the webhook URL.

## Duplicate prevention and delivery

- GitHub sends one `published` activity when each release is first published.
- A per-release concurrency group prevents overlapping runs.
- GitHub Actions **reruns are intentionally skipped** to avoid re-posts when a
  prior network request may have succeeded but its outcome is unknown.
- Discord is called with `wait=true`; a returned message ID confirms posting.
- Only explicit HTTP 429 rate-limit rejections are retried. Network timeouts and
  other ambiguous failures require a human to check `#announcements` before
  manually deciding whether to post. There is no automatic resend.
- `allowed_mentions: {parse: []}` prevents unintentional role/user/everyone pings.

This is **best-effort at-most-once delivery**, not a strict distributed
exactly-once guarantee. Publishing a release twice via independent events could
still cause more than one message; eliminating that edge case would require
persistent delivery receipts.

## Validation

Run the offline tests without any webhook:

```sh
python3 -m unittest tests.scripts.test_discord_release_announcement
```

To inspect a payload with no network request, save a GitHub `release.published`
event JSON to a local file, then run:

```sh
python3 scripts/discord_release_announcement.py --dry-run --event-path /path/to/event.json
```

Do **not** publish a dummy GitHub Release solely to test this workflow: that
would be a real public release and would generate a real Discord announcement.

## Troubleshooting

- **Missing secret:** create `DISCORD_RELEASE_WEBHOOK` in repository Actions
  secrets. Connector tooling cannot read or set this secret.
- **HTTP 401/403/404:** verify the channel webhook exists, has not been
  regenerated, and its URL matches the GitHub secret.
- **Failed / timed-out run:** check Discord for a delivered message *before*
  considering any manual post. Re-running the workflow will intentionally skip.
- **No run at all:** confirm that the notifier was merged into the default
  branch `Stable` before the release was published.
