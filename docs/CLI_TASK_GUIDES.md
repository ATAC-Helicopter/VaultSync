# VaultSync CLI task guides

These focused guides are printed individually with `vaultsync docs --task TOPIC`.
Run `vaultsync docs --full` for these guides, the practical handbook, and the
generated reference for every registered command. The 1.9 CLI is in development.

## setup

**Goal:** select the exact CLI build and database without changing shared settings.

```sh
vaultsync --version --json
vaultsync docs --task inspect
vaultsync projects list --db /absolute/path/to/existing-vault.db --output json
```

The last command opens an existing database read-only. An absent database reports
an error; it does not initialize one. For a new disposable database, use the
`inspect` task below. `vaultsync init --db PATH` writes the shared application
configuration, so it is intentionally absent from disposable examples. The
[handbook](CLI.md) covers package installation, PATH, and shell completion.

## inspect

**Goal:** discover project and snapshot IDs, compare indexed changes, and see
exactly what a snapshot proves. This Bash/Zsh example requires the installed
1.9 CLI and Python 3. It creates an isolated source and database, then removes
that temporary workspace when the shell exits:

```sh
scratch=$(mktemp -d)
trap 'test -n "$scratch" && rm -rf -- "$scratch"' EXIT
mkdir -p "$scratch/source"
printf 'first\n' > "$scratch/source/state.txt"
db="$scratch/vault.db"
vaultsync projects add 'Guide Sample' "$scratch/source" --db "$db" --quiet
vaultsync snapshots create 'Guide Sample' --db "$db" --quiet
printf 'second\n' > "$scratch/source/state.txt"
vaultsync snapshots create 'Guide Sample' --db "$db" --quiet
project_id=$(vaultsync projects list --db "$db" --output json |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["projects"][0]["id"])')
snapshot_id=$(vaultsync snapshots list 'Guide Sample' --db "$db" --output json |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["snapshots"][0]["id"])')
vaultsync projects show --id "$project_id" --db "$db" --output json
vaultsync snapshots show 'Guide Sample' --id "$snapshot_id" --db "$db" --output json
vaultsync snapshots diff 'Guide Sample' --db "$db" --output json
```

The IDs are local to this database. The project list and snapshot list are
read-only v1 JSON; the diff compares the latest two indexed source states. A
snapshot stores hashes and metadata, not recoverable backup bytes. The output
contains local paths, so review it before sharing.

For an existing project with recorded backups, inspect the records and verify a
supported folder payload before restoring:

```sh
vaultsync backups list 'Photos' --db /absolute/path/to/vault.db --output json
vaultsync backups show 'Photos' --id 7 --db /absolute/path/to/vault.db --output json
vaultsync backups verify 'Photos' --id 7 --db /absolute/path/to/vault.db --output json
```

Replace `7` with an ID from the list. Listing and showing records do not check
payload bytes. Verification supports full, unencrypted folder backups with indexed
hashes; it reports missing or changed files and rejects other formats explicitly.

## mirror

**Goal:** preview a live source transfer into a separate destination. Replace
the paths and project name with an existing registration:

```sh
vaultsync projects show 'Photos' --db /absolute/path/to/vault.db --output json
vaultsync mirror 'Photos' /absolute/path/to/mirror --db /absolute/path/to/vault.db --dry-run
# Only after reviewing the source and destination:
vaultsync mirror 'Photos' /absolute/path/to/mirror --db /absolute/path/to/vault.db
vaultsync verify 'Photos' /absolute/path/to/mirror --db /absolute/path/to/vault.db --full
```

`mirror` uses the platform transfer tool (rsync or robocopy). Its dry run
previews transfer work; the actual invocation changes the destination. `verify`
checks the supplied folder against the latest indexed snapshot, so create a
fresh snapshot before relying on that comparison. Neither a mirror nor a
successful verification creates a recorded recovery point or verifies unrelated
backup storage.

## restore

**Goal:** preview a restore from a recorded folder backup before writing to a
separate target. Replace names and absolute paths with your own. A snapshot or
mirror alone cannot satisfy this workflow; a supported recorded backup must
already exist.

```sh
vaultsync snapshots list 'Photos' --db /absolute/path/to/vault.db --output json
vaultsync snapshots show 'Photos' --id 123 --db /absolute/path/to/vault.db --output json
vaultsync backups list 'Photos' --db /absolute/path/to/vault.db --output json
vaultsync backups verify 'Photos' --id 7 --db /absolute/path/to/vault.db --output json
vaultsync recovery restore 'Photos' /absolute/path/to/restore-preview \
  --snapshot 123 --db /absolute/path/to/vault.db --dry-run --json
# After checking the selected backup, target, and dry-run result:
vaultsync recovery restore 'Photos' /absolute/path/to/restore-preview \
  --snapshot 123 --db /absolute/path/to/vault.db --json
```

Replace `123` with an ID from this project's snapshot list that has a recorded
folder backup. The CLI rejects missing, archive, and encrypted backup data. A
dry run does not write restored files; the second invocation does. `--clean`
also removes extra target files and should only be used after its own dry run.
Current CLI restore uses the selected local database and cannot yet recover a
portable repository on a clean machine.

## automate

**Goal:** schedule a bounded check without relying on an interactive shell.
Pin the installed CLI path, an existing database, and a known destination.
The examples below verify a folder against the latest indexed snapshot; they
do not create recorded backups. Run the command manually once and check its
exit status before scheduling it.

For cron on macOS/Linux, write a private executable script using your actual
paths, then schedule that script. The task environment may not contain your
interactive `PATH` or a mounted destination:

```sh
#!/usr/bin/env bash
set -euo pipefail
"$HOME/.dotnet/tools/vaultsync" verify 'Photos' /mounted/backup/Photos \
  --db "$HOME/.local/share/VaultSync/vault.db" --full --quiet
```

```cron
15 3 * * * /absolute/path/to/verify-vaultsync.sh
```

For a systemd user timer on Linux, replace the paths and write these files under
`~/.config/systemd/user/`:

```ini
# vaultsync-verify.service
[Unit]
Description=Verify VaultSync Photos mirror
[Service]
Type=oneshot
ExecStart=%h/.dotnet/tools/vaultsync verify Photos /mounted/backup/Photos --db %h/.local/share/VaultSync/vault.db --full --quiet
```

```ini
# vaultsync-verify.timer
[Unit]
Description=Daily VaultSync mirror verification
[Timer]
OnCalendar=*-*-* 03:15:00
Persistent=true
[Install]
WantedBy=timers.target
```

Enable it with `systemctl --user daemon-reload` followed by
`systemctl --user enable --now vaultsync-verify.timer`. Inspect the service
status and journal for failures. A user timer requires a
running user manager; use your platform's documented mechanism if it must run
while logged out.

For Windows Task Scheduler, save this PowerShell script and create a task that
runs `powershell.exe -NoProfile -File C:\Scripts\Verify-VaultSync.ps1` under an account
that can access the destination:

```powershell
$vaultsync = Join-Path $env:USERPROFILE '.dotnet\tools\vaultsync.exe'
& $vaultsync verify 'Photos' 'D:\Backups\Photos' `
  --db 'C:\Users\Alice\AppData\Local\VaultSync\vault.db' --full --quiet
exit $LASTEXITCODE
```

Replace the sample database path; use `vaultsync config path` to discover the
actual store. Scheduler logs, environment, network mounts, and credentials
need platform-specific qualification. Treat any nonzero result as incomplete
verification and do not turn a snapshot or mirror success into a recovery claim.

## migrate

**Goal:** move scripts to grouped routes without changing legacy JSON parsing by
accident. Compatibility routes remain available throughout the 1.9 family.

| Existing route | Grouped or clearer route |
| --- | --- |
| `add-project` | `projects add` |
| `list-projects` | `projects list` |
| `snapshot` | `snapshots create` |
| `history` | `snapshots list` |
| `diff` | `snapshots diff` |
| `prune` | `snapshots prune` |
| `sync` | `mirror` (live transfer, no recorded backup) |
| `restore` | `recovery restore` (recorded folder backup) |

Keep `--json` while a script depends on the old payload shape and casing.
Migrate read-only project/snapshot inspection to explicit `--output json` one
command at a time; it returns a versioned `schemaVersion`, `operation`,
`status`, `data`, `error`, and `exitCode` envelope. Never combine `--json` and
`--output`. Snapshot IDs must belong to the selected project. Quiet or
redirected `projects remove` and `remove-project` now require `--yes` after the
target is reviewed. `--quiet` is output control, not consent.

Handle exit 0 as success, 1 as an operational/repository failure, 2 as invalid
input for the documented v1 inspections, and 130 as cancellation where the
watch contract implements it. Other commands still have legacy result rules;
check their exact help and do not assume uniform exit codes yet.
