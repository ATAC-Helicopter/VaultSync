# Performance benchmarks

VaultSync's release benchmark is a deterministic console harness for the two
large-data paths most likely to regress during Chronicle stabilization:

- loading a 10,000-event SQLite history using the same repository calls and
  materialization pattern as the History workspace;
- comparing two 100,000-file snapshot inventories with ten percent modified;
- cancelling that high-file-count comparison while it is running.

The fixture is generated locally and deleted after the run. No user database,
backup, repository, network share, or credential is read.

## Run the release profile

Use a Release build on an otherwise idle machine:

```bash
dotnet run --project benchmarks/VaultSync.Benchmarks/VaultSync.Benchmarks.csproj \
  --configuration Release -- \
  --enforce \
  --output artifacts/benchmarks/1.8.9.json
```

The JSON report records the UTC time, source identity, operating system,
architecture, runtime, logical processor count, GC mode, fixture sizes, p50,
p95, maximum duration, p95 allocated bytes, budgets, and pass/fail result.
Attach that report to the release PR. Compare results only when the fixture
sizes and machine profile match.

The `Performance Benchmarks` workflow runs the same enforced profile on
GitHub-hosted Windows, Linux, and macOS runners when the harness or Core paths
change. Each job uploads its machine-profiled JSON report for release evidence.

## Budgets

| Scenario | Default fixture | p95 duration | p95 allocation |
|---|---:|---:|---:|
| History repository/materialization | 10,000 events | 500 ms | 48 MiB |
| Snapshot comparison | 100,000 files | 500 ms | 64 MiB |
| Snapshot-comparison cancellation | 100,000 files | 250 ms | Not measured |

These are release ceilings, not optimization targets. A passing median cannot
hide a failing p95, allocation budget, or cancellation tail. Run without
`--enforce` to gather diagnostic evidence without returning a failing exit
code. Fixture sizes and iterations can be overridden with `--history-events`,
`--file-count`, and `--iterations`, but altered runs do not replace the release
profile.

## Qualification notes

- Do not compare Debug and Release results.
- Record power mode and whether the machine was virtualized in the release PR.
- Rerun a failed scenario once on an idle machine. Treat a repeated failure as
  a release blocker or document an explicitly reviewed budget change.
- Treat the cross-platform workflow as a repeatable regression gate and the
  release-machine run as the hardware-specific baseline. Keep both sets of
  JSON evidence because hosted-runner timing does not replace a controlled
  release-machine measurement.

## Historical Chronicle baseline (1.8.8)

The first controlled run used macOS 27 on Arm64, .NET 10.0.11, eight logical
processors, workstation GC, and commit `3131ad2`. All release budgets passed.
The complete report is stored as
[`chronicle-macos-arm64.json`](release-evidence/baselines/chronicle-macos-arm64.json).

| Scenario | p50 | p95 | p95 allocation |
|---|---:|---:|---:|
| History repository/materialization | 65.82 ms | 100.59 ms | 12.77 MiB |
| Snapshot comparison | 74.49 ms | 84.95 ms | 29.39 MiB |
| Snapshot-comparison cancellation | 51.21 ms | 55.04 ms | Not measured |
