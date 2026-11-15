# Reporting bugs

Thanks for helping improve VaultSync. Clear, reproducible reports make problems easier to investigate and fix.

## Before reporting

- Check that you are using the latest published version for your chosen update channel.
- Try reproducing the issue after restarting VaultSync.
- Consult [Troubleshooting](Troubleshooting.md) and [FAQ](FAQ.md).
- Where possible, reproduce the problem using disposable files and a separate test destination.

## Where to report

**GitHub Issues is the official bug tracker.**

Submit a report using the [GitHub issue forms](https://github.com/ATAC-Helicopter/VaultSync/issues/new/choose).

For troubleshooting before filing a report, use the [VaultSync Discord community](https://discord.gg/sgP8UzeAQt) and its public support Forum.

You may also request a private support ticket through the community server when a restricted conversation is appropriate.

Discord discussions and bot-generated community ideas are not automatically confirmed software defects. Reproducible bugs should be tracked on GitHub.

## What to include

- VaultSync version, build identity, and installation channel.
- Operating system and version.
- Clear steps to reproduce the problem.
- Expected behavior and actual behavior.
- Relevant error messages and safe screenshots.
- Whether the issue affects local, removable, or network-backed storage.
- Whether testing used synthetic data, an existing backup, a read-only recovery drill, or an actual restore.

## Logs and diagnostics

For backup issues, include relevant recent operation logs.

For update issues, provide the installed version, offered version, update status, and patch-preflight diagnostics.

You can export a support bundle through **Settings > Advanced**. Review its contents before sharing and exclude unnecessary optional diagnostics.

VaultSync does not upload support bundles or crash reports automatically. Never publish passwords, tokens, encryption recovery material, confidential paths, or private backup contents.

## Community follow-up

If a public Discord discussion identifies a reproducible bug, create or link the corresponding GitHub Issue instead of relying on a Forum thread as the engineering record.

When a fix or workaround is verified and safe to share, maintainers should update the appropriate documentation page.

## Security vulnerabilities

**Never disclose an unpatched vulnerability in a public issue, Discord Forum, or support ticket.**

Follow the [private security reporting policy](../../SECURITY.md).
