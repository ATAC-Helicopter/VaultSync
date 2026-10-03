# Development CLI workflows (1.9)

These commands require a **1.9 development CLI**. Current stable is **1.8.9**;
the examples are not a promise that the stable executable has these routes or
JSON contracts. Use an explicit test database and disposable source/destination
folders when trying a development build. See [development status](Development-Status.md).

## Choose the right operation

| Task | Operation | What it establishes |
| --- | --- | --- |
| Inspect registrations/history | `projects list/show`, `snapshots list/show/diff` | Read-only local index information |
| Index current source state | `snapshots create` | File indexes/hashes; no recoverable backup bytes |
| Transfer live source | `mirror` (legacy `sync`) | A live mirror; no recorded recovery point |
| Record a folder backup | `backups create` | Stored bytes and a backup record through the shared service |
| Check stored bytes | `backups verify`, `backups verify-all` | Indexed-hash checks of supported recorded payloads |
| Recover selected paths | `recovery restore` (legacy `restore`) | Restore from recorded bytes, not the current source |
| Preview a watcher | `watch --dry-run` | A finite plan; no watcher, snapshots, transfers, or verification |

## Inspect without changing the repository

```sh
vaultsync projects list --db ./vault-dev.db --output json
vaultsync projects show "Demo" --db ./vault-dev.db --output json
vaultsync snapshots list "Demo" --db ./vault-dev.db --limit 10 --output json
vaultsync backups list "Demo" --db ./vault-dev.db --output json
```

Inspect the returned local IDs before selecting snapshots or backups. Numeric
project names remain literal names; `projects show --id ID` selects an explicit
local ID. Indexes and backup records do not prove payload availability.

## Record, verify, and recover

Prepare an existing source folder containing `Documents`, an existing mounted
backup destination, and a separate restore target. Review the preview first:

```sh
vaultsync projects add "Demo" ./source --db ./vault-dev.db --quiet
vaultsync backups create "Demo" --destination ./backup-destination --db ./vault-dev.db --dry-run --output json
vaultsync backups create "Demo" --destination ./backup-destination --db ./vault-dev.db --output json > backup-result.json
backup_id=$(jq -r '.data.backupId' backup-result.json)
vaultsync backups verify "Demo" --id "$backup_id" --db ./vault-dev.db --output json
vaultsync recovery restore "Demo" ./restore-target --backup-id "$backup_id" --include Documents --db ./vault-dev.db --dry-run --output json
vaultsync recovery restore "Demo" ./restore-target --backup-id "$backup_id" --include Documents --db ./vault-dev.db --output json
```

Check each command's exit code before continuing. This shell example uses `jq`.
Creation and CLI verification currently support full, unencrypted folder backups.
Selective restore rejects `--clean` and preserves unrelated target files. It
checks staged bytes before replacing each target file; corruption fails explicitly.
Archive/encrypted CLI parity and independent database-free recovery remain pending.

## Preview a watcher, then start it deliberately

```sh
vaultsync watch "Demo" --db ./vault-dev.db --verify --dest ./live-mirror --dry-run --output json
vaultsync watch "Demo" --db ./vault-dev.db --verify --dest ./live-mirror --quiet
```

The preview returns operation `watch.plan` and exits. It reads an existing
database/configuration without initializing, migrating, or saving defaults.
It checks project registration and source-directory existence. It does not probe
the destination, transfer tool, or payloads; its evidence flags are false.
Earlier 1.9 development dry runs created snapshots and stayed in listening mode;
scripts must now remove `--dry-run` to start the live session. Live JSON event
streaming is not implemented. `--verify` implies mirroring and requires `--dest`.
Ctrl-C stops the live session with 130 after queued and active work drains.
Failed cycles terminate the session instead of silently continuing.

## Automation and compatibility

`--output json` selects the v1 result envelope on documented commands; legacy
`--json` retains its previous shape. Do not combine them. stdout contains the
machine result; operational diagnostics stay on stderr/private logs. Quiet mode
does not authorize removal: unattended `projects remove` requires `--yes`.
Read the exact command contract before assigning exit meanings across workflows;
bulk incomplete results differ from single-operation results.

See the [handbook](../CLI.md), [task guides](../CLI_TASK_GUIDES.md),
[output contract](../CLI_OUTPUT.md), [command reference](../CLI_COMMAND_REFERENCE.md),
and [audit/migration decisions](../CLI_REWORK.md). `vaultsync docs --task` prints
the bundled guides offline; `vaultsync completion bash|zsh|powershell` prints
generated shell completion. Supported-platform completion qualification remains open.
