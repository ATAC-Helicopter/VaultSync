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

public sealed class BackupIndexConsistencyServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public BackupIndexConsistencyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-consistency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "vaultsync.db");
    }

    [Fact]
    public void Scan_WithHealthyIndex_ReturnsNoFindings()
    {
        var repo = CreateRepository();
        var projectId = repo.AddProject(new Project
        {
            Name = "Project One",
            RootPath = Path.Combine(_tempDir, "ProjectOne"),
            Preset = "dotnet"
        });
        var snapshotId = repo.CreateSnapshot(projectId, 10, 2048);
        repo.CreateBackup(projectId, snapshotId, "manual", 1024, "project-one/backup", _tempDir, "Primary");

        var service = new BackupIndexConsistencyService(repo);
        var report = service.Scan();

        Assert.False(report.HasIssues);
        Assert.Empty(report.Findings);
        Assert.Equal(1, report.ProjectCount);
        Assert.Equal(1, report.SnapshotCount);
        Assert.Equal(1, report.BackupCount);
    }

    [Fact]
    public void Scan_DetectsDuplicateExternalIds_AndProjectMismatch()
    {
        var repo = CreateRepository();
        var projectOneId = repo.AddProject(new Project
        {
            Name = "Project One",
            RootPath = Path.Combine(_tempDir, "ProjectOne"),
            Preset = "dotnet"
        });
        var projectTwoId = repo.AddProject(new Project
        {
            Name = "Project Two",
            RootPath = Path.Combine(_tempDir, "ProjectTwo"),
            Preset = "dotnet"
        });
        var snapshotId = repo.CreateSnapshot(projectOneId, 20, 4096);
        var secondSnapshotId = repo.CreateSnapshot(projectTwoId, 30, 8192);
        var backupId = repo.CreateBackupFromMetadata(
            "backup-dup",
            projectTwoId,
            snapshotId,
            DateTime.UtcNow,
            "manual",
            2048,
            "project-two/backup",
            _tempDir,
            "Primary",
            isProtected: false,
            isImported: false);

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            connection.Execute("UPDATE projects SET external_id = 'dup-project';");
            connection.Execute("UPDATE snapshots SET external_id = 'dup-snapshot' WHERE id IN @ids;", new { ids = new[] { snapshotId, secondSnapshotId } });
            connection.Execute("UPDATE backups SET external_id = 'dup-backup' WHERE id = @id;", new { id = backupId });
            connection.Execute("INSERT INTO backups(external_id, project_id, snapshot_id, created_utc, type, backup_mode, total_bytes, path, destination_path, destination_alias, origin_machine_name, is_protected, is_encrypted, crypto_descriptor_json, is_imported) VALUES('dup-backup', @projectId, @snapshotId, @createdUtc, 'manual', 'full', 1, 'dup-path', @dest, 'Primary', '', 0, 0, '{}', 0);",
                new
                {
                    projectId = projectTwoId,
                    snapshotId,
                    createdUtc = DateTime.UtcNow.ToString("u"),
                    dest = _tempDir
                });
        }

        var service = new BackupIndexConsistencyService(repo);
        var report = service.Scan();

        Assert.True(report.HasIssues);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.DuplicateProjectExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.DuplicateSnapshotExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.DuplicateBackupExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.BackupSnapshotProjectMismatch);
    }

    [Fact]
    public void Scan_DetectsMissingExternalIds()
    {
        var repo = CreateRepository();
        var projectId = repo.AddProject(new Project
        {
            Name = "Project No External",
            RootPath = Path.Combine(_tempDir, "ProjectNoExternal"),
            Preset = "dotnet"
        });
        var snapshotId = repo.CreateSnapshot(projectId, 5, 512);
        var backupId = repo.CreateBackup(projectId, snapshotId, "manual", 64, "project-no-external/backup", _tempDir, "Primary");

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        connection.Execute("UPDATE projects SET external_id = '' WHERE id = @id;", new { id = projectId });
        connection.Execute("UPDATE snapshots SET external_id = '' WHERE project_id = @projectId;", new { projectId });
        connection.Execute("UPDATE backups SET external_id = '' WHERE id = @id;", new { id = backupId });

        var service = new BackupIndexConsistencyService(repo);
        var report = service.Scan();

        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.MissingProjectExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.MissingSnapshotExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.MissingBackupExternalId);
    }

    private SqliteRepository CreateRepository()
    {
        var repo = new SqliteRepository(_dbPath);
        repo.EnsureSchema();
        return repo;
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
