using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class MetadataSyncTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

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
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            AppConfig cfg = CloneConfig(originalConfig);
            cfg.ProjectsRoot = projectsRoot;
            AppConfigStore.Save(cfg);

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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
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
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            AppConfig cfg = CloneConfig(originalConfig);
            cfg.ProjectsRoot = projectsRoot;
            AppConfigStore.Save(cfg);

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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
    }

    [Fact]
    public void ImportFromStore_RepairsZeroByteLegacyImportedBackups()
    {
        string backupRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectsRoot = CreateTempDir();
        string backupFolder = Path.Combine(backupRoot, "legacy-project", "2026-05-08_10-00-00");
        Directory.CreateDirectory(backupFolder);
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            AppConfig cfg = CloneConfig(originalConfig);
            cfg.ProjectsRoot = projectsRoot;
            AppConfigStore.Save(cfg);

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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
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
        AppConfig cfg = AppConfigStore.Load();
        cfg.ProjectsRoot = localProjectsRoot;
        AppConfigStore.Save(cfg);

        try
        {
            var service = new MetadataSyncService(repo);
            MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

            Assert.Equal(MetadataSyncStatus.Success, result.Status);

            Project project = repo.GetProjectByName("Unsafe Root Project");
            Assert.NotNull(project);
            Assert.Equal(Path.Combine(localProjectsRoot, "Unsafe Root Project"), project!.RootPath);
        }
        finally
        {
            cfg.ProjectsRoot = string.Empty;
            AppConfigStore.Save(cfg);
        }
    }

    [Fact]
    public void ImportFromStore_FallsBackToProjectsRootWhenRootHintIsVaultSyncTemp()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var localProjectsRoot = CreateTempDir();
        var tempImportRoot = Path.Combine(
            Path.GetTempPath(),
            "vaultsync-meta-import",
            Guid.NewGuid().ToString("N"));
        var tempRootHint = Path.Combine(tempImportRoot, "Temp Root Project");
        Directory.CreateDirectory(tempRootHint);
        _tempDirs.Add(tempImportRoot);

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
        var cfg = AppConfigStore.Load();
        cfg.ProjectsRoot = localProjectsRoot;
        AppConfigStore.Save(cfg);

        try
        {
            var service = new MetadataSyncService(repo);
            var result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

            Assert.Equal(MetadataSyncStatus.Success, result.Status);

            var project = repo.GetProjectByName("Temp Root Project");
            Assert.NotNull(project);
            Assert.Equal(Path.Combine(localProjectsRoot, "Temp Root Project"), project!.RootPath);
        }
        finally
        {
            cfg.ProjectsRoot = string.Empty;
            AppConfigStore.Save(cfg);
        }
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
        AppConfig cfg = AppConfigStore.Load();
        string oldProjectsRoot = cfg.ProjectsRoot;
        cfg.ProjectsRoot = string.Empty;
        AppConfigStore.Save(cfg);

        try
        {
            var service = new MetadataSyncService(repo);
            MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

            Assert.Equal(MetadataSyncStatus.Success, result.Status);

            Project project = repo.GetProjectByName("Preserve Root Hint");
            Assert.NotNull(project);
            Assert.Equal(sourceRootHint, project!.RootPath);
        }
        finally
        {
            cfg.ProjectsRoot = oldProjectsRoot;
            AppConfigStore.Save(cfg);
        }
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
        repo.AddProject(new Project
        {
            Name = "Repair Existing Root",
            RootPath = string.Empty,
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

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
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            AppConfig cfg = CloneConfig(originalConfig);
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
            repo.AddProject(new Project
            {
                Name = "Display Name",
                RootPath = @"D:\Dev\real-folder",
                Preset = "dotnet",
                CreatedUtc = DateTime.UtcNow
            });

            var service = new MetadataSyncService(repo);
            MetadataSyncResult result = service.ImportFromStore(metaRoot, new MetadataSyncOptions(true, false));

            Assert.Equal(MetadataSyncStatus.Success, result.Status);

            Project project = repo.GetProjectByName("Display Name");
            Assert.NotNull(project);
            Assert.Equal(localProjectRoot, project!.RootPath);
        }
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
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
        int projectId = repo.AddProject(new Project
        {
            Name = "Project Tombstone",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

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
        int projectId = repo.AddProject(new Project
        {
            Name = "Project Export",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

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
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
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
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
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
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            SqliteRepository repo = CreateRepository(dbPath);
            int projectId = repo.AddProject(new Project
            {
                Name = "Project Export Auto Backup",
                RootPath = CreateTempDir(),
                Preset = "unity",
                CreatedUtc = DateTime.UtcNow
            });

            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
    }

    [Fact]
    public void ExportProjectToStore_ProjectSettings_TransportAutoBackupEnabledWithoutBackup()
    {
        string metaRoot = CreateTempDir();
        string sourceDbPath = Path.Combine(CreateTempDir(), "vaultsync-source.db");
        string targetDbPath = Path.Combine(CreateTempDir(), "vaultsync-target.db");
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            SqliteRepository sourceRepo = CreateRepository(sourceDbPath);
            int projectId = sourceRepo.AddProject(new Project
            {
                Name = "Project Settings Only",
                RootPath = CreateTempDir(),
                Preset = "unity",
                CreatedUtc = DateTime.UtcNow
            });

            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
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
        Func<Project, string> previousResolver = MetadataSyncService.ProjectColorResolver;
        Action<string, string> previousApplier = MetadataSyncService.ProjectColorApplier;
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

        try
        {
            MetadataSyncService.ProjectColorResolver = _ => expectedAvatarColor;
            MetadataSyncService.ProjectColorApplier = null;

            SqliteRepository repo = CreateRepository(dbPath);
            int projectId = repo.AddProject(new Project
            {
                Name = "Project Export Contract",
                RootPath = CreateTempDir(),
                Preset = "unity",
                CreatedUtc = DateTime.UtcNow
            });

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

            var service = new MetadataSyncService(repo);
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
        finally
        {
            MetadataSyncService.ProjectColorResolver = previousResolver;
            MetadataSyncService.ProjectColorApplier = previousApplier;
        }
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_AppliesTrackedFieldsWhenCreatingProject()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        DateTime now = DateTime.UtcNow;
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            var destination = new BackupDestination
            {
                Path = CreateTempDir(),
                Alias = "Imported Destination",
                Active = true
            };
            string destinationId = DestinationIdentityService.GetId(destination);
            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_NormalizesPreferredDestinationIdFromAliasWhenCreatingProject()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            var destination = new BackupDestination
            {
                Path = CreateTempDir(),
                Alias = "NAS Primary",
                Active = true
            };
            string destinationId = DestinationIdentityService.GetId(destination);
            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
    }

    [Fact]
    public void ImportFromStore_ProjectSettings_RecordsNormalizedPreferredDestinationInConflict()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        DateTime now = DateTime.UtcNow;
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            var destination = new BackupDestination
            {
                Path = CreateTempDir(),
                Alias = "NAS Imported",
                Active = true
            };
            string destinationId = DestinationIdentityService.GetId(destination);
            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
    }

    [Fact]
    public void ImportFromStore_MetadataContract_ImportsSelectedFieldsExplicitly()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        var capturedColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Func<Project, string> previousResolver = MetadataSyncService.ProjectColorResolver;
        Action<string, string> previousApplier = MetadataSyncService.ProjectColorApplier;
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

        try
        {
            MetadataSyncService.ProjectColorResolver = null;
            MetadataSyncService.ProjectColorApplier = (externalId, color) => capturedColors[externalId] = color;

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
            var service = new MetadataSyncService(repo);
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
        finally
        {
            MetadataSyncService.ProjectColorResolver = previousResolver;
            MetadataSyncService.ProjectColorApplier = previousApplier;
        }
    }

    [Fact]
    public void ImportFromStore_AppliesProjectTombstone()
    {
        string metaRoot = CreateTempDir();
        string dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        string projectRoot = CreateTempDir();
        AppConfig originalConfig = CloneConfig(AppConfigStore.Load());

        try
        {
            SqliteRepository repo = CreateRepository(dbPath);
            int projectId = repo.AddProject(new Project
            {
                ExternalId = "proj-remove-me",
                Name = "Project Remove Me",
                RootPath = projectRoot,
                Preset = "unity",
                CreatedUtc = DateTime.UtcNow
            });

            AppConfig cfg = CloneConfig(originalConfig);
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
        finally
        {
            AppConfigStore.Save(originalConfig);
        }
    }

    [Fact]
    public void ExportImportRoundTrip_PreservesMixedPlainAndEncryptedBackups()
    {
        string metaRoot = CreateTempDir();
        string sourceDbPath = Path.Combine(CreateTempDir(), "vaultsync-source.db");
        string targetDbPath = Path.Combine(CreateTempDir(), "vaultsync-target.db");

        SqliteRepository sourceRepo = CreateRepository(sourceDbPath);
        int projectId = sourceRepo.AddProject(new Project
        {
            Name = "Project Mixed",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

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
        MetadataSyncService.ProjectColorResolver = null;
        MetadataSyncService.ProjectColorApplier = null;

        foreach (string path in _tempDirs.OrderByDescending(p => p.Length))
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
        string path = Path.Combine(Path.GetTempPath(), $"vaultsync-test-{Guid.NewGuid():N}");
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

    private static AppConfig CloneConfig(AppConfig config)
    {
        string json = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }
}
