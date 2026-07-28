# Native recoverability engine

VaultSync 1.8.4 incorporates the useful deterministic recovery logic developed in the ProofRestore Build Off prototype. ProofRestore is source provenance, not a runtime dependency: VaultSync does not embed its Next.js application, merge its repository, start a browser, call OpenAI, or contact a hosted service.

The port is native C# under `VaultSync.Core.Recoverability` and works entirely with VaultSync projects, snapshots, backup identities, and local storage adapters.

![Recovery drill evidence produced by the native recoverability engine](images/Recovery_Drill.png)

## What the proof establishes

A recovery proof answers a narrower and more defensible question than “did the backup job finish?”:

> For the selected snapshot path, which stored file bytes can VaultSync read now, which bytes match the snapshot's expected SHA-256 and size, and what would a restore need to do?

The proof does not write restored files. It selects a bounded set, reads stored content, evaluates it, and returns a versioned result (`1.0`) containing:

- an overall verdict: fully recoverable, partially recoverable, unrecoverable, or inconclusive;
- item verdicts: verified, unavailable, corrupted, or inconclusive;
- dry-run actions: create, overwrite, skip identical, conflict, unavailable, or not evaluated;
- selected and verified file/byte totals;
- stable evidence IDs, codes, severity, path, and explanation.

Reachability is not verification. A folder or archive can exist and open while one of its files is missing or corrupt. VaultSync only emits `hash_match` after it has read the complete stored file and matched both its SHA-256 and size. Missing expected hashes, locked encrypted content, access failures, and safety limits remain inconclusive rather than being promoted to success.

## Execution flow

```text
Recovery page
  -> newest project backup and linked snapshot
  -> expected file rows from local SQLite metadata
  -> recorded-destination resolution
  -> bounded folder or ZIP adapter
  -> complete SHA-256 + size observations
  -> deterministic item verdicts
  -> read-only destination conflict simulation
  -> bounded drill evidence in SQLite
  -> expandable UI detail and Markdown evidence appendix
```

`RecoverabilityService` is the decision boundary. `SnapshotExplorerService.ReadStoredFileEvidenceAsync` is the storage adapter used for ordinary folders and plain ZIP backups. It reuses Snapshot Explorer's path normalization, duplicate ZIP rejection, linked-source rejection, and archive format detection.

The default proof selects at most 5,000 files and reads at most 2 GiB of uncompressed content. Extra matching identities are omitted with `selection_limit_reached`; selected files that exceed the byte budget receive `verification_limit_reached`. Neither state is called missing or verified. Cancellation is checked between files and passed into each asynchronous hash operation.

## Path and archive safety

Snapshot paths are treated as untrusted data because metadata can be imported from another machine.

The engine:

- uses relative POSIX-style identities and case-sensitive matching;
- rejects empty file identities, absolute paths, drive paths, NULs, `.` and `..` segments, repeated empty segments, and duplicate normalized paths;
- resolves folder content under the selected backup root;
- refuses linked source or destination components that could redirect a read outside that root;
- preserves filesystem-root destinations while applying the same containment checks;
- rejects duplicate ZIP file entries instead of choosing one ambiguously;
- uses ZIP entries' uncompressed sizes for the global read budget;
- never searches an unrelated destination for same-named content.

The original-location simulation also resolves every destination path beneath the project root. It hashes an existing same-size destination to identify an identical file; otherwise a newer different file is a conflict and an older different file is a potential overwrite. These are plans only—no directory or file is created.

## Encrypted and offline recovery points

Locked encrypted archives are detected and their descriptor can be checked by the surrounding recovery drill, but the native proof does not request a password in the background. Without decrypted bytes it returns inconclusive evidence. Offline or missing destinations fail availability and cannot count as recoverable or toward 3-2-1 readiness.

This separation prevents a convenient score from causing surprise credential prompts or false claims.

## Retention interaction

Metadata-valid and byte-verified are different trust levels. Existing retention preflight already preserves the last metadata-valid restore point. In 1.8.4, deletion planning also receives the set of backups whose latest stored drill passed:

- if multiple byte-verified points exist, an old verified point can be selected normally;
- if only one remains, it receives `preserve-last-byte-verified-point`;
- retention may choose another eligible candidate instead;
- a proof never protects a point permanently—the user-facing protection marker remains the explicit permanent control.

The Settings retention simulation uses the same deletion plan as real cleanup.

## Evidence persistence and reports

Recovery drills retain aggregate integrity and restore-plan checks plus at most 100 warning/error evidence rows. The per-project history remains bounded to 20 drills. This prevents a large backup from growing the metadata database without limit.

The Recovery page exposes the latest proof in an expandable, selectable panel. Markdown export contains a recovery-proof appendix with code, result, path, evidence ID, and detail. Reports contain metadata and findings, not file contents, hashes, credentials, or destination paths.

## ProofRestore provenance

The following ProofRestore concepts were intentionally preserved:

- strict versioned results;
- deterministic path and snapshot inputs;
- stable evidence codes;
- separate item and request verdicts;
- availability, hash, and size checks;
- safe-copy and original-location planning;
- explicit conflict outcomes;
- retention awareness;
- deterministic Markdown reporting;
- an absolute rule that presentation or language models cannot change a verdict.

The prototype's manifest import, Next.js UI, browser Recovery Lab, optional natural-language interpreter, and hosted deployment model were not ported. VaultSync already has stronger native sources of truth: its SQLite snapshot inventory, recorded destination identities, backup encryption format, Recovery UI, and retention engine.

## Primary implementation and tests

- `src/VaultSync.Core/Recoverability/RecoverabilityModels.cs`
- `src/VaultSync.Core/Recoverability/RecoverabilityService.cs`
- `src/VaultSync.Core/Services/SnapshotExplorerService.cs`
- `src/VaultSync.Core/Services/RecoveryDrillService.cs`
- `src/VaultSync.Core/Services/BackupService.cs`
- `src/VaultSync.UI/ViewModels/RecoveryViewModel.cs`
- `src/VaultSync.UI/Services/RecoveryReportExporter.cs`
- `tests/VaultSync.Core.Tests/RecoverabilityServiceTests.cs`
- `tests/VaultSync.Core.Tests/RecoveryDrillServiceTests.cs`
- `tests/VaultSync.Core.Tests/BackupRetentionPreflightTests.cs`

Regression coverage includes healthy folder and ZIP content, missing and corrupt objects, locked encryption, read limits, case-sensitive and segment-bounded selection, unsafe and duplicate paths, identical destinations, newer conflicts, stable evidence IDs, non-destructive drills, report evidence, and preservation of the last byte-verified point.
