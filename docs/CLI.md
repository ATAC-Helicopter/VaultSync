# VaultSync CLI handbook

VaultSync CLI provides terminal access to project registration, source-state
snapshots, live mirroring, verification, recorded-folder restore, diagnostics,
and configuration. The 1.9 command rework is in development; compatibility routes
remain available throughout the 1.9 family.

- Website: https://fglabs.dev/vaultsync
- Source: https://github.com/ATAC-Helicopter/VaultSync
- Releases: https://github.com/ATAC-Helicopter/VaultSync/releases/latest
- Issues: https://github.com/ATAC-Helicopter/VaultSync/issues
- Focused setup, inspection, mirror, restore, automation, and migration guides:
  [CLI_TASK_GUIDES.md](CLI_TASK_GUIDES.md)
- Exhaustive generated command help: [CLI_COMMAND_REFERENCE.md](CLI_COMMAND_REFERENCE.md)

Run `vaultsync` with no arguments for the introductory screen, `vaultsync docs`
for the compact documentation menu, `vaultsync docs --full` for this bundled
handbook, task guides, and exhaustive generated command reference, `vaultsync
docs --task inspect` for one workflow, `vaultsync docs --open` to open the online
copy, or `vaultsync COMMAND --help` for exact options.

Shell completion suggests command names, options, task-guide topics after
`docs --task`, and supported values after `--output`. It does not inspect the
database or suggest project names and IDs.

## Install and identify the build

VaultSync CLI targets .NET 10 and is packaged as the `vaultsync.cli` .NET tool.
For a local 1.9 development package from a checkout:

```sh
dotnet pack src/VaultSync.CLI/VaultSync.CLI.csproj -c Release
dotnet tool install --global vaultsync.cli --version 1.9.0 \
  --add-source src/VaultSync.CLI/bin/ToolPackages
```

If the tool is already installed, use `dotnet tool update --global` with the
same package, version, and source arguments. A global .NET tool is placed in
`$HOME/.dotnet/tools` on macOS/Linux and `%USERPROFILE%\.dotnet\tools` on Windows.
If `vaultsync` is not found after installation, add that directory to your shell's
`PATH` and open a new terminal. For the current session:

```sh
# Bash or Zsh on macOS/Linux
export PATH="$HOME/.dotnet/tools:$PATH"
command -v vaultsync
```

```powershell
# PowerShell on Windows
$env:PATH = (Join-Path $env:USERPROFILE '.dotnet\tools') + [IO.Path]::PathSeparator + $env:PATH
Get-Command vaultsync
```

Persist the path in your shell profile or user environment. Scheduled jobs should
use the absolute path to the installed tool because their environment may differ
from an interactive terminal. Inspect the exact executable before automation:

```sh
vaultsync --version
vaultsync --version --json
```

The JSON identity includes version, channel, source commit, runtime, package kind,
official-build state, and signature state. Do not infer release provenance from a
filename alone.

## Shell completion

The CLI prints generated command and option completion for Bash, Zsh, and
PowerShell. These scripts cover grouped and compatibility routes and are embedded
in the packaged tool. They suggest command names and option names; they do not
read the database, enumerate local paths, or suggest project names.

```sh
# Bash: load for this terminal, or save the output and source it from ~/.bashrc.
source <(vaultsync completion bash)

# Zsh: install in a directory on fpath before compinit runs.
mkdir -p "$HOME/.zsh/completions"
vaultsync completion zsh > "$HOME/.zsh/completions/_vaultsync"
# Put this line before `autoload -Uz compinit; compinit` in ~/.zshrc:
# fpath=("$HOME/.zsh/completions" $fpath)
```

```powershell
# PowerShell: load for this session, or put the line in $PROFILE.
vaultsync completion powershell | Out-String | Invoke-Expression
```

Regenerate the installed script after updating VaultSync. `vaultsync completion
SHELL` prints plain script text, so it can be redirected to a file without a
banner. Use `vaultsync completion --help` for the accepted shell names.

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
vaultsync projects add "My project" ./src --preset dotnet --db ./vault.db
vaultsync projects list --db ./vault.db --output json
vaultsync snapshots create "My project" --db ./vault.db --quiet
vaultsync snapshots list "My project" --db ./vault.db --output json
vaultsync mirror "My project" /absolute/path/to/mirror --db ./vault.db --dry-run
vaultsync doctor --db ./vault.db
```

These commands use an explicit database; they do not change the shared configured
database. `projects add` creates a new store when the path is absent. Run a dry
run before a new mirror, restore, or pruning plan. Review the selected project,
destination, and snapshot IDs before removing or restoring anything. For a full
isolated walkthrough with IDs discovered from JSON, run `vaultsync docs --task
inspect` or read the [task guides](CLI_TASK_GUIDES.md).

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
                           [--output text|json]
vaultsync snapshots list NAME [--limit COUNT] [--db PATH]
                         [--output text|json]
vaultsync snapshots show PROJECT --id ID [--db PATH] [--output text|json]
vaultsync snapshots diff NAME [A] [B] [--limit COUNT] [--db PATH]
                         [--output text|json]
vaultsync snapshots prune NAME (--keep-last N | --before YYYY-MM-DD)
                          [--dry-run] [--db PATH] [--quiet] [--json]
```

`create` supports a versioned result with the persisted snapshot ID and counts.
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

### Recorded backups

```text
vaultsync backups create PROJECT --destination PATH [--db PATH] [--dry-run]
                         [--quiet] [--output text|json]
vaultsync backups list PROJECT [--limit COUNT] [--db PATH] [--output text|json]
vaultsync backups show PROJECT --id ID [--db PATH] [--output text|json]
vaultsync backups verify PROJECT --id ID [--limit COUNT] [--db PATH]
                         [--output text|json]
vaultsync backups verify-all [--project NAME | --filter TEXT] [--limit COUNT]
                             [--failure-limit COUNT] [--db PATH]
                             [--output text|json]
```

`create` writes a new full, unencrypted folder backup through the shared backup
service. It requires an existing destination outside the project source, creates
a fresh fully hashed snapshot, and records the backup in the selected database.
`--dry-run` checks the source and destination and reports an estimated file/byte
count without writing either. The estimate can come from the latest snapshot;
the actual run scans again. An encryption policy that requires an archive is
rejected by this route. `--quiet` suppresses successful text output; `--output
json` writes one versioned result and routes service diagnostics to the private
CLI log.

`list` and `show` inspect project-scoped backup records without exposing storage
paths or crypto descriptors. Their `payloadChecked: false` field matters: a record
does not prove that bytes are present. `verify` hashes indexed files in a full,
unencrypted folder backup and reports mismatches. It rejects archive, encrypted,
and incremental formats explicitly; it does not test whether a restore target is
safe or whether a portable repository can be recovered on a new machine.
`verify-all` checks recorded backups across projects or an exact `--project`;
`--filter` matches project-name fragments. Each backup gets an outcome. Failed
or omitted work returns nonzero, so a bounded run cannot claim that every backup
passed. Listing and verification open the selected database read-only.

### Recovery

```text
vaultsync recovery restore NAME DESTINATION [--backup-id ID | --snapshot ID]
                           [--include RELATIVE_PATH ...] [--dry-run] [--clean]
                           [--db PATH] [--output text|json]
```

The current CLI restore supports recorded folder backups. Archive and encrypted
recovery are rejected until qualified handlers are available. It never restores
from the live source merely because a snapshot exists. Use command help for
snapshot selection, dry-run, cleanup, and empty-directory options.
`--backup-id` selects the exact project-scoped backup record. Repeat `--include`
to restore specific files or directory subtrees; unrelated target files remain.
Selective restore rejects `--clean` because cleanup outside the selected paths
would be surprising. The versioned JSON result reports the selected backup,
snapshot, file count, and copy/delete counts. Legacy `--json` keeps its old shape.

### Live mirroring and verification

```text
vaultsync mirror NAME DESTINATION [--dry-run] [--db PATH] [--quiet] [--output text|json]
vaultsync verify NAME FROM [--percent N | --full] [--db PATH] [--quiet] [--json]
                       [--output text|json]
vaultsync watch NAME [--db PATH] [--dest PATH] [--sync] [--verify]
                    [--debounce-ms N] [--dry-run] [--quiet]
```

`mirror` is the explicitly named live-transfer command. It uses the same handler
as compatibility route `sync`; neither route creates a recorded backup. Verification
checks a supplied folder against the latest indexed snapshot. Watch creates a
startup snapshot and then debounces changes; `--verify` implies sync and requires
`--dest`. Cancellation returns 130 after filesystem events stop and queued/active
work drains. Watcher JSON events and unified cycle-failure reporting remain planned.
Both `mirror`/`sync` and `verify` offer v1 `--output json`. Verification reports
bounded mismatches against the latest snapshot; a live mirror remains distinct
from a recorded backup. Legacy `verify --json` is unchanged.

### Diagnostics, destinations, presets, and configuration

```text
vaultsync doctor [--db PATH] [--check-dest PATH] [--quiet] [--output text|json]
vaultsync destinations [--test] [--json]
vaultsync presets list
vaultsync presets show NAME
vaultsync config show
vaultsync config path
vaultsync config set-db PATH
vaultsync self-test [--db PATH] [--quiet]
vaultsync version [--json]
vaultsync docs [--full | --open | --url | --task TOPIC]
vaultsync completion bash|zsh|powershell
```

`config show` is a full configuration view, not a redacted support export. Review
its contents before sharing it. Destination tests may access mounts or credential
providers. Doctor uses isolated probes and should not overwrite existing files.
`doctor --output json` emits check codes and counts without paths or raw errors;
it requires an existing database rather than initializing one.

## Structured output v1

Explicit `--output json` is currently supported by:

- `projects list` and compatibility `list-projects`;
- `projects show`;
- `snapshots list` and compatibility `history`;
- `snapshots create` and compatibility `snapshot`;
- `snapshots show`;
- `snapshots diff` and compatibility `diff`;
- `backups create`, `backups list`, `backups show`, `backups verify`, and `backups verify-all`;
- `recovery restore` and compatibility `restore`.
- `mirror` and compatibility `sync`, plus `verify`.
- `doctor` diagnostics.

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
input, an absent selector, or a live-folder verification mismatch, and exit 130 is cancellation where that contract is
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

The [task guides](CLI_TASK_GUIDES.md) include cron, systemd user timer, and
Windows Task Scheduler examples, plus legacy route and JSON migration steps.
Use `vaultsync docs --task automate` or `vaultsync docs --task migrate` to print
only the relevant guide in a terminal.

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
parity, uniform JSON across all commands, watcher event streams,
project/path-aware completion, portable emergency recovery, and final cross-platform
automation qualification remain planned under their owning 1.9 issues. Disk imaging and bootable
recovery commands will appear only with qualified recovery services and support
contracts.

`mirror`, `verify`, and `recovery restore` read an existing selected database
without initializing or migrating it. A missing `--db` path returns exit 1 with
a short stderr error and does not create the database or its parent directory.

For implementation state and compatibility decisions, see [CLI_REWORK.md](CLI_REWORK.md).
For the release scope and gates, see [RELEASE_1.9.0.md](RELEASE_1.9.0.md).
