# 1.9 CLI command and behavior audit

Implementation started 2026-09-18 under VS-1972, VS-1973, and VS-1974.
[ROADMAP.md](../ROADMAP.md) owns scope and delivery state. This document records
code-grounded inventory and initial design decisions; it is not a second roadmap.
The first implementation slice is in the 1.9.0 development branch, not 1.8.9 stable.
The full audit contract remains under review/integration in the draft release PR.

## Command inventory and decisions

The inventory follows `src/VaultSync.CLI/Program.cs` and the command handlers.
New grouped routes reuse the same handlers rather than forwarding argument strings.

| Existing route | Decision | 1.9 direction and current implementation |
| --- | --- | --- |
| `add-project` | Keep compatibility route; add resource route | `projects add` implemented with identical source/preset/DB handling |
| `list-projects` | Keep compatibility route; extend inspection later | `projects list` implemented; filters/limits and explicit versioned `--output json`; current `--json` retained |
| `remove-project` | Keep route; change unsafe confirmation behavior | `projects remove` implemented; both require `--yes` with quiet/redirected input |
| `set-path` | Keep compatibility route | `projects set-path` implemented; validates an existing source folder |
| `update-path` | Keep existing alias | Same handler as `set-path`; no independent behavior |
| `snapshot` | Keep compatibility route; clarify indexing | `snapshots create` implemented; scans/hashes/indexes source state, stores no backup payload |
| `history` | Keep compatibility route; add resource listing | `snapshots list` implements versioned read-only results and positive limits; current JSON retained |
| — | Add single-resource inspection | `snapshots show PROJECT --id ID` reports one same-project index, History markers, and aggregate backup references without exposing paths or claiming payload availability |
| `diff` | Keep compatibility route; add resource comparison | `snapshots diff` implements bounded versioned read-only results and same-project ID checks; compares indexed records, not two stored backup payloads |
| `prune` | Keep compatibility route; make retention scope explicit | `snapshots prune` implemented; local-index retention, existing dry-run and backed/protected-point safeguards retained |
| `restore` | Keep compatibility route; extend qualified formats later | `recovery restore` implemented; recorded folder backups only; archive/encrypted inputs remain rejected |
| `sync` | Keep live-mirroring compatibility route | `mirror` implemented over the same rsync/robocopy service; does not create a recorded backup |
| `verify` | Keep current workflow; evolve evidence contract | Currently checks a supplied folder against the latest indexed snapshot, sample/full; must not imply whole-vault or image validation |
| `watch` | Keep current behavior; evolve unattended lifecycle | Existing snapshot/live-sync/verification loop; explicit DB selection and drained cancellation implemented; uniform headless results remain pending |
| `doctor` | Keep current diagnostics; evolve machine results | Existing isolated write probes and concurrent tool-output draining retained; stderr/JSON/provenance contract pending |
| `destinations` | Keep existing listing/testing invocation | Future resource actions must preserve today's `--test` and `--json`; testing can involve mount/credential access |
| `init` | Keep explicit initialization | Future profile/config selection must preserve shared CLI/desktop storage and owner-only data policy |
| `version` | Keep existing build-identity output | Existing JSON identity retained |
| `docs` | Add documentation entry point | Compact terminal guide plus bundled `--full`, browser `--open`, and pipe-friendly `--url` access to the full handbook |
| `--version` | Keep early read-only shortcut | Remains before logging/config initialization |
| `config show` | Keep legacy output; add safe inspect/export later | Current full serialized config is not a redacted support export; secrets/privacy audit required |
| `config path` | Keep current meaning | Returns configured database path, not the configuration file path; new file-location inspection needs an explicit action |
| `config set-db` | Keep explicit mutation | Existing path expansion and shared config write retained; profile/import migration remains gated |
| `presets list` | Keep current names | Shared preset parity and discoverability remain follow-up work |
| `presets show` | Keep current preset inspection | 1.8.9 containment/literal-path safeguards retained |
| `self-test` | Keep isolated diagnostics | Default temporary DB/workspace retained; explicit `--db` remains intentional integration behavior |
| Previously unregistered `DiscoverProjectsCommand` | Expose existing service | `projects discover` implemented: candidate child source folders, no registration or recovery-evidence claim |

No current registered command is silently removed or repurposed in this slice.
Actual stored-backup creation, backup-format inspection, image operations, remote
providers, bulk selectors, stable project-ID selectors, and emergency database-free
recovery require their owning shared services and qualification contracts.

## Resource and data semantics

- `projects`: source registrations and their local indexed history.
- `snapshots`: indexed source state and changes. Creating a snapshot alone is not
  a stored backup and cannot establish recoverability.
- `mirror`: transfer current live source files. It does not publish a recorded
  recovery point, retention policy, or immutable backup.
- `backups`: reserved design direction for recorded recovery payload operations;
  no fake backup command is introduced over the mirroring service.
- `recovery`: recorded-byte restore and, later, independent recovery workflows.
- `destinations`, `config`, `presets`: resource inspection and explicit management.

Project names remain the positional selector in mutation commands. New read-only
`projects show [name]` accepts a literal name or explicit `--id`, with conflicting
selectors rejected before any database access. Versioned list output exposes IDs.
Stable numeric IDs and filter/bulk selectors must become explicit alternatives
under VS-1973 rather than interpreting a numeric name as an ID. Conflicting
selectors must fail before any mutation. Bulk work must expose per-item outcomes
and partial failure; it must not return success when only some requested work ran.

## Configuration and path contract

`ConfigHelper.ResolveDb` currently uses this order:

1. Explicit `--db`.
2. Shared `AppConfig.DbPath`.
3. Legacy `~/.vaultsync/config.json` `Database` fallback.
4. Shared-store default.

Keep that order for existing invocations. `ConfigHelper.Load` can additionally
migrate a legacy DB value into an empty shared path; a future read-only inspection
contract must separate inspection from migration. Avoid adding a second independent
CLI database/config authority. `watch --db PATH` now follows the same explicit
selection precedence.

Keep paths literal apart from the documented leading `~` expansion. The new
`projects discover --root` expands and resolves the explicit root using a fresh
in-memory config; it does not load/migrate the user's shared configuration. It
lists immediate candidate child folders and does not claim DB-backed snapshot
statistics. Default discovery uses the configured Projects Root. Empty/missing
root reporting and filesystem-link policy need explicit decisions before broader
recursive or repository discovery is implemented.

## Compatibility window and the first behavior change

Existing registered routes remain available throughout the 1.9 family. No removal
is scheduled in this audit; a later removal requires a reviewed compatibility
boundary, documentation, and tested migration. Grouped commands currently use
legacy settings, output shapes, and operation semantics. Compatibility aliases
must not print migration warnings into JSON stdout.

BUG-19001 intentionally changes unsafe behavior in both old and new project
removal routes: `--quiet` no longer implies permission to remove registration and
snapshot history. Redirected input also requires explicit `--yes`. Rejection
returns exit code 2 and actionable stderr guidance before resolving config or
opening a database. Interactive removal still asks for confirmation.

Script migration:

```sh
# Before: quiet output also bypassed confirmation.
vaultsync remove-project "My project" --quiet

# 1.9: explicit authorization is required.
vaultsync remove-project "My project" --yes --quiet
# Equivalent resource route:
vaultsync projects remove "My project" --yes --quiet
```

Removal continues to leave source and stored backup files intact. `--yes` must
never bypass containment, backup integrity, destination identity, or supported
format checks in other destructive workflows.

## Output and unattended contracts

Legacy JSON payloads are unversioned and inconsistent in property casing.
The next slice implements an explicit v1 envelope for project list/show under
`--output json`, including parse/selection errors and process exit codes. See
[the implemented output contract](CLI_OUTPUT.md). Existing database inspection
uses read-only connections; no initialization or migration occurs in this mode.
Do not silently replace existing `--json` output. VS-1974 must extend the implemented versioned mode to the remaining workflows
before scripts depend on a shared envelope across commands. That
contract must include schema version, operation/resource identity, status,
per-item results, evidence scope, limitations, actionable errors, and exit code.
Streaming progress/events need a separate documented framing contract.

Existing failures vary: verification/doctor/watch commonly use 2, aborted removal
uses 1, mirroring exposes tool/service exit codes, and other failures can go
through Spectre exception handling. Project list/show now document 0/1/2 under explicit versioned output. A uniform
public exit-code table across all commands is still pending; the new removal guard's 2 is not a claim that every command conforms.
Legacy automation must have a migration path before exit meanings change.

Pending requirements:

- machine results on stdout; diagnostics/progress on stderr;
- no decorative ANSI/progress for non-TTY output; predictable quiet behavior;
- explicit no-prompt behavior with safe credential input;
- bounded cancellation and cleanup, including child processes and partial work;
- partial-failure reporting for bulk tasks;
- redacted configuration/diagnostics and secret-safe argument handling.

The initial slice removes the startup log of raw argv. This does not establish
full redaction: command-specific logs still include project/path values and
exception messages; those sources require a separate privacy pass before the
full unattended contract can be qualified.

## Development examples

Replace `vaultsync` with the installed CLI executable name on the target platform.
These routes require a 1.9 development build.

```sh
vaultsync projects add "My project" ./source --db ./test-vault.db --quiet
vaultsync projects list --db ./test-vault.db --json
vaultsync projects discover --root ./candidate-sources --json
vaultsync snapshots create "My project" --db ./test-vault.db --quiet
vaultsync snapshots list "My project" --db ./test-vault.db --limit 10 --json

# After capturing at least two snapshots:
vaultsync snapshots diff "My project" --db ./test-vault.db --json
vaultsync snapshots prune "My project" --db ./test-vault.db --keep-last 10 --dry-run --json
vaultsync mirror "My project" ./mirror-target --db ./test-vault.db --dry-run
vaultsync recovery restore "My project" ./restore-target --db ./test-vault.db --dry-run --json
```

A mirror is not a recorded backup: `recovery restore` requires an actual recorded
folder-backup entry, and cannot restore merely because the example mirror ran.
Archive/encryption, independent recovery, image/device operations, and offsite
providers remain explicitly unsupported until their owning release work qualifies.

## First-slice evidence

Isolated integration coverage exercises both legacy and grouped project lifecycle
routes, literal names/paths, JSON listing, snapshot bytes/hashes without stored
backup creation, dry-run versus actual index pruning, source-folder discovery,
recorded-byte restore, and restore-clean dry-run preservation. Two pre-fix cases
reproduced quiet removal without `--yes`; both must preserve the registration,
local snapshot history, and source data after the guard is applied.

## Watcher shutdown contract

`watch NAME --db PATH --quiet` runs against the selected store and suppresses
startup/progress guidance. It still creates a snapshot at startup; snapshots are
indexes, not recorded backup bytes. Ctrl-C or a supplied command cancellation token
returns 130 after filesystem events are disabled and all queued/active debounce
work has been cancelled and drained. A cancelled session detaches its Ctrl-C
handler; it cannot create later snapshots. Cancellation before startup does not
initialize the database. BUG-19002 owns the previous shutdown and token-disposal
defects; VS-1973 owns explicit store selection and VS-1974 owns unattended behavior.

This does not yet unify watcher failure reporting, JSON events, or dry-run semantics.
The existing cycle failure handling remains pending under VS-1974; exit 130 reports
cancellation rather than qualification of all prior cycles.

## Terminal identity and documentation

A no-argument invocation now renders a dedicated introductory UI: a compact text
logo, bordered name/version and purpose, documentation/website/repository/release
links, six starter tasks, and pointers to root and command help. It returns success
without dumping the full command tree. Root `--help` uses a smaller header before
normal generated help. Command execution and structured output receive no decoration.

`vaultsync docs` shows the compact documentation menu. `docs --full` prints the
embedded practical handbook followed by the generated exhaustive command reference,
`docs --url`
prints only its online URL, and `docs --open` asks the operating system to open that
URL. `docs/CLI.md` and `docs/CLI_COMMAND_REFERENCE.md` are embedded for runtime use and
included in the tool package. `scripts/generate_cli_reference.py` rebuilds the latter
from every registered grouped, utility, branch, and compatibility command.
This is early implementation under VS-1977 and VS-1978; shell completion, platform
PATH guidance, scheduler examples, and script-migration runbooks remain pending.
