using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public void ImportFromStore_ProjectSettings_AppliesEncryptionPolicyAndKeyRef()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var projectRoot = CreateTempDir();

        var store = CreateStore(metaRoot);
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

        var repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        var project = repo.GetProjectByName("Project Settings");
        Assert.NotNull(project);
        Assert.Equal(ProjectEncryptionPolicy.Encrypted, project!.EncryptionPolicy);
        Assert.Equal("project-key-ref-01", project.EncryptionKeyRef);
    }

    [Fact]
    public void ExportBackupToStore_ProjectSettings_IncludeEncryptionFields()
    {
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");

        var repo = CreateRepository(dbPath);
        var projectId = repo.AddProject(new Project
        {
            Name = "Project Export Settings",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow,
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted,
            EncryptionKeyRef = "project-key-ref-export"
        });

        var snapshotId = repo.CreateSnapshot(projectId, 2, 500);
        var backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            500,
            "project-export-settings/2025-01-01_00-00-00",
            metaRoot,
            "Primary");

        var service = new MetadataSyncService(repo);
        var result = service.ExportBackupToStore(metaRoot, backupId, "1.5.0", "machine-settings");
        Assert.Equal(MetadataSyncStatus.Success, result.Status);

        var store = new MetadataStore(metaRoot);
        var metaProject = store.ListProjects().Single();
        using var doc = JsonDocument.Parse(metaProject.SettingsJson);
        Assert.True(doc.RootElement.TryGetProperty("encryptionPolicy", out var policy));
        Assert.Equal(ProjectEncryptionPolicy.Encrypted, policy.GetString());
        Assert.True(doc.RootElement.TryGetProperty("encryptionKeyRef", out var keyRef));
        Assert.Equal("project-key-ref-export", keyRef.GetString());
    }

    [Fact]
    public void ExportImportRoundTrip_PreservesMixedPlainAndEncryptedBackups()
    {
        var metaRoot = CreateTempDir();
        var sourceDbPath = Path.Combine(CreateTempDir(), "vaultsync-source.db");
        var targetDbPath = Path.Combine(CreateTempDir(), "vaultsync-target.db");

        var sourceRepo = CreateRepository(sourceDbPath);
        var projectId = sourceRepo.AddProject(new Project
        {
            Name = "Project Mixed",
            RootPath = CreateTempDir(),
            Preset = "unity",
            CreatedUtc = DateTime.UtcNow
        });

        var plainSnapshotId = sourceRepo.CreateSnapshot(projectId, 10, 10_000);
        var plainPath = "project-mixed/2026-01-01_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, plainPath));
        var plainBackupId = sourceRepo.CreateBackup(
            projectId,
            plainSnapshotId,
            "manual",
            10_000,
            plainPath,
            metaRoot,
            "Primary");

        var encryptedSnapshotId = sourceRepo.CreateSnapshot(projectId, 11, 11_000);
        var encryptedPath = "project-mixed/2026-01-02_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, encryptedPath));
        var encryptedDescriptor = BackupCryptoDescriptor
            .Encrypted("aes-256-cbc-hmac-sha256-v1", "pbkdf2-sha256-v1", "pbkdf2-iter-210000")
            .ToMetadataJson(true);
        var encryptedBackupId = sourceRepo.CreateBackup(
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
        var exportResult = sourceService.ExportBackupToStore(
            metaRoot,
            encryptedBackupId,
            "1.5.0",
            "machine-source",
            forceBackfill: true);

        Assert.Equal(MetadataSyncStatus.Success, exportResult.Status);

        var targetRepo = CreateRepository(targetDbPath);
        var importService = new MetadataSyncService(targetRepo);
        var importResult = importService.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, importResult.Status);
        Assert.Equal(2, importResult.ImportedBackups);

        var sourcePlainBackup = sourceRepo.GetBackupById(plainBackupId);
        var sourceEncryptedBackup = sourceRepo.GetBackupById(encryptedBackupId);
        Assert.NotNull(sourcePlainBackup);
        Assert.NotNull(sourceEncryptedBackup);

        var importedPlain = targetRepo.GetBackupByExternalId(sourcePlainBackup!.ExternalId);
        var importedEncrypted = targetRepo.GetBackupByExternalId(sourceEncryptedBackup!.ExternalId);
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
        var metaRoot = CreateTempDir();
        var dbPath = Path.Combine(CreateTempDir(), "vaultsync.db");
        var projectRoot = CreateTempDir();
        var backupPathRel = "legacy-project/2026-01-01_00-00-00";
        Directory.CreateDirectory(Path.Combine(metaRoot, backupPathRel));

        CreateLegacyStoreWithoutEncryptionColumns(metaRoot, projectRoot, backupPathRel);

        var repo = CreateRepository(dbPath);
        var service = new MetadataSyncService(repo);
        var result = service.ImportFromStore(metaRoot, MetadataSyncOptions.Default);

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.ImportedProjects);
        Assert.Equal(1, result.ImportedSnapshots);
        Assert.Equal(1, result.ImportedBackups);

        var backup = repo.GetBackupByExternalId("legacy-backup-1");
        Assert.NotNull(backup);
        Assert.False(backup!.IsEncrypted);
        Assert.Equal(BackupCryptoDescriptor.PlainMetadataJson, backup.CryptoDescriptorJson);
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

    private static void CreateLegacyStoreWithoutEncryptionColumns(string rootPath, string projectRoot, string backupPathRel)
    {
        var metaDir = Path.Combine(rootPath, ".vaultsync", "meta");
        Directory.CreateDirectory(metaDir);
        var dbPath = Path.Combine(metaDir, "vaultsync.meta.db");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();

        using (var cmd = connection.CreateCommand())
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

        var now = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);

        using (var cmd = connection.CreateCommand())
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

        using (var cmd = connection.CreateCommand())
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

        using (var cmd = connection.CreateCommand())
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

        using (var cmd = connection.CreateCommand())
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
