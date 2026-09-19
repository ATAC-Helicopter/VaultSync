# CLI structured output v1

Available in the 1.9 development CLI for `projects list`, `list-projects`,
`projects show`, `snapshots list`, and legacy `history` with explicit
`--output json`. This contract does not yet apply to snapshot creation/diff,
mirror, restore, watch, or other command results. Legacy `--json`
retains its existing payload and casing. Do not combine the two output options.

These inspection invocations open an existing database in read-only mode. They
do not initialize the database, create its parent directory, migrate schema, or
change SQLite journal configuration. `--output text` also selects read-only
listing; listing without `--output` retains its legacy initialization behavior.
Unsupported/older schemas require an explicit supported migration, not an
implicit migration during inspection.

## Requests

```sh
vaultsync projects list --db ./vault.db --output json
vaultsync projects list --db ./vault.db --filter alpha --preset dotnet --limit 10 --output json
vaultsync projects show "My project" --db ./vault.db --output json
vaultsync projects show --id 42 --db ./vault.db --output json
vaultsync snapshots list "My project" --db ./vault.db --limit 10 --output json
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
| `operation` | `projects.list`, `projects.show`, or `snapshots.list` |
| `status` | `success` or `error` |
| `data` | Success payload; null on failure |
| `error` | Null on success; failure `{code, message}` |
| `exitCode` | Matches the process/command result |

| Exit | Meaning for these operations |
| ---: | --- |
| 0 | Success, including an empty filtered list |
| 1 | Database inaccessible, invalid/unsupported schema, or an operational failure |
| 2 | Invalid arguments/options/selector or project not found |

Unknown options, malformed typed values, missing option values, incompatible
selectors, invalid positive limits, and conflicting output modes fail explicitly in the
versioned mode. Human `--help` requests display normal terminal help rather than
a result envelope. Other commands' exit codes and cancellation semantics have
not yet been unified under this contract.

Error codes currently include `invalid_options`, `project_not_found`,
`repository_unavailable`, and `command_failed`. Error messages are actionable,
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

`snapshots.list` identifies its project with the same project-summary shape and
returns `snapshots`, `count` (after the optional positive limit), and `totalCount`.
Each snapshot contains its local `id`, ISO 8601 UTC `createdUtc`, `fileCount`, and
`totalBytes`. Snapshot IDs are local to the selected database. The list describes
source indexes and hashes; it does not claim that recoverable backup bytes exist.
An unknown project is `project_not_found` with exit 2. An empty history is a
successful empty list.

Legacy `history --json` keeps its existing array, PascalCase field names, and UTC
text dates. It retains initialization behavior unless explicit `--output` is used.

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
