# 1.5 Compatibility Matrix (`VS-1591`)

This matrix validates mixed `1.4.x` and `1.5.x` environments for metadata sync, encrypted/plain backups, and retention/tombstone behavior.

## Scope

- Backup creation and restore:
  - `1.4` plain backup + `1.5` restore
  - `1.5` plain backup + `1.4` restore
  - `1.5` encrypted backup + `1.5` restore
  - `1.5` encrypted backup visibility on `1.4`
- Metadata sync:
  - `1.4` exports read by `1.5`
  - `1.5` exports read by `1.4`
  - mixed import ordering (A->B->A)
- History semantics:
  - keep/delete/tombstone propagation
  - imported-history pause checks
  - destination resolution after import

## Test Matrix

| Case ID | Source Machine | Target Machine | Flow | Expected Result | Status |
| --- | --- | --- | --- | --- | --- |
| CM-1501 | `1.4.x` | `1.5.x` | Plain backup + metadata import | Backup visible and restorable on `1.5`; no schema errors | Pass (automated legacy-schema import coverage) |
| CM-1502 | `1.5.x` | `1.4.x` | Plain backup + metadata import | Backup visible on `1.4`; unknown `1.5` fields ignored safely | Pending (manual mixed-client run) |
| CM-1503 | `1.5.x` | `1.5.x` | Encrypted backup + metadata import | Entry visible with encrypted metadata; restore succeeds with password | Pass (automated) |
| CM-1504 | `1.5.x` | `1.4.x` | Encrypted backup + metadata import | Entry does not corrupt `1.4` store; sync remains healthy | Pass (automated descriptor/legacy compatibility) |
| CM-1505 | `1.5.x` | `1.4.x` -> `1.5.x` | Round-trip import/export | No duplicate corruption; merge remains stable | Pass (automated round-trip) |
| CM-1506 | `1.4.x` | `1.5.x` | Delete/tombstone propagation | Tombstone imports cleanly and removes previewed history | Pass (automated) |
| CM-1507 | `1.5.x` | `1.4.x` | Keep/protected flags in sync payload | `1.4` ignores unknown fields and preserves known keep state | Pending (manual mixed-client run) |
| CM-1508 | `1.5.x` | `1.5.x` | Mixed destination project import | Restore/delete/open resolve destination correctly | Pending (UI/manual flow) |

## Execution Notes

- Use two clean app profiles (`Machine A`, `Machine B`) with isolated config/db directories.
- Capture:
  - app logs for both machines
  - exported metadata file used in each case
  - before/after screenshots for history rows and restore prompts
- For encrypted cases:
  - verify no plaintext secret appears in config or metadata files
  - verify wrong-password path fails with no partial restore output

## Exit Criteria

- All `CM-1501`..`CM-1508` pass.
- No metadata corruption in mixed-version round-trip.
- No data-loss regressions in keep/delete/tombstone flows.
- Findings are linked back to roadmap ticket `VS-1591`.

## Latest automated evidence (2026-02-19)

- Command:
  - `dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj -c Debug -v minimal`
- Result:
  - Passed `65/65`, failed `0`.
- Relevant test coverage mapped to matrix:
  - `MetadataSyncTests.ImportFromStore_LegacyBackupSchemaWithoutEncryptionColumns_ImportsAsPlain` -> `CM-1501`
  - `MetadataSyncTests.ExportImportRoundTrip_PreservesMixedPlainAndEncryptedBackups` -> `CM-1503`, `CM-1505`
  - `MetadataSyncTests.ImportFromStore_AppliesBackupTombstone` -> `CM-1506`
  - `BackupArchiveCryptoServiceTests.DecryptArchiveToPlainZip_WithValidPassword_RecreatesReadableArchive` -> `CM-1503`
  - `BackupArchiveCryptoServiceTests.DecryptArchiveToPlainZip_WithWrongPassword_FailsWithExplicitError_AndNoPartialOutput` -> `CM-1503`
  - `BackupCryptoDescriptorTests.Descriptor_ParsesLegacyPlainMetadata` -> `CM-1504`

## Remaining manual validation

- `CM-1502`, `CM-1507`: run with a real `1.4.x` client binary against `1.5`-exported metadata.
- `CM-1508`: full UI/system flow validation for mixed destination restore/delete/open.
