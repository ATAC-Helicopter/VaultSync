# VaultSync Devlog → Discord (public GitHub Actions)

This notifier was moved from private `ATAC-Helicopter/fglabs-site` to this public repository to avoid GitHub Actions private-repository minute consumption. It reads only the public first-party feed at https://fglabs.dev/devlog/feed.xml?project=VaultSync and sends an embed to the configured Discord webhook. It does not require any access to the private website repository.

## Activation (one-time maintainer setup)

1. In **VaultSync → Settings → Secrets and variables → Actions → Secrets**, add **repository secret** `DISCORD_DEVLOG_WEBHOOK` with the webhook URL for the Discord `#dev-updates` channel. Do not commit, paste, or echo the webhook URL in issues, logs, or terminal output.
2. Optional: add **repository variable** `DISCORD_DEVLOG_ROLE_ID` with the numeric ID of the opt-in Dev Updates role. If absent, no role is mentioned. Never set the `@everyone` role.
3. In **VaultSync → Actions → VaultSync devlog to Discord → Run workflow**, choose `Stable`. For the **one-time VaultSync 1.8.10 article announcement**, leave `publish` unchecked and check **publish_latest**. Thereafter the schedule sends genuinely new articles automatically; `publish` can be used to catch up if needed.
4. Examine the `notify` job result and Discord channel. An hourly schedule checks for new entries; the exact execution time may vary.
5. The old `fglabs-site` workflow must remain disabled to prevent duplicate messages.

## State migration

Delivery state is stored on the standalone branch `automation/vaultsync-devlog-state` in `state.json`. The last delivered article identifier was preserved from the previous website workflow. The old cursor article no longer exists in the first-party feed, so `last_guid` was explicitly baselined to the new **VaultSync 1.8.10** article while `last_sent_guid` still points to the older delivered article. This is intentional: the scheduled run will not replay historical entries, and **one manual `publish_latest=true` run after the webhook is configured will announce the 1.8.10 article once**. This file contains only public article identifiers, never secrets.

- The first run with **no** cursor seeds the latest article without reposting history.
- With a migrated cursor, pending articles are sent oldest-first, up to five per run.
- `publish_latest=true` sends the most recent article once only if no older unannounced articles are pending.
- After a successful Discord webhook response, the cursor is committed. A network failure or webhook error does not advance it.
- A crash after Discord accepts a message but before the state commit can exceptionally cause a duplicate. State loss or an unknown cursor fails closed rather than backfilling the feed.

## Scope and security

The webhook is a GitHub Actions secret; never store it in repository content. For private security reports, use VaultSync's security policy, not Discord. The script accepts only FG Labs VaultSync article URLs, limits the feed and messages, restricts mentions to an optional opted-in role, and rejects unrecognized webhook URL shapes. The publication job alone receives `contents: write`; PR validation has read access and cannot publish.

The Discord channel is for FG Labs devlog articles, including diaries about released builds. **Release announcements** and critical notices should still be posted separately to `#announcements`. No GitHub release assets or store builds are changed by this notifier.

## Test locally

```bash
python3 -m unittest discover -s tests/scripts -p 'test_discord_devlog.py' -v
python3 -m py_compile scripts/discord_devlog_notify.py
```
