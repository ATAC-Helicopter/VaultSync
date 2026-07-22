# Disaster recovery in VaultSync

VaultSync 1.8.4 adds local, explainable disaster-recovery checks. It does not upload backup metadata, contact a hosted service, or claim that a backup is recoverable without examining it.

## Recovery drills

A recovery drill opens the newest recorded recovery point for a project without copying files back into the project. It checks:

1. The project, backup, and snapshot records still link to one another.
2. The recorded recovery point can be found at a configured or recorded destination.
3. A folder or ZIP payload can be opened and inventoried, up to 5,000 files.
4. When a complete snapshot file count exists, the stored inventory count matches it.

The result is **Passed**, **Attention**, or **Failed** and is stored in the local VaultSync SQLite database. The Recovery page shows the latest result and the exact check that needs attention. The exported Markdown recovery report includes drill coverage. History is bounded to the newest 20 drills per project.

An encrypted recovery point is intentionally reported as limited unless it is unlocked: the drill validates that the encrypted payload and descriptor are present, but it does not request or retain a password merely to improve a score. A passed drill is evidence that the tested checks succeeded; it is not a replacement for periodically performing a real restore on important data.

Relevant implementation:

- `src/VaultSync.Core/Services/RecoveryDrillService.cs`
- `src/VaultSync.Core/Models/DisasterRecoveryModels.cs`
- `src/VaultSync.Core/Repositories/SqliteRepository.cs`

## The 3-2-1 advisor

For every project, the advisor measures:

- **3 copies:** the project source plus currently reachable recovery points on distinct recorded destinations.
- **2 media:** distinct local volumes, mounted volumes, or network authorities represented by those reachable copies.
- **1 offsite:** at least one reachable destination that the user explicitly marked as offsite.

VaultSync does not infer physical location from a path, mount name, hostname, or protocol. A NAS in the same room is not automatically offsite, and a cloud-mounted folder cannot be proven offsite from its filesystem path. Configure this in **Settings > Backups > Backup destinations > Count as offsite copy** only when you know the destination is held at a different physical location.

The advisor resolves each record only through its recorded destination identity (or an explicitly matching moved alias). Missing and disconnected payloads remain in history but do not count toward copies, media, or offsite readiness. VaultSync does not silently mount storage, read credentials, or create a backup while the Recovery page is being viewed.

Relevant implementation:

- `src/VaultSync.Core/Services/DisasterRecoveryAdvisorService.cs`
- `src/VaultSync.Core/Services/BackupContentPathResolver.cs`
- `src/VaultSync.Core/Config/AppConfig.cs`

## Protected recovery points and recommendations

Protected points use the existing History/Backups protection marker and are excluded from automatic retention cleanup. VaultSync recommends an unprotected point when it detects, in priority order:

- a release, version, delivery, final, or submission label/tag;
- a large deletion;
- high project churn;
- no protected recent baseline before cleanup or risky work.

Recommendations never protect data automatically. The user must press **Protect point**, and the same state then appears in Recovery, History, and Backups.

## Local data and removal

Drill history contains local database IDs, timestamps, status, counts, and human-readable check results. It contains no file content, credentials, or transmitted identifier. Deleting the associated project, snapshot, or backup removes its drill rows through SQLite foreign-key cleanup. Removing the VaultSync metadata database removes all drill history.

## Verification checklist

- Disconnect a destination and confirm the drill fails with a reachability action.
- Reconnect it and confirm a readable folder/ZIP can pass.
- Confirm an encrypted point is marked limited without prompting for a password.
- Mark exactly one destination as offsite and verify only projects with a copy there receive offsite credit.
- Protect a recommended point and confirm the recommendation disappears and retention protection is visible in History and Backups.
- Export the Recovery report and confirm it includes 3-2-1, drill, protected-point, and per-project details.
