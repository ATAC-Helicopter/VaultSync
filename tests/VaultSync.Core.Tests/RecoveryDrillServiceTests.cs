using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryDrillServiceTests : IDisposable
{
    private readonly TempDirectory _tempRoot = new();

    [Fact]
    public async Task RunAsync_PassesWithoutWritingIntoProjectOrBackup()
    {
        string projectRoot = CreateTempDir();
        string backupRoot = CreateTempDir();
        string relative = Path.Combine("App", "2026-07-22");
        string content = Path.Combine(backupRoot, relative);
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "one.txt"), "one");
        File.WriteAllText(Path.Combine(content, "two.txt"), "two");
        string[] before = Directory.GetFileSystemEntries(content);

        var project = new Project { Id = 1, Name = "App", RootPath = projectRoot, Preset = "default" };
        var backup = new Backup { Id = 10, ProjectId = 1, SnapshotId = 20, Path = relative, DestinationPath = backupRoot };
        var snapshot = new Snapshot { Id = 20, ProjectId = 1, FileCount = 2 };
        var config = new AppConfig { Backups = new BackupsConfig { BackupRoot = backupRoot } };

        RecoveryDrillResult result = await new RecoveryDrillService().RunAsync(project, backup, snapshot, config);

        Assert.Equal(RecoveryDrillStatus.Passed, result.Status);
        Assert.Equal(2, result.FilesExamined);
        Assert.Equal(before, Directory.GetFileSystemEntries(content));
        Assert.Empty(Directory.GetFileSystemEntries(projectRoot));
    }

    [Fact]
    public void Repository_PersistsRecoveryDrillHistory()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "Drill", CreateTempDir());
        int snapshotId = repo.CreateSnapshot(projectId, 1, 3);
        int backupId = repo.CreateBackup(projectId, snapshotId, "manual", 3, "Drill/point", CreateTempDir(), "Primary");
        var result = new RecoveryDrillResult
        {
            ProjectId = projectId,
            SnapshotId = snapshotId,
            BackupId = backupId,
            Status = RecoveryDrillStatus.Attention,
            ChecksPassed = 2,
            ChecksTotal = 3,
            FilesExamined = 1,
            IsLimited = true,
            Summary = "Limited",
            ChecksJson = "[]"
        };

        int id = repo.AddRecoveryDrill(result);
        RecoveryDrillResult stored = Assert.Single(repo.GetRecoveryDrills());

        Assert.True(id > 0);
        Assert.Equal(RecoveryDrillStatus.Attention, stored.Status);
        Assert.Equal(backupId, stored.BackupId);
        Assert.True(stored.IsLimited);
    }

    [Fact]
    public void Repository_BoundsRecoveryDrillHistoryPerProject()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "Bounded Drill", CreateTempDir());
        int snapshotId = repo.CreateSnapshot(projectId, 1, 3);
        int backupId = repo.CreateBackup(projectId, snapshotId, "manual", 3, "Drill/point", CreateTempDir(), "Primary");

        for (int index = 0; index < 25; index++)
        {
            repo.AddRecoveryDrill(new RecoveryDrillResult
            {
                ProjectId = projectId,
                SnapshotId = snapshotId,
                BackupId = backupId,
                RunUtc = DateTime.UtcNow.AddMinutes(index),
                Status = RecoveryDrillStatus.Passed,
                ChecksPassed = 3,
                ChecksTotal = 3,
                Summary = $"Run {index}"
            });
        }

        List<RecoveryDrillResult> drills = repo.GetRecoveryDrills();
        Assert.Equal(20, drills.Count);
        Assert.Equal("Run 24", drills[0].Summary);
        Assert.Equal("Run 5", drills[^1].Summary);
    }

    private string CreateTempDir()
    {
        string path = Path.Combine(_tempRoot.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => _tempRoot.Dispose();
}
