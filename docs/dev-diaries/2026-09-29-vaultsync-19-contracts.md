# VaultSync 1.9 dev update — what a backup script can know

Draft: 2026-09-29. For the FG Labs site; not a release announcement.
Planned site path: https://fglabs.dev/devlog/vaultsync-19-what-a-backup-script-can-know

The word **protected** can do an impressive amount of work in a backup app. A destination is configured, a backup record exists, a mirror ran—and suddenly everything sounds certain. Those are different facts. I have been making the 1.9 CLI say which one it actually knows.

Last time I showed a real recorded folder backup, verification, and restore preview from the terminal. This update is about the less photogenic work behind a trustworthy result: what gets reported, what remains unchecked, and what happens when protection fails halfway through.

## Configured is not reachable

The 1.9 development CLI now has an opt-in, versioned `destinations --output json` result. It reads the current configuration without creating defaults or printing destination paths or credential names. Most usefully, it says whether accessibility was checked. A line in a settings file is not a successful network probe.

This is output from the actual development CLI, using a disposable configuration with one example destination:

```json
{
  "schemaVersion": 1,
  "operation": "destinations.list",
  "status": "success",
  "data": {
    "destinations": [
      {
        "position": 1,
        "alias": "Studio archive",
        "active": true,
        "offsite": true,
        "preMounted": false,
        "accessibilityChecked": false
      }
    ],
    "count": 1
  },
  "error": null,
  "exitCode": 0
}
```

The example configuration contained a path and a credential; neither appears above. `accessibilityChecked: false` is the honest answer because this command did not try to connect. The existing `--test` path stays separate until its machine-output behavior is qualified. Older `destinations --json` scripts still get their array, including `[]` when nothing is configured. A schema change should not arrive disguised as a routine update.

## A watcher that knows when to stop

The development watcher now starts observing source changes before its first snapshot and mirror. An edit during that startup window should not fall through the floorboards. If a mirror or later cycle fails, the watcher stops with an error instead of sitting there quietly while a script assumes protection is still running. Cancellation has its own exit result and drains pending work.

There is a packaging version of the same lesson. The AppImage build now checks the extracted package root and launchers for usable permissions. A catalog test could not launch the existing 1.8.9 asset under Firejail. The build fix is in the 1.9 work, but an already published download cannot be retroactively repaired; the catalog needs a future asset built with it.

## A map before moving the furniture

The [1.9 desktop architecture draft](https://github.com/ATAC-Helicopter/VaultSync/pull/729) maps today's eight screens to four user intents: Protect, History, Recover, and Manage. It proposes one validated route and rules for keeping a selected project, filters, and unfinished work when you navigate. Existing pages stay available while replacements earn parity, accessibility, localization, and platform evidence. It is a map for a staged move, not a screenshot of a new interface.

## Still development work

These changes are in draft 1.9 pull requests: [updater compatibility #721](https://github.com/ATAC-Helicopter/VaultSync/pull/721), [CLI and watcher #722](https://github.com/ATAC-Helicopter/VaultSync/pull/722), [AppImage packaging #726](https://github.com/ATAC-Helicopter/VaultSync/pull/726), and [desktop route architecture #729](https://github.com/ATAC-Helicopter/VaultSync/pull/729). Windows, macOS, and Linux build/test checks pass on the first three; SonarQube Cloud still errors while looking up these pull requests. The draft release promotion is [#683](https://github.com/ATAC-Helicopter/VaultSync/pull/683). **VaultSync 1.9 has not shipped.**

Disk-image format, supported systems, independent boot, image-to-disk restore, post-restore validation, and exact installed 1.8.9 upgrades remain open release gates. A passing build is useful evidence. It is not a bootable recovery plan.

For scripts, the next useful question is: which result would you want to distinguish most clearly—configured, reachable, backed up, or verified?
