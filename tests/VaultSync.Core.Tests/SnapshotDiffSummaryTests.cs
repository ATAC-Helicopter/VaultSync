using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SnapshotDiffSummaryTests : IDisposable
{
    private readonly TempDirectory _tempRoot = new();

    [Fact]
    public void Repository_CreateSnapshot_PersistsDiffSummaryFields()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string root = CreateTempDir();
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(
            repo,
            "Diff Repo Project",
            root,
            preset: string.Empty,
            createdUtc: DateTime.UtcNow);

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

        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(
            repo,
            "Diff Service Project",
            projectRoot,
            preset: string.Empty,
            createdUtc: DateTime.UtcNow);
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
    public async Task SnapshotService_CreateSnapshot_IgnoresImportedSnapshotWhenChoosingLocalDiffBaseline()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        string backupRoot = CreateTempDir();

        File.WriteAllText(Path.Combine(projectRoot, "data.bin"), "1234567890");

        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(
            repo,
            "Cross Machine Diff Project",
            projectRoot,
            preset: string.Empty,
            createdUtc: DateTime.UtcNow);
        Project project = repo.GetProjectById(projectId);
        Assert.NotNull(project);

        var service = new SnapshotService(repo, new HashService());
        int localSnapshotId = await service.CreateSnapshotAsync(
            project!,
            fullHash: false,
            hashNow: false,
            maxSnapshotsToKeep: null,
            ct: CancellationToken.None);
        _ = repo.CreateBackup(
            projectId,
            localSnapshotId,
            "manual",
            10,
            "Cross Machine Diff Project/local",
            backupRoot,
            "Local");

        int importedSnapshotId = repo.CreateSnapshotFromMetadata(
            "imported-cross-machine-snapshot",
            projectId,
            DateTime.UtcNow.AddDays(1),
            1,
            14L * 1024L * 1024L * 1024L,
            new SnapshotDiffSummary(Added: 1, Modified: 0, Deleted: 0, NetSizeBytes: 14L * 1024L * 1024L * 1024L, TopChangedPaths: []));
        _ = repo.CreateBackupFromMetadata(
            "imported-cross-machine-backup",
            projectId,
            importedSnapshotId,
            DateTime.UtcNow.AddDays(1),
            "manual",
            14L * 1024L * 1024L * 1024L,
            "Cross Machine Diff Project/imported",
            backupRoot,
            "Imported",
            isProtected: false,
            isImported: true,
            originMachineName: "other-machine");

        File.AppendAllText(Path.Combine(projectRoot, "data.bin"), "12345");

        int nextLocalSnapshotId = await service.CreateSnapshotAsync(
            project!,
            fullHash: false,
            hashNow: false,
            maxSnapshotsToKeep: null,
            ct: CancellationToken.None);

        Snapshot nextLocalSnapshot = repo.GetSnapshotById(nextLocalSnapshotId)!;
        Assert.NotNull(nextLocalSnapshot);
        Assert.Equal(5, nextLocalSnapshot.DiffNetBytes);
        Assert.Equal(1, nextLocalSnapshot.DiffModified);
        Assert.Equal(nextLocalSnapshotId, repo.GetLatestLocalSnapshotForProject(projectId)?.Id);
    }

    [Fact]
    public async Task SnapshotService_CreateSnapshot_PreservesCaseDistinctMetadataPaths()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        File.WriteAllText(Path.Combine(projectRoot, "Foo.txt"), "upper");

        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(
            repo,
            "Case Distinct Project",
            projectRoot,
            preset: string.Empty,
            createdUtc: DateTime.UtcNow);
        int baselineId = repo.CreateSnapshot(projectId, 2, 10);
        repo.InsertFiles(
            baselineId,
            [
                new FileEntry("Foo.txt", 5, File.GetLastWriteTimeUtc(Path.Combine(projectRoot, "Foo.txt")), "UPPER"),
                new FileEntry("foo.txt", 5, DateTime.UtcNow.AddDays(-1), "LOWER")
            ]);

        Project project = repo.GetProjectById(projectId)!;
        int nextId = await new SnapshotService(repo, new HashService()).CreateSnapshotAsync(
            project,
            fullHash: false,
            hashNow: false,
            maxSnapshotsToKeep: null,
            ct: CancellationToken.None);

        Snapshot next = repo.GetSnapshotById(nextId)!;
        Assert.Equal(1, next.DiffDeleted);
        Assert.Equal(0, next.DiffAdded);
        Assert.Equal("Foo.txt", Assert.Single(repo.GetFilesForSnapshot(nextId)).RelPath);
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

        SqliteRepository repo = TestRepository.Create(dbPath);
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
        _tempRoot.Dispose();
    }

    private string CreateTempDir()
    {
        string path = Path.Combine(_tempRoot.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
