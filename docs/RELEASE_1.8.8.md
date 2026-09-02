# VaultSync 1.8.8 — Chronicle Stabilization

This is the maintained implementation-status page for the active `1.8.8`
release. The canonical scope remains in
[`ROADMAP.md`](../ROADMAP.md#188--chronicle-stabilization).

## Release identity

| Field | Value |
|---|---|
| Current stable | `1.8.7` (`v1.8.7`, released 2026-08-21) |
| Active target | `1.8.8` |
| Planning started | 2026-08-24 |
| Stable target | 2026-09-01 |
| Maximum date | 2026-09-04 |
| Working branch | `release/1.8.8` |
| Integration branch | `Dev` |
| Stable branch | `Stable` |
| Primary patch predecessor | `1.8.7` |
| Additional platform candidates | `1.8.2`, `1.8.3`, `1.8.5`, `1.8.6` on Windows, macOS, and Linux when the target remains overlay-safe |
| Tagline | *A stable foundation for larger recovery.* |

The seven-day target keeps this release narrow. P0 safety and qualification
work blocks promotion; unfinished non-blocking work moves forward rather than
expanding the release beyond the fourteen-day ceiling.

## 1.8.7 feedback and evidence intake

Snapshot taken on 2026-08-24, three days after publication:

- no GitHub issue was opened or updated after the `v1.8.7` release was
  published, so there is no confirmed user-reported regression to promote into
  `1.8.8` yet;
- the 1.8.7 PR quality gate reported zero new issues, `82.3%` coverage on new
  code, and `0.0%` duplication on new code;
- raw release-asset counts are still too small and too automation-heavy to
  infer adoption: installer/package downloads are in single digits, while
  patch-manifest requests are dominated by update checks;
- the shipped release page still listed physical NAS/SMB, upgrade,
  cross-platform, theme, accessibility, static-analysis, and dependency
  qualification as planned. Evidence for those manual gates must be recorded,
  not assumed, in this release;
- the release branch and `Dev` ended with identical trees but different commit
  IDs because the release PR used rebase merge. Repository merge policy is part
  of the 1.8.8 kickoff so future integration retains commit ancestry and Stable
  remains a release-only merge spine.

Feedback remains an open intake throughout the release. New reports should be
triaged by reproducibility and data-safety impact, linked to the `1.8.8`
milestone when accepted, and added to the changelog only after a fix exists.

On 2026-09-01, a Linux `1.8.2` installation reproduced a failed `1.8.7`
installer fallback: VaultSync exited as soon as `pkexec` started, before the
password prompt had completed. `BUG-18147` changes Debian fallback to wait for
the actual privileged install result and adds an authenticated-helper handoff
for protected patch installs. `VS-1889` also qualifies direct Linux overlays
from older exact bases when their published file inventories contain no file
that the target omits; all other versions retain installer fallback.

## Code baseline

The largest current implementation surfaces align with the existing
decomposition work:

| Surface | Lines at kickoff | Release relevance |
|---|---:|---|
| `SettingsViewModel.cs` | 5,049 | desktop fault isolation |
| `BackupsViewModel.cs` | 5,049 | backup workflow isolation |
| `MetadataSyncService.cs` | 4,357 | metadata interruption and merge safety |
| `ProjectsViewModel.cs` | 3,969 | desktop fault isolation |
| `BackupService.cs` | 3,741 | backup interruption and cancellation |
| `AppViewModel.BackupHistoryHandlers.cs` | 3,032 | history workflow isolation |

Line count is a routing signal, not an acceptance criterion. Decomposition must
preserve behavior, add focused contract tests, and reduce the blast radius of
failure handling without becoming a broad rewrite.

## Delivery sequence

1. Establish repeatable large-history and high-file-count benchmark fixtures,
   budgets, and baseline results (`VS-1823`). The harness and budgets are now
   maintained in [`PERFORMANCE_BENCHMARKS.md`](PERFORMANCE_BENCHMARKS.md); the
   supported-platform baseline reports remain release-gate evidence.
2. Exercise cancellation, disconnection, corruption, retention, migration,
   clean-state recovery, and the exact `1.8.7` upgrade path (`VS-1882`).
3. Extract the smallest backup, metadata, and view-model boundaries needed to
   isolate failures found by those exercises (`VS-1821`, `VS-1822`).
4. Close localization, accessibility, scaling, contrast, narrow-layout,
   dependency, installer, and updater findings (`VS-1883`, `VS-1884`).
5. Run and record the complete supported-platform matrix before promotion
   (`VS-1881`).

## Release gates

- benchmark commands, fixtures, machine profile, and results are reproducible;
- budgets cover large histories and high file counts without hiding tail
  latency, memory growth, or cancellation time;
- plain and encrypted backup/restore pass interruption, destination loss,
  corruption, retention, and clean-machine recovery exercises;
- unmodified `1.8.7` installations can update or fall back to the complete
  installer without losing projects, repositories, or recovery evidence;
- Linux `1.8.2`, `1.8.3`, `1.8.5`, and `1.8.6` use a direct patch only when the
  generated manifest retains them after file-inventory qualification; Linux
  `1.8.4` and any omitted base use installer fallback without premature exit;
- Windows, macOS, and Linux artifacts pass build, install, launch, backup,
  restore, update, and uninstall checks appropriate to each package;
- all maintained translations, keyboard and screen-reader paths, scaling,
  themes, contrast, narrow layouts, SonarQube, CodeQL, and dependency audits
  pass with evidence linked from the release PR;
- the release branch merges into `Dev` without squashing its commit history,
  and `Dev` is promoted into `Stable` with a merge commit.

## Initial work queue

- [`VS-1823` / #382](https://github.com/ATAC-Helicopter/VaultSync/issues/382):
  performance budgets and release benchmarks (`P0`) — harness, local baseline,
  and cross-platform evidence workflow complete.
- [`VS-1882`](../ROADMAP.md#188--chronicle-stabilization): lifecycle and
  recovery fault matrix (`P0`) — compression interruption is qualified for
  plain and encrypted archives, encryption now observes cancellation between
  copied chunks, restart cleanup fails closed for invalid resume checkpoints,
  verification cancellation propagates, and the release-scope durable-write
  boundaries are complete.
- [`VS-1881`](../ROADMAP.md#188--chronicle-stabilization): supported-platform
  release matrix (`P0`) — complete for the 1.8.8 release candidate.
- [`VS-1821` / #380](https://github.com/ATAC-Helicopter/VaultSync/issues/380):
  backup and metadata decomposition (`P1`) — snapshot creation, deferred hashing,
  checkpoint telemetry, and native-copy execution now use focused boundaries;
  release scope is complete.
- [`VS-1822` / #381](https://github.com/ATAC-Helicopter/VaultSync/issues/381):
  desktop view-model decomposition (`P1`) — release-scope fault boundaries and
  qualification are complete.
- [`VS-1883`](../ROADMAP.md#188--chronicle-stabilization): localization and
  accessibility defects (`P1`) — complete for the 1.8.8 release candidate.
- [`BUG-18116` / #569](https://github.com/ATAC-Helicopter/VaultSync/issues/569):
  unintended horizontal page and dialog scrolling (`P1`) — shared and explicit
  scroll policies are implemented and runtime qualification is complete.
- [`BUG-18117` / #570](https://github.com/ATAC-Helicopter/VaultSync/issues/570):
  retention deletion confinement across filesystem links (`P0`) — fixed on the
  release branch and awaiting integration through #568.
- [`VS-1889` / #613](https://github.com/ATAC-Helicopter/VaultSync/issues/613):
  exact multi-version patch qualification (`P1`) — implemented for
  platform-specific overlay-safe candidates and verified through current-head
  release checks.
- [`BUG-18146` / #611](https://github.com/ATAC-Helicopter/VaultSync/issues/611):
  startup tray project discovery (`P1`) — implemented and covered
  with an isolated profile plus focused projection tests.
- [`BUG-18147` / #612](https://github.com/ATAC-Helicopter/VaultSync/issues/612):
  Linux privileged updater handoff (`P0`) — implemented with
  Debian exit-result handling and an elevated patch-helper readiness handshake;
  release-scope Linux qualification is complete.
- [`BUG-18148` / #614](https://github.com/ATAC-Helicopter/VaultSync/issues/614):
  macOS system-Python script validation (`P1`) — postponed annotations remove
  the accidental local dependency on a separately installed Python 3.10+.
- [`BUG-18149` / #615](https://github.com/ATAC-Helicopter/VaultSync/issues/615):
  updater quality-gate regression (`P0`) — all eight reported security and
  maintainability findings are corrected and hosted Sonar is green.
- [`BUG-18150` / #616](https://github.com/ATAC-Helicopter/VaultSync/issues/616):
  patch-base inventory identity (`P0`) — mismatched target versions and
  case-colliding managed paths now fail qualification.
- [`BUG-18151` / #617](https://github.com/ATAC-Helicopter/VaultSync/issues/617):
  updater documentation consistency (`P1`) — public and maintainer guidance now
  matches conditional Linux multi-version qualification and installer fallback.
- [`BUG-18152` / #618](https://github.com/ATAC-Helicopter/VaultSync/issues/618):
  Linux compositor stability (`P0`) — the app and updater prefer native Wayland,
  fall back automatically to X11, avoid translucent top-level surfaces, and were
  verified in a live Wayland session with protocol traffic.
- [`BUG-18118` / #571](https://github.com/ATAC-Helicopter/VaultSync/issues/571):
  fail-closed source loss after snapshot creation (`P0`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18119` / #572](https://github.com/ATAC-Helicopter/VaultSync/issues/572):
  cross-process decrypted-workspace isolation (`P1`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18120` / #577](https://github.com/ATAC-Helicopter/VaultSync/issues/577):
  disposable-tree cleanup confinement across filesystem links (`P0`) — fixed on
  the release branch and awaiting integration through #568.
- [`BUG-18121` / #579](https://github.com/ATAC-Helicopter/VaultSync/issues/579):
  abandoned verified release-cache write cleanup (`P1`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18122` / #580](https://github.com/ATAC-Helicopter/VaultSync/issues/580):
  abandoned identity and credential-index write cleanup (`P1`) — fixed on the
  release branch and awaiting integration through #568.
- [`BUG-18123` / #581](https://github.com/ATAC-Helicopter/VaultSync/issues/581):
  abandoned sanitized support-bundle staging cleanup (`P1`) — fixed on the
  release branch and awaiting integration through #568.
- [`BUG-18124` / #582](https://github.com/ATAC-Helicopter/VaultSync/issues/582):
  temporary telemetry-export retention (`P1`) — fixed on the release branch and
  awaiting integration through #568.
- [`VS-1884` / #491](https://github.com/ATAC-Helicopter/VaultSync/issues/491):
  dependencies, packaging, updater, and compatibility (`P1`) — complete. The
  2026-08-26 dependency audit found no vulnerable or directly outdated
  packages; reviewed transitive and legacy packages are currently constrained
  by the supported Avalonia and Windows notification stacks. The Windows App
  SDK notification migration (#586) and xUnit v3 test-platform migration (#587)
  are isolated 1.9 work rather than unsafe patch-level package swaps. Pinned
  artifact download, attestation, and matched CodeQL init/analyze actions are
  updated to their current reviewed releases, and future CodeQL action updates
  are grouped so init/analyze cannot arrive as incompatible independent PRs.
- [`VS-1885` / #573](https://github.com/ATAC-Helicopter/VaultSync/issues/573):
  release static-analysis debt (`P1`) — all reported Project automation,
  onboarding, and storage-hygiene findings are fixed on the release branch.
- [`VS-1886` / #578](https://github.com/ATAC-Helicopter/VaultSync/issues/578):
  bounded archive and support-package memory pressure (`P1`) — implemented on
  the release branch and awaiting integration through #568.
- [`VS-1887` / #583](https://github.com/ATAC-Helicopter/VaultSync/issues/583):
  actionable onboarding plus non-blocking Guide and Schedule progress (`P1`) —
  implemented on the release branch and awaiting integration through #568.
- [`VS-1888` / #607](https://github.com/ATAC-Helicopter/VaultSync/issues/607):
  remaining core Sonar annotations (`P1`) — async archive opening, scanner and
  diff boundaries, recoverability analysis, repository write contracts, and
  explicit best-effort cleanup are implemented; final-head Sonar and CodeQL pass.
- [`BUG-18125` / #584](https://github.com/ATAC-Helicopter/VaultSync/issues/584):
  unpredictable authenticated restore staging (`P1`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18126` / #585](https://github.com/ATAC-Helicopter/VaultSync/issues/585):
  decrypted-workspace cleanup root confinement (`P1`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18127` / #588](https://github.com/ATAC-Helicopter/VaultSync/issues/588):
  overlapping backup cancellation ownership (`P0`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18128` / #589](https://github.com/ATAC-Helicopter/VaultSync/issues/589):
  duplicate and unbounded Sonar release-branch analysis (`P1`) — fixed on the
  release branch and awaiting integration through #568.
- [`BUG-18129` / #590](https://github.com/ATAC-Helicopter/VaultSync/issues/590):
  cancelled snapshot scan-cache publication (`P0`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18130` / #591](https://github.com/ATAC-Helicopter/VaultSync/issues/591):
  duplicate or unbounded release-branch CI, CodeQL, and quality runs (`P1`) —
  fixed on the release branch and awaiting integration through #568.
- [`BUG-18131` / #592](https://github.com/ATAC-Helicopter/VaultSync/issues/592):
  snapshot traversal across linked source paths (`P0`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18132` / #593](https://github.com/ATAC-Helicopter/VaultSync/issues/593):
  swallowed destination-verification cancellation (`P0`) — fixed on the release
  branch and awaiting integration through #568.
- [`BUG-18133` / #594](https://github.com/ATAC-Helicopter/VaultSync/issues/594):
  interrupted restore and sandbox-apply operations now roll back live target
  changes; full release-scope qualification is complete.
- [`BUG-18134` / #598](https://github.com/ATAC-Helicopter/VaultSync/issues/598):
  cancelled metadata operations stop before local writes, portable exports use
  transaction rollback, and interrupted schema/legacy imports restore the prior
  SQLite repository and configuration state.
- [`BUG-18135` / #599](https://github.com/ATAC-Helicopter/VaultSync/issues/599):
  cancellation after the durable backup commit no longer deletes completed data
  while returning success.
- [`BUG-18136` / #600](https://github.com/ATAC-Helicopter/VaultSync/issues/600):
  cancellation during the final scan entry can no longer publish a partial scan;
  plain and encrypted upload cancellation are also covered by lifecycle tests.
- [`BUG-18137` / #601](https://github.com/ATAC-Helicopter/VaultSync/issues/601):
  backup data is revalidated immediately before metadata publication so a
  vanished destination artifact cannot create a dangling success row.
- [`BUG-18138` / #602](https://github.com/ATAC-Helicopter/VaultSync/issues/602):
  failed restore rollback now preserves prior-file evidence at a reported,
  hygiene-managed recovery path when the destination has disappeared.
- [`BUG-18139` / #603](https://github.com/ATAC-Helicopter/VaultSync/issues/603):
  retention no longer treats an unavailable destination as proof that indexed
  remote backups are gone.
- [`BUG-18140` / #604](https://github.com/ATAC-Helicopter/VaultSync/issues/604):
  deferred metadata replay validates the copied store before retiring its queue;
  consumed evidence is retained for one day and then removed safely.
- [`BUG-18141` / #605](https://github.com/ATAC-Helicopter/VaultSync/issues/605):
  metadata-conflict resolution and backup-index repair now expose independent
  busy state so unrelated Settings commands remain available.
- [`BUG-18142` / #606](https://github.com/ATAC-Helicopter/VaultSync/issues/606):
  failed and integrity-rejected complete-installer downloads remove their
  temporary working file immediately instead of waiting for startup cleanup.
- [`BUG-18143` / #608](https://github.com/ATAC-Helicopter/VaultSync/issues/608):
  cancellation performance qualification now uses a dedicated measured worker
  so small hosted runners cannot starve the cancellation request.
- [`BUG-18144` / #609](https://github.com/ATAC-Helicopter/VaultSync/issues/609):
  isolated recording, troubleshooting, and test profiles now keep their default
  database inside the selected configuration directory instead of opening the
  normal per-user database.
- [`BUG-18145` / #610](https://github.com/ATAC-Helicopter/VaultSync/issues/610):
  metadata-conflict review now stacks Base, Local, and Imported values vertically
  so long values remain readable in narrow windows without introducing
  horizontal scrolling.

Exact `1.8.7` compatibility now has frozen-schema and configuration tests that
preserve repository records, encrypted-backup descriptors, user paths, schedules,
retention, and encryption choices. Version `1.8.7` remains the primary patch
predecessor on every platform. Windows, macOS, and Linux can additionally
advertise 1.8.2, 1.8.3, 1.8.5, and 1.8.6 only when final-payload inventory
validation keeps that base overlay-safe; 1.8.4 and every omitted base use the
complete-installer fallback.
Physical updater and fallback qualification is complete for the 1.8.8 release
candidate.
CodeQL init/analyze are coordinated on `4.37.9`, and Sonar Java setup uses the
validated `6.0.0` action pin.

## Maintainer links

- [Roadmap](../ROADMAP.md#188--chronicle-stabilization)
- [Release procedure](RELEASING.md)
- [1.8.7 release notes](WHATS_NEW.md#187)
- [Updater contract](UPDATER.md)
- [Storage hygiene](STORAGE_HYGIENE.md)
- [Cross-machine safety](CROSS_MACHINE_SAFETY.md)
