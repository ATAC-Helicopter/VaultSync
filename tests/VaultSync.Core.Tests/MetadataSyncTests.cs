using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class MetadataSyncTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void ImportFromStore_ImportsBackupWhenPathExists_AndMarksRestore()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var projectRoot = CreateTempDir();
        var backupPathRel = "project-one/2025-01-01_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, "project-one", "2025-01-01_00-00-00"));

        var store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-a");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-1",
            Name = "Project One",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow.AddDays(-2),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-1",
            ProjectExternalId = "proj-1",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            FileCount = 10,
            TotalBytes = 1024
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-1",
            ProjectExternalId = "proj-1",
            SnapshotExternalId = "snap-1",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            Type = "manual",
            TotalBytes = 2048,
            PathRel = backupPathRel,
            DestinationAlias = "Primary",
            IsProtected = false,
            IsEncrypted = false,
            KdfParamsJson = "{}"
        });

        var repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, true));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(1, result.ImportedSnapshots);
        Assert.Equal(1, result.ImportedBackups);

        var project = repo.GetProjectByName("Project One");
        Assert.NotNull(project);
        Assert.True(project!.NeedsRestore);

        Assert.NotNull(repo.GetSnapshotByExternalId("snap-1"));
        Assert.NotNull(repo.GetBackupByExternalId("backup-1"));
    }

    [Fact]
    public void ImportFromStore_SkipsBackupWhenPathMissing_AndWritesBackupTombstone()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var projectRoot = CreateTempDir();

        var store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-a");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-missing",
            Name = "Project Missing Backup",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow.AddDays(-2),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-missing",
            ProjectExternalId = "proj-missing",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            FileCount = 5,
            TotalBytes = 4096
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-missing",
            ProjectExternalId = "proj-missing",
            SnapshotExternalId = "snap-missing",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            Type = "manual",
            TotalBytes = 4096,
            PathRel = "missing-folder/backup-1",
            DestinationAlias = "Primary",
            IsProtected = false,
            IsEncrypted = false,
            KdfParamsJson = "{}"
        });

        var repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(1, result.ImportedSnapshots);
        Assert.Equal(0, result.ImportedBackups);
        Assert.NotNull(repo.GetSnapshotByExternalId("snap-missing"));
        Assert.Null(repo.GetBackupByExternalId("backup-missing"));

        var refreshedStore = new MetadataStore(metaRoot);
        var backupTombstones = refreshedStore
            .ListTombstones()
            .Where(t => string.Equals(t.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntityId)
            .ToList();
        Assert.Contains("backup-missing", backupTombstones, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportFromStore_CanSkipRestoreFlag()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        var store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-2",
            Name = "Project Two",
            Preset = "dotnet",
            RootPathHint = CreateTempDir(),
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        var repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        var project = repo.GetProjectByName("Project Two");
        Assert.NotNull(project);
        Assert.False(project!.NeedsRestore);
    }

    [Fact]
    public void ImportFromStore_BlocksNewerSchema()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        var store = CreateStore(metaRoot);
        store.UpsertMetaInfo(new MetaInfo
        {
            SchemaVersion = MetadataStore.CurrentSchemaVersion + 1,
            CreatedUtc = DateTime.UtcNow,
            LastWriteUtc = DateTime.UtcNow,
            WriterAppVersion = "2.0.0",
            WriterMachineId = "machine-b"
        });

        var repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot);

        Assert.Equal(MetadataSyncStatus.Incompatible, result.Status);
    }

    [Fact]
    public void ImportFromStore_AppliesBackupTombstone()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        var repo = CreateRepository(dbPath);
        var projectId = repo.AddProject(new Project
        {
            Name = "Project Tombstone",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

        var snapshotId = repo.CreateSnapshotFromMetadata("snap-ts", projectId, DateTime.UtcNow, 1, 100);
        repo.CreateBackupFromMetadata(
            "backup-ts",
            projectId,
            snapshotId,
            DateTime.UtcNow,
            "manual",
            100,
            "project-tombstone/2025-01-01_00-00-00",
            CreateTempDir(),
            "Primary",
            false,
            false);

        Assert.NotNull(repo.GetBackupByExternalId("backup-ts"));

        var store = CreateStore(metaRoot);
        store.AddTombstone(new MetaTombstone
        {
            EntityType = "backup",
            EntityId = "backup-ts",
            DeletedUtc = DateTime.UtcNow,
            OriginMachineId = "machine-c"
        });

        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Null(repo.GetBackupByExternalId("backup-ts"));
    }

    [Fact]
    public void ExportBackupToStore_WritesMetadata()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        var repo = CreateRepository(dbPath);
        var projectId = repo.AddProject(new Project
        {
            Name = "Project Export",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

        var snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        var backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-export/2025-01-01_00-00-00",
            metaRoot,
            "Primary");

        var service = new MetadataSyncService(repo);
        var result = service.ExportBackupToStore(metaRoot, backupId, "1.0.0", "machine-d");

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var store = new MetadataStore(metaRoot);
        var backups = store.ListBackups().ToList();
        var projects = store.ListProjects().ToList();

        Assert.Single(backups);
        Assert.Single(projects);
        Assert.Equal("Primary", backups[0].DestinationAlias);

        var updatedBackup = repo.GetBackupById(backupId);
        Assert.False(string.IsNullOrWhiteSpace(updatedBackup?.ExternalId));
    }

    public void Dispose()
    {
        foreach (var path in _tempDirs.OrderByDescending(p => p.Length))
            TryDeleteDir(path);
    }

    private static MetadataStore CreateStore(string rootPath)
    {
        var store = new MetadataStore(rootPath);
        store.EnsureSchema();
        return store;
    }

    private static SqliteRepository CreateRepository(string dbPath)
    {
        var repo = new SqliteRepository(dbPath);
        repo.EnsureSchema();
        return repo;
    }

    private static void SeedMetaInfo(MetadataStore store, string machineId)
    {
        store.UpsertMetaInfo(new MetaInfo
        {
            SchemaVersion = MetadataStore.CurrentSchemaVersion,
            CreatedUtc = DateTime.UtcNow,
            LastWriteUtc = DateTime.UtcNow,
            WriterAppVersion = "1.0.0",
            WriterMachineId = machineId
        });
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vaultsync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void TryDeleteDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

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
