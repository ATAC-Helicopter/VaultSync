# VaultSync 1.9.0 — Disk and Bootable Recovery Foundation

Status: active planning and architecture, started 2026-09-18. No disk-support,
CLI-rework delivery, or native upgrade qualification is implied by kickoff.
Canonical scope and delivery state remain in [ROADMAP.md](../ROADMAP.md).

| Field | Value |
| --- | --- |
| Current stable | `1.8.9` / `v1.8.9`, published 2026-09-16 |
| Active target | `1.9.0`, stable channel, planned stage |
| Stable target | 2027-03-26; existing milestone horizon retained |
| Family horizon | 2027-09-24; later patch targets remain subject to sequencing |
| Working branch | `release/1.9.0` |
| Integration / promotion | `Dev`, then `Stable` through merge commits |
| Primary upgrade predecessor | Exact `1.8.9`; qualification pending |
| Store package identity | `1.9.0.0`; build identity only, submission pending |
| Tagline | *Recover when the installed system cannot.* |

Beta qualification will precede stable support claims. Beta numbering, dates,
and supported preview scope will be recorded when their contracts are approved;
the 1.9.0 identity here remains the stable planning target.

## First delivery sequence

1. Approve UI architecture and disk format, engine/system matrix, and evidence
   contracts: VS-1910, VS-1917, VS-1918, VS-1919.
2. Audit the CLI and agree behavior and script compatibility: VS-1972.
3. Build the CLI command/service and unattended-output foundations:
   VS-1973, VS-1974, VS-1975. These can proceed without destructive disk work.
4. Qualify Windows notifications and the xUnit v3 migration independently:
   VS-1928, VS-1929.
5. After architecture approval, implement and qualify the complete imaging,
   validation, independent boot, restore, and post-restore loop: VS-1904–VS-1909.
6. Record supported-system limits and exact 1.8.9 upgrade evidence before promotion.

Architecture decisions can reduce or resequence scope when prototypes do not
support the intended stable claims. Existing backups remain independently
recoverable during migration. Paid signing is feasibility work under VS-1902;
unavailable signing must not weaken published integrity and provenance.

## CLI rework

This release begins the full 1.9 CLI program, including new commands and
necessary behavior changes. Terminal use should earn its place for repeated
and bulk tasks, precise inspection, scripting, scheduled verification, and
independent recovery. Cosmetic help cleanup alone does not complete the work.

The audit must explicitly resolve the current flat command tree, duplicated
configuration paths, live mirroring versus stored backups, restore and
verification scope, unstructured errors, logging of command arguments, prompt
and cancellation behavior, and missing shared-service workflows. Preserve the
1.8.9 restore, prune, literal-path, diagnostic-probe, and rsync safety fixes.

VS-1972–VS-1975 own audit, command/service implementation, unattended contracts,
and useful backup/restore tasks in 1.9.0. VS-1977 and VS-1978 own discovery,
completion, and script migration documentation in 1.9.1. VS-1976 owns portable
headless recovery in 1.9.2. VS-1953, VS-1955, and VS-1956 retain reporting,
local event hooks, and stable parity in 1.9.5; VS-1979 qualifies the accumulated
CLI behavior and compatibility. Offsite commands follow the qualified 1.9.3
providers rather than promising unsupported operations in 1.9.0.

## Promotion gates

- Reviewed format, engine/isolation, supported-system, and evidence contracts.
- Exact source/destination identity, overwrite preview, containment, capacity,
  cancellation, checkpoint, corruption, and partial-image tests.
- Independent boot, supported image-to-disk restore, and post-restore validation
  on representative hardware and virtual machines.
- Explicit filesystem, partition, encryption, live-capture, Secure Boot, driver,
  and hardware limitations; no universal bare-metal claims.
- CLI workflow safety, legacy migration, stable machine output, exit codes,
  non-interactive execution, cancellation, and redacted diagnostics.
- Windows, macOS, Linux, localization, accessibility, static analysis,
  dependency, supply-chain, packaging, and exact 1.8.9 upgrade gates.
- Metadata, issues, milestones, Project 7 fields/dates, and the draft PR agree.

The legacy desktop shell remains until replacement parity is proven. The
1.8.9 tracking was completed on maintainer instruction 2026-09-18. Historical
unproven checks remain documented in the closed VS-1894 / #633 record; the
closeout does not convert them into passed checks. Applicable 1.9 qualification
still requires fresh evidence.

## Kickoff validation

- Canonical/public release metadata consumers match the 1.9.0 planning identity.
- All 100 existing script tests pass.
- CLI and desktop warning-as-error builds pass with zero warnings/errors.
- The CLI version JSON reports 1.9.0 as a development build.
- Created 29 missing execution issues and aligned all 51 approved 1.9 entries
  with unique IDs, owning patch milestones, labels, and Project fields.
- The final Project date audit covers 521 items with zero repairs or unresolved
  dates; the roadmap description dry-run proposes zero managed-body changes.
- These checks validate kickoff consistency; product and native upgrade gates
  above remain pending.

## Implementation started — 2026-09-18

VS-1972, VS-1973, and VS-1974 are In progress. The first slice adds grouped
project/snapshot/recovery commands, source-folder discovery, and explicit mirror
naming while retaining legacy routes. Raw argv logging is removed. BUG-19001
requires explicit confirmation for quiet or redirected-input project removal.
The [CLI audit](CLI_REWORK.md) records inventory, compatibility, behavior, and
remaining service/output gaps. No disk implementation is implied. The initial implementation passes all 932
.NET tests and 100 script tests; the two quiet-removal cases failed before the
fix and pass afterward. [BUG-19001 / #699](https://github.com/ATAC-Helicopter/VaultSync/issues/699)
remains In progress until integrated.

## Project inspection and output slice

Projects list/show now support explicit versioned `--output json` with stable
result/error envelopes and 0/1/2 outcomes. Listing supports case-insensitive
name/preset filters and positive limits, while show accepts a literal name or
explicit local ID. Numeric names remain literal. Explicit-output listing and
all show invocations use read-only database connections, leaving initialization
and schema migration to explicit operations. Legacy `--json` is preserved.

The [v1 output contract](CLI_OUTPUT.md) and envelope schema document this scoped
implementation. Remaining commands do not yet implement the uniform output
contract. All 949 .NET tests pass; the initial implementation's previous hosted
Windows/macOS/Linux, CodeQL, Store/metadata preflights, and Sonar checks passed.

## Snapshot-history inspection slice

`snapshots list` and legacy `history` now accept explicit `--output json` for a
versioned result envelope containing project identity, limited snapshot rows,
and returned/total counts. These calls open existing databases read-only and do
not initialize or migrate stores. Invalid options fail before database access;
missing stores and projects have stable errors. Legacy `history --json` retains
its original array and property casing. Snapshot rows describe indexes and hashes,
not recoverable backup bytes. All 960 .NET tests and 100 script tests pass.

## Single-snapshot inspection slice

`snapshots show PROJECT --id ID` opens an existing database read-only and enforces
project ownership before returning one snapshot. Versioned output includes identity,
size and change summaries, optional History markers, and aggregate recorded-backup,
encrypted, and protected record counts. It omits paths, destination identities,
crypto descriptors, file entries, and hashes. Backup counts are records, not claims
that payload bytes are available or verified. This started VS-1975's practical
inspection work; subsequent slices added folder backup execution and selective
restore, while bulk workflows remain.
All 970 .NET tests and 100 script tests pass for the accumulated implementation.

## Snapshot-diff inspection slice

`snapshots diff` and legacy `diff` now accept explicit `--output json` for a
versioned read-only comparison. Both snapshot IDs must belong to the selected
project before paths are read. Path arrays are deterministic and bounded by a
positive limit; complete counts and an explicit truncation flag remain available.
Legacy JSON retains its original shape while inheriting the project boundary.
[BUG-19003 / #716](https://github.com/ATAC-Helicopter/VaultSync/issues/716)
tracks the previously unrestricted cross-project selection. All 965 .NET tests
and 100 script tests pass; roadmap and Project date audits are clean.

## Watcher lifecycle slice

`watch --db PATH` selects the same store as other CLI operations. Quiet sessions
suppress startup/progress guidance. Cancellation at startup, while idle, or with
pending changes now terminates with exit 130 after events are disabled and all
queued/active debounce work has been cancelled and drained. Ctrl-C handlers are
detached, and token cancellation/disposal shares a lock to prevent disposal races.
[BUG-19002 / #700](https://github.com/ATAC-Helicopter/VaultSync/issues/700) remains
In progress until integrated. Watcher JSON, cycle failure propagation, and dry-run
contracts remain pending under VS-1974.

All 954 .NET tests pass with warning-as-error compilation, including startup/idle/
pending-change cancellation, superseded active-work draining, and concurrent
trigger/cancel/completion. All 100 script tests and release metadata checks pass.
The prior project-inspection push passed hosted Windows/macOS/Linux builds and
release profiles, CodeQL, Store/metadata preflights, and the Sonar quality gate.

## CLI identity and handbook foundation

A no-argument invocation now displays a dedicated introductory UI with a compact
VaultSync text logo, running version, purpose, documentation/website/repository/
release links, six starter commands, and help pointers. Root help remains a separate,
smaller presentation. Neither path can contaminate command or structured output.
The new `docs` command displays documentation choices, prints the embedded handbook
plus an exhaustive generated command reference, opens the online copy, or
returns its URL for scripts. At this foundation stage both Markdown files were embedded and packaged;
`scripts/generate_cli_reference.py` rebuilds exact help for every registered route.
`vaultsync completion bash|zsh|powershell` prints generated, embedded command/option
completion, and the handbook documents tool PATH setup. This advances VS-1977/VS-1978
on the shared branch; the task guides and value completion below add the next slice.
Platform completion qualification and full migration/recovery guidance remain 1.9.1.
All 979 .NET tests and 105 script tests pass; the generated tool package contains
both Markdown documents and the assembly carries both embedded copies. The generated
reference check is deterministic and fails when the committed output is stale.

## CLI completion and installation guidance

`vaultsync completion bash|zsh|powershell` now prints generated command and option
completion from scripts embedded in the tool. The generation check inspects the
registered command tree and rejects missing routes or stale scripts. The CLI handbook
documents a pinned local 1.9 tool install, global-tool PATH setup, and shell loading.
Local validation passes 984 .NET tests, 109 script tests, Bash/Zsh syntax and
completion probes, both generation checks, release metadata, and a warning-free
NuGet tool pack. PowerShell execution and installation on supported Windows
packages remain to be qualified; no hosted Actions run was started for this slice.

## CLI task guides and quiet snapshot output

`vaultsync docs --task` now selects setup, inspection, mirror, restore,
automation, or migration guidance from the bundled task-guide document.
`docs --full` includes all three CLI documents. The disposable inspection
walkthrough was executed against an isolated temporary database and returned
versioned project, snapshot, and diff results with one changed file. The
handbook quick start now uses an explicit database and discovers actual IDs
instead of suggesting `init` or a fixed sample ID. CLI snapshot, watcher,
and self-test snapshot services send diagnostics to the private CLI log;
quiet snapshot stdout is empty in a focused regression test. Platform scheduler
recipes and PowerShell completion still require their supported-system gates.
Generated Bash/Zsh/PowerShell completion now also suggests the bundled task
topics after `docs --task` and `text`/`json` after supported `--output` options.
The generator reads task headings from the bundled guide, and Bash/Zsh probes
exercise the value suggestions locally.

The CLI demo exposed mirror filter diagnostics in stdout. Mirror, watcher, and
self-test paths now pass the private CLI logger into platform transfer runners;
quiet mirror output and the preview screen no longer include core filter traces.
The Windows runner also avoids creating the destination before a dry run.
The preview labels source and target separately and says when the preview completes.
Quiet mirror, watcher-mirror, and self-test failures report briefly on stderr;
transfer details remain in the private log.
The isolated demo transcript is local evidence only; Windows transfer behavior
still needs its supported-system gate.

The unattended-safety slice opens mirror, verify, and recorded-folder
restore databases read-only. An absent `--db` path now returns exit 1 on stderr
without creating the database, its parent directory, or a target. Existing
restore and transfer behavior remains covered by local tests; supported-platform
qualification remains pending.

Recorded-backup inspection now has project-scoped `backups list/show` routes
with local IDs and no exposed destination or crypto descriptors. The read-only
`backups verify` route checks indexed hashes against a full, unencrypted folder
payload and returns bounded per-file failures in v1 JSON. Missing data and
unsupported formats fail explicitly. `backups create` uses the shared service to
write a full folder backup to an explicit destination after a dry-run preflight;
it takes a fully hashed snapshot for subsequent verification. Archive/encrypted
verification and bulk work remain under VS-1973–VS-1975.
Creation now also returns a versioned JSON result and routes shared-service
diagnostics to the private CLI log.
Restore now selects a project-scoped backup ID or snapshot and can limit writes
to repeated relative file/directory paths. Selective restore preserves unrelated
target files and rejects `--clean`; the v1 result reports affected counts while
legacy `--json` stays compatible. Staged bytes are checked before replacing a
target file, closing [BUG-19004 / #717](https://github.com/ATAC-Helicopter/VaultSync/issues/717).
`backups verify-all` now checks bounded records across projects, returning a
per-backup outcome and a nonzero incomplete result for any failure or omitted
record. It uses the same full folder verifier as the single-backup command.
`snapshots create` now returns a v1 JSON result with the persisted snapshot ID
and counts, keeping service diagnostics out of machine output.
`mirror`/`sync` and `verify` now return one v1 result with `--output json`.
Mirror distinguishes dry runs from actual transfers and explicitly reports that
it did not record a backup; verify bounds failure details against the latest
indexed snapshot. Legacy text and `verify --json` remain compatible.

## CLI execution tracking

| ID | Issue | Release | Work |
| --- | --- | --- | --- |
| VS-1972 | [#669](https://github.com/ATAC-Helicopter/VaultSync/issues/669) | 1.9.0 | Command, behavior, and compatibility audit |
| VS-1973 | [#670](https://github.com/ATAC-Helicopter/VaultSync/issues/670) | 1.9.0 | Commands and shared services |
| VS-1974 | [#671](https://github.com/ATAC-Helicopter/VaultSync/issues/671) | 1.9.0 | Output and unattended execution |
| VS-1975 | [#672](https://github.com/ATAC-Helicopter/VaultSync/issues/672) | 1.9.0 | Practical power-user workflows |
| BUG-19004 | [#717](https://github.com/ATAC-Helicopter/VaultSync/issues/717) | 1.9.0 | Recheck staged restore bytes |
| VS-1977 | [#678](https://github.com/ATAC-Helicopter/VaultSync/issues/678) | 1.9.1 | Help, discovery, and completion |
| VS-1978 | [#679](https://github.com/ATAC-Helicopter/VaultSync/issues/679) | 1.9.1 | Handbook and script migration |
| VS-1976 | [#685](https://github.com/ATAC-Helicopter/VaultSync/issues/685) | 1.9.2 | Independent headless recovery |
| VS-1979 | [#695](https://github.com/ATAC-Helicopter/VaultSync/issues/695) | 1.9.5 | Cross-platform CLI and script qualification |

Kickoff tracking: [VS-1980 / #673](https://github.com/ATAC-Helicopter/VaultSync/issues/673).

## Tracking

- [Draft release PR #683](https://github.com/ATAC-Helicopter/VaultSync/pull/683), `release/1.9.0` → `Dev`
- [1.9.0 milestone](https://github.com/ATAC-Helicopter/VaultSync/milestone/14)
- [1.9 family milestone](https://github.com/ATAC-Helicopter/VaultSync/milestone/15)
- [Delivery Project 7](https://github.com/users/ATAC-Helicopter/projects/7)
- [Maintainer strategy](RECOVERY_HORIZON_STRATEGY.md)
