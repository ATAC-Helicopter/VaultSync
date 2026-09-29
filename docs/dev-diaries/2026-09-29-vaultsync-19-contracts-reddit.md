# VaultSync 1.9 dev update: what a backup script can actually know

Draft for Reddit. Publish only after the matching site entry is live at the planned URL below.

I have been continuing VaultSync's 1.9 work after the CLI backup/restore preview shown last week. The latest slice is about reporting only what the app has actually checked.

The development CLI has a new opt-in `destinations --output json` result. It can list configured destinations without printing paths or credential names, and it says `accessibilityChecked: false` when it has not probed the destination. In a real run against a disposable config, one entry came back as:

```json
{"alias":"Studio archive","active":true,"offsite":true,"accessibilityChecked":false}
```

The old `destinations --json` array remains available, including `[]` for an empty config. I do not want an existing script to get a new schema by accident.

I also tightened the watcher: it now observes edits during startup and stops with an error if a mirror or later cycle fails. The Linux AppImage build checks extracted root/launcher permissions after packaging. That fix needs a future asset; it cannot repair the existing 1.8.9 download.

On the desktop side, I have drafted the navigation contract for Protect, History, Recover, and Manage. It maps the current screens and sets rules for preserving selection and unfinished work while old workflows remain available. It is an architecture draft, not a shipped UI redesign.

All of this is in the draft [1.9 PR stack](https://github.com/ATAC-Helicopter/VaultSync/pull/728) (#721, #722, #726, #729), with the final release promotion still [draft #683](https://github.com/ATAC-Helicopter/VaultSync/pull/683). Windows, macOS, and Linux builds/tests pass on the implementation PRs. Disk imaging, bootable restore, supported-system qualification, and exact installed 1.8.9 upgrades are still open. **1.9 has not shipped.**

When reading backup status from a CLI, which distinction matters most to you: configured, reachable, backed up, or verified?

[Longer development note on the FG Labs site](https://fglabs.dev/devlog/vaultsync-19-what-a-backup-script-can-know).
