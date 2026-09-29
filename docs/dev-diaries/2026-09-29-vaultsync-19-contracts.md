# VaultSync 1.9 dev update — a useful answer before a risky action

Draft: 2026-09-29. For the FG Labs site; not a release announcement.

The previous 1.9 update showed a recorded folder backup, verification, and a restore preview from the CLI. Since then, I have been working on a less visible question: what should VaultSync say when a script asks about protection, and what should it refuse to imply?

That question matters before disk imaging or bootable recovery. A destination can be configured without being reachable. A backup record can exist while its stored files are missing. A live mirror can complete without creating a recovery point. Treating any of those as equivalent would make an automation script sound more confident than the evidence allows.

## A destination inventory with a clear limit

The 1.9 development CLI now has an opt-in, versioned `destinations --output json` result. It reads the current configuration without creating defaults or exposing destination paths or credential names. It reports whether accessibility was checked, rather than guessing that a configured destination is online.

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

The example configuration contained a path and a credential, but neither appears in the result. `accessibilityChecked: false` is deliberate: listing settings is not a network probe. The existing `--test` path remains separate until its machine-output behavior is qualified. Legacy `destinations --json` still returns its old array shape, including `[]` for an empty configuration, so a script is not silently moved to the new schema.

## A watcher should stop when protection fails

The development watcher now starts observing source changes before its first snapshot and mirror. That closes a gap where an edit during startup could be missed. A failed mirror or later watch cycle ends the session with an error instead of leaving a quiet watcher running as though protection were continuing. Cancellation still has its own exit result and drains pending work.

The AppImage build also now checks the extracted package root and launchers for usable permissions. This addresses a catalog test that could not launch the existing 1.8.9 asset under Firejail. The old published asset cannot change; the catalog will need a future release built with the fix.

## Planning the desktop path

The 1.9 desktop architecture draft maps today's Dashboard, Projects, Backups, Schedule, History, Recovery, Guide, and Settings screens to four user intents: Protect, History, Recover, and Manage. It proposes one validated route and explicit rules for preserving a selected project, filters, and unfinished work. Existing pages remain available while each replacement is reviewed for functional parity, accessibility, localization, and supported platforms. This is a review draft, not a new desktop screen.

## Where the release stands

These changes are in draft 1.9 pull requests: [updater compatibility #721](https://github.com/ATAC-Helicopter/VaultSync/pull/721), [CLI and watcher #722](https://github.com/ATAC-Helicopter/VaultSync/pull/722), [AppImage packaging #726](https://github.com/ATAC-Helicopter/VaultSync/pull/726), and [desktop route architecture #729](https://github.com/ATAC-Helicopter/VaultSync/pull/729). Windows, macOS, and Linux build/test checks pass on the first three; SonarQube Cloud still errors while looking up these pull requests. The draft release promotion is [#683](https://github.com/ATAC-Helicopter/VaultSync/pull/683). **VaultSync 1.9 has not shipped.**

Disk-image format, supported systems, independent boot, image-to-disk restore, post-restore validation, and exact installed 1.8.9 upgrade qualification remain open release gates. I would rather leave those promises open than turn a passing build into a recovery claim.

For scripts, the next useful question is: which result would you want to distinguish most clearly—configured, reachable, backed up, or verified?
