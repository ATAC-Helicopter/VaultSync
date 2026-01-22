using System;
using System.IO;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public class MetadataSyncTests
{
    [Fact]
    public void ImportFromStore_CreatesProjectSnapshotBackup_AndMarksRestore()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        try
        {
            var store = new MetadataStore(metaRoot);
            store.EnsureSchema();
            store.UpsertMetaInfo(new MetaInfo
            {
                SchemaVersion = MetadataStore.CurrentSchemaVersion,
                CreatedUtc = DateTime.UtcNow,
                LastWriteUtc = DateTime.UtcNow,
                WriterAppVersion = "1.0.0",
                WriterMachineId = "machine-a"
            });

            var projectRoot = CreateTempDir();
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
                CreatedUtc = DateTime.UtcNow,
                Type = "manual",
                TotalBytes = 2048,
                PathRel = "project-one/2025-01-01_00-00-00",
                DestinationAlias = "Primary",
                IsProtected = false,
                IsEncrypted = false,
                KdfParamsJson = "{}"
            });

            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

            var service = new MetadataSyncService(repo);
            var result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, true));

            Assert.Equal(MetadataSyncStatus.Success, result.Status);
            Assert.Equal(1, result.ImportedProjects);
            Assert.Equal(1, result.ImportedSnapshots);
            Assert.Equal(1, result.ImportedBackups);

            var project = repo.GetProjectByName("Project One");
            Assert.NotNull(project);
            Assert.True(project!.NeedsRestore);

            var snapshot = repo.GetSnapshotByExternalId("snap-1");
            var backup = repo.GetBackupByExternalId("backup-1");
            Assert.NotNull(snapshot);
            Assert.NotNull(backup);
        }
        finally
        {
            TryDeleteDir(metaRoot);
            TryDeleteDir(Path.GetDirectoryName(dbPath) ?? string.Empty);
        }
    }

    [Fact]
    public void ImportFromStore_CanSkipRestoreFlag()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        try
        {
            var store = new MetadataStore(metaRoot);
            store.EnsureSchema();

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

            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

            var service = new MetadataSyncService(repo);
            var result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

            Assert.Equal(MetadataSyncStatus.Success, result.Status);
            var project = repo.GetProjectByName("Project Two");
            Assert.NotNull(project);
            Assert.False(project!.NeedsRestore);
        }
        finally
        {
            TryDeleteDir(metaRoot);
            TryDeleteDir(Path.GetDirectoryName(dbPath) ?? string.Empty);
        }
    }

    [Fact]
    public void ImportFromStore_BlocksNewerSchema()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        try
        {
            var store = new MetadataStore(metaRoot);
            store.EnsureSchema();
            store.UpsertMetaInfo(new MetaInfo
            {
                SchemaVersion = MetadataStore.CurrentSchemaVersion + 1,
                CreatedUtc = DateTime.UtcNow,
                LastWriteUtc = DateTime.UtcNow,
                WriterAppVersion = "2.0.0",
                WriterMachineId = "machine-b"
            });

            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

            var service = new MetadataSyncService(repo);
            var result = service.ImportFromStore(metaRoot);

            Assert.Equal(MetadataSyncStatus.Incompatible, result.Status);
        }
        finally
        {
            TryDeleteDir(metaRoot);
            TryDeleteDir(Path.GetDirectoryName(dbPath) ?? string.Empty);
        }
    }

    [Fact]
    public void ImportFromStore_AppliesBackupTombstone()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        try
        {
            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

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

            var store = new MetadataStore(metaRoot);
            store.EnsureSchema();
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
        finally
        {
            TryDeleteDir(metaRoot);
            TryDeleteDir(Path.GetDirectoryName(dbPath) ?? string.Empty);
        }
    }

    [Fact]
    public void ExportBackupToStore_WritesMetadata()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        try
        {
            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

            var projectId = repo.AddProject(new Project
            {
                Name = "Project Export",
                RootPath = CreateTempDir(),
                Preset = "unity",
                CreatedUtc = DateTime.UtcNow
            });

            var snapshotId = repo.CreateSnapshot(projectId, 2, 500);
            var backupId = repo.CreateBackup(projectId, snapshotId, "manual", 500, "project-export/2025-01-01_00-00-00", metaRoot, "Primary");

            var service = new MetadataSyncService(repo);
            var result = service.ExportBackupToStore(metaRoot, backupId, "1.0.0", "machine-d");

            Assert.Equal(MetadataSyncStatus.Success, result.Status);

            var store = new MetadataStore(metaRoot);
            var backups = store.ListBackups().ToList();
            var projects = store.ListProjects().ToList();

            Assert.Single(backups);
            Assert.Single(projects);

            var exportedBackup = backups[0];
            Assert.Equal("Primary", exportedBackup.DestinationAlias);

            var updatedBackup = repo.GetBackupById(backupId);
            Assert.False(string.IsNullOrWhiteSpace(updatedBackup?.ExternalId));
        }
        finally
        {
            TryDeleteDir(metaRoot);
            TryDeleteDir(Path.GetDirectoryName(dbPath) ?? string.Empty);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vaultsync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
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
            // ignore cleanup failures
        }
    }
}
