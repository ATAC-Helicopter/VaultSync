# VaultSync CLI handbook

VaultSync CLI provides terminal access to project registration, source-state
snapshots, live mirroring, verification, recorded-folder restore, diagnostics,
and configuration. The 1.9 command rework is in development; compatibility routes
remain available throughout the 1.9 family.

- Website: https://fglabs.dev/vaultsync
- Source: https://github.com/ATAC-Helicopter/VaultSync
- Releases: https://github.com/ATAC-Helicopter/VaultSync/releases/latest
- Issues: https://github.com/ATAC-Helicopter/VaultSync/issues
- Exhaustive generated command help: [CLI_COMMAND_REFERENCE.md](CLI_COMMAND_REFERENCE.md)

Run `vaultsync` with no arguments for the introductory screen, `vaultsync docs`
for the compact documentation menu, `vaultsync docs --full` for this bundled
handbook plus the exhaustive generated command reference, `vaultsync docs --open` to open the online copy, or
`vaultsync COMMAND --help` for exact command options.

## Install and identify the build

VaultSync CLI targets .NET 10 and is packaged as the `vaultsync.cli` .NET tool.
Use the package or release instructions for the version and platform you intend
to run. Inspect the exact executable before automation:

```sh
vaultsync --version
vaultsync --version --json
```

The JSON identity includes version, channel, source commit, runtime, package kind,
official-build state, and signature state. Do not infer release provenance from a
filename alone.

## Concepts that affect every command

A **project** is a registered source folder and preset. A **snapshot** is a local
index of source paths, sizes, timestamps, and hashes. A snapshot does not store a
second copy of file bytes. A **recorded backup** references payload written by a
backup workflow. `mirror`/`sync` copies the live source to a destination and does
not create a recorded recovery point.

A successful snapshot or mirror is therefore not proof that recoverable backup
bytes exist. Inspect backup records and verify stored data before making recovery
claims. Commands that currently lack those services fail or describe their scope
rather than representing a live mirror as a backup.

Local numeric project and snapshot IDs belong to the selected database. They are
not portable identifiers across machines. Commands that accept snapshot IDs verify
that they belong to the selected project before reading their records.

## Configuration and database selection

Commands with `--db PATH` use this precedence:

1. Explicit `--db PATH`.
2. The shared application configuration database path.
3. The legacy `~/.vaultsync/config.json` `Database` value.
4. The shared-store default.

Use `vaultsync config path` to inspect the selected configured path and
`vaultsync config set-db PATH` to update it. `vaultsync init --db PATH` explicitly
initializes a store. The versioned inspection commands open an existing store
read-only and do not create parent directories, initialize tables, migrate schema,
or change journal configuration.

Paths remain literal except documented leading-`~` expansion. Quote project names
and paths containing whitespace.

## Quick start

```sh
vaultsync init
vaultsync projects add "My project" ./src --preset dotnet
vaultsync projects list
vaultsync snapshots create "My project"
vaultsync snapshots list "My project"
vaultsync snapshots show "My project" --id 42
vaultsync snapshots diff "My project" 42 41
vaultsync mirror "My project" /Volumes/Backup --dry-run
vaultsync doctor
```

Run a dry run before a new mirror, restore, or pruning plan. Review the selected
project, destination, and snapshot IDs before removing or restoring anything.

## Resource/action commands

### Projects

```text
vaultsync projects add NAME PATH [--preset PRESET] [--db PATH] [--quiet]
vaultsync projects list [--filter TEXT] [--preset PRESET] [--limit COUNT]
                        [--db PATH] [--output text|json]
vaultsync projects show [NAME | --id ID] [--db PATH] [--output text|json]
vaultsync projects discover [--root PATH] [--json]
vaultsync projects set-path NAME NEW_PATH [--db PATH] [--quiet]
vaultsync projects remove NAME [--db PATH] [--yes] [--quiet]
```

`projects discover` lists immediate candidate folders. It does not register them
or claim snapshot/backup coverage. Quiet or redirected project removal requires
explicit `--yes`; rejection occurs before configuration or database changes.
Project removal deletes the local registration and index history, while source and
stored backup files remain.

### Snapshots

```text
vaultsync snapshots create NAME [--full-hash] [--db PATH] [--quiet]
vaultsync snapshots list NAME [--limit COUNT] [--db PATH]
                         [--output text|json]
vaultsync snapshots show PROJECT --id ID [--db PATH] [--output text|json]
vaultsync snapshots diff NAME [A] [B] [--limit COUNT] [--db PATH]
                         [--output text|json]
vaultsync snapshots prune NAME (--keep-last N | --before YYYY-MM-DD)
                          [--dry-run] [--db PATH] [--quiet] [--json]
```

`list`, `show`, and `diff` support read-only v1 output. `show` reports snapshot
identity, file/byte and change totals, optional History metadata, and aggregate
recorded/encrypted/protected backup-record counts. It omits paths, destinations,
crypto descriptors, file entries, and hashes. Backup-record counts do not prove
that payload bytes are online, readable, verified, or sufficient for recovery.

Diff defaults to latest versus previous. Explicit IDs and inferred IDs must belong
to the named project. `--limit` bounds each returned path category in structured
results; complete counts and `pathsTruncated` remain available.

Pruning affects eligible local snapshot indexes. Recorded-backup snapshots and
History-protected snapshots are retained. Use `--dry-run` before applying a plan.

### Recovery

```text
vaultsync recovery restore NAME DESTINATION [options]
```

The current CLI restore supports recorded folder backups. Archive and encrypted
recovery are rejected until qualified handlers are available. It never restores
from the live source merely because a snapshot exists. Use command help for
snapshot selection, dry-run, cleanup, and empty-directory options.

### Live mirroring and verification

```text
vaultsync mirror NAME DESTINATION [--dry-run] [--db PATH] [--quiet]
vaultsync verify NAME FROM [--percent N | --full] [--db PATH] [--quiet] [--json]
vaultsync watch NAME [--db PATH] [--dest PATH] [--sync] [--verify]
                    [--debounce-ms N] [--dry-run] [--quiet]
```

`mirror` is the explicitly named live-transfer command. It uses the same handler
as compatibility route `sync`; neither route creates a recorded backup. Verification
checks a supplied folder against the latest indexed snapshot. Watch creates a
startup snapshot and then debounces changes; `--verify` implies sync and requires
`--dest`. Cancellation returns 130 after filesystem events stop and queued/active
work drains. Watcher JSON events and unified cycle-failure reporting remain planned.

### Diagnostics, destinations, presets, and configuration

```text
vaultsync doctor [--db PATH] [--check-dest PATH] [--quiet]
vaultsync destinations [--test] [--json]
vaultsync presets list
vaultsync presets show NAME
vaultsync config show
vaultsync config path
vaultsync config set-db PATH
vaultsync self-test [--db PATH] [--quiet]
vaultsync version [--json]
vaultsync docs [--full | --open | --url]
```

`config show` is a full configuration view, not a redacted support export. Review
its contents before sharing it. Destination tests may access mounts or credential
providers. Doctor uses isolated probes and should not overwrite existing files.

## Structured output v1

Explicit `--output json` is currently supported by:

- `projects list` and compatibility `list-projects`;
- `projects show`;
- `snapshots list` and compatibility `history`;
- `snapshots show`;
- `snapshots diff` and compatibility `diff`.

Each invocation writes one JSON object to stdout:

```json
{
  "schemaVersion": 1,
  "operation": "snapshots.show",
  "status": "success",
  "data": {},
  "error": null,
  "exitCode": 0
}
```

Exit 0 is success, exit 1 is an operational/repository failure, exit 2 is invalid
input or an absent selector, and exit 130 is cancellation where that contract is
implemented. Errors use stable codes. Consumers should ignore additional object
properties and must not parse human text. `--output json` is distinct from legacy
`--json`; do not combine them. The exact implemented envelope and payload fields
are documented in [CLI_OUTPUT.md](CLI_OUTPUT.md).

## Compatibility routes

These legacy commands remain during the 1.9 compatibility window:

| Compatibility route | Resource/action route |
| --- | --- |
| `add-project` | `projects add` |
| `list-projects` | `projects list` |
| `remove-project` | `projects remove` |
| `set-path` / `update-path` | `projects set-path` |
| `snapshot` | `snapshots create` |
| `history` | `snapshots list` |
| `diff` | `snapshots diff` |
| `prune` | `snapshots prune` |
| `restore` | `recovery restore` |
| `sync` | `mirror` |

Legacy `--json` payloads retain their existing casing and shapes. Necessary safety
fixes, such as explicit unattended-removal confirmation and same-project diff IDs,
apply to both old and grouped routes.

## Automation guidance

- Pin the VaultSync version and inspect `--version --json` in scheduled jobs.
- Pass `--db` explicitly when the job must use a specific store.
- Use `--output json` only on operations documented for the v1 contract.
- Redirect and parse stdout; keep stderr for diagnostics.
- Treat every nonzero exit as incomplete work. Handle 130 as cancellation.
- Pass `--yes` only after reviewing a removal target; `--quiet` is not consent.
- Use dry runs for mirror, restore, and prune plans.
- Do not treat snapshot IDs, backup records, or successful mirrors as recovery proof.
- Never publish full configuration, local paths, user notes, or logs without review.

## Current 1.9 development limits

The CLI rework is active. Bulk project operations, complete recorded-backup command
parity, selective restore, uniform JSON across all commands, watcher event streams,
completion scripts, portable emergency recovery, and final cross-platform automation
qualification remain planned under their owning 1.9 issues. Disk imaging and bootable
recovery commands will appear only with qualified recovery services and support
contracts.

For implementation state and compatibility decisions, see [CLI_REWORK.md](CLI_REWORK.md).
For the release scope and gates, see [RELEASE_1.9.0.md](RELEASE_1.9.0.md).
