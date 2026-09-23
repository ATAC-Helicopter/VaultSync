# CLI structured output v1

Available in the 1.9 development CLI for `projects list`, `list-projects`,
`projects show`, `snapshots create`, `snapshots list`, `snapshots show`, `snapshots diff`, `backups list`,
`backups show`, `backups verify`, `backups verify-all`, `backups create`,
`recovery restore`, `mirror`/`sync`, `verify`, and legacy `history`/`diff`
with explicit `--output json`. This contract does not yet apply to watch or
other command results. Legacy `--json`
retains its existing payload and casing. Do not combine the two output options.

The inspection and verification invocations open an existing database in
read-only mode. They do not initialize the database, create its parent directory, migrate schema, or
change SQLite journal configuration. `--output text` also selects read-only
listing; listing without `--output` retains its legacy initialization behavior.
Unsupported/older schemas require an explicit supported migration, not an
implicit migration during inspection.
`backups create` requires an existing database but writes a new fully hashed
snapshot and backup record unless `--dry-run` is set. Its dry run opens the
database read-only and leaves the source and destination unchanged.
`snapshots create` also writes a new snapshot. It checks project registration
read-only before opening the store for writing in versioned mode.

## Requests

```sh
vaultsync projects list --db ./vault.db --output json
vaultsync projects list --db ./vault.db --filter alpha --preset dotnet --limit 10 --output json
vaultsync projects show "My project" --db ./vault.db --output json
vaultsync projects show --id 42 --db ./vault.db --output json
vaultsync snapshots list "My project" --db ./vault.db --limit 10 --output json
vaultsync snapshots create "My project" --db ./vault.db --output json
vaultsync snapshots show "My project" --id 42 --db ./vault.db --output json
vaultsync snapshots diff "My project" 42 41 --db ./vault.db --limit 200 --output json
vaultsync backups list "My project" --db ./vault.db --output json
vaultsync backups show "My project" --id 7 --db ./vault.db --output json
vaultsync backups verify "My project" --id 7 --db ./vault.db --output json
vaultsync backups verify-all --filter My --db ./vault.db --output json
vaultsync backups create "My project" --destination /mounted/backup --db ./vault.db --dry-run --output json
vaultsync recovery restore "My project" /safe/target --backup-id 7 --include Documents --dry-run --output json
vaultsync mirror "My project" /safe/target --db ./vault.db --dry-run --output json
vaultsync verify "My project" /safe/target --db ./vault.db --full --output json
```

`--filter` matches a case-insensitive project-name fragment. `--preset` matches
a preset name case-insensitively. Filters apply before the positive `--limit`.
Results keep repository name ordering. No match is a successful empty list.

`show` requires exactly one selector. Numeric names remain literal names;
`show 42` selects the project named `42`, while `show --id 42` selects local ID
42. IDs belong to the selected local database and must not be assumed to identify
the same project on another machine. `externalId` is exposed separately when
present; selection by external ID is not implemented in this slice.

## Envelope and process result

Normal execution writes exactly one JSON object to stdout with no progress,
markup, or log messages mixed into it. The object contains:

| Property | Meaning |
| --- | --- |
| `schemaVersion` | `1` |
| `operation` | The documented resource action, such as `backups.verify` |
| `status` | `success` or `error` |
| `data` | Success payload; null on failure |
| `error` | Null on success; failure `{code, message}` and optional `details` |
| `exitCode` | Matches the process/command result |

| Exit | Meaning for these operations |
| ---: | --- |
| 0 | Success, including an empty filtered list |
| 1 | Database inaccessible, invalid/unsupported schema, or an operational failure |
| 2 | Invalid arguments/options/selector, project not found, or live-folder verification mismatch |
| 3 | Bulk verification has failed or omitted records; inspect per-backup results |
| 130 | Backup creation or verification was cancelled before completion |

`mirror` can also return a platform transfer-tool exit code on failure. Its
error includes `toolExitCode`; inspect the destination before retrying.

Unknown options, malformed typed values, missing option values, incompatible
selectors, invalid positive limits, and conflicting output modes fail explicitly in the
versioned mode. Human `--help` requests display normal terminal help rather than
a result envelope. Other commands' exit codes and cancellation semantics have
not yet been unified under this contract.

Error codes currently include `invalid_options`, `project_not_found`,
`snapshot_history_empty`, `snapshot_not_found`, `backup_not_found`,
`backup_unavailable`, `unsupported_backup_format`, `verification_failed`,
`cancelled`, `destination_unavailable`, `source_unavailable`,
`unsafe_destination`, `insufficient_space`, `backup_failed`,
`backup_not_created`, `bulk_verification_incomplete`, `mirror_failed`, `repository_unavailable`,
and `command_failed`.
Error messages are actionable,
without embedding the underlying exception or connection string. Consumers
should use codes rather than parsing text and tolerate additional properties.
The envelope schema is [cli-result-v1.schema.json](schemas/cli-result-v1.schema.json).

## Project payloads

Project summaries contain `id`, `externalId`, `name`, `rootPath`, `preset`,
`createdUtc` (ISO 8601 UTC), and `needsRestore`. They omit encryption key references
and the full application configuration.

`projects.list` data contains `projects`, `count` (returned), `matchedCount`
(after filters, before the limit), and `totalCount` (all registered projects).
`projects.show` data contains one `project`.


## Snapshot history payload

`snapshots.create` returns `projectId`, local `snapshotId`, UTC `createdUtc`,
`fileCount`, `totalBytes`, and added/modified/deleted/unchanged counts when the
service supplies them. It indexes source state; it does not write a backup.
The compatibility `snapshot` route accepts the same opt-in v1 mode.

`snapshots.list` identifies its project with the same project-summary shape and
returns `snapshots`, `count` (after the optional positive limit), and `totalCount`.
Each snapshot contains its local `id`, ISO 8601 UTC `createdUtc`, `fileCount`, and
`totalBytes`. Snapshot IDs are local to the selected database. The list describes
source indexes and hashes; it does not claim that recoverable backup bytes exist.
An unknown project is `project_not_found` with exit 2. An empty history is a
successful empty list.

Legacy `history --json` keeps its existing array, PascalCase field names, and UTC
text dates. It retains initialization behavior unless explicit `--output` is used.


## Single-snapshot payload

`snapshots.show PROJECT --id ID` requires an explicit positive local ID and
verifies that it belongs to the named project before reading its metadata. The
result includes snapshot identity, UTC creation time, file and byte totals,
change-summary counts, optional History label/note/tags/protection/known-good
metadata, and aggregate recorded-backup, encrypted-backup, and protected-backup
record counts. It omits backup paths, destination identities, crypto descriptors,
file paths, and hashes.

Backup counts describe database records only. They do not assert that payload
bytes are currently available, readable, verified, or sufficient for recovery.
A foreign or missing ID returns `snapshot_not_found` without exposing its metadata.
The command is new in 1.9 and always opens the existing database read-only,
including its default human output.

## Snapshot diff payload

`snapshots.diff` accepts two local snapshot IDs, or defaults to the latest and
previous snapshots. Both IDs must belong to the selected project. A foreign,
missing, or non-inferable selection returns `snapshot_not_found` with exit 2
before any file paths are read. A project without snapshots returns
`snapshot_history_empty`.

The result names the older baseline as `fromSnapshotId` and newer target as
`toSnapshotId`. `paths` contains deterministic ordinal-sorted `added`, `deleted`,
`modified`, and `unchanged` arrays. The positive `--limit` bounds each array;
`summary` retains complete category and file counts, while `pathsTruncated`
explicitly reports omitted paths and `pathLimit` records the applied bound.
Legacy `diff --json` keeps its original `A`/`B`, path arrays, and summary shape,
but now also rejects cross-project IDs.

## Recorded backup payloads

`backups.create` takes a project and explicit existing destination. A dry-run
result reports `projectId`, `dryRun: true`, `estimatedFiles`, `estimatedBytes`,
null backup/snapshot IDs, and `payloadChecked: false`. A completed creation
returns the new local `backupId` and `snapshotId` with `dryRun: false`.
The estimate may use the latest indexed snapshot and can differ from the
source rescanned during creation. Creation does not assert payload integrity;
run `backups verify` afterward. The new route rejects encryption policies that
require archive format. For creation only, exit 1 also covers failed source or
destination access and backup execution; exit 2 covers an unsafe destination or
unsupported format.

`backups.list` returns a project summary, `backups`, `count`, and `totalCount`.
`backups.show` returns the project and one backup. Each backup record includes its
local ID, external ID, snapshot ID, UTC creation time, type, mode, byte total,
protected/encrypted/imported flags, and `payloadChecked: false`. Storage paths,
destination identities, and crypto descriptors are omitted. Both commands are
record inspection only; absent bytes can still have a valid record.

`backups.verify` accepts a positive project-scoped backup ID. It supports full,
unencrypted folder payloads with indexed file hashes. It rejects unsupported
formats and missing data rather than inferring that a record is healthy. A result
reports `checkedFiles`, `passedFiles`, `failedFiles`, bounded `failures`,
`failureLimit`, `failuresTruncated`, and `payloadChecked: true`. A hash mismatch,
missing/unsafe file, unreadable file, or missing hash produces
`verification_failed` with the same result under `error.details` and exit 1.
`--limit` bounds returned failures, not the number of files checked. Failure paths
come from the selected project's snapshot and may be sensitive when shared.

`backups.verify-all` checks up to 100 records by default, ordered by project
name and newest backup within each project. `--project` selects one exact name;
`--filter` matches project-name fragments; they cannot be combined. `--limit`
bounds backup records and `--failure-limit` bounds file failures per backup.
The result contains per-backup status/code/counts, `count`, `totalCount`,
`passedCount`, `failedCount`, and `resultsTruncated`. An empty selection succeeds.
Any failed record or omitted record returns `bulk_verification_incomplete` with
these results in `error.details`; exit 1 means all checked records failed, and
exit 3 means mixed outcomes or an omitted tail. A pass applies only to the
checked full, unencrypted folder backups with indexed hashes.

## Restore payload

`recovery.restore` is available for the grouped `recovery restore` and
compatibility `restore` routes. It selects the latest recorded backup by default,
or a positive project-scoped `--backup-id` or `--snapshot` ID. It reports
`projectId`, `backupId`, `snapshotId`, `dryRun`, `clean`, `selectedPaths`,
`selectedFiles`, `copied`, `deleted`, and `deletedDirectories`. Repeated
`--include` paths select files or directory subtrees relative to the backup root.
The command rejects `--include` with `--clean`; unrelated target files stay
untouched. A dry run verifies selected backup bytes and target safety without
writing the target. A live restore also verifies copied staging bytes before
replacing each target file. Legacy `--json` keeps its prior payload and casing.
The v1 error `restore_failed` reports an operational failure with exit 1; if a
live restore has already copied some files, inspect the target before retrying.

## Live mirror and folder verification

`mirror` and compatibility `sync` report `projectId`, project name, source and
destination, `dryRun`, `recordedBackup: false`, and elapsed seconds. A mirror
transfers current source content through the platform tool; it does not create a
recorded backup or prove the destination matches an indexed snapshot. A dry run
does not create the target. Versioned mode keeps tool diagnostics in the private
CLI log and writes one result envelope.

`verify` compares a supplied folder with the latest indexed snapshot, not with
a recorded backup. It accepts `--full` or a 1–100 `--percent` sample and returns
checked/passed counts, failure count, up to 100 relative paths and reasons,
`failuresTruncated`, and elapsed seconds. On a mismatch these counts appear in
`error.details` with exit 2. Hashes and underlying read-error text are omitted
from v1 output. Legacy `verify --json` retains its existing payload.

Example selection failure:

```json
{
  "schemaVersion": 1,
  "operation": "projects.show",
  "status": "error",
  "data": null,
  "error": {
    "code": "project_not_found",
    "message": "No registered project matches the supplied selector."
  },
  "exitCode": 2
}
```
