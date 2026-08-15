using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class MetadataSyncTests : IDisposable
{
    private readonly List<TempDirectory> _tempDirs = [];

    [Fact]
    public async System.Threading.Tasks.Task PreviewImportFromStoreAsync_WithEmptyPath_ReleasesItsPerRootGate()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var service = new MetadataSyncService(CreateRepository(dbPath));

        MetadataSyncPreview first = await service.PreviewImportFromStoreAsync(string.Empty);
        MetadataSyncPreview second = await service.PreviewImportFromStoreAsync(string.Empty);

        Assert.Equal(MetadataSyncStatus.InvalidPath, first.Status);
        Assert.Equal(MetadataSyncStatus.InvalidPath, second.Status);
    }

    [Fact]
    public async System.Threading.Tasks.Task PreviewImportFromStoreAsync_WithInvalidFullPath_UsesAStableFallbackGate()
    {
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var service = new MetadataSyncService(CreateRepository(dbPath));

        MetadataSyncPreview preview = await service.PreviewImportFromStoreAsync("invalid\0root");

        Assert.Equal(MetadataSyncStatus.NoStore, preview.Status);
    }

    [Fact]
    public void PreviewImportFromStore_CountsChangesWithoutMutatingTheRepository()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string remoteBackupPath = Path.Combine("remote-project", "2026-08-15_10-00-00");
        Directory.CreateDirectory(Path.Combine(metaRoot, remoteBackupPath));

        SqliteRepository repo = CreateRepository(dbPath);
        int localProjectId = repo.AddProject(new Project
        {
            ExternalId = "local-project",
            Name = "Local Project",
            RootPath = CreateTempDir(),
            Preset = "dotnet",
            CreatedUtc = DateTime.UtcNow.AddDays(-2)
        });
        int localSnapshotId = repo.CreateSnapshotFromMetadata(
            "local-snapshot",
            localProjectId,
            DateTime.UtcNow.AddDays(-1),
            fileCount: 1,
            totalBytes: 64);
        repo.CreateBackupFromMetadata(
            "deleted-by-tombstone",
            localProjectId,
            localSnapshotId,
            DateTime.UtcNow.AddHours(-2),
            "manual",
            64,
            "local/tombstoned",
            metaRoot,
            "Primary",
            isProtected: false,
            isImported: false);
        repo.CreateBackupFromMetadata(
            "missing-remote-path",
            localProjectId,
            localSnapshotId,
            DateTime.UtcNow.AddHours(-1),
            "manual",
            64,
            "local/missing",
            metaRoot,
            "Primary",
            isProtected: false,
            isImported: false);

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "remote-machine");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "remote-project",
            Name = "Remote Project",
            Preset = "node",
            RootPathHint = CreateTempDir(),
            CreatedUtc = DateTime.UtcNow.AddDays(-3),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "remote-snapshot",
            ProjectExternalId = "remote-project",
            CreatedUtc = DateTime.UtcNow.AddHours(-3),
            FileCount = 3,
            TotalBytes = 512
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "dead-snapshot",
            ProjectExternalId = "remote-project",
            CreatedUtc = DateTime.UtcNow.AddHours(-4),
            FileCount = 1,
            TotalBytes = 32
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "remote-backup",
            ProjectExternalId = "remote-project",
            SnapshotExternalId = "remote-snapshot",
            CreatedUtc = DateTime.UtcNow.AddHours(-2),
            Type = "manual",
            TotalBytes = 512,
            PathRel = remoteBackupPath,
            DestinationAlias = "Primary",
            KdfParamsJson = "{}"
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "missing-remote-path",
            ProjectExternalId = "remote-project",
            SnapshotExternalId = "dead-snapshot",
            CreatedUtc = DateTime.UtcNow.AddHours(-1),
            Type = "manual",
            TotalBytes = 32,
            PathRel = "../outside-repository",
            DestinationAlias = "Primary",
            KdfParamsJson = "{}"
        });
        store.AddTombstone(new MetaTombstone
        {
            EntityType = "backup",
            EntityId = "deleted-by-tombstone",
            DeletedUtc = DateTime.UtcNow,
            OriginMachineId = "remote-machine"
        });
        store.AddTombstone(new MetaTombstone
        {
            EntityType = "snapshot",
            EntityId = "dead-snapshot",
            DeletedUtc = DateTime.UtcNow,
            OriginMachineId = "remote-machine"
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncPreview preview = service.PreviewImportFromStore(metaRoot, MetadataSyncOptions.Default);
        MetadataSyncPreview cachedPreview = service.PreviewImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, preview.Status);
        Assert.Equal(1, preview.NewProjects);
        Assert.Equal(0, preview.LinkedProjects);
        Assert.Equal(1, preview.NewSnapshots);
        Assert.Equal(1, preview.NewBackups);
        Assert.Equal(2, preview.DeletedBackups);
        Assert.Equal(preview, cachedPreview);
        Assert.Null(repo.GetProjectByExternalId("remote-project"));
        Assert.NotNull(repo.GetBackupByExternalId("deleted-by-tombstone"));
        Assert.NotNull(repo.GetBackupByExternalId("missing-remote-path"));
    }

    [Fact]
    public async System.Threading.Tasks.Task ExportBackupTombstoneToStoreAsync_PersistsTombstoneAndReleasesItsPerRootGate()
    {
        string rootPath = CreateTempDir();

        await MetadataSyncService.ExportBackupTombstoneToStoreAsync(
            rootPath,
            "backup-deleted",
            "1.8.3",
            "machine-a");

        MetaTombstone tombstone = Assert.Single(new MetadataStore(rootPath).ListTombstones());
        Assert.Equal("backup", tombstone.EntityType);
        Assert.Equal("backup-deleted", tombstone.EntityId);
        Assert.Equal("machine-a", tombstone.OriginMachineId);
    }

    [Fact]
    public void ImportFromStore_ImportsBackupWhenPathExists_AndMarksRestore()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        const string backupPathRel = "project-one/2025-01-01_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, "project-one", "2025-01-01_00-00-00"));

        MetadataStore store = CreateStore(metaRoot);
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

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, true));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(1, result.ImportedSnapshots);
        Assert.Equal(1, result.ImportedBackups);

        Project project = repo.GetProjectByName("Project One");
        Assert.NotNull(project);
        Assert.True(project!.NeedsRestore);

        Assert.NotNull(repo.GetSnapshotByExternalId("snap-1"));
        Assert.NotNull(repo.GetBackupByExternalId("backup-1"));
    }

    [Fact]
    public void ImportFromStore_UnchangedReadOnlySource_UsesSuccessfulImportCache()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig
        {
            ProjectsRoot = CreateTempDir(),
            DbPath = dbPath
        });

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-cache");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-cache",
            Name = "Cache Project",
            Preset = "dotnet",
            RootPathHint = CreateTempDir(),
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncOptions options = new MetadataSyncOptions(true, false)
            .AsReadOnlySource()
            .WithUnchangedSourceSkip();

        MetadataSyncResult first = service.ImportFromStore(metaRoot, options);
        MetadataSyncResult second = service.ImportFromStore(metaRoot, options);

        Assert.Equal(MetadataSyncStatus.Success, first.Status);
        Assert.Equal(1, first.ImportedProjects);
        Assert.Equal(MetadataSyncStatus.Success, second.Status);
        Assert.Equal(0, second.ImportedProjects);
        Assert.Equal("Metadata source unchanged.", second.Message);
        Assert.Single(AppConfigStore.Load().Advanced.MetadataImportCache.Sources);
    }

    [Fact]
    public void ImportFromStore_UnchangedReadOnlySource_DoesNotSkipWhenLocalRepositoryIsEmpty()
    {
        string metaRoot = CreateTempDir();
        string firstDbPath = Path.Combine(CreateTempDir(), "vaultsync-first.db");
        string secondDbPath = Path.Combine(CreateTempDir(), "vaultsync-second.db");
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig
        {
            ProjectsRoot = CreateTempDir(),
            DbPath = firstDbPath
        });

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-cache");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-cache",
            Name = "Cache Project",
            Preset = "dotnet",
            RootPathHint = CreateTempDir(),
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        MetadataSyncOptions options = new MetadataSyncOptions(true, false)
            .AsReadOnlySource()
            .WithUnchangedSourceSkip();
        var firstService = new MetadataSyncService(CreateRepository(firstDbPath));
        MetadataSyncResult first = firstService.ImportFromStore(metaRoot, options);
        Assert.Equal(MetadataSyncStatus.Success, first.Status);
        Assert.Equal(1, first.ImportedProjects);

        SqliteRepository secondRepo = CreateRepository(secondDbPath);
        var secondService = new MetadataSyncService(secondRepo);
        MetadataSyncResult second = secondService.ImportFromStore(metaRoot, options);

        Assert.Equal(MetadataSyncStatus.Success, second.Status);
        Assert.Equal(1, second.ImportedProjects);
        Assert.NotNull(secondRepo.GetProjectByName("Cache Project"));
    }

    [Fact]
    public void ImportFromStore_UnchangedReadOnlySource_DoesNotSkipWhenLocalRepositoryHasUnrelatedCountCoverage()
    {
        string metaRoot = CreateTempDir();
        string firstDbPath = Path.Combine(CreateTempDir(), "vaultsync-first.db");
        string secondDbPath = Path.Combine(CreateTempDir(), "vaultsync-second.db");
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig
        {
            ProjectsRoot = CreateTempDir(),
            DbPath = firstDbPath
        });

        string backupPathRel = Path.Combine("cache-project", "2026-05-22_09-00-00");
        Directory.CreateDirectory(Path.Combine(metaRoot, backupPathRel));
        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-cache");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-cache",
            Name = "Cache Project",
            Preset = "dotnet",
            RootPathHint = CreateTempDir(),
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-cache",
            ProjectExternalId = "proj-cache",
            CreatedUtc = DateTime.UtcNow,
            FileCount = 1,
            TotalBytes = 128
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-cache",
            ProjectExternalId = "proj-cache",
            SnapshotExternalId = "snap-cache",
            CreatedUtc = DateTime.UtcNow,
            Type = "manual",
            TotalBytes = 128,
            PathRel = backupPathRel,
            DestinationAlias = "Primary",
            IsProtected = false,
            IsEncrypted = false,
            KdfParamsJson = "{}"
        });

        MetadataSyncOptions options = new MetadataSyncOptions(true, false)
            .AsReadOnlySource()
            .WithUnchangedSourceSkip();
        var firstService = new MetadataSyncService(CreateRepository(firstDbPath));
        MetadataSyncResult first = firstService.ImportFromStore(metaRoot, options);
        Assert.Equal(MetadataSyncStatus.Success, first.Status);
        Assert.Equal(1, first.ImportedProjects);
        Assert.Equal(1, first.ImportedSnapshots);
        Assert.Equal(1, first.ImportedBackups);

        SqliteRepository secondRepo = CreateRepository(secondDbPath);
        SeedUnrelatedImportedHistory(secondRepo, CreateTempDir());
        var secondService = new MetadataSyncService(secondRepo);
        MetadataSyncResult second = secondService.ImportFromStore(metaRoot, options);

        Assert.Equal(MetadataSyncStatus.Success, second.Status);
        Assert.NotEqual("Metadata source unchanged.", second.Message);
        Assert.NotNull(secondRepo.GetProjectByExternalId("proj-cache"));
        Assert.NotNull(secondRepo.GetSnapshotByExternalId("snap-cache"));
        Assert.NotNull(secondRepo.GetBackupByExternalId("backup-cache"));
    }

    [Fact]
    public void ImportFromStore_UnchangedReadOnlySource_RechecksMissingBackupPathWhenFolderAppears()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectsRoot = CreateTempDir();
        string backupPathRel = Path.Combine("cache-project", "2026-05-22_09-00-00");
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig
        {
            ProjectsRoot = projectsRoot,
            DbPath = dbPath
        });

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-cache");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-cache",
            Name = "Cache Project",
            Preset = "dotnet",
            RootPathHint = Path.Combine(projectsRoot, "Cache Project"),
            CreatedUtc = DateTime.UtcNow.AddDays(-2),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-cache",
            ProjectExternalId = "proj-cache",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            FileCount = 1,
            TotalBytes = 128
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-cache",
            ProjectExternalId = "proj-cache",
            SnapshotExternalId = "snap-cache",
            CreatedUtc = DateTime.UtcNow,
            Type = "manual",
            TotalBytes = 128,
            PathRel = backupPathRel,
            DestinationAlias = "Primary",
            IsProtected = false,
            IsEncrypted = false,
            KdfParamsJson = "{}"
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncOptions options = new MetadataSyncOptions(true, false)
            .AsReadOnlySource()
            .WithUnchangedSourceSkip();
        MetadataSyncResult first = service.ImportFromStore(metaRoot, options);
        Assert.Equal(MetadataSyncStatus.Success, first.Status);
        Assert.Equal(0, first.ImportedBackups);

        Directory.CreateDirectory(Path.Combine(metaRoot, backupPathRel));
        File.WriteAllBytes(Path.Combine(metaRoot, backupPathRel, "data.bin"), new byte[128]);
        MetadataSyncResult second = service.ImportFromStore(metaRoot, options);

        Assert.Equal(MetadataSyncStatus.Success, second.Status);
        Assert.Equal(1, second.ImportedBackups);
        Assert.NotNull(repo.GetBackupByExternalId("backup-cache"));
    }

    [Fact]
    public void ImportFromStore_RebuildsHistoryFromDestinationFoldersWhenMetadataHasNoBackups()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectsRoot = CreateTempDir();
        string projectFolder = Path.Combine(metaRoot, "blueprints");
        string firstBackupFolder = Path.Combine(projectFolder, "2026-05-06_08-15-06");
        string secondBackupFolder = Path.Combine(projectFolder, "2026-05-07_12-42-10");
        Directory.CreateDirectory(firstBackupFolder);
        Directory.CreateDirectory(secondBackupFolder);
        File.WriteAllBytes(Path.Combine(firstBackupFolder, "data.bin"), new byte[123]);
        File.WriteAllBytes(Path.Combine(secondBackupFolder, "data.bin"), new byte[456]);
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig { ProjectsRoot = projectsRoot });

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-legacy-folder");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-blueprints",
            Name = "Blueprints",
            Preset = "dotnet",
            RootPathHint = @"\\server\share\Dev\Blueprints",
            CreatedUtc = new DateTime(2026, 5, 5, 14, 26, 32, DateTimeKind.Utc),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(2, result.ImportedSnapshots);
        Assert.Equal(2, result.ImportedBackups);

        Project project = repo.GetProjectByName("Blueprints");
        Assert.NotNull(project);
        Assert.True(project!.NeedsRestore);

        List<Backup> backups = [.. repo.GetBackupsForProject(project.Id)];
        Assert.Equal(2, backups.Count);
        Assert.All(backups, backup => Assert.True(backup.IsImported));
        Assert.Contains(backups, backup => backup.TotalBytes == 123);
        Assert.Contains(backups, backup => backup.TotalBytes == 456);
        Assert.Contains(backups, backup => backup.Path == Path.Combine("blueprints", "2026-05-06_08-15-06"));
        Assert.Contains(backups, backup => backup.Path == Path.Combine("blueprints", "2026-05-07_12-42-10"));
    }

    [Fact]
    public void ImportFromStore_RebuildsHistoryFromDestinationFoldersWithoutMetadataStore()
    {
        string backupRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectsRoot = CreateTempDir();
        string firstBackupFolder = Path.Combine(backupRoot, "risiko-3d", "2026-05-06_10-14-22");
        string secondBackupFolder = Path.Combine(backupRoot, "risiko-3d", "2026-05-07_09-00-00");
        Directory.CreateDirectory(firstBackupFolder);
        Directory.CreateDirectory(secondBackupFolder);
        File.WriteAllBytes(Path.Combine(firstBackupFolder, "data.bin"), new byte[321]);
        File.WriteAllBytes(Path.Combine(secondBackupFolder, "data.bin"), new byte[654]);
        Directory.CreateDirectory(Path.Combine(backupRoot, ".vaultsync"));
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig { ProjectsRoot = projectsRoot });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(backupRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(2, result.ImportedSnapshots);
        Assert.Equal(2, result.ImportedBackups);

        Project project = repo.GetProjectByName("risiko-3d");
        Assert.NotNull(project);
        Assert.Equal(Path.Combine(projectsRoot, "risiko-3d"), project!.RootPath);
        List<Backup> backups = [.. repo.GetBackupsForProject(project.Id)];
        Assert.Equal(2, backups.Count);
        Assert.Contains(backups, backup => backup.TotalBytes == 321);
        Assert.Contains(backups, backup => backup.TotalBytes == 654);
    }

    [Fact]
    public void ImportFromStore_RepairsZeroByteLegacyImportedBackups()
    {
        string backupRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectsRoot = CreateTempDir();
        string backupFolder = Path.Combine(backupRoot, "legacy-project", "2026-05-08_10-00-00");
        Directory.CreateDirectory(backupFolder);
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig { ProjectsRoot = projectsRoot });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult firstImport = service.ImportFromStore(backupRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, firstImport.Status);
        Assert.Equal(1, firstImport.ImportedBackups);

        Project project = repo.GetProjectByName("legacy-project");
        Assert.NotNull(project);
        Backup originalBackup = Assert.Single(repo.GetBackupsForProject(project!.Id));
        Assert.Equal(0, originalBackup.TotalBytes);

        File.WriteAllBytes(Path.Combine(backupFolder, "restored-size.bin"), new byte[789]);

        MetadataSyncResult repairImport = service.ImportFromStore(backupRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, repairImport.Status);
        Assert.Equal(0, repairImport.ImportedBackups);
        Assert.Equal(1, repairImport.RepairedBackups);

        Backup repairedBackup = Assert.Single(repo.GetBackupsForProject(project.Id));
        Assert.Equal(789, repairedBackup.TotalBytes);
        Snapshot repairedSnapshot = repo.GetSnapshotById(repairedBackup.SnapshotId);
        Assert.NotNull(repairedSnapshot);
        Assert.Equal(789, repairedSnapshot!.TotalBytes);
    }

    [Fact]
    public void ImportFromStore_RemapsRootedBackupPathToConfiguredDestination()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        string backupPathRel = Path.Combine("project-one", "2025-01-01_00-00-00");
        Directory.CreateDirectory(Path.Combine(metaRoot, backupPathRel));

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-a");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-rooted-path",
            Name = "Project One",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow.AddDays(-2),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-rooted-path",
            ProjectExternalId = "proj-rooted-path",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            FileCount = 10,
            TotalBytes = 1024
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-rooted-path",
            ProjectExternalId = "proj-rooted-path",
            SnapshotExternalId = "snap-rooted-path",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            Type = "manual",
            TotalBytes = 2048,
            PathRel = @"Z:\VaultSyncBackups\project-one\2025-01-01_00-00-00",
            DestinationAlias = "Primary",
            IsProtected = false,
            IsEncrypted = false,
            KdfParamsJson = "{}"
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedBackups);

        Backup backup = repo.GetBackupByExternalId("backup-rooted-path");
        Assert.NotNull(backup);
        Assert.Equal(backupPathRel, backup!.Path);
        Assert.Equal(metaRoot, backup.DestinationPath);
    }

    [Fact]
    public void ImportFromStore_SkipsBackupWhenPathMissing_AndWritesBackupTombstone()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        MetadataStore store = CreateStore(metaRoot);
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

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

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
    public void ImportFromStore_ReadOnlySource_SkipsMissingBackupTombstoneWrite()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-a");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-readonly-source",
            Name = "Project Readonly Source",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow.AddDays(-2),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-readonly-source",
            ProjectExternalId = "proj-readonly-source",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            FileCount = 5,
            TotalBytes = 4096
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-readonly-source-missing",
            ProjectExternalId = "proj-readonly-source",
            SnapshotExternalId = "snap-readonly-source",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            Type = "manual",
            TotalBytes = 4096,
            PathRel = "missing-folder/backup-1",
            DestinationAlias = "Primary",
            IsProtected = false,
            IsEncrypted = false,
            KdfParamsJson = "{}"
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default.AsReadOnlySource());

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(1, result.ImportedSnapshots);
        Assert.Equal(0, result.ImportedBackups);

        var refreshedStore = new MetadataStore(metaRoot);
        var backupTombstones = refreshedStore
            .ListTombstones()
            .Where(t => string.Equals(t.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntityId)
            .ToList();
        Assert.DoesNotContain("backup-readonly-source-missing", backupTombstones, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportFromStore_ActiveWriterAutomaticallyMakesSourceReadOnly()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-active-writer");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-busy-source",
            Name = "Project Busy Source",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow.AddDays(-2),
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });
        store.UpsertSnapshot(new MetaSnapshot
        {
            ExternalId = "snap-busy-source",
            ProjectExternalId = "proj-busy-source",
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            FileCount = 5,
            TotalBytes = 4096
        });
        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-busy-source-missing",
            ProjectExternalId = "proj-busy-source",
            SnapshotExternalId = "snap-busy-source",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            Type = "manual",
            TotalBytes = 4096,
            PathRel = "missing-folder/backup-1",
            DestinationAlias = "Primary",
            KdfParamsJson = "{}"
        });

        var leaseService = new RepositoryLeaseService();
        using RepositoryLeaseHandle writer = AcquireTestLease(leaseService, metaRoot, "another-writer");
        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo, repositoryLeaseService: leaseService);

        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.DoesNotContain(
            new MetadataStore(metaRoot).ListTombstones(),
            tombstone => string.Equals(tombstone.EntityId, "backup-busy-source-missing", StringComparison.Ordinal));
        Assert.True(writer.IsOwner);
    }

    [Fact]
    public void ImportFromStore_CanSkipRestoreFlag()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        MetadataStore store = CreateStore(metaRoot);
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

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Project project = repo.GetProjectByName("Project Two");
        Assert.NotNull(project);
        Assert.False(project!.NeedsRestore);
    }

    [Fact]
    public void ImportFromStore_FallsBackToProjectsRootWhenRootHintIsUnsafe()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string localProjectsRoot = CreateTempDir();

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-unsafe-root",
            Name = "Unsafe Root Project",
            Preset = "dotnet",
            RootPathHint = "\\\\server\\share\\system32",
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig { ProjectsRoot = localProjectsRoot });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        Project project = repo.GetProjectByName("Unsafe Root Project");
        Assert.NotNull(project);
        Assert.Equal(Path.Combine(localProjectsRoot, "Unsafe Root Project"), project!.RootPath);
    }

    [Fact]
    public void ImportFromStore_FallsBackToProjectsRootWhenRootHintIsVaultSyncTemp()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var localProjectsRoot = CreateTempDir();
        var tempImportRoot = new TempDirectory(Path.Combine(
            Path.GetTempPath(),
            "vaultsync-meta-import",
            Guid.NewGuid().ToString("N")));
        _tempDirs.Add(tempImportRoot);
        var tempRootHint = Path.Combine(tempImportRoot.Path, "Temp Root Project");
        Directory.CreateDirectory(tempRootHint);

        var store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-temp-root",
            Name = "Temp Root Project",
            Preset = "dotnet",
            RootPathHint = tempRootHint,
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        var repo = CreateRepository(dbPath);
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig { ProjectsRoot = localProjectsRoot });

        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var project = repo.GetProjectByName("Temp Root Project");
        Assert.NotNull(project);
        Assert.Equal(Path.Combine(localProjectsRoot, "Temp Root Project"), project!.RootPath);
    }

    [Fact]
    public void ImportFromStore_PreservesRootHint_WhenNoLocalMappingIsAvailable()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        const string sourceRootHint = "/source-machine/Projects/Project Root";

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-preserve-hint",
            Name = "Preserve Root Hint",
            Preset = "dotnet",
            RootPathHint = sourceRootHint,
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        using var configScope = new TestAppConfigScope();
        AppConfig cfg = AppConfigStore.Load();
        cfg.ProjectsRoot = string.Empty;
        AppConfigStore.Save(cfg);

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        Project project = repo.GetProjectByName("Preserve Root Hint");
        Assert.NotNull(project);
        Assert.Equal(sourceRootHint, project!.RootPath);
    }

    [Fact]
    public void ImportFromStore_RepairsExistingProjectWithEmptyRootPath()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-repair-root",
            Name = "Repair Existing Root",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        TestRepository.AddProject(repo, "Repair Existing Root", string.Empty, "unity", DateTime.UtcNow);

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        Project project = repo.GetProjectByName("Repair Existing Root");
        Assert.NotNull(project);
        Assert.Equal(projectRoot, project!.RootPath);
    }

    [Fact]
    public void ImportFromStore_RepairsExistingProjectWithStaleCrossOsRootPath()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectsRoot = CreateTempDir();
        string localProjectRoot = Path.Combine(projectsRoot, "real-folder");
        Directory.CreateDirectory(localProjectRoot);

        using var configScope = new TestAppConfigScope();
        AppConfig cfg = AppConfigStore.Load();
        cfg.ProjectsRoot = projectsRoot;
        AppConfigStore.Save(cfg);

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-cross-os-root",
            Name = "Display Name",
            Preset = "dotnet",
            RootPathHint = @"D:\Dev\real-folder",
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        TestRepository.AddProject(repo, "Display Name", @"D:\Dev\real-folder", "dotnet", DateTime.UtcNow);

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        Project project = repo.GetProjectByName("Display Name");
        Assert.NotNull(project);
        Assert.Equal(localProjectRoot, project!.RootPath);
    }

    [Fact]
    public void ImportFromStore_BlocksNewerSchema()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertMetaInfo(new MetaInfo
        {
            SchemaVersion = MetadataStore.CurrentSchemaVersion + 1,
            CreatedUtc = DateTime.UtcNow,
            LastWriteUtc = DateTime.UtcNow,
            WriterAppVersion = "2.0.0",
            WriterMachineId = "machine-b"
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot);

        Assert.Equal(MetadataSyncStatus.Incompatible, result.Status);
    }

    [Fact]
    public void ImportFromStore_AppliesBackupTombstone()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = TestRepository.AddProject(repo, "Project Tombstone", CreateTempDir(), "unity", DateTime.UtcNow);

        int snapshotId = repo.CreateSnapshotFromMetadata("snap-ts", projectId, DateTime.UtcNow, 1, 100);
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

        MetadataStore store = CreateStore(metaRoot);
        store.AddTombstone(new MetaTombstone
        {
            EntityType = "backup",
            EntityId = "backup-ts",
            DeletedUtc = DateTime.UtcNow,
            OriginMachineId = "machine-c"
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Null(repo.GetBackupByExternalId("backup-ts"));
    }

    [Fact]
    public void ExportBackupToStore_WritesMetadata()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = TestRepository.AddProject(repo, "Project Export", CreateTempDir(), "unity", DateTime.UtcNow);

        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-export/2025-01-01_00-00-00",
            metaRoot,
            "Primary");

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ExportBackupToStore(metaRoot, backupId, "1.0.0", "machine-d");

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var store = new MetadataStore(metaRoot);
        var backups = store.ListBackups().ToList();
        var projects = store.ListProjects().ToList();

        Assert.Single(backups);
        Assert.Single(projects);
        Assert.Equal("Primary", backups[0].DestinationAlias);

        Backup updatedBackup = repo.GetBackupById(backupId);
        Assert.False(string.IsNullOrWhiteSpace(updatedBackup?.ExternalId));
    }

    [Fact]
    public void ExportBackupToStore_ActiveWriterBlocksMetadataMutation()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = TestRepository.AddProject(repo, "Project Busy Export", CreateTempDir(), "unity", DateTime.UtcNow);
        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-busy/2026-08-12_00-00-00",
            metaRoot,
            "Primary");
        var leaseService = new RepositoryLeaseService();
        using RepositoryLeaseHandle writer = AcquireTestLease(leaseService, metaRoot, "another-writer");
        var service = new MetadataSyncService(repo, repositoryLeaseService: leaseService);

        MetadataSyncResult result = service.ExportBackupToStore(metaRoot, backupId, "1.8.7", "machine-local");

        Assert.Equal(MetadataSyncStatus.RepositoryBusy, result.Status);
        Assert.False(File.Exists(new MetadataStore(metaRoot).DatabasePath));
        Assert.True(writer.IsOwner);
    }

    [Fact]
    public void ExportProjectToStore_ActiveWriterBlocksMetadataMutation()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = TestRepository.AddProject(repo, "Project Busy Settings", CreateTempDir(), "unity", DateTime.UtcNow);
        var leaseService = new RepositoryLeaseService();
        using RepositoryLeaseHandle writer = AcquireTestLease(leaseService, metaRoot, "another-writer");
        var service = new MetadataSyncService(repo, repositoryLeaseService: leaseService);

        MetadataSyncResult result = service.ExportProjectToStore(metaRoot, projectId, "1.8.7", "machine-local");

        Assert.Equal(MetadataSyncStatus.RepositoryBusy, result.Status);
        Assert.False(File.Exists(new MetadataStore(metaRoot).DatabasePath));
        Assert.True(writer.IsOwner);
    }

    [Fact]
    public void ExportBackupToStore_DeferredQueueFlushesOnceAndIsRemoved()
    {
        string unavailableRoot = Path.Combine(CreateTempDir(), "offline-destination");
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = TestRepository.AddProject(repo, "Project Deferred Export", CreateTempDir(), "unity", DateTime.UtcNow);
        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-deferred/2026-08-12_00-00-00",
            unavailableRoot,
            "Offline");
        var service = new MetadataSyncService(repo);
        string deferredRoot = GetExpectedDeferredRoot(unavailableRoot);

        MetadataSyncResult queued = service.ExportBackupToStore(
            unavailableRoot,
            backupId,
            "1.8.7",
            "machine-local");
        Assert.Equal(MetadataSyncStatus.WriteFailed, queued.Status);
        Assert.True(File.Exists(new MetadataStore(deferredRoot).DatabasePath));

        Directory.CreateDirectory(unavailableRoot);
        MetadataSyncResult flushed = service.ExportBackupToStore(
            unavailableRoot,
            backupId,
            "1.8.7",
            "machine-local");

        Assert.Equal(MetadataSyncStatus.Success, flushed.Status);
        Assert.False(Directory.Exists(deferredRoot));
        Assert.Single(new MetadataStore(unavailableRoot).ListBackups());
    }

    [Fact]
    public void ExportBackupToStore_DeferredQueueCannotOverwriteDivergedDestination()
    {
        string unavailableRoot = Path.Combine(CreateTempDir(), "diverged-destination");
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = TestRepository.AddProject(repo, "Project Deferred Conflict", CreateTempDir(), "unity", DateTime.UtcNow);
        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-conflict/2026-08-12_00-00-00",
            unavailableRoot,
            "Offline");
        var service = new MetadataSyncService(repo);
        string deferredRoot = GetExpectedDeferredRoot(unavailableRoot);

        MetadataSyncResult queued = service.ExportBackupToStore(
            unavailableRoot,
            backupId,
            "1.8.7",
            "machine-local");
        Assert.Equal(MetadataSyncStatus.WriteFailed, queued.Status);

        Directory.CreateDirectory(unavailableRoot);
        MetadataStore destinationStore = CreateStore(unavailableRoot);
        destinationStore.UpsertProject(new MetaProject
        {
            ExternalId = "remote-project",
            Name = "Remote Project",
            Preset = "generic",
            RootPathHint = "/remote/project",
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{}",
            UpdatedUtc = DateTime.UtcNow
        });

        MetadataSyncResult blocked = service.ExportBackupToStore(
            unavailableRoot,
            backupId,
            "1.8.7",
            "machine-local");

        Assert.Equal(MetadataSyncStatus.RepositoryBusy, blocked.Status);
        Assert.True(File.Exists(new MetadataStore(deferredRoot).DatabasePath));
        MetaProject remote = Assert.Single(new MetadataStore(unavailableRoot).ListProjects());
        Assert.Equal("remote-project", remote.ExternalId);
        Directory.Delete(deferredRoot, recursive: true);
    }

    [Fact]
    public async System.Threading.Tasks.Task ExportBackupTombstoneToStoreAsync_ActiveWriterBlocksTombstone()
    {
        string metaRoot = CreateTempDir();
        var leaseService = new RepositoryLeaseService();
        using RepositoryLeaseHandle writer = AcquireTestLease(leaseService, metaRoot, "another-writer");

        await MetadataSyncService.ExportBackupTombstoneToStoreAsync(
            metaRoot,
            "backup-must-not-write",
            "1.8.7",
            "machine-local",
            leaseOwnerId: Guid.NewGuid().ToString("N"));

        Assert.False(File.Exists(new MetadataStore(metaRoot).DatabasePath));
        Assert.True(writer.IsOwner);
    }

    [Fact]
    public void ExportBackupToStore_MissingBackup_SkipsWithoutCreatingStore()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);

        MetadataSyncResult result = service.ExportBackupToStore(metaRoot, 180, "1.8.0", "machine-d");

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(0, result.ImportedProjects);
        Assert.Equal(0, result.ImportedSnapshots);
        Assert.Equal(0, result.ImportedBackups);
        Assert.False(File.Exists(new MetadataStore(metaRoot).DatabasePath));
    }

    [Fact]
    public void MetadataStore_WriteBatch_RollsBackAllWritesOnFailure()
    {
        string metaRoot = CreateTempDir();
        MetadataStore store = CreateStore(metaRoot);

        Assert.Throws<InvalidOperationException>(() =>
            store.ExecuteWriteBatch(() =>
            {
                store.UpsertProject(new MetaProject
                {
                    ExternalId = "project-batch",
                    Name = "Batch Project",
                    Preset = "generic",
                    RootPathHint = CreateTempDir(),
                    CreatedUtc = DateTime.UtcNow,
                    SettingsJson = "{}",
                    UpdatedUtc = DateTime.UtcNow
                });
                throw new InvalidOperationException("Simulated export failure.");
            }));

        Assert.Empty(store.ListProjects());
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_AppliesEncryptionPolicyAndKeyRef()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-settings-1",
            Name = "Project Settings",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{\"encryptionPolicy\":\"encrypted\",\"encryptionKeyRef\":\"project-key-ref-01\"}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Project project = repo.GetProjectByName("Project Settings");
        Assert.NotNull(project);
        Assert.Equal(ProjectEncryptionPolicy.Encrypted, project!.EncryptionPolicy);
        Assert.Equal("project-key-ref-01", project.EncryptionKeyRef);
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_AppliesAutoBackupPreference()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        using var configScope = new TestAppConfigScope();
        AppConfig cfg = AppConfigStore.Load();
        cfg.Backups.AutoBackupDisabledProjects.Clear();
        AppConfigStore.Save(cfg);

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-settings-auto",
            Name = "Project Auto Backup",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow,
            SettingsJson = "{\"autoBackupEnabled\":false}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Project project = repo.GetProjectByName("Project Auto Backup");
        Assert.NotNull(project);

        AppConfig refreshedConfig = AppConfigStore.Load();
        Assert.Contains(project!.Id, refreshedConfig.Backups.AutoBackupDisabledProjects);
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_UpdatesExistingProjectKeyRef()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        DateTime now = DateTime.UtcNow;

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            ExternalId = "proj-settings-existing",
            Name = "Project Settings Existing",
            RootPath = projectRoot,
            Preset = "unity",
            CreatedUtc = now.AddDays(-2),
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted,
            EncryptionKeyRef = "local-key-ref-old"
        });

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-settings-existing",
            Name = "Project Settings Existing",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = now.AddDays(-2),
            SettingsJson = "{\"encryptionPolicy\":\"encrypted\",\"encryptionKeyRef\":\"remote-key-ref-new\"}",
            UpdatedUtc = now
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Project project = repo.GetProjectById(projectId);
        Assert.NotNull(project);
        Assert.Equal(ProjectEncryptionPolicy.Encrypted, project!.EncryptionPolicy);
        Assert.Equal("remote-key-ref-new", project.EncryptionKeyRef);
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_CanClearExistingProjectKeyRef()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        DateTime now = DateTime.UtcNow;

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            ExternalId = "proj-settings-clear",
            Name = "Project Settings Clear",
            RootPath = projectRoot,
            Preset = "unity",
            CreatedUtc = now.AddDays(-2),
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted,
            EncryptionKeyRef = "local-key-ref"
        });

        MetadataStore store = CreateStore(metaRoot);
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-settings-clear",
            Name = "Project Settings Clear",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = now.AddDays(-2),
            SettingsJson = "{\"encryptionPolicy\":\"encrypted\",\"encryptionKeyRef\":null}",
            UpdatedUtc = now
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Project project = repo.GetProjectById(projectId);
        Assert.NotNull(project);
        Assert.Equal(ProjectEncryptionPolicy.Encrypted, project!.EncryptionPolicy);
        Assert.Null(project.EncryptionKeyRef);
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_RecordsConflictInsteadOfOverwritingTrackedFields()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        DateTime now = DateTime.UtcNow;

        using var configScope = new TestAppConfigScope();
        AppConfig cfg = AppConfigStore.Load();
        cfg.Advanced.ProjectMetadataConflicts.Clear();
        AppConfigStore.Save(cfg);

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            ExternalId = "proj-conflict-1",
            Name = "Project Conflict",
            RootPath = projectRoot,
            Preset = "unity",
            CreatedUtc = now.AddDays(-2),
            PreferredDestinationId = "dest-local",
            RestoreMode = ProjectRestoreMode.Direct,
            VerificationPolicy = ProjectVerificationPolicy.Always,
            Tags = "local,stable"
        });

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-conflict");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-conflict-1",
            Name = "Project Conflict",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = now.AddDays(-2),
            SettingsJson = "{\"preferredDestinationId\":\"dest-imported\",\"restoreMode\":\"sandbox\",\"verificationPolicy\":\"manual\",\"tags\":\"imported,remote\"}",
            UpdatedUtc = now
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        Project project = repo.GetProjectById(projectId);
        Assert.NotNull(project);
        Assert.Equal("dest-local", project!.PreferredDestinationId);
        Assert.Equal(ProjectRestoreMode.Direct, project.RestoreMode);
        Assert.Equal(ProjectVerificationPolicy.Always, project.VerificationPolicy);
        Assert.Equal("local,stable", project.Tags);

        AppConfig refreshedConfig = AppConfigStore.Load();
        ProjectMetadataConflictRecord conflict = Assert.Single(refreshedConfig.Advanced.ProjectMetadataConflicts);
        Assert.Equal(projectId, conflict.ProjectId);
        Assert.Equal("machine-conflict", conflict.SourceMachineId);
        Assert.Equal("dest-local", conflict.Local.PreferredDestinationId);
        Assert.Equal("dest-imported", conflict.Imported.PreferredDestinationId);
        Assert.Equal(ProjectRestoreMode.Direct, conflict.Local.RestoreMode);
        Assert.Equal(ProjectRestoreMode.Sandbox, conflict.Imported.RestoreMode);
    }

    [Fact]
    public void ExportBackupToStore_ProjectSettings_IncludeEncryptionFields()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            Name = "Project Export Settings",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow,
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted,
            EncryptionKeyRef = "project-key-ref-export"
        });

        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-export-settings/2025-01-01_00-00-00",
            metaRoot,
            "Primary");

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ExportBackupToStore(metaRoot, backupId, "1.5.0", "machine-settings");
        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var store = new MetadataStore(metaRoot);
        MetaProject metaProject = store.ListProjects().Single();
        using var doc = JsonDocument.Parse(metaProject.SettingsJson);
        Assert.True(doc.RootElement.TryGetProperty("encryptionPolicy", out JsonElement policy));
        Assert.Equal(ProjectEncryptionPolicy.Encrypted, policy.GetString());
        Assert.True(doc.RootElement.TryGetProperty("encryptionKeyRef", out JsonElement keyRef));
        Assert.Equal("project-key-ref-export", keyRef.GetString());
    }

    [Fact]
    public void ExportBackupToStore_ProjectSettings_IncludeNullEncryptionKeyRefWhenUnset()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            Name = "Project Export Settings No KeyRef",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow,
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted
        });

        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-export-settings-no-keyref/2025-01-01_00-00-00",
            metaRoot,
            "Primary");

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ExportBackupToStore(metaRoot, backupId, "1.5.0", "machine-settings");
        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var store = new MetadataStore(metaRoot);
        MetaProject metaProject = store.ListProjects().Single();
        using var doc = JsonDocument.Parse(metaProject.SettingsJson);
        Assert.True(doc.RootElement.TryGetProperty("encryptionPolicy", out JsonElement policy));
        Assert.Equal(ProjectEncryptionPolicy.Encrypted, policy.GetString());
        Assert.True(doc.RootElement.TryGetProperty("encryptionKeyRef", out JsonElement keyRef));
        Assert.Equal(JsonValueKind.Null, keyRef.ValueKind);
    }

    [Fact]
    public void ExportBackupToStore_ProjectSettings_IncludeAutoBackupEnabledWhenDisabled()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        using var configScope = new TestAppConfigScope();
        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = TestRepository.AddProject(repo, "Project Export Auto Backup", CreateTempDir(), "unity", DateTime.UtcNow);

        AppConfig cfg = AppConfigStore.Load();
        cfg.Backups.AutoBackupDisabledProjects = [projectId];
        AppConfigStore.Save(cfg);

        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-export-auto-backup/2025-01-01_00-00-00",
            metaRoot,
            "Primary");

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ExportBackupToStore(metaRoot, backupId, "1.7.2", "machine-settings");
        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var store = new MetadataStore(metaRoot);
        MetaProject metaProject = store.ListProjects().Single();
        using var doc = JsonDocument.Parse(metaProject.SettingsJson);
        Assert.True(doc.RootElement.TryGetProperty("autoBackupEnabled", out JsonElement autoBackupEnabled));
        Assert.False(autoBackupEnabled.GetBoolean());
    }

    [Fact]
    public void ExportProjectToStore_ProjectSettings_TransportAutoBackupEnabledWithoutBackup()
    {
        string metaRoot = CreateTempDir();
        string sourceDbPath = Path.Combine(CreateTempDir(), "vaultsync-source.db");
        string targetDbPath = Path.Combine(CreateTempDir(), "vaultsync-target.db");

        using var configScope = new TestAppConfigScope();
        SqliteRepository sourceRepo = CreateRepository(sourceDbPath);
        int projectId = TestRepository.AddProject(sourceRepo, "Project Settings Only", CreateTempDir(), "unity", DateTime.UtcNow);

        AppConfig cfg = AppConfigStore.Load();
        cfg.Backups.AutoBackupDisabledProjects = [projectId];
        AppConfigStore.Save(cfg);

        var exportService = new MetadataSyncService(sourceRepo);
        MetadataSyncResult exportResult = exportService.ExportProjectToStore(metaRoot, projectId, "1.7.3", "machine-source");
        Assert.Equal(MetadataSyncStatus.Success, exportResult.Status);

        var store = new MetadataStore(metaRoot);
        MetaProject metaProject = store.ListProjects().Single();
        using (var doc = JsonDocument.Parse(metaProject.SettingsJson))
        {
            Assert.True(doc.RootElement.TryGetProperty("autoBackupEnabled", out JsonElement autoBackupEnabled));
            Assert.False(autoBackupEnabled.GetBoolean());
        }

        cfg.Backups.AutoBackupDisabledProjects.Clear();
        AppConfigStore.Save(cfg);

        SqliteRepository targetRepo = CreateRepository(targetDbPath);
        var importService = new MetadataSyncService(targetRepo);
        MetadataSyncResult importResult = importService.ImportFromStore(metaRoot, MetadataSyncOptions.Default);
        Assert.Equal(MetadataSyncStatus.Success, importResult.Status);

        Project importedProject = targetRepo.GetProjectByName("Project Settings Only");
        Assert.NotNull(importedProject);
        AppConfig refreshedConfig = AppConfigStore.Load();
        Assert.Contains(importedProject!.Id, refreshedConfig.Backups.AutoBackupDisabledProjects);
    }

    [Fact]
    public void ExportBackupToStore_ProjectSettings_IncludeDestinationRestoreVerificationAndTags()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            Name = "Project Export Tracked Settings",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow,
            PreferredDestinationId = "dest-nas-primary",
            RestoreMode = ProjectRestoreMode.Sandbox,
            VerificationPolicy = ProjectVerificationPolicy.Manual,
            Tags = "remote,critical"
        });

        int snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-export-tracked-settings/2025-01-01_00-00-00",
            metaRoot,
            "Primary");

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ExportBackupToStore(metaRoot, backupId, "1.7.3", "machine-settings");
        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var store = new MetadataStore(metaRoot);
        MetaProject metaProject = store.ListProjects().Single();
        using var doc = JsonDocument.Parse(metaProject.SettingsJson);
        Assert.True(doc.RootElement.TryGetProperty("preferredDestinationId", out JsonElement destinationId));
        Assert.Equal("dest-nas-primary", destinationId.GetString());
        Assert.True(doc.RootElement.TryGetProperty("restoreMode", out JsonElement restoreMode));
        Assert.Equal(ProjectRestoreMode.Sandbox, restoreMode.GetString());
        Assert.True(doc.RootElement.TryGetProperty("verificationPolicy", out JsonElement verificationPolicy));
        Assert.Equal(ProjectVerificationPolicy.Manual, verificationPolicy.GetString());
        Assert.True(doc.RootElement.TryGetProperty("tags", out JsonElement tags));
        Assert.Equal("remote,critical", tags.GetString());
    }

    [Fact]
    public void ExportBackupToStore_MetadataContract_ExportsSelectedFieldsExplicitly()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        const string expectedAvatarColor = "#1A2B3C";
        string expectedTopPathsJson = JsonSerializer.Serialize(new[]
        {
            new SnapshotDiffPathStat("Assets/Scripts/Game.cs", 4, 3072),
            new SnapshotDiffPathStat("Assets/Scenes/Main.unity", 2, 1024)
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        {
            SqliteRepository repo = CreateRepository(dbPath);
            int projectId = TestRepository.AddProject(repo, "Project Export Contract", CreateTempDir(), "unity", DateTime.UtcNow);

            var diffSummary = new SnapshotDiffSummary(
                5,
                3,
                1,
                4096,
                SnapshotDiffSummary.ParseTopChangedPaths(expectedTopPathsJson));

            int snapshotId = repo.CreateSnapshot(projectId, 42, 65_536, diffSummary);
            const string backupPath = "project-export-contract/2026-04-17_12-00-00";
            int backupId = repo.CreateBackup(
                projectId,
                snapshotId,
                "manual",
                65_536,
                backupPath,
                metaRoot,
                "Archive NAS",
                backupMode: BackupModes.Incremental,
                isProtected: true);

            var service = new MetadataSyncService(repo, projectColorResolver: _ => expectedAvatarColor);
            MetadataSyncResult result = service.ExportBackupToStore(metaRoot, backupId, "1.7.3", "machine-contract");
            Assert.Equal(MetadataSyncStatus.Success, result.Status);

            var store = new MetadataStore(metaRoot);
            MetaProject metaProject = Assert.Single(store.ListProjects());
            MetaSnapshot metaSnapshot = Assert.Single(store.ListSnapshots());
            MetaBackup metaBackup = Assert.Single(store.ListBackups());

            using (var doc = JsonDocument.Parse(metaProject.SettingsJson))
            {
                Assert.True(doc.RootElement.TryGetProperty("avatarColor", out JsonElement avatarColor));
                Assert.Equal(expectedAvatarColor, avatarColor.GetString());
            }

            Assert.Equal(5, metaSnapshot.DiffAdded);
            Assert.Equal(3, metaSnapshot.DiffModified);
            Assert.Equal(1, metaSnapshot.DiffDeleted);
            Assert.Equal(4096, metaSnapshot.DiffNetBytes);
            Assert.Equal(expectedTopPathsJson, metaSnapshot.DiffTopPathsJson);

            Assert.Equal(BackupModes.Incremental, metaBackup.BackupMode);
            Assert.True(metaBackup.IsProtected);
            Assert.Equal("machine-contract", metaBackup.OriginMachineName);
            Assert.Equal("Archive NAS", metaBackup.DestinationAlias);
        }
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_AppliesTrackedFieldsWhenCreatingProject()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        DateTime now = DateTime.UtcNow;

        using var configScope = new TestAppConfigScope();
        var destination = new BackupDestination
        {
            Path = CreateTempDir(),
            Alias = "Imported Destination",
            Active = true
        };
        string destinationId = DestinationIdentityService.GetId(destination);
        AppConfig cfg = AppConfigStore.Load();
        cfg.Advanced.ProjectMetadataConflicts.Clear();
        cfg.Backups.Destinations = [destination];
        AppConfigStore.Save(cfg);

        SqliteRepository repo = CreateRepository(dbPath);
        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-settings-apply");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-settings-apply",
            Name = "Project Settings Apply",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = now.AddDays(-2),
            SettingsJson = $"{{\"preferredDestinationId\":\"{destinationId}\",\"restoreMode\":\"sandbox\",\"verificationPolicy\":\"manual\",\"tags\":\"imported,remote\"}}",
            UpdatedUtc = now
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        Project project = repo.GetProjectByExternalId("proj-settings-apply");
        Assert.NotNull(project);
        Assert.Equal(destinationId, project!.PreferredDestinationId);
        Assert.Equal(ProjectRestoreMode.Sandbox, project.RestoreMode);
        Assert.Equal(ProjectVerificationPolicy.Manual, project.VerificationPolicy);
        Assert.Equal("imported,remote", project.Tags);

        AppConfig refreshedConfig = AppConfigStore.Load();
        Assert.Empty(refreshedConfig.Advanced.ProjectMetadataConflicts);
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_NormalizesPreferredDestinationIdFromAliasWhenCreatingProject()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        using var configScope = new TestAppConfigScope();
        var destination = new BackupDestination
        {
            Path = CreateTempDir(),
            Alias = "NAS Primary",
            Active = true
        };
        string destinationId = DestinationIdentityService.GetId(destination);
        AppConfig cfg = AppConfigStore.Load();
        cfg.Backups.Destinations = [destination];
        AppConfigStore.Save(cfg);

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-alias-import");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-settings-alias",
            Name = "Project Settings Alias",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            SettingsJson = "{\"preferredDestinationId\":\"NAS Primary\"}",
            UpdatedUtc = DateTime.UtcNow
        });

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Project project = repo.GetProjectByExternalId("proj-settings-alias");
        Assert.NotNull(project);
        Assert.Equal(destinationId, project!.PreferredDestinationId);
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_RecordsNormalizedPreferredDestinationInConflict()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        DateTime now = DateTime.UtcNow;

        using var configScope = new TestAppConfigScope();
        var destination = new BackupDestination
        {
            Path = CreateTempDir(),
            Alias = "NAS Imported",
            Active = true
        };
        string destinationId = DestinationIdentityService.GetId(destination);
        AppConfig cfg = AppConfigStore.Load();
        cfg.Advanced.ProjectMetadataConflicts.Clear();
        cfg.Backups.Destinations = [destination];
        AppConfigStore.Save(cfg);

        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            ExternalId = "proj-conflict-normalized",
            Name = "Project Conflict Normalized",
            RootPath = projectRoot,
            Preset = "unity",
            CreatedUtc = now.AddDays(-2),
            PreferredDestinationId = "dest-local",
            RestoreMode = ProjectRestoreMode.Direct,
            VerificationPolicy = ProjectVerificationPolicy.Always,
            Tags = "local"
        });

        MetadataStore store = CreateStore(metaRoot);
        SeedMetaInfo(store, "machine-conflict-normalized");
        store.UpsertProject(new MetaProject
        {
            ExternalId = "proj-conflict-normalized",
            Name = "Project Conflict Normalized",
            Preset = "unity",
            RootPathHint = projectRoot,
            CreatedUtc = now.AddDays(-2),
            SettingsJson = "{\"preferredDestinationId\":\"NAS Imported\",\"restoreMode\":\"sandbox\",\"verificationPolicy\":\"manual\",\"tags\":\"imported\"}",
            UpdatedUtc = now
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Project project = repo.GetProjectById(projectId);
        Assert.NotNull(project);
        Assert.Equal("dest-local", project!.PreferredDestinationId);

        AppConfig refreshedConfig = AppConfigStore.Load();
        ProjectMetadataConflictRecord conflict = Assert.Single(refreshedConfig.Advanced.ProjectMetadataConflicts);
        Assert.Equal(destinationId, conflict.Imported.PreferredDestinationId);
        Assert.Equal("dest-local", conflict.Local.PreferredDestinationId);
    }

    [Fact]
    public void ImportFromStore_MetadataContract_ImportsSelectedFieldsExplicitly()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        var capturedColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string projectExternalId = "proj-contract-import";
        const string snapshotExternalId = "snap-contract-import";
        const string backupExternalId = "backup-contract-import";
        const string backupPathRel = "project-contract-import/2026-04-17_13-00-00";
        string topPathsJson = JsonSerializer.Serialize(new[]
        {
            new SnapshotDiffPathStat("src/App.cs", 6, 8192),
            new SnapshotDiffPathStat("assets/logo.png", 1, 4096)
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        Directory.CreateDirectory(Path.Combine(metaRoot, backupPathRel));

        {
            MetadataStore store = CreateStore(metaRoot);
            SeedMetaInfo(store, "machine-import-source");
            store.UpsertProject(new MetaProject
            {
                ExternalId = projectExternalId,
                Name = "Project Import Contract",
                Preset = "unity",
                RootPathHint = projectRoot,
                CreatedUtc = DateTime.UtcNow.AddDays(-2),
                SettingsJson = "{\"avatarColor\":\"#ABCDEF\"}",
                UpdatedUtc = DateTime.UtcNow
            });
            store.UpsertSnapshot(new MetaSnapshot
            {
                ExternalId = snapshotExternalId,
                ProjectExternalId = projectExternalId,
                CreatedUtc = DateTime.UtcNow.AddDays(-1),
                FileCount = 17,
                TotalBytes = 81_920,
                DiffAdded = 7,
                DiffModified = 4,
                DiffDeleted = 2,
                DiffNetBytes = 12_288,
                DiffTopPathsJson = topPathsJson
            });
            store.UpsertBackup(new MetaBackup
            {
                ExternalId = backupExternalId,
                ProjectExternalId = projectExternalId,
                SnapshotExternalId = snapshotExternalId,
                CreatedUtc = DateTime.UtcNow.AddHours(-2),
                Type = "manual",
                BackupMode = BackupModes.Incremental,
                TotalBytes = 81_920,
                PathRel = backupPathRel,
                DestinationAlias = "Remote Vault",
                OriginMachineName = "machine-import-source",
                IsProtected = true,
                IsEncrypted = false,
                KdfParamsJson = BackupCryptoDescriptor.PlainMetadataJson
            });

            SqliteRepository repo = CreateRepository(dbPath);
            var service = new MetadataSyncService(
                repo,
                projectColorApplier: (externalId, color) => capturedColors[externalId] = color);
            MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

            Assert.Equal(MetadataSyncStatus.Success, result.Status);

            Assert.True(capturedColors.TryGetValue(projectExternalId, out string importedColor));
            Assert.Equal("#ABCDEF", importedColor);

            Snapshot importedSnapshot = repo.GetSnapshotByExternalId(snapshotExternalId);
            Assert.NotNull(importedSnapshot);
            Assert.Equal(7, importedSnapshot!.DiffAdded);
            Assert.Equal(4, importedSnapshot.DiffModified);
            Assert.Equal(2, importedSnapshot.DiffDeleted);
            Assert.Equal(12_288, importedSnapshot.DiffNetBytes);
            Assert.Equal(topPathsJson, importedSnapshot.DiffTopPathsJson);

            Backup importedBackup = repo.GetBackupByExternalId(backupExternalId);
            Assert.NotNull(importedBackup);
            Assert.Equal(BackupModes.Incremental, importedBackup!.BackupMode);
            Assert.True(importedBackup.IsProtected);
            Assert.Equal("machine-import-source", importedBackup.OriginMachineName);
            Assert.Equal("Remote Vault", importedBackup.DestinationAlias);
        }
    }

    [Fact]
    public void ImportFromStore_AppliesProjectTombstone()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();

        using var configScope = new TestAppConfigScope();
        SqliteRepository repo = CreateRepository(dbPath);
        int projectId = repo.AddProject(new Project
        {
            ExternalId = "proj-remove-me",
            Name = "Project Remove Me",
            RootPath = projectRoot,
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

        AppConfig cfg = AppConfigStore.Load();
        cfg.Backups.AutoBackupDisabledProjects = [projectId];
        AppConfigStore.Save(cfg);

        MetadataStore store = CreateStore(metaRoot);
        store.AddTombstone(new MetaTombstone
        {
            EntityType = "project",
            EntityId = "proj-remove-me",
            DeletedUtc = DateTime.UtcNow,
            OriginMachineId = "machine-delete"
        });

        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Null(repo.GetProjectById(projectId));

        AppConfig refreshedConfig = AppConfigStore.Load();
        Assert.DoesNotContain(projectId, refreshedConfig.Backups.AutoBackupDisabledProjects);
    }

    [Fact]
    public void ExportImportRoundTrip_PreservesMixedPlainAndEncryptedBackups()
    {
        string metaRoot = CreateTempDir();
        string sourceDbPath = Path.Combine(CreateTempDir(), "vaultsync-source.db");
        string targetDbPath = Path.Combine(CreateTempDir(), "vaultsync-target.db");

        SqliteRepository sourceRepo = CreateRepository(sourceDbPath);
        int projectId = TestRepository.AddProject(sourceRepo, "Project Mixed", CreateTempDir(), "unity", DateTime.UtcNow);

        int plainSnapshotId = sourceRepo.CreateSnapshot(projectId, 10, 10_000);
        const string plainPath = "project-mixed/2026-01-01_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, plainPath));
        int plainBackupId = sourceRepo.CreateBackup(
            projectId,
            plainSnapshotId,
            "manual",
            10_000,
            plainPath,
            metaRoot,
            "Primary");

        int encryptedSnapshotId = sourceRepo.CreateSnapshot(projectId, 11, 11_000);
        const string encryptedPath = "project-mixed/2026-01-02_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, encryptedPath));
        string encryptedDescriptor = BackupCryptoDescriptor
            .Encrypted("aes-256-cbc-hmac-sha256-v1", "pbkdf2-sha256-v1", "pbkdf2-iter-210000")
            .ToMetadataJson(true);
        int encryptedBackupId = sourceRepo.CreateBackup(
            projectId,
            encryptedSnapshotId,
            "manual",
            11_000,
            encryptedPath,
            metaRoot,
            "Primary",
            isEncrypted: true,
            cryptoDescriptorJson: encryptedDescriptor);

        var sourceService = new MetadataSyncService(sourceRepo);
        MetadataSyncResult exportResult = sourceService.ExportBackupToStore(
            metaRoot,
            encryptedBackupId,
            "1.5.0",
            "machine-source",
            forceBackfill: true);

        Assert.Equal(MetadataSyncStatus.Success, exportResult.Status);

        SqliteRepository targetRepo = CreateRepository(targetDbPath);
        var importService = new MetadataSyncService(targetRepo);
        MetadataSyncResult importResult = importService.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, importResult.Status);
        Assert.Equal(2, importResult.ImportedBackups);

        Backup sourcePlainBackup = sourceRepo.GetBackupById(plainBackupId);
        Backup sourceEncryptedBackup = sourceRepo.GetBackupById(encryptedBackupId);
        Assert.NotNull(sourcePlainBackup);
        Assert.NotNull(sourceEncryptedBackup);

        Backup importedPlain = targetRepo.GetBackupByExternalId(sourcePlainBackup!.ExternalId);
        Backup importedEncrypted = targetRepo.GetBackupByExternalId(sourceEncryptedBackup!.ExternalId);
        Assert.NotNull(importedPlain);
        Assert.NotNull(importedEncrypted);

        Assert.False(importedPlain!.IsEncrypted);
        Assert.Equal(BackupCryptoDescriptor.PlainMetadataJson, importedPlain.CryptoDescriptorJson);

        Assert.True(importedEncrypted!.IsEncrypted);
        var importedDescriptor = BackupCryptoDescriptor.FromMetadata(
            importedEncrypted.IsEncrypted,
            importedEncrypted.CryptoDescriptorJson);
        Assert.Equal("aes-256-cbc-hmac-sha256-v1", importedDescriptor.Algorithm);
        Assert.Equal("pbkdf2-sha256-v1", importedDescriptor.KdfProfile);
        Assert.Equal("pbkdf2-iter-210000", importedDescriptor.KdfParamRef);
    }

    [Fact]
    public void ImportFromStore_LegacyBackupSchemaWithoutEncryptionColumns_ImportsAsPlain()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        const string backupPathRel = "legacy-project/2026-01-01_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, backupPathRel));

        CreateLegacyStoreWithoutEncryptionColumns(metaRoot, projectRoot, backupPathRel);

        SqliteRepository repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        MetadataSyncResult result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(1, result.ImportedSnapshots);
        Assert.Equal(1, result.ImportedBackups);

        Backup backup = repo.GetBackupByExternalId("legacy-backup-1");
        Assert.NotNull(backup);
        Assert.False(backup!.IsEncrypted);
        Assert.Equal(BackupCryptoDescriptor.PlainMetadataJson, backup.CryptoDescriptorJson);
    }

    public void Dispose()
    {
        foreach (TempDirectory directory in _tempDirs.OrderByDescending(directory => directory.Path.Length))
            directory.Dispose();
    }

    private static MetadataStore CreateStore(string rootPath)
    {
        var store = new MetadataStore(rootPath);
        store.EnsureSchema();
        return store;
    }

    private static SqliteRepository CreateRepository(string dbPath)
    {
        return TestRepository.Create(dbPath);
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

    private static RepositoryLeaseHandle AcquireTestLease(
        RepositoryLeaseService service,
        string rootPath,
        string operation)
    {
        RepositoryLeaseAcquireResult result = service.TryAcquire(
            rootPath,
            new RepositoryLeaseRequest(
                Guid.NewGuid().ToString("N"),
                "Test writer",
                operation,
                "1.8.7"));
        Assert.Equal(RepositoryLeaseAcquireStatus.Acquired, result.Status);
        return Assert.IsType<RepositoryLeaseHandle>(result.Handle);
    }

    private static string GetExpectedDeferredRoot(string rootPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootPath));
        return Path.Combine(
            Path.GetTempPath(),
            "vaultsync-meta-export",
            HashService.FormatHexLower(hash));
    }

    private static void SeedUnrelatedImportedHistory(SqliteRepository repo, string rootPath)
    {
        int projectId = repo.AddProject(new Project
        {
            ExternalId = "proj-unrelated",
            Name = "Unrelated Project",
            RootPath = Path.Combine(rootPath, "Unrelated Project"),
            Preset = "generic",
            CreatedUtc = DateTime.UtcNow
        });
        int snapshotId = repo.CreateSnapshotFromMetadata(
            "snap-unrelated",
            projectId,
            DateTime.UtcNow,
            fileCount: 1,
            totalBytes: 64);
        repo.CreateBackupFromMetadata(
            "backup-unrelated",
            projectId,
            snapshotId,
            DateTime.UtcNow,
            "manual",
            64,
            Path.Combine("unrelated-project", "2026-05-22_10-00-00"),
            rootPath,
            "Unrelated",
            isProtected: false,
            isImported: true);
    }

    private string CreateTempDir()
    {
        var directory = new TempDirectory();
        _tempDirs.Add(directory);
        return directory.Path;
    }

    private static void CreateLegacyStoreWithoutEncryptionColumns(string rootPath, string projectRoot, string backupPathRel)
    {
        string metaDir = Path.Combine(rootPath, ".vaultsync", "meta");
        Directory.CreateDirectory(metaDir);
        string dbPath = Path.Combine(metaDir, "vaultsync.meta.db");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS meta_info(
                  schema_version INTEGER NOT NULL,
                  created_utc TEXT NOT NULL,
                  last_write_utc TEXT NOT NULL,
                  writer_app_version TEXT NOT NULL,
                  writer_machine_id TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS projects(
                  external_id TEXT PRIMARY KEY,
                  name TEXT NOT NULL,
                  preset TEXT NOT NULL,
                  root_path_hint TEXT NOT NULL,
                  created_utc TEXT NOT NULL,
                  settings_json TEXT NOT NULL,
                  updated_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS snapshots(
                  external_id TEXT PRIMARY KEY,
                  project_external_id TEXT NOT NULL,
                  created_utc TEXT NOT NULL,
                  file_count INTEGER NOT NULL,
                  total_bytes INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS backups(
                  external_id TEXT PRIMARY KEY,
                  project_external_id TEXT NOT NULL,
                  snapshot_external_id TEXT NOT NULL,
                  created_utc TEXT NOT NULL,
                  type TEXT NOT NULL,
                  total_bytes INTEGER NOT NULL,
                  path_rel TEXT NOT NULL,
                  destination_alias TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS tombstones(
                  entity_type TEXT NOT NULL,
                  entity_id TEXT NOT NULL,
                  deleted_utc TEXT NOT NULL,
                  origin_machine_id TEXT NOT NULL,
                  PRIMARY KEY(entity_type, entity_id)
                );
                """;
            _ = cmd.ExecuteNonQuery();
        }

        string now = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO meta_info(schema_version, created_utc, last_write_utc, writer_app_version, writer_machine_id)
                VALUES($schemaVersion, $createdUtc, $lastWriteUtc, $appVersion, $machineId);
                """;
            cmd.Parameters.AddWithValue("$schemaVersion", MetadataStore.CurrentSchemaVersion);
            cmd.Parameters.AddWithValue("$createdUtc", now);
            cmd.Parameters.AddWithValue("$lastWriteUtc", now);
            cmd.Parameters.AddWithValue("$appVersion", "1.4.0");
            cmd.Parameters.AddWithValue("$machineId", "legacy-machine");
            _ = cmd.ExecuteNonQuery();
        }

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO projects(external_id, name, preset, root_path_hint, created_utc, settings_json, updated_utc)
                VALUES($externalId, $name, $preset, $rootPathHint, $createdUtc, $settingsJson, $updatedUtc);
                """;
            cmd.Parameters.AddWithValue("$externalId", "legacy-project-1");
            cmd.Parameters.AddWithValue("$name", "Legacy Project");
            cmd.Parameters.AddWithValue("$preset", "unity");
            cmd.Parameters.AddWithValue("$rootPathHint", projectRoot);
            cmd.Parameters.AddWithValue("$createdUtc", now);
            cmd.Parameters.AddWithValue("$settingsJson", "{}");
            cmd.Parameters.AddWithValue("$updatedUtc", now);
            _ = cmd.ExecuteNonQuery();
        }

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO snapshots(external_id, project_external_id, created_utc, file_count, total_bytes)
                VALUES($externalId, $projectExternalId, $createdUtc, $fileCount, $totalBytes);
                """;
            cmd.Parameters.AddWithValue("$externalId", "legacy-snapshot-1");
            cmd.Parameters.AddWithValue("$projectExternalId", "legacy-project-1");
            cmd.Parameters.AddWithValue("$createdUtc", now);
            cmd.Parameters.AddWithValue("$fileCount", 5);
            cmd.Parameters.AddWithValue("$totalBytes", 2048);
            _ = cmd.ExecuteNonQuery();
        }

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO backups(external_id, project_external_id, snapshot_external_id, created_utc, type, total_bytes, path_rel, destination_alias)
                VALUES($externalId, $projectExternalId, $snapshotExternalId, $createdUtc, $type, $totalBytes, $pathRel, $destinationAlias);
                """;
            cmd.Parameters.AddWithValue("$externalId", "legacy-backup-1");
            cmd.Parameters.AddWithValue("$projectExternalId", "legacy-project-1");
            cmd.Parameters.AddWithValue("$snapshotExternalId", "legacy-snapshot-1");
            cmd.Parameters.AddWithValue("$createdUtc", now);
            cmd.Parameters.AddWithValue("$type", "manual");
            cmd.Parameters.AddWithValue("$totalBytes", 2048);
            cmd.Parameters.AddWithValue("$pathRel", backupPathRel);
            cmd.Parameters.AddWithValue("$destinationAlias", "LegacyPrimary");
            _ = cmd.ExecuteNonQuery();
        }
    }

}
