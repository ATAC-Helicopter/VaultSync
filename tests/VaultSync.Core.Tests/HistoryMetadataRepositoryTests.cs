using System;
using System.IO;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class HistoryMetadataRepositoryTests : IDisposable
{
    private readonly TempDirectory _tempRoot = new();

    [Fact]
    public void EnsureSchema_IsIdempotent_ForHistoryMetadataTables()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);

        repo.EnsureSchema();
        repo.EnsureSchema();

        int projectId = TestRepository.AddProject(repo, "Schema Project", CreateTempDir());
        int snapshotId = repo.CreateSnapshot(projectId, 2, 512);

        repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = snapshotId,
            Label = "First milestone",
            Tags = "release,known-good",
            IsKnownGood = true
        });

        SnapshotHistoryMetadata metadata = repo.GetSnapshotHistoryMetadata(snapshotId);
        Assert.NotNull(metadata);
        Assert.Equal("First milestone", metadata!.Label);
        Assert.True(metadata.IsKnownGood);
    }

    [Fact]
    public void UpsertSnapshotHistoryMetadata_KeepsKnownGoodUniquePerProject()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "Known Good Project", CreateTempDir());
        int firstSnapshot = repo.CreateSnapshot(projectId, 10, 1024);
        int secondSnapshot = repo.CreateSnapshot(projectId, 11, 2048);

        repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = firstSnapshot,
            Label = "Stable before refactor",
            IsKnownGood = true
        });
        repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = secondSnapshot,
            Label = "Stable after refactor",
            IsKnownGood = true
        });

        SnapshotHistoryMetadata first = repo.GetSnapshotHistoryMetadata(firstSnapshot)!;
        SnapshotHistoryMetadata second = repo.GetSnapshotHistoryMetadata(secondSnapshot)!;

        Assert.False(first.IsKnownGood);
        Assert.True(second.IsKnownGood);
    }

    [Fact]
    public void GetSnapshotHistoryMetadataBySnapshotIds_ReturnsRequestedMarkers()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "Marker Project", CreateTempDir());
        int firstSnapshot = repo.CreateSnapshot(projectId, 5, 512);
        int secondSnapshot = repo.CreateSnapshot(projectId, 6, 768);

        repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = firstSnapshot,
            Tags = "client-demo",
            IsProtected = true
        });

        var map = repo.GetSnapshotHistoryMetadataBySnapshotIds([firstSnapshot, secondSnapshot]);

        Assert.Single(map);
        Assert.True(map[firstSnapshot].IsProtected);
        Assert.Equal("client-demo", map[firstSnapshot].Tags);
    }

    [Fact]
    public void SnapshotDelete_CascadesHistoryMetadata()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "Cascade Project", CreateTempDir());
        int snapshotId = repo.CreateSnapshot(projectId, 4, 256);

        repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = snapshotId,
            Note = "Temporary marker"
        });

        repo.DeleteSnapshotsById("Cascade Project", [snapshotId]);

        Assert.Null(repo.GetSnapshotHistoryMetadata(snapshotId));
    }

    [Fact]
    public void AddRestoreHistoryEvent_PersistsRecoverableTimelineNode()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "Restore Event Project", CreateTempDir());
        int snapshotId = repo.CreateSnapshot(projectId, 20, 4096);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            4096,
            "Restore Event Project/backup.zip",
            CreateTempDir(),
            "Primary");

        int eventId = repo.AddRestoreHistoryEvent(new RestoreHistoryEvent
        {
            ProjectId = projectId,
            BackupId = backupId,
            SnapshotId = snapshotId,
            RestoreMode = ProjectRestoreMode.Sandbox,
            TargetPath = "/tmp/vaultsync-restore",
            Note = "Sandbox restore checked"
        });

        RestoreHistoryEvent restoreEvent = repo.GetRecentRestoreHistoryEvents(5).Single(e => e.Id == eventId);
        Assert.Equal(ProjectRestoreMode.Sandbox, restoreEvent.RestoreMode);
        Assert.Equal("completed", restoreEvent.Status);
        Assert.Equal("Sandbox restore checked", restoreEvent.Note);
    }

    private string CreateTempDir()
    {
        string path = Path.Combine(_tempRoot.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => _tempRoot.Dispose();
}
