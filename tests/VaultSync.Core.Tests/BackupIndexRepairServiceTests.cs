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

public sealed class BackupIndexRepairServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public BackupIndexRepairServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "vaultsync.db");
    }

    [Fact]
    public void BuildPlan_FindsExactBackupProjectRemapAction()
    {
        var repo = CreateRepository();
        var projectA = CreateProject(repo, "Alpha");
        var projectB = CreateProject(repo, "Bravo");
        var snapshotId = repo.CreateSnapshot(projectA, 10, 1024);
        var backupId = repo.CreateBackupFromMetadata(
            "backup-one",
            projectB,
            snapshotId,
            DateTime.UtcNow,
            "manual",
            1024,
            "alpha/backup",
            _tempDir,
            "Primary",
            isProtected: false,
            isImported: false);

        var service = new BackupIndexRepairService(repo);
        var plan = service.BuildPlan();

        var action = Assert.Single(plan.Actions);
        Assert.Equal(BackupIndexRepairCode.ReassignBackupProjectFromSnapshot, action.Code);
        Assert.Equal(backupId, action.BackupId);
        Assert.Equal(projectB, action.CurrentProjectId);
        Assert.Equal(projectA, action.TargetProjectId);
    }

    [Fact]
    public void ApplyPlan_ReassignsBackupProjectToSnapshotOwner()
    {
        var repo = CreateRepository();
        var projectA = CreateProject(repo, "Alpha");
        var projectB = CreateProject(repo, "Bravo");
        var snapshotId = repo.CreateSnapshot(projectA, 10, 1024);
        var backupId = repo.CreateBackupFromMetadata(
            "backup-one",
            projectB,
            snapshotId,
            DateTime.UtcNow,
            "manual",
            1024,
            "alpha/backup",
            _tempDir,
            "Primary",
            isProtected: false,
            isImported: false);

        var service = new BackupIndexRepairService(repo);
        var plan = service.BuildPlan();
        var applied = service.ApplyPlan(plan);

        Assert.Equal(1, applied);
        var repaired = Assert.Single(repo.GetAllBackups(), backup => backup.Id == backupId);
        Assert.Equal(projectA, repaired.ProjectId);
    }

    [Fact]
    public void BuildPlan_ReportsBlockedIssues_ForMissingSnapshotOrProject()
    {
        var repo = CreateRepository();
        var projectId = CreateProject(repo, "Alpha");
        var snapshotId = repo.CreateSnapshot(projectId, 10, 1024);
        var backupId = repo.CreateBackupFromMetadata(
            "backup-one",
            projectId,
            snapshotId,
            DateTime.UtcNow,
            "manual",
            1024,
            "alpha/backup",
            _tempDir,
            "Primary",
            isProtected: false,
            isImported: false);

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            connection.Execute("PRAGMA foreign_keys = OFF;");
            connection.Execute(
                "UPDATE backups SET snapshot_id = @snapshotId, project_id = @projectId WHERE id = @id;",
                new { snapshotId = 9999, projectId = 9999, id = backupId });
            connection.Execute("PRAGMA foreign_keys = ON;");
        }

        var service = new BackupIndexRepairService(repo);
        var plan = service.BuildPlan();

        Assert.Empty(plan.Actions);
        Assert.Contains(plan.BlockedIssues, issue => issue.Code == BackupIndexRepairCode.BackupSnapshotMissing);
        Assert.Contains(plan.BlockedIssues, issue => issue.Code == BackupIndexRepairCode.BackupProjectMissing);
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
            RootPath = Path.Combine(_tempDir, name),
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
