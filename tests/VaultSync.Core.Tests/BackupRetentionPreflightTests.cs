using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupRetentionPreflightTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();
    private readonly string _dbPath;
    private readonly string _backupRoot;

    public BackupRetentionPreflightTests()
    {
        _dbPath = Path.Combine(_tempDir.Path, "vaultsync.db");
        _backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(_backupRoot);
    }

    [Fact]
    public void EnforceRetentionForProject_BlocksPrune_WhenItWouldRemoveLastValidRestorePoint()
    {
        SqliteRepository repo = CreateRepository();
        var service = new BackupService(repo);
        int projectId = CreateProject(repo, "Retention Project");
        int otherProjectId = CreateProject(repo, "Other Project");
        int validSnapshotId = repo.CreateSnapshot(projectId, 10, 1024);
        int invalidSnapshotId = repo.CreateSnapshot(otherProjectId, 20, 2048);

        string validPath = "retention-project/2026-03-12_10-00-00";
        string invalidPath = "retention-project/2026-03-12_11-00-00";

        repo.CreateBackupFromMetadata(
            "backup-valid",
            projectId,
            validSnapshotId,
            new DateTime(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc),
            "manual",
            1024,
            validPath,
            _backupRoot,
            "Primary",
            isProtected: false,
            isImported: false);

        repo.CreateBackupFromMetadata(
            "backup-invalid",
            projectId,
            invalidSnapshotId,
            new DateTime(2026, 3, 12, 11, 0, 0, DateTimeKind.Utc),
            "manual",
            2048,
            invalidPath,
            _backupRoot,
            "Primary",
            isProtected: false,
            isImported: false);

        Directory.CreateDirectory(Path.Combine(_backupRoot, validPath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.Combine(_backupRoot, invalidPath.Replace('/', Path.DirectorySeparatorChar)));

        service.EnforceRetentionForProject(projectId, _backupRoot, maxSnapshotsToKeep: 1);

        var remainingBackups = repo.GetBackupsForProject(projectId).ToList();
        Assert.Equal(2, remainingBackups.Count);
        Assert.Contains(remainingBackups, backup => backup.ExternalId == "backup-valid");
        Assert.Contains(remainingBackups, backup => backup.ExternalId == "backup-invalid");
    }

    [Fact]
    public void EnforceRetentionForProject_AllowsPrune_WhenAnotherValidRestorePointRemains()
    {
        SqliteRepository repo = CreateRepository();
        var service = new BackupService(repo);
        int projectId = CreateProject(repo, "Retention Project");
        int snapshotA = repo.CreateSnapshot(projectId, 10, 1024);
        int snapshotB = repo.CreateSnapshot(projectId, 20, 2048);

        string oldPath = "retention-project/2026-03-12_10-00-00";
        string newPath = "retention-project/2026-03-12_11-00-00";

        repo.CreateBackupFromMetadata(
            "backup-old",
            projectId,
            snapshotA,
            new DateTime(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc),
            "manual",
            1024,
            oldPath,
            _backupRoot,
            "Primary",
            isProtected: false,
            isImported: false);

        repo.CreateBackupFromMetadata(
            "backup-new",
            projectId,
            snapshotB,
            new DateTime(2026, 3, 12, 11, 0, 0, DateTimeKind.Utc),
            "manual",
            2048,
            newPath,
            _backupRoot,
            "Primary",
            isProtected: false,
            isImported: false);

        Directory.CreateDirectory(Path.Combine(_backupRoot, oldPath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.Combine(_backupRoot, newPath.Replace('/', Path.DirectorySeparatorChar)));

        service.EnforceRetentionForProject(projectId, _backupRoot, maxSnapshotsToKeep: 1);

        var remainingBackups = repo.GetBackupsForProject(projectId).ToList();
        Assert.Single(remainingBackups);
        Assert.Equal("backup-new", remainingBackups[0].ExternalId);
    }

    [Fact]
    public void EnforceRetentionForProject_KeepsSnapshotMetadataProtectedBackup()
    {
        SqliteRepository repo = CreateRepository();
        var service = new BackupService(repo);
        int projectId = CreateProject(repo, "Protected Metadata Project");
        int protectedSnapshotId = repo.CreateSnapshot(projectId, 10, 1024);
        int middleSnapshotId = repo.CreateSnapshot(projectId, 20, 2048);
        int newestSnapshotId = repo.CreateSnapshot(projectId, 30, 4096);

        string protectedPath = "protected-metadata/2026-03-12_10-00-00";
        string middlePath = "protected-metadata/2026-03-12_11-00-00";
        string newestPath = "protected-metadata/2026-03-12_12-00-00";

        repo.CreateBackupFromMetadata(
            "backup-protected-by-snapshot",
            projectId,
            protectedSnapshotId,
            new DateTime(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc),
            "manual",
            1024,
            protectedPath,
            _backupRoot,
            "Primary",
            isProtected: false,
            isImported: false);
        repo.CreateBackupFromMetadata(
            "backup-middle",
            projectId,
            middleSnapshotId,
            new DateTime(2026, 3, 12, 11, 0, 0, DateTimeKind.Utc),
            "manual",
            2048,
            middlePath,
            _backupRoot,
            "Primary",
            isProtected: false,
            isImported: false);
        repo.CreateBackupFromMetadata(
            "backup-newest",
            projectId,
            newestSnapshotId,
            new DateTime(2026, 3, 12, 12, 0, 0, DateTimeKind.Utc),
            "manual",
            4096,
            newestPath,
            _backupRoot,
            "Primary",
            isProtected: false,
            isImported: false);
        repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = protectedSnapshotId,
            IsProtected = true
        });

        Directory.CreateDirectory(Path.Combine(_backupRoot, protectedPath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.Combine(_backupRoot, middlePath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.Combine(_backupRoot, newestPath.Replace('/', Path.DirectorySeparatorChar)));

        service.EnforceRetentionForProject(projectId, _backupRoot, maxSnapshotsToKeep: 1);

        var remainingBackups = repo.GetBackupsForProject(projectId).Select(backup => backup.ExternalId).ToList();
        Assert.Contains("backup-protected-by-snapshot", remainingBackups);
        Assert.Contains("backup-newest", remainingBackups);
        Assert.DoesNotContain("backup-middle", remainingBackups);
    }

    [Fact]
    public void BuildRetentionDeletionPlan_SkipsLastValidRestorePoint_AndSelectsNextEligibleCandidate()
    {
        int projectId = 42;
        int otherProjectId = 7;
        int validSnapshotId = 100;
        int invalidSnapshotId = 200;

        var validOld = new Backup
        {
            Id = 1,
            ProjectId = projectId,
            SnapshotId = validSnapshotId,
            CreatedUtc = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)
        };
        var invalidNewer = new Backup
        {
            Id = 2,
            ProjectId = projectId,
            SnapshotId = invalidSnapshotId,
            CreatedUtc = new DateTime(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc)
        };

        Dictionary<int, Snapshot> snapshots = new[]
        {
            new Snapshot { Id = validSnapshotId, ProjectId = projectId },
            new Snapshot { Id = invalidSnapshotId, ProjectId = otherProjectId }
        }.ToDictionary(x => x.Id);

        System.Collections.Generic.IReadOnlyList<BackupService.BackupRetentionCandidateDecision> plan = BackupService.BuildRetentionDeletionPlan(
            projectId,
            new[] { validOld, invalidNewer },
            new[] { validOld, invalidNewer },
            snapshots,
            deleteQuota: 1);

        Assert.Contains(plan, x => x.BackupId == 1 && !x.Selected && x.Code == "preserve-last-restorable-point");
        Assert.Contains(plan, x => x.BackupId == 2 && x.Selected && x.Code == "selected");
    }

    [Fact]
    public void BuildRetentionDeletionPlan_StopsSelectingOnceQuotaIsSatisfied()
    {
        int projectId = 5;
        int snapshotId = 10;
        Backup[] backups = new[]
        {
            new Backup { Id = 1, ProjectId = projectId, SnapshotId = snapshotId, CreatedUtc = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc) },
            new Backup { Id = 2, ProjectId = projectId, SnapshotId = snapshotId, CreatedUtc = new DateTime(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc) },
            new Backup { Id = 3, ProjectId = projectId, SnapshotId = snapshotId, CreatedUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc) }
        };
        var snapshots = new[] { new Snapshot { Id = snapshotId, ProjectId = projectId } }.ToDictionary(x => x.Id);

        System.Collections.Generic.IReadOnlyList<BackupService.BackupRetentionCandidateDecision> plan = BackupService.BuildRetentionDeletionPlan(projectId, backups, backups, snapshots, deleteQuota: 1);

        Assert.Contains(plan, x => x.BackupId == 1 && x.Selected);
        Assert.Contains(plan, x => x.BackupId == 2 && !x.Selected && x.Code == "quota-satisfied");
        Assert.Contains(plan, x => x.BackupId == 3 && !x.Selected && x.Code == "quota-satisfied");
    }

    [Fact]
    public void BuildRetentionDeletionPlan_PreservesLastByteVerifiedPoint()
    {
        int projectId = 5;
        Backup[] backups =
        [
            new Backup { Id = 1, ProjectId = projectId, SnapshotId = 10, CreatedUtc = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc) },
            new Backup { Id = 2, ProjectId = projectId, SnapshotId = 20, CreatedUtc = new DateTime(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc) },
            new Backup { Id = 3, ProjectId = projectId, SnapshotId = 30, CreatedUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc) }
        ];
        Dictionary<int, Snapshot> snapshots = backups.ToDictionary(
            backup => backup.SnapshotId,
            backup => new Snapshot { Id = backup.SnapshotId, ProjectId = projectId });

        System.Collections.Generic.IReadOnlyList<BackupService.BackupRetentionCandidateDecision> plan =
            BackupService.BuildRetentionDeletionPlan(
                projectId,
                backups,
                backups,
                snapshots,
                deleteQuota: 1,
                byteVerifiedBackupIds: new HashSet<int> { 1 });

        Assert.Contains(plan, decision =>
            decision.BackupId == 1 &&
            !decision.Selected &&
            decision.Code == "preserve-last-byte-verified-point");
        Assert.Contains(plan, decision => decision.BackupId == 2 && decision.Selected);
    }

    private SqliteRepository CreateRepository()
    {
        return TestRepository.Create(_dbPath);
    }

    private int CreateProject(SqliteRepository repo, string name)
    {
        return TestRepository.AddProject(repo, name, Path.Combine(_tempDir.Path, name.Replace(' ', '_')));
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }
}
