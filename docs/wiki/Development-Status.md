# Recovery Horizon development status

Updated October 3, 2026. Current published stable is **1.8.9** (September 16,
2026). **1.9.0 is in development**, with a retained stable target of March 26,
2027. The family horizon is September 24, 2027; later patches remain subject
to sequencing. [ROADMAP.md](../../ROADMAP.md) owns scope and delivery state.

## What exists in development

The [development CLI](CLI-Development.md) supports grouped project/snapshot/
backup/recovery commands, recorded full-folder backup creation, bounded single
and bulk verification, selective restore, read-only inspection, pruning previews,
versioned machine results, and finite watcher plans. Live mirroring is explicitly
separate from a recorded backup. Legacy command routes and JSON remain available
with the documented unattended-removal safety change.

The pending PR stack also contains destination inventory, watcher startup
observation, updater owner compatibility, AppImage packaging fixes, a UI route
architecture draft, and contribution/development documentation. A merge into an
intermediate stack branch is not a shipped release or a completed release gate.

## Review and integration order

| Layer | Work | Parent / destination |
| --- | --- | --- |
| [#721](https://github.com/ATAC-Helicopter/VaultSync/pull/721) | Updater compatibility | `release/1.9.0` |
| [#722](https://github.com/ATAC-Helicopter/VaultSync/pull/722) | Destination output and watcher startup | #721 |
| [#726](https://github.com/ATAC-Helicopter/VaultSync/pull/726) | AppImage permissions and CI | #722 |
| [#729](https://github.com/ATAC-Helicopter/VaultSync/pull/729) | VS-1910 route architecture draft | #726 |
| [#730](https://github.com/ATAC-Helicopter/VaultSync/pull/730) | Development diary and mirror | #729 |
| [#731](https://github.com/ATAC-Helicopter/VaultSync/pull/731) | Stacked PR workflow | #730 |
| `work/1.9-watch-preview` | BUG-19007 / #734, finite plans and tracking | #731 |

Merge bottom-up with merge commits, retarget each child after its parent merges,
and review its resulting diff and checks. The separate draft release PR
[#683](https://github.com/ATAC-Helicopter/VaultSync/pull/683) integrates
`release/1.9.0` into `Dev`; promotion from `Dev` to `Stable` follows its own gates.
[#725](https://github.com/ATAC-Helicopter/VaultSync/pull/725) is separate workflow
guidance against `Dev`, not an extra layer in this stack.

## What remains before release

- Remaining unattended output, cancellation/redaction, live watcher events,
  bulk CLI mutations, and qualified archive/encrypted CLI workflows.
- Approval of UI routes (VS-1910), disk format (VS-1917), engine/supported-system
  matrix (VS-1918), and recovery identities/evidence (VS-1919).
- Imaging, independent boot, supported image restore, and post-restore
  qualification (VS-1904–VS-1909). No disk-recovery capability is established by
  folder-backup or CLI tests.
- Supported-platform runtime/package and completion evidence, exact installed
  1.8.9 upgrade evidence, and required protected-branch checks.
- Independent Windows notification and xUnit v3 qualification.

The route architecture remains a review draft. The existing shell stays until
replacement parity is demonstrated. Updater owner compatibility does not authorize
a repository transfer before a compatible release ships and is adopted.

## Evidence and tracking

The [October 3 local audit](../release-evidence/1.9-audit-2026-10-03.md) records
1,053 .NET and 111 Python tests, zero-warning solution compilation, a CLI tool
package, dependency/localization checks and an isolated recorded-byte recovery
exercise. The tracking follow-up also fixes older GitHub CLI pagination in the
Project date audit. Local checks do not replace supported-platform release gates.

- [Delivery Project #7](https://github.com/users/ATAC-Helicopter/projects/7), linked to this repository.
- [1.9.0 milestone](https://github.com/ATAC-Helicopter/VaultSync/milestone/14).
- [Release contract](../RELEASE_1.9.0.md) and [route draft](../ROUTE_ARCHITECTURE_1.9.md).
- [Latest published release](https://github.com/ATAC-Helicopter/VaultSync/releases/latest).
- [Updates](Updates.md) distinguishes stable installation from development.
