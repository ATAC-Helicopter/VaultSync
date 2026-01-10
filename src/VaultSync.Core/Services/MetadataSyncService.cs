using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public sealed class MetadataSyncService
{
    private readonly SqliteRepository _repo;

    public MetadataSyncService(SqliteRepository repo)
    {
        _repo = repo;
    }

    public MetadataSyncResult ImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        var opts = options ?? MetadataSyncOptions.Default;

        if (string.IsNullOrWhiteSpace(rootPath))
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, "Root path is empty.");

        var store = new MetadataStore(rootPath);
        if (!File.Exists(store.DatabasePath))
            return MetadataSyncResult.Failure(MetadataSyncStatus.NoStore, "Metadata store not found.");

        MetaInfo? metaInfo;
        try
        {
            metaInfo = store.GetMetaInfo();
        }
        catch (Exception ex)
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, ex.Message);
        }

        if (metaInfo != null && metaInfo.SchemaVersion > MetadataStore.CurrentSchemaVersion)
        {
            return MetadataSyncResult.Failure(
                MetadataSyncStatus.Incompatible,
                $"Metadata schema {metaInfo.SchemaVersion} is newer than supported {MetadataStore.CurrentSchemaVersion}.");
        }

        var importedProjects = 0;
        var importedSnapshots = 0;
        var importedBackups = 0;
        var appliedTombstones = 0;
        var projectMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var snapshotMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var config = AppConfigStore.Load();
        var localProjects = _repo.GetAllProjects().ToList();

        foreach (var p in localProjects)
        {
            if (!string.IsNullOrWhiteSpace(p.ExternalId))
                projectMap[p.ExternalId] = p.Id;
        }

        IEnumerable<MetaProject> metaProjects;
        IEnumerable<MetaSnapshot> metaSnapshots;
        IEnumerable<MetaBackup> metaBackups;
        IEnumerable<MetaTombstone> metaTombstones;

        try
        {
            metaProjects = store.ListProjects().ToList();
            metaSnapshots = store.ListSnapshots().ToList();
            metaBackups = store.ListBackups().ToList();
            metaTombstones = store.ListTombstones().ToList();
        }
        catch (Exception ex)
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, ex.Message);
        }

        foreach (var metaProject in metaProjects)
        {
            if (string.IsNullOrWhiteSpace(metaProject.ExternalId))
                continue;

            if (projectMap.ContainsKey(metaProject.ExternalId))
                continue;

            var existingByName = localProjects.FirstOrDefault(p =>
                string.Equals(p.Name, metaProject.Name, StringComparison.OrdinalIgnoreCase));

            if (existingByName != null)
            {
                if (string.IsNullOrWhiteSpace(existingByName.ExternalId))
                {
                    _repo.UpdateProjectExternalId(existingByName.Id, metaProject.ExternalId);
                }

                projectMap[metaProject.ExternalId] = existingByName.Id;
                continue;
            }

            if (!opts.AllowCreateProjects)
                continue;

            var projectRoot = !string.IsNullOrWhiteSpace(metaProject.RootPathHint)
                ? metaProject.RootPathHint
                : (config.ProjectsRoot ?? string.Empty);

            var project = new Project
            {
                ExternalId = metaProject.ExternalId,
                Name = metaProject.Name,
                RootPath = projectRoot,
                Preset = metaProject.Preset,
                CreatedUtc = metaProject.CreatedUtc,
                NeedsRestore = opts.MarkNeedsRestoreOnImport
            };

            var newId = _repo.AddProject(project);
            projectMap[metaProject.ExternalId] = newId;
            importedProjects++;
        }

        foreach (var metaSnapshot in metaSnapshots)
        {
            if (string.IsNullOrWhiteSpace(metaSnapshot.ExternalId))
                continue;

            if (!projectMap.TryGetValue(metaSnapshot.ProjectExternalId, out var projectId))
                continue;

            var existing = _repo.GetSnapshotByExternalId(metaSnapshot.ExternalId);
            if (existing != null)
            {
                snapshotMap[metaSnapshot.ExternalId] = existing.Id;
                continue;
            }

            var id = _repo.CreateSnapshotFromMetadata(
                metaSnapshot.ExternalId,
                projectId,
                metaSnapshot.CreatedUtc,
                metaSnapshot.FileCount,
                metaSnapshot.TotalBytes);

            snapshotMap[metaSnapshot.ExternalId] = id;
            importedSnapshots++;
        }

        foreach (var metaBackup in metaBackups)
        {
            if (string.IsNullOrWhiteSpace(metaBackup.ExternalId))
                continue;

            if (!projectMap.TryGetValue(metaBackup.ProjectExternalId, out var projectId))
                continue;

            if (!snapshotMap.TryGetValue(metaBackup.SnapshotExternalId, out var snapshotId))
            {
                var existingSnapshot = _repo.GetSnapshotByExternalId(metaBackup.SnapshotExternalId);
                if (existingSnapshot != null)
                {
                    snapshotId = existingSnapshot.Id;
                }
                else
                {
                    continue;
                }
            }

            var existing = _repo.GetBackupByExternalId(metaBackup.ExternalId);
            if (existing != null)
                continue;

            _repo.CreateBackupFromMetadata(
                metaBackup.ExternalId,
                projectId,
                snapshotId,
                metaBackup.CreatedUtc,
                metaBackup.Type,
                metaBackup.TotalBytes,
                metaBackup.PathRel,
                rootPath,
                metaBackup.DestinationAlias,
                metaBackup.IsProtected);
            importedBackups++;
        }

        foreach (var tombstone in metaTombstones)
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityId))
                continue;

            if (string.Equals(tombstone.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            {
                var existing = _repo.GetBackupByExternalId(tombstone.EntityId);
                if (existing != null)
                {
                    _repo.DeleteBackupById(existing.Id);
                    appliedTombstones++;
                }
            }
        }

        return new MetadataSyncResult(
            MetadataSyncStatus.Success,
            importedProjects,
            importedSnapshots,
            importedBackups,
            appliedTombstones,
            string.Empty);
    }

    public MetadataSyncResult ExportBackupToStore(string rootPath, int backupId, string appVersion, string machineId)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, "Root path is empty.");

        var store = new MetadataStore(rootPath);
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        var backup = _repo.GetBackupById(backupId);
        if (backup == null)
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Backup not found.");

        var project = _repo.GetProjectById(backup.ProjectId);
        if (project == null)
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Project not found.");

        var snapshot = _repo.GetSnapshotById(backup.SnapshotId);
        if (snapshot == null)
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Snapshot not found.");

        var projectExternalId = EnsureProjectExternalId(project);
        var snapshotExternalId = EnsureSnapshotExternalId(snapshot);
        var backupExternalId = EnsureBackupExternalId(backup);

        var now = DateTime.UtcNow;
        var metaInfo = store.GetMetaInfo();
        if (metaInfo == null)
        {
            metaInfo = new MetaInfo
            {
                SchemaVersion = MetadataStore.CurrentSchemaVersion,
                CreatedUtc = now,
                LastWriteUtc = now,
                WriterAppVersion = appVersion,
                WriterMachineId = machineId
            };
        }
        else
        {
            metaInfo.LastWriteUtc = now;
            metaInfo.WriterAppVersion = appVersion;
            metaInfo.WriterMachineId = machineId;
        }

        try
        {
            store.UpsertMetaInfo(metaInfo);
            store.UpsertProject(new MetaProject
            {
                ExternalId = projectExternalId,
                Name = project.Name,
                Preset = project.Preset,
                RootPathHint = project.RootPath,
                CreatedUtc = project.CreatedUtc,
                SettingsJson = "{}",
                UpdatedUtc = now
            });
            store.UpsertSnapshot(new MetaSnapshot
            {
                ExternalId = snapshotExternalId,
                ProjectExternalId = projectExternalId,
                CreatedUtc = snapshot.CreatedUtc,
                FileCount = snapshot.FileCount,
                TotalBytes = snapshot.TotalBytes
            });
            store.UpsertBackup(new MetaBackup
            {
                ExternalId = backupExternalId,
                ProjectExternalId = projectExternalId,
                SnapshotExternalId = snapshotExternalId,
                CreatedUtc = backup.CreatedUtc,
                Type = backup.Type,
                TotalBytes = backup.TotalBytes,
                PathRel = backup.Path,
                DestinationAlias = backup.DestinationAlias,
                IsProtected = backup.IsProtected,
                IsEncrypted = false,
                KdfParamsJson = "{}"
            });
        }
        catch (Exception ex)
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        return new MetadataSyncResult(
            MetadataSyncStatus.Success,
            0,
            0,
            1,
            0,
            string.Empty);
    }

    private string EnsureProjectExternalId(Project project)
    {
        if (!string.IsNullOrWhiteSpace(project.ExternalId))
            return project.ExternalId;

        var id = NewExternalId();
        _repo.UpdateProjectExternalId(project.Id, id);
        return id;
    }

    private string EnsureSnapshotExternalId(Snapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ExternalId))
            return snapshot.ExternalId;

        var id = NewExternalId();
        _repo.UpdateSnapshotExternalId(snapshot.Id, id);
        return id;
    }

    private string EnsureBackupExternalId(Backup backup)
    {
        if (!string.IsNullOrWhiteSpace(backup.ExternalId))
            return backup.ExternalId;

        var id = NewExternalId();
        _repo.UpdateBackupExternalId(backup.Id, id);
        return id;
    }

    private static string NewExternalId() => Guid.NewGuid().ToString("N");
}

public sealed record MetadataSyncOptions(bool AllowCreateProjects, bool MarkNeedsRestoreOnImport)
{
    public static MetadataSyncOptions Default => new(true, true);
}

public sealed record MetadataSyncResult(
    MetadataSyncStatus Status,
    int ImportedProjects,
    int ImportedSnapshots,
    int ImportedBackups,
    int AppliedTombstones,
    string Message)
{
    public static MetadataSyncResult Failure(MetadataSyncStatus status, string message) =>
        new(status, 0, 0, 0, 0, message);
}

public enum MetadataSyncStatus
{
    Success,
    NoStore,
    InvalidPath,
    InvalidStore,
    Incompatible,
    WriteFailed
}
