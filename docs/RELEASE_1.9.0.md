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

## CLI execution tracking

| ID | Issue | Release | Work |
| --- | --- | --- | --- |
| VS-1972 | [#669](https://github.com/ATAC-Helicopter/VaultSync/issues/669) | 1.9.0 | Command, behavior, and compatibility audit |
| VS-1973 | [#670](https://github.com/ATAC-Helicopter/VaultSync/issues/670) | 1.9.0 | Commands and shared services |
| VS-1974 | [#671](https://github.com/ATAC-Helicopter/VaultSync/issues/671) | 1.9.0 | Output and unattended execution |
| VS-1975 | [#672](https://github.com/ATAC-Helicopter/VaultSync/issues/672) | 1.9.0 | Practical power-user workflows |
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
