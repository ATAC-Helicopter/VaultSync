using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SnapshotDiffSummaryTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void Repository_CreateSnapshot_PersistsDiffSummaryFields()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string root = CreateTempDir();
        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            Name = "Diff Repo Project",
            RootPath = root,
            Preset = string.Empty,
            CreatedUtc = DateTime.UtcNow
        });

        var summary = new SnapshotDiffSummary(
            Added: 3,
            Modified: 2,
            Deleted: 1,
            NetSizeBytes: 4096,
            TopChangedPaths:
            [
                new SnapshotDiffPathStat("src", 4, 8192),
                new SnapshotDiffPathStat("assets", 2, 1024)
            ]);

        int snapshotId = repo.CreateSnapshot(projectId, 12, 32_768, summary);
        Snapshot snapshot = repo.GetSnapshotById(snapshotId);

        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot!.DiffAdded);
        Assert.Equal(2, snapshot.DiffModified);
        Assert.Equal(1, snapshot.DiffDeleted);
        Assert.Equal(4096, snapshot.DiffNetBytes);

        IReadOnlyList<SnapshotDiffPathStat> topPaths = SnapshotDiffSummary.ParseTopChangedPaths(snapshot.DiffTopPathsJson);
        Assert.Equal(2, topPaths.Count);
        Assert.Equal("src", topPaths[0].Path);
        Assert.Equal(4, topPaths[0].Changes);
    }

    [Fact]
    public async Task SnapshotService_CreateSnapshot_ComputesAndPersistsSummary()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "a.txt"), "0123456789");
        File.WriteAllText(Path.Combine(projectRoot, "docs", "readme.md"), "01234567890123456789");

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            Name = "Diff Service Project",
            RootPath = projectRoot,
            Preset = string.Empty,
            CreatedUtc = DateTime.UtcNow
        });
        Project project = repo.GetProjectById(projectId);
        Assert.NotNull(project);

        var service = new SnapshotService(repo, new HashService());
        await service.CreateSnapshotAsync(project!, fullHash: false, hashNow: false, maxSnapshotsToKeep: null, ct: CancellationToken.None);

        File.WriteAllText(Path.Combine(projectRoot, "src", "a.txt"), "0123456789ABCDE");
        File.WriteAllText(Path.Combine(projectRoot, "src", "new.txt"), "1234567");
        File.Delete(Path.Combine(projectRoot, "docs", "readme.md"));

        await service.CreateSnapshotAsync(project!, fullHash: false, hashNow: false, maxSnapshotsToKeep: null, ct: CancellationToken.None);

        var snapshots = repo.GetSnapshotsForProject("Diff Service Project").OrderByDescending(s => s.CreatedUtc).ToList();
        Assert.True(snapshots.Count >= 2);

        Snapshot latest = snapshots[0];
        Assert.Equal(1, latest.DiffAdded);
        Assert.Equal(1, latest.DiffModified);
        Assert.Equal(1, latest.DiffDeleted);
        Assert.Equal(-8, latest.DiffNetBytes);

        IReadOnlyList<SnapshotDiffPathStat> topPaths = SnapshotDiffSummary.ParseTopChangedPaths(latest.DiffTopPathsJson);
        Assert.NotEmpty(topPaths);
        Assert.Equal("src", topPaths[0].Path);
        Assert.Equal(2, topPaths[0].Changes);
    }

    [Fact]
    public void MetadataSync_ImportFromStore_PreservesSnapshotDiffSummary()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        var store = new MetadataStore(metaRoot);
        store.EnsureSchema();
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-diff-sync",
            Name = "Diff Sync Project",
            Preset = string.Empty,
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-diff-sync",
            ProjectExternalId = "proj-diff-sync",
            CreatedUtc = DateTime.UtcNow,
            FileCount = 5,
            TotalBytes = 2048,
            DiffAdded = 2,
            DiffModified = 1,
            DiffDeleted = 1,
            DiffNetBytes = 128,
            DiffTopPathsJson = "[{\"path\":\"src\",\"changes\":3,\"changedBytes\":256}]"
        });
        string backupPathRel = "diff-sync/2026-02-17_10-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, backupPathRel));
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-diff-sync",
            ProjectExternalId = "proj-diff-sync",
            SnapshotExternalId = "snap-diff-sync",
            CreatedUtc = DateTime.UtcNow,
            Type = "manual",
            BackupMode = BackupModes.Full,
            TotalBytes = 2048,
            PathRel = backupPathRel,
            DestinationAlias = "Primary",
            OriginMachineName = "machine-diff-sync",
            IsProtected = false,
            IsEncrypted = false,
            KdfParamsJson = "{}"
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var sync = new MetadataSyncService(repo);
        MetadataSyncResult result = sync.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Snapshot snapshot = repo.GetSnapshotByExternalId("snap-diff-sync");
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.DiffAdded);
        Assert.Equal(1, snapshot.DiffModified);
        Assert.Equal(1, snapshot.DiffDeleted);
        Assert.Equal(128, snapshot.DiffNetBytes);

        IReadOnlyList<SnapshotDiffPathStat> topPaths = SnapshotDiffSummary.ParseTopChangedPaths(snapshot.DiffTopPathsJson);
        Assert.Single(topPaths);
        Assert.Equal("src", topPaths[0].Path);
        Assert.Equal(3, topPaths[0].Changes);
    }

    public void Dispose()
    {
        foreach (string path in _tempDirs.OrderByDescending(p => p.Length))
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }

    private static SqliteRepository CreateRepository(string dbPath)
    {
        var repo = new SqliteRepository(dbPath);
        repo.EnsureSchema();
        return repo;
    }

    private string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vaultsync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }
}
