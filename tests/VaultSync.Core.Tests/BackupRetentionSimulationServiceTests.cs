using System;
using System.IO;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupRetentionSimulationServiceTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();
    private readonly SqliteRepository _repo;

    public BackupRetentionSimulationServiceTests()
    {
        _repo = TestRepository.Create(Path.Combine(_tempDir.Path, "vaultsync.db"));
    }

    [Fact]
    public void Simulate_ReportsSuggestedDeletes_ForProjectsOverRetentionCap()
    {
        Project project = CreateProject("Alpha");
        SeedBackup(project.Id, DateTime.UtcNow.AddDays(-3), 100);
        SeedBackup(project.Id, DateTime.UtcNow.AddDays(-2), 120);
        SeedBackup(project.Id, DateTime.UtcNow.AddDays(-1), 140);

        var service = new BackupRetentionSimulationService(_repo);
        BackupRetentionSimulationResult result = service.Simulate(maxSnapshotsPerProject: 2);

        ProjectRetentionSimulationProjectResult projectResult = Assert.Single(result.Projects);
        Assert.True(projectResult.CanPrune);
        Assert.Equal(1, projectResult.DeleteQuota);
        Assert.Equal(1, projectResult.SelectedDeleteCount);
        Assert.Equal(100, projectResult.SelectedDeleteBytes);
        Assert.Equal(1, result.SuggestedDeleteCount);
    }

    [Fact]
    public void Simulate_FlagsBlockedProjects_WhenPreflightWouldRemoveLastRestorePoint()
    {
        Project project = CreateProject("Blocked");
        SeedBackup(project.Id, DateTime.UtcNow.AddDays(-2), 100);
        Project otherProject = CreateProject("Other");
        int foreignSnapshotId = _repo.CreateSnapshot(otherProject.Id, DateTime.UtcNow.AddDays(-1).Ticks, 120);
        _repo.CreateBackup(
            project.Id,
            foreignSnapshotId,
            "manual",
            120,
            $"backup-{foreignSnapshotId}",
            @"D:\Backups",
            "Primary");

        var service = new BackupRetentionSimulationService(_repo);
        BackupRetentionSimulationResult result = service.Simulate(maxSnapshotsPerProject: 1);

        ProjectRetentionSimulationProjectResult projectResult = Assert.Single(result.Projects);
        Assert.False(projectResult.CanPrune);
        Assert.Equal("retention-last-restorable-point", projectResult.PreflightCode);
        Assert.Equal(1, result.BlockedProjectCount);
    }

    [Fact]
    public void Simulate_TreatsSnapshotMetadataProtectedBackupsAsKept()
    {
        Project project = CreateProject("Protected");
        int protectedSnapshotId = SeedBackup(project.Id, DateTime.UtcNow.AddDays(-3), 100);
        SeedBackup(project.Id, DateTime.UtcNow.AddDays(-2), 120);
        SeedBackup(project.Id, DateTime.UtcNow.AddDays(-1), 140);
        _repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = protectedSnapshotId,
            IsProtected = true
        });

        var service = new BackupRetentionSimulationService(_repo);
        BackupRetentionSimulationResult result = service.Simulate(maxSnapshotsPerProject: 1);

        ProjectRetentionSimulationProjectResult projectResult = Assert.Single(result.Projects);
        Assert.True(projectResult.CanPrune);
        Assert.Equal(2, projectResult.UnprotectedBackupCount);
        Assert.Equal(1, projectResult.SelectedDeleteCount);
        Assert.Equal(120, projectResult.SelectedDeleteBytes);
    }

    private Project CreateProject(string name)
    {
        TestRepository.AddProject(_repo, name, $@"C:\Projects\{name}");
        return _repo.GetAllProjects().Single(project => project.Name == name);
    }

    private int SeedBackup(int projectId, DateTime createdUtc, long totalBytes)
    {
        int snapshotId = _repo.CreateSnapshot(projectId, createdUtc.Ticks, totalBytes);
        _repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            totalBytes,
            $"backup-{snapshotId}",
            @"D:\Backups",
            "Primary");

        return snapshotId;
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }
}
