# Issue #846 — backup topology and fault-injection test coverage

Canonical issue: [BUG-19049 / #846](https://github.com/ATAC-Helicopter/VaultSync/issues/846).
Pull request: [#866](https://github.com/ATAC-Helicopter/VaultSync/pull/866).

This is an **evidence-oriented breakdown of executable xUnit cases**, not a claim of 100% code coverage or end-to-end release qualification. Counts reflect `[Theory]` expansion across each test's data rows, not distinct C# methods. All tests use disposable local paths and an isolated SQLite repository; no source project or mounted network share is mutated.

## Executable coverage by behavior (baseline 207)

| Class / method | Cases | Purpose |
|---|---:|---|
| `BackupTopologyMatrix846Tests.Preflight_RejectsUnsafeTopologyBeforeScanningOrCache` | 30 | 15 unsafe path shapes x 2 archive modes; no cached bypass |
| `BackupTopologyMatrix846Tests.RunBackup_RejectsUnsafeTopologyBeforeSideEffects` | 60 | 15 unsafe shapes x 2 modes x automatic/manual; no artifacts or history |
| `BackupTopologyMatrix846Tests.RunBackup_RejectsUnsafePreferredFinalRootBeforeStaging` | 30 | Final destination overlap despite safe staging location |
| `BackupTopologyMatrix846Tests.Preflight_AcceptsClearlySeparatedRoots` | 12 | Six safe path relationships x 2 modes |
| `BackupTopologyMatrix846Tests.RunBackup_PreservesExternalDestinationsAndContents` | 24 | Six safe paths x 2 formats x manual/automatic; check actual ZIP/file contents |
| `BackupTopologyMatrix846Tests.Inputs_RejectedBeforeAnyArchiveOrMetadata` | 6 | Empty source, empty destination, missing destination x formats |
| `BackupTopologyMatrix846Tests.RejectedRun_DoesNotAlterPreviouslyCompletedArchiveOrHistory` | 1 | Byte-identical old archive and retained SQLite backup/snapshot history |
| `BackupTopologyMatrix846Tests.SiblingNonArchiveDestination_ThreeIndependentRunsRemainUsable` | 1 | Real repeated unarchived backup control |
| `BackupTopologyMatrix846Tests.PreflightAndRuntime_ExplainSameUnsafeTopology` | 3 | Consistent exception messages in three representative unsafe shapes |
| `BackupTopologyRegression846Tests` (five xUnit methods) | 11 | Four preflight overlap, four runtime overlap, cache poisoning, final-root overlap, triple ZIP run |
| `BackupResilienceSupplement846Tests` (11 xUnit methods) | 29 | Normalization (6), distinct sibling controls (4), disappearance after preflight (2), cancellation before start (4), unsafe cancellation (2), 49-file bounded preflight (2), real binary/Unicode ZIP (2), escape paths (3), linked parent read/write controls (2), linked scan controls (2) |
| **Baseline** | **207** | All green on Windows 2022 / macOS 15 / Ubuntu 24.04 via [Actions run 43780268442](https://github.com/ameenalqershi/VaultSync/actions/runs/43780268442) |

## Additional deterministic filesystem fault injection (18 cross-platform-verified cases)

| Class / method | Cases | Actual fault | Limits |
|---|---:|---|---|
| `BackupFailureInjection846Tests.DestinationRenamedDuringInitialProgress_StopsWithoutHistory` | 4 | Actual `Directory.Move` during the first progress callback | Simulates disappearance **before file writes**, not unmount midway through copying |
| `ExistingFileAtProjectBackupDirectory_RejectsWithoutMetadata` | 4 | Real `Directory.CreateDirectory` collision with an existing regular file | Not a real disk-full or power outage |
| `CancellationTriggeredByFirstProgressReport_CreatesNoHistory` | 4 | Actual cancellation token fired by progress callback | Pre-commit cancellation, not hard process termination |
| `ExistingDestinationReplacedByRegularFileAfterPreflight_RefusesRun` | 2 | Actual directory deletion followed by creation of regular file | Deterministic pre-run target mutation, not a malicious concurrent symlink swap |
| `GuaranteedFreeSpaceThreshold_RejectsBeforeSnapshotCommit` | 2 | Exercises actual disk-space query and a conservative 100% free-space threshold | **Not** physical ENOSPC or quota exhaustion |
| `NestedDirectorySymlink_EscapeIsRejectedForWrite/Read` | 2 | Actual directory symlink in nested source path | Linux/macOS; Windows symlink-creation is skipped by policy-dependent fixture |
| **Additional** | **18** | Controlled filesystem and cancellation failures | All 18 pass as part of the 225-case test run on the three OSes; Windows symlink branches do not create links |
| **Current total** | **225** | Distinct xUnit case instances, not 225 independent correctness theorems | No code coverage percentage established |

## What the tests do **not** establish

- Lexical `Path.GetFullPath` source/destination separation is not sufficient to prove safety against **symlink/reparse-point aliases** of entire roots, hard links, bind mounts or race-driven swaps. Nested helper probes do not prove that these root-alias attacks are rejected. A separate targeted implementation/security review is necessary.
- No **real SMB/NAS outage**, live mount detach, network latency/loss, `ENOSPC` on a dedicated quota filesystem, power-cut, forced process kill after partial archive write, or crash-consistency recovery test has been demonstrated. A local rename or file collision is a valid deterministic fault, **not** a substitute for those failure mechanisms.
- No qualification of encrypted archive **restore**, real credentials, all historical backup formats, large sustained throughput or memory pressure; these should be run in explicitly authorized isolated storage/devices and the appropriate integration suites.
- The Windows-hosted runner does not guarantee permission to create symlinks. Conditional tests return before creating a link on Windows, so **do not count these as actual Windows symlink validation**, despite their xUnit pass status.
- Native hosted CI here compiles the Core test project, not the entire installer/package/release pipeline. Tracking-policy labels and the separate release audit need maintainer attention.

## Evidence and interpretation

Current public QA evidence: [225 targeted / 2,899 Core on each of Windows 2022, macOS 15 and Ubuntu 24.04, with zero build warnings/errors](https://github.com/ameenalqershi/VaultSync/actions/runs/43781772674). Three downloadable TRX artifacts accompany the hosted matrix. Earlier [207 targeted / 2,881 Core](https://github.com/ameenalqershi/VaultSync/actions/runs/43780268442) and [178 targeted / 2,852 Core](https://github.com/ameenalqershi/VaultSync/actions/runs/43778065662) baselines also passed. CI success does **not** replace full release qualification.

PR #866 is a public contribution, unrelated to the separate private VaultSync security advisory. References use `Refs #846`, never automatically close the issue.
