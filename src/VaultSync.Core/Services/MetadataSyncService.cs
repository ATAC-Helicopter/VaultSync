using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace VaultSync.Core.Services;

public sealed class MetadataSyncService
{
    private readonly SqliteRepository _repo;
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, MetadataSyncPreview Preview)> _previewCache =
        new(StringComparer.OrdinalIgnoreCase);

    public MetadataSyncService(SqliteRepository repo)
    {
        _repo = repo;
    }

    public MetadataSyncResult ImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        var opts = options ?? MetadataSyncOptions.Default;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.WriteLine("[MetadataSync] Import failed: root path is empty.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, "Root path is empty.");
        }

        var store = new MetadataStore(rootPath);
        if (!File.Exists(store.DatabasePath))
        {
            Console.WriteLine($"[MetadataSync] Import skipped: store not found at '{store.DatabasePath}'.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.NoStore, "Metadata store not found.");
        }

        if (ShouldUseTempCopy(store.DatabasePath) && TryCopyStoreForRead(store.DatabasePath, out var walTempRoot))
        {
            Console.WriteLine($"[MetadataSync] Import using temp copy (wal detected): '{walTempRoot}'.");
            try
            {
                return ImportFromStoreInternal(rootPath, new MetadataStore(walTempRoot), opts);
            }
            finally
            {
                TryDeleteTempStore(walTempRoot);
            }
        }

        try
        {
            return ImportFromStoreInternal(rootPath, store, opts);
        }
        catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
        {
            Console.WriteLine($"[MetadataSync] Import failed opening store at '{store.DatabasePath}': {ex.Message}");
            if (!TryCopyStoreForRead(store.DatabasePath, out var tempRoot))
                return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, ex.Message);

            Console.WriteLine($"[MetadataSync] Import retrying from temp copy: '{tempRoot}'.");
            try
            {
                return ImportFromStoreInternal(rootPath, new MetadataStore(tempRoot), opts);
            }
            finally
            {
                TryDeleteTempStore(tempRoot);
            }
        }
    }

    public MetadataSyncPreview PreviewImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        var opts = options ?? MetadataSyncOptions.Default;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.WriteLine("[MetadataSync] Preview failed: root path is empty.");
            return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidPath, rootPath, string.Empty, "Root path is empty.");
        }

        var store = new MetadataStore(rootPath);
        if (!File.Exists(store.DatabasePath))
        {
            Console.WriteLine($"[MetadataSync] Preview skipped: store not found at '{store.DatabasePath}'.");
            return MetadataSyncPreview.Failure(MetadataSyncStatus.NoStore, rootPath, store.DatabasePath, "Metadata store not found.");
        }

        if (ShouldUseTempCopy(store.DatabasePath) && TryCopyStoreForRead(store.DatabasePath, out var walTempRoot))
        {
            Console.WriteLine($"[MetadataSync] Preview using temp copy (wal detected): '{walTempRoot}'.");
            try
            {
                return PreviewImportFromStoreInternal(rootPath, new MetadataStore(walTempRoot), opts);
            }
            finally
            {
                TryDeleteTempStore(walTempRoot);
            }
        }

        try
        {
            return PreviewImportFromStoreInternal(rootPath, store, opts);
        }
        catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
        {
            Console.WriteLine($"[MetadataSync] Preview failed opening store at '{store.DatabasePath}': {ex.Message}");
            if (!TryCopyStoreForRead(store.DatabasePath, out var tempRoot))
                return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidStore, rootPath, store.DatabasePath, ex.Message);

            Console.WriteLine($"[MetadataSync] Preview retrying from temp copy: '{tempRoot}'.");
            try
            {
                return PreviewImportFromStoreInternal(rootPath, new MetadataStore(tempRoot), opts);
            }
            finally
            {
                TryDeleteTempStore(tempRoot);
            }
        }
    }

    private MetadataSyncResult ImportFromStoreInternal(string rootPath, MetadataStore store, MetadataSyncOptions opts)
    {
        MetaInfo? metaInfo;
        try
        {
            metaInfo = store.GetMetaInfo();
        }
        catch (Exception ex)
        {
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
            Console.WriteLine($"[MetadataSync] Import failed: invalid store at '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, ex.Message);
        }

        if (metaInfo != null && metaInfo.SchemaVersion > MetadataStore.CurrentSchemaVersion)
        {
            Console.WriteLine($"[MetadataSync] Import blocked: schema {metaInfo.SchemaVersion} > supported {MetadataStore.CurrentSchemaVersion}.");
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

        var projectExternalMap = _repo.GetProjectExternalIdMap();
        foreach (var pair in projectExternalMap)
        {
            projectMap[pair.Key] = pair.Value;
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
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
            Console.WriteLine($"[MetadataSync] Import failed while reading store '{rootPath}': {ex.Message}");
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
                NeedsRestore = false
            };

            var newId = _repo.AddProject(project);
            projectMap[metaProject.ExternalId] = newId;
            importedProjects++;
        }

        var snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        foreach (var pair in snapshotExternalMap)
        {
            snapshotMap[pair.Key] = pair.Value;
        }

        foreach (var metaSnapshot in metaSnapshots)
        {
            if (string.IsNullOrWhiteSpace(metaSnapshot.ExternalId))
                continue;

            if (!projectMap.TryGetValue(metaSnapshot.ProjectExternalId, out var projectId))
                continue;

            if (snapshotMap.ContainsKey(metaSnapshot.ExternalId))
                continue;

            var id = _repo.CreateSnapshotFromMetadata(
                metaSnapshot.ExternalId,
                projectId,
                metaSnapshot.CreatedUtc,
                metaSnapshot.FileCount,
                metaSnapshot.TotalBytes);

            snapshotMap[metaSnapshot.ExternalId] = id;
            importedSnapshots++;
        }

        var backupExternalMap = _repo.GetBackupExternalIdMap();
        var tombstonedBackupIds = metaTombstones
            .Where(t => string.Equals(t.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var metaBackup in metaBackups)
        {
            if (string.IsNullOrWhiteSpace(metaBackup.ExternalId))
                continue;

            if (tombstonedBackupIds.Contains(metaBackup.ExternalId))
                continue;

            if (!projectMap.TryGetValue(metaBackup.ProjectExternalId, out var projectId))
                continue;

            if (!snapshotMap.TryGetValue(metaBackup.SnapshotExternalId, out var snapshotId))
            {
                continue;
            }

            if (backupExternalMap.ContainsKey(metaBackup.ExternalId))
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
                metaBackup.IsProtected,
                isImported: true);
            importedBackups++;
        }

        foreach (var tombstone in metaTombstones)
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityId))
                continue;

            if (string.Equals(tombstone.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            {
                if (backupExternalMap.TryGetValue(tombstone.EntityId, out var existingId))
                {
                    _repo.DeleteBackupById(existingId);
                    appliedTombstones++;
                }
            }
        }

        var result = new MetadataSyncResult(
            MetadataSyncStatus.Success,
            importedProjects,
            importedSnapshots,
            importedBackups,
            appliedTombstones,
            string.Empty);
        if (opts.MarkNeedsRestoreOnImport)
        {
            var liveBackups = metaBackups
                .Where(b => !string.IsNullOrWhiteSpace(b.ExternalId) && !tombstonedBackupIds.Contains(b.ExternalId));
            UpdateNeedsRestoreFlags(projectMap, liveBackups);
        }
        Console.WriteLine($"[MetadataSync] Import complete from '{rootPath}': projects={importedProjects}, snapshots={importedSnapshots}, backups={importedBackups}, tombstones={appliedTombstones}.");
        return result;
    }

    private void UpdateNeedsRestoreFlags(IReadOnlyDictionary<string, int> projectMap, IEnumerable<MetaBackup> metaBackups)
    {
        if (projectMap.Count == 0)
            return;

        var localLatestByProject = _repo.GetLatestBackupUtcByProject();

        var importedLatestByExternalId = metaBackups
            .Where(b => !string.IsNullOrWhiteSpace(b.ProjectExternalId))
            .GroupBy(b => b.ProjectExternalId)
            .ToDictionary(g => g.Key, g => g.Max(b => b.CreatedUtc), StringComparer.OrdinalIgnoreCase);

        foreach (var (externalId, projectId) in projectMap)
        {
            if (!importedLatestByExternalId.TryGetValue(externalId, out var importedLatest))
                continue;

            localLatestByProject.TryGetValue(projectId, out var localLatest);
            var needsRestore = importedLatest > localLatest;
            _repo.UpdateProjectNeedsRestore(projectId, needsRestore);
        }
    }

    private MetadataSyncPreview PreviewImportFromStoreInternal(string rootPath, MetadataStore store, MetadataSyncOptions opts)
    {
        MetaInfo? metaInfo;
        try
        {
            metaInfo = store.GetMetaInfo();
        }
        catch (Exception ex)
        {
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
            Console.WriteLine($"[MetadataSync] Preview failed: invalid store at '{rootPath}': {ex.Message}");
            return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidStore, rootPath, store.DatabasePath, ex.Message);
        }

        if (metaInfo != null && metaInfo.SchemaVersion > MetadataStore.CurrentSchemaVersion)
        {
            Console.WriteLine($"[MetadataSync] Preview blocked: schema {metaInfo.SchemaVersion} > supported {MetadataStore.CurrentSchemaVersion}.");
            return MetadataSyncPreview.Failure(
                MetadataSyncStatus.Incompatible,
                rootPath,
                store.DatabasePath,
                $"Metadata schema {metaInfo.SchemaVersion} is newer than supported {MetadataStore.CurrentSchemaVersion}.");
        }

        if (metaInfo != null &&
            _previewCache.TryGetValue(rootPath, out var cached) &&
            cached.LastWriteUtc == metaInfo.LastWriteUtc)
        {
            return cached.Preview;
        }

        var addProjects = 0;
        var linkProjects = 0;
        var addSnapshots = 0;
        var addBackups = 0;
        var deleteBackups = 0;

        var projectMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var localProjects = _repo.GetAllProjects().ToList();
        var projectExternalMap = _repo.GetProjectExternalIdMap();
        foreach (var pair in projectExternalMap)
        {
            projectMap[pair.Key] = pair.Value;
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
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
            Console.WriteLine($"[MetadataSync] Preview failed while reading store '{rootPath}': {ex.Message}");
            return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidStore, rootPath, store.DatabasePath, ex.Message);
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
                    linkProjects++;
                }

                projectMap[metaProject.ExternalId] = existingByName.Id;
                continue;
            }

            if (!opts.AllowCreateProjects)
                continue;

            addProjects++;
            projectMap[metaProject.ExternalId] = -1;
        }

        var snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        foreach (var metaSnapshot in metaSnapshots)
        {
            if (string.IsNullOrWhiteSpace(metaSnapshot.ExternalId))
                continue;

            if (!projectMap.ContainsKey(metaSnapshot.ProjectExternalId))
                continue;

            if (snapshotExternalMap.ContainsKey(metaSnapshot.ExternalId))
                continue;

            addSnapshots++;
        }

        var backupExternalMap = _repo.GetBackupExternalIdMap();
        var tombstonedBackupIds = metaTombstones
            .Where(t => string.Equals(t.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var metaBackup in metaBackups)
        {
            if (string.IsNullOrWhiteSpace(metaBackup.ExternalId))
                continue;

            if (tombstonedBackupIds.Contains(metaBackup.ExternalId))
                continue;

            if (!projectMap.ContainsKey(metaBackup.ProjectExternalId))
                continue;

            if (backupExternalMap.ContainsKey(metaBackup.ExternalId))
                continue;

            addBackups++;
        }

        foreach (var tombstone in metaTombstones)
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityId))
                continue;

            if (string.Equals(tombstone.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            {
                if (backupExternalMap.ContainsKey(tombstone.EntityId))
                {
                    deleteBackups++;
                }
            }
        }

        var preview = new MetadataSyncPreview(
            MetadataSyncStatus.Success,
            rootPath,
            store.DatabasePath,
            addProjects,
            linkProjects,
            addSnapshots,
            addBackups,
            deleteBackups,
            string.Empty);

        if (metaInfo != null)
        {
            _previewCache[rootPath] = (metaInfo.LastWriteUtc, preview);
        }

        return preview;
    }

    private static bool IsCannotOpenOrLocked(SqliteException ex)
    {
        return ex.SqliteErrorCode == 14 || ex.SqliteErrorCode == 5;
    }

    private static bool TryCopyStoreForRead(string databasePath, out string tempRoot)
    {
        tempRoot = string.Empty;
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "vaultsync-meta-import", Guid.NewGuid().ToString("N"));
            var tempDir = Path.Combine(root, ".vaultsync", "meta");
            Directory.CreateDirectory(tempDir);
            var destPath = Path.Combine(tempDir, Path.GetFileName(databasePath));
            File.Copy(databasePath, destPath, overwrite: true);
            TryCopySidecar(databasePath, destPath, "-wal");
            TryCopySidecar(databasePath, destPath, "-shm");
            tempRoot = root;
            return true;
        }
        catch
        {
            tempRoot = string.Empty;
            return false;
        }
    }

    private static void TryDeleteTempStore(string tempRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static void TryCopySidecar(string sourceDbPath, string destDbPath, string suffix)
    {
        try
        {
            var source = sourceDbPath + suffix;
            if (!File.Exists(source))
                return;

            var dest = destDbPath + suffix;
            File.Copy(source, dest, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Temp copy missing sidecar '{suffix}': {ex.Message}");
        }
    }

    private static bool ShouldUseTempCopy(string databasePath)
    {
        return File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm");
    }

    public MetadataSyncResult ExportBackupToStore(string rootPath, int backupId, string appVersion, string machineId, bool forceBackfill = false)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.WriteLine("[MetadataSync] Export failed: root path is empty.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, "Root path is empty.");
        }

        var store = new MetadataStore(rootPath);
        Console.WriteLine($"[MetadataSync] Export target store: '{store.DatabasePath}'.");
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Export failed: store init error at '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        var backup = _repo.GetBackupById(backupId);
        if (backup == null)
        {
            Console.WriteLine($"[MetadataSync] Export failed: backup {backupId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Backup not found.");
        }

        var project = _repo.GetProjectById(backup.ProjectId);
        if (project == null)
        {
            Console.WriteLine($"[MetadataSync] Export failed: project {backup.ProjectId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Project not found.");
        }

        var snapshot = _repo.GetSnapshotById(backup.SnapshotId);
        if (snapshot == null)
        {
            Console.WriteLine($"[MetadataSync] Export failed: snapshot {backup.SnapshotId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Snapshot not found.");
        }

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

        var exportedProjects = 0;
        var exportedSnapshots = 0;
        var exportedBackups = 0;
        var backfilled = false;

        try
        {
            store.UpsertMetaInfo(metaInfo);
        if (forceBackfill || !store.HasProject(projectExternalId))
        {
            backfilled = true;
            var counts = ExportProjectHistory(store, project, projectExternalId, now);
            exportedProjects = 1;
            exportedSnapshots = counts.snapshots;
            exportedBackups = counts.backups;
            }
            else
            {
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
                    DestinationAlias = backup.DestinationAlias ?? string.Empty,
                    IsProtected = backup.IsProtected,
                    IsEncrypted = false,
                    KdfParamsJson = "{}"
                });
                exportedBackups = 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Export failed writing store '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        var exportResult = new MetadataSyncResult(
            MetadataSyncStatus.Success,
            exportedProjects,
            exportedSnapshots,
            exportedBackups,
            0,
            string.Empty);
        Console.WriteLine(backfilled
            ? $"[MetadataSync] Export complete (backfill) for project '{project.Name}' to '{rootPath}': snapshots={exportedSnapshots}, backups={exportedBackups}."
            : $"[MetadataSync] Export complete for backup {backupId} to '{rootPath}'.");
        LogStoreCounts(store);
        return exportResult;
    }

    public void ExportBackupTombstoneToStore(string rootPath, string backupExternalId, string appVersion, string machineId)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(backupExternalId))
            return;

        var store = new MetadataStore(rootPath);
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Tombstone export failed: store init error at '{rootPath}': {ex.Message}");
            return;
        }

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
            store.AddTombstone(new MetaTombstone
            {
                EntityType = "backup",
                EntityId = backupExternalId,
                DeletedUtc = now,
                OriginMachineId = machineId
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Tombstone export failed writing store '{rootPath}': {ex.Message}");
        }
    }

    private (int snapshots, int backups) ExportProjectHistory(
        MetadataStore store,
        Project project,
        string projectExternalId,
        DateTime now)
    {
        var snapshots = _repo.GetSnapshotsForProject(project.Name).ToList();
        var backups = _repo.GetBackupsForProject(project.Id).ToList();
        Console.WriteLine($"[MetadataSync] Export history for '{project.Name}': snapshots={snapshots.Count}, backups={backups.Count}.");
        var snapshotExternalIds = new Dictionary<int, string>();

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

        foreach (var snap in snapshots)
        {
            var snapExternal = EnsureSnapshotExternalId(snap);
            snapshotExternalIds[snap.Id] = snapExternal;
            store.UpsertSnapshot(new MetaSnapshot
            {
                ExternalId = snapExternal,
                ProjectExternalId = projectExternalId,
                CreatedUtc = snap.CreatedUtc,
                FileCount = snap.FileCount,
                TotalBytes = snap.TotalBytes
            });
        }

        var exportedBackups = 0;
        var skippedBackups = 0;
        foreach (var backup in backups)
        {
            if (!snapshotExternalIds.TryGetValue(backup.SnapshotId, out var snapshotExternalId))
            {
                var snap = _repo.GetSnapshotById(backup.SnapshotId);
                if (snap is null)
                {
                    skippedBackups++;
                    continue;
                }

                snapshotExternalId = EnsureSnapshotExternalId(snap);
                snapshotExternalIds[snap.Id] = snapshotExternalId;
                store.UpsertSnapshot(new MetaSnapshot
                {
                    ExternalId = snapshotExternalId,
                    ProjectExternalId = projectExternalId,
                    CreatedUtc = snap.CreatedUtc,
                    FileCount = snap.FileCount,
                    TotalBytes = snap.TotalBytes
                });
            }

            var backupExternalId = EnsureBackupExternalId(backup);
            store.UpsertBackup(new MetaBackup
            {
                ExternalId = backupExternalId,
                ProjectExternalId = projectExternalId,
                SnapshotExternalId = snapshotExternalId,
                CreatedUtc = backup.CreatedUtc,
                Type = backup.Type,
                TotalBytes = backup.TotalBytes,
                PathRel = backup.Path,
                DestinationAlias = backup.DestinationAlias ?? string.Empty,
                IsProtected = backup.IsProtected,
                IsEncrypted = false,
                KdfParamsJson = "{}"
            });
            exportedBackups++;
        }

        if (skippedBackups > 0)
        {
            Console.WriteLine($"[MetadataSync] Export history skipped {skippedBackups} backups without snapshots for '{project.Name}'.");
        }

        return (snapshots.Count, exportedBackups);
    }

    private static void LogStoreCounts(MetadataStore store)
    {
        try
        {
            var projects = store.ListProjects().Count();
            var snapshots = store.ListSnapshots().Count();
            var backups = store.ListBackups().Count();
            Console.WriteLine($"[MetadataSync] Store counts at '{store.DatabasePath}': projects={projects}, snapshots={snapshots}, backups={backups}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Store count read failed at '{store.DatabasePath}': {ex.Message}");
        }
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

public sealed record MetadataSyncPreview(
    MetadataSyncStatus Status,
    string RootPath,
    string DatabasePath,
    int NewProjects,
    int LinkedProjects,
    int NewSnapshots,
    int NewBackups,
    int DeletedBackups,
    string Message)
{
    public bool HasChanges =>
        NewProjects > 0 ||
        LinkedProjects > 0 ||
        NewSnapshots > 0 ||
        NewBackups > 0 ||
        DeletedBackups > 0;

    public static MetadataSyncPreview Failure(MetadataSyncStatus status, string rootPath, string databasePath, string message) =>
        new(status, rootPath, databasePath, 0, 0, 0, 0, 0, message);
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
