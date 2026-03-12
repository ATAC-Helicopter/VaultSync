using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupRetentionPreflightTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _backupRoot;

    public BackupRetentionPreflightTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "vaultsync.db");
        _backupRoot = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(_backupRoot);
    }

    [Fact]
    public void EnforceRetentionForProject_BlocksPrune_WhenItWouldRemoveLastValidRestorePoint()
    {
        var repo = CreateRepository();
        var service = new BackupService(repo);
        var projectId = CreateProject(repo, "Retention Project");
        var otherProjectId = CreateProject(repo, "Other Project");
        var validSnapshotId = repo.CreateSnapshot(projectId, 10, 1024);
        var invalidSnapshotId = repo.CreateSnapshot(otherProjectId, 20, 2048);

        var validPath = "retention-project/2026-03-12_10-00-00";
        var invalidPath = "retention-project/2026-03-12_11-00-00";

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
        var repo = CreateRepository();
        var service = new BackupService(repo);
        var projectId = CreateProject(repo, "Retention Project");
        var snapshotA = repo.CreateSnapshot(projectId, 10, 1024);
        var snapshotB = repo.CreateSnapshot(projectId, 20, 2048);

        var oldPath = "retention-project/2026-03-12_10-00-00";
        var newPath = "retention-project/2026-03-12_11-00-00";

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

    private SqliteRepository CreateRepository()
    {
        var repo = new SqliteRepository(_dbPath);
        repo.EnsureSchema();
        return repo;
    }

    private int CreateProject(SqliteRepository repo, string name)
    {
        return repo.AddProject(new Project
        {
            Name = name,
            RootPath = Path.Combine(_tempDir, name.Replace(' ', '_')),
            Preset = "dotnet"
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }
}
