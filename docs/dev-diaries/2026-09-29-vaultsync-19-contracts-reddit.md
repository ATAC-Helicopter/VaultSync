# VaultSync 1.9 dev update: what a backup script can know

The word **protected** can hide a lot of different facts. A destination is configured, a backup record exists, a mirror ran—but which part did the app actually check?

I have been tightening that answer in VaultSync's 1.9 development CLI. The new opt-in `destinations --output json` command lists configured destinations without exposing paths or credential names. It also reports `accessibilityChecked: false` when it has not tried to reach one. This is an excerpt from a real run with a disposable configuration:

```json
{"alias":"Studio archive","active":true,"offsite":true,"accessibilityChecked":false}
```

The older `destinations --json` array still works, including `[]` for an empty config. Existing scripts should not acquire a new schema by surprise.

The watcher now observes source edits during its first snapshot and mirror. If a mirror or later cycle fails, it stops with an error instead of quietly continuing. I also added extracted-permission checks to the Linux AppImage build; that fix needs a future release asset and cannot change the already published 1.8.9 download.

There is a desktop architecture draft too: Protect, History, Recover, and Manage, with existing workflows kept available while replacements prove parity. It is a plan, not a new screen.

This is work in the [draft 1.9 PR stack](https://github.com/ATAC-Helicopter/VaultSync/pull/728). Windows, macOS, and Linux build/test checks pass on the implementation PRs, but disk imaging, bootable restore, supported-system qualification, and exact installed 1.8.9 upgrades remain open. **VaultSync 1.9 has not shipped.**

The [longer devlog is now on the FG Labs site](https://fglabs.dev/devlog/vaultsync-19-what-a-backup-script-can-know).

Which distinction would help you most in a backup CLI: configured, reachable, backed up, or verified?
