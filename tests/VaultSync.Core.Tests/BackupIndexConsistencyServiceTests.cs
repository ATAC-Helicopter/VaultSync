using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupIndexConsistencyServiceTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();
    private readonly string _dbPath;

    public BackupIndexConsistencyServiceTests()
    {
        _dbPath = Path.Combine(_tempDir.Path, "vaultsync.db");
    }

    [Fact]
    public void Scan_WithHealthyIndex_ReturnsNoFindings()
    {
        SqliteRepository repo = CreateRepository();
        int projectId = TestRepository.AddProject(repo, "Project One", Path.Combine(_tempDir.Path, "ProjectOne"));
        int snapshotId = repo.CreateSnapshot(projectId, 10, 2048);
        repo.CreateBackup(projectId, snapshotId, "manual", 1024, "project-one/backup", _tempDir.Path, "Primary");

        var service = new BackupIndexConsistencyService(repo);
        BackupIndexConsistencyReport report = service.Scan();

        Assert.False(report.HasIssues);
        Assert.Empty(report.Findings);
        Assert.Equal(1, report.ProjectCount);
        Assert.Equal(1, report.SnapshotCount);
        Assert.Equal(1, report.BackupCount);
    }

    [Fact]
    public void Scan_DetectsDuplicateExternalIds_AndProjectMismatch()
    {
        SqliteRepository repo = CreateRepository();
        int projectOneId = TestRepository.AddProject(repo, "Project One", Path.Combine(_tempDir.Path, "ProjectOne"));
        int projectTwoId = TestRepository.AddProject(repo, "Project Two", Path.Combine(_tempDir.Path, "ProjectTwo"));
        int snapshotId = repo.CreateSnapshot(projectOneId, 20, 4096);
        int secondSnapshotId = repo.CreateSnapshot(projectTwoId, 30, 8192);
        int backupId = repo.CreateBackupFromMetadata(
            "backup-dup",
            projectTwoId,
            snapshotId,
            DateTime.UtcNow,
            "manual",
            2048,
            "project-two/backup",
            _tempDir.Path,
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
                    dest = _tempDir.Path
                });
        }

        var service = new BackupIndexConsistencyService(repo);
        BackupIndexConsistencyReport report = service.Scan();

        Assert.True(report.HasIssues);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.DuplicateProjectExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.DuplicateSnapshotExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.DuplicateBackupExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.BackupSnapshotProjectMismatch);
    }

    [Fact]
    public void Scan_DetectsMissingExternalIds()
    {
        SqliteRepository repo = CreateRepository();
        int projectId = TestRepository.AddProject(repo, "Project No External", Path.Combine(_tempDir.Path, "ProjectNoExternal"));
        int snapshotId = repo.CreateSnapshot(projectId, 5, 512);
        int backupId = repo.CreateBackup(projectId, snapshotId, "manual", 64, "project-no-external/backup", _tempDir.Path, "Primary");

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        connection.Execute("UPDATE projects SET external_id = '' WHERE id = @id;", new { id = projectId });
        connection.Execute("UPDATE snapshots SET external_id = '' WHERE project_id = @projectId;", new { projectId });
        connection.Execute("UPDATE backups SET external_id = '' WHERE id = @id;", new { id = backupId });

        var service = new BackupIndexConsistencyService(repo);
        BackupIndexConsistencyReport report = service.Scan();

        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.MissingProjectExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.MissingSnapshotExternalId);
        Assert.Contains(report.Findings, f => f.Code == BackupIndexConsistencyCode.MissingBackupExternalId);
    }

    [Fact]
    public void Scan_SortsSamplesDeterministically_AndBuildsStableSummary()
    {
        SqliteRepository repo = CreateRepository();
        int projectB = TestRepository.AddProject(repo, "Zulu", Path.Combine(_tempDir.Path, "Zulu"));
        int projectA = TestRepository.AddProject(repo, "Alpha", Path.Combine(_tempDir.Path, "Alpha"));

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        connection.Execute("UPDATE projects SET external_id = '' WHERE id IN @ids;", new { ids = new[] { projectB, projectA } });

        var service = new BackupIndexConsistencyService(repo);
        BackupIndexConsistencyReport report = service.Scan();
        BackupIndexConsistencySummary summary = BackupIndexConsistencyService.BuildSummary(report);

        BackupIndexConsistencyFinding finding = Assert.Single(report.Findings, f => f.Code == BackupIndexConsistencyCode.MissingProjectExternalId);
        Assert.Equal(new[] { $"{projectB}:Zulu", $"{projectA}:Alpha" }, finding.Samples);
        Assert.Contains(BackupIndexConsistencyCode.MissingProjectExternalId, summary.TopFindingCodes);
        Assert.Equal(report.WarningCount, summary.WarningCount);
        Assert.Equal(report.ErrorCount, summary.ErrorCount);
    }

    private SqliteRepository CreateRepository()
    {
        return TestRepository.Create(_dbPath);
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }
}
