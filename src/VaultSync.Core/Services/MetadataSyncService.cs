using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
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
    private static readonly SemaphoreSlim MetadataIoGate = new(1, 1);

    public static Func<Project, string?>? ProjectColorResolver { get; set; }
    public static Action<string, string>? ProjectColorApplier { get; set; }

    public MetadataSyncService(SqliteRepository repo)
    {
        _repo = repo;
    }

    public MetadataSyncResult ImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        MetadataIoGate.Wait();
        try
        {
            WaitForNetworkReady(rootPath);
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
        finally
        {
            MetadataIoGate.Release();
        }
    }

    public MetadataSyncPreview PreviewImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        MetadataIoGate.Wait();
        try
        {
            WaitForNetworkReady(rootPath);
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
        finally
        {
            MetadataIoGate.Release();
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
        var affectedProjectIds = new HashSet<int>();
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

            var parsedSettings = ParseProjectSettings(metaProject.SettingsJson);
            TryApplyProjectColor(metaProject);

            if (projectMap.TryGetValue(metaProject.ExternalId, out var mappedProjectId))
            {
                _repo.UpdateProjectEncryptionSettings(
                    mappedProjectId,
                    parsedSettings.EncryptionPolicy,
                    parsedSettings.EncryptionKeyRef);
                continue;
            }

            var existingByName = localProjects.FirstOrDefault(p =>
                string.Equals(p.Name, metaProject.Name, StringComparison.OrdinalIgnoreCase));

            if (existingByName != null)
            {
                if (string.IsNullOrWhiteSpace(existingByName.ExternalId))
                {
                    _repo.UpdateProjectExternalId(existingByName.Id, metaProject.ExternalId);
                }

                _repo.UpdateProjectEncryptionSettings(
                    existingByName.Id,
                    parsedSettings.EncryptionPolicy,
                    parsedSettings.EncryptionKeyRef);
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
                NeedsRestore = false,
                EncryptionPolicy = parsedSettings.EncryptionPolicy,
                EncryptionKeyRef = parsedSettings.EncryptionKeyRef
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

        var tombstonedBackupIds = metaTombstones
            .Where(t => string.Equals(t.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tombstonedSnapshotIds = metaTombstones
            .Where(t => string.Equals(t.EntityType, "snapshot", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveSnapshotExternalIds = new HashSet<string>(
            metaBackups
                .Where(b => !string.IsNullOrWhiteSpace(b.SnapshotExternalId))
                .Where(b => !tombstonedBackupIds.Contains(b.ExternalId))
                .Select(b => b.SnapshotExternalId),
            StringComparer.OrdinalIgnoreCase);
        var missingSnapshotExternalIds = metaSnapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.ExternalId))
            .Where(s => !liveSnapshotExternalIds.Contains(s.ExternalId))
            .Select(s => s.ExternalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var missingSnapshotId in missingSnapshotExternalIds)
        {
            tombstonedSnapshotIds.Add(missingSnapshotId);
        }

        foreach (var metaSnapshot in metaSnapshots)
        {
            if (string.IsNullOrWhiteSpace(metaSnapshot.ExternalId))
                continue;

            if (tombstonedSnapshotIds.Contains(metaSnapshot.ExternalId))
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
        var missingBackupExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metaBackup in metaBackups)
        {
            if (string.IsNullOrWhiteSpace(metaBackup.ExternalId))
                continue;

            if (tombstonedBackupIds.Contains(metaBackup.ExternalId))
                continue;

            var normalizedPathRel = NormalizeBackupPathRel(metaBackup.PathRel);
            if (!BackupPathExists(rootPath, normalizedPathRel))
            {
                missingBackupExternalIds.Add(metaBackup.ExternalId);
                tombstonedBackupIds.Add(metaBackup.ExternalId);
                continue;
            }

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
                normalizedPathRel,
                rootPath,
                metaBackup.DestinationAlias,
                metaBackup.IsProtected,
                isImported: true,
                originMachineName: metaBackup.OriginMachineName,
                isEncrypted: metaBackup.IsEncrypted,
                cryptoDescriptorJson: metaBackup.KdfParamsJson);
            importedBackups++;
            affectedProjectIds.Add(projectId);
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

        if (missingBackupExternalIds.Count > 0)
        {
            foreach (var missingExternalId in missingBackupExternalIds)
            {
                if (backupExternalMap.TryGetValue(missingExternalId, out var existingId))
                {
                    _repo.DeleteBackupById(existingId);
                    appliedTombstones++;
                }
            }

            TryExportMissingBackupTombstones(rootPath, missingBackupExternalIds);
        }

        if (missingSnapshotExternalIds.Count > 0)
        {
            var removedSnapshots = 0;
            foreach (var missingExternalId in missingSnapshotExternalIds)
            {
                var snapshot = _repo.GetSnapshotByExternalId(missingExternalId);
                if (snapshot == null)
                    continue;

                if (_repo.HasBackupForSnapshot(snapshot.ProjectId, snapshot.Id))
                    continue;

                var project = _repo.GetProjectById(snapshot.ProjectId);
                if (project == null)
                    continue;

                var deleted = _repo.DeleteSnapshotsById(project.Name, new[] { snapshot.Id });
                removedSnapshots += deleted.Snapshots;
            }

            if (removedSnapshots > 0)
            {
                TryExportMissingSnapshotTombstones(rootPath, missingSnapshotExternalIds);
            }
        }

        var result = new MetadataSyncResult(
            MetadataSyncStatus.Success,
            importedProjects,
            importedSnapshots,
            importedBackups,
            appliedTombstones,
            string.Empty)
        {
            AffectedProjectIds = affectedProjectIds.ToArray()
        };
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
            if (needsRestore)
            {
                var project = _repo.GetProjectById(projectId);
                if (!string.IsNullOrWhiteSpace(project?.RootPath) &&
                    Directory.Exists(project.RootPath) &&
                    HasLocalChangesNewerThan(project.RootPath, importedLatest))
                {
                    needsRestore = false;
                }
            }
            _repo.UpdateProjectNeedsRestore(projectId, needsRestore);
        }
    }

    private static bool HasLocalChangesNewerThan(string rootPath, DateTime importedLatestUtc)
    {
        try
        {
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                IEnumerable<string> dirs;
                try
                {
                    dirs = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, ".vaultsync", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var di = new DirectoryInfo(dir);
                        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    stack.Push(dir);
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(current);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) > importedLatestUtc)
                            return true;
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
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
            metaProjects = store.ListProjects();
            metaSnapshots = store.ListSnapshots();
            metaBackups = store.ListBackups();
            metaTombstones = store.ListTombstones();
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
        var backupExternalMap = _repo.GetBackupExternalIdMap();
        var tombstonedBackupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tombstonedSnapshotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveSnapshotExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tombstone in metaTombstones)
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityId))
                continue;

            if (string.Equals(tombstone.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            {
                tombstonedBackupIds.Add(tombstone.EntityId);
                if (backupExternalMap.ContainsKey(tombstone.EntityId))
                    deleteBackups++;
            }
            else if (string.Equals(tombstone.EntityType, "snapshot", StringComparison.OrdinalIgnoreCase))
            {
                tombstonedSnapshotIds.Add(tombstone.EntityId);
            }
        }

        foreach (var metaBackup in metaBackups)
        {
            if (string.IsNullOrWhiteSpace(metaBackup.ExternalId))
                continue;

            if (!string.IsNullOrWhiteSpace(metaBackup.SnapshotExternalId) &&
                !tombstonedBackupIds.Contains(metaBackup.ExternalId))
            {
                liveSnapshotExternalIds.Add(metaBackup.SnapshotExternalId);
            }

            if (tombstonedBackupIds.Contains(metaBackup.ExternalId))
                continue;

            var normalizedPathRel = NormalizeBackupPathRel(metaBackup.PathRel);
            if (!BackupPathExists(rootPath, normalizedPathRel))
            {
                tombstonedBackupIds.Add(metaBackup.ExternalId);
                if (backupExternalMap.ContainsKey(metaBackup.ExternalId))
                    deleteBackups++;
                continue;
            }

            if (!projectMap.ContainsKey(metaBackup.ProjectExternalId))
                continue;

            if (backupExternalMap.ContainsKey(metaBackup.ExternalId))
                continue;

            addBackups++;
        }

        foreach (var metaSnapshot in metaSnapshots)
        {
            if (string.IsNullOrWhiteSpace(metaSnapshot.ExternalId))
                continue;

            if (!liveSnapshotExternalIds.Contains(metaSnapshot.ExternalId))
            {
                tombstonedSnapshotIds.Add(metaSnapshot.ExternalId);
                continue;
            }

            if (tombstonedSnapshotIds.Contains(metaSnapshot.ExternalId))
                continue;

            if (!projectMap.ContainsKey(metaSnapshot.ProjectExternalId))
                continue;

            if (snapshotExternalMap.ContainsKey(metaSnapshot.ExternalId))
                continue;

            addSnapshots++;
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

    private static bool BackupPathExists(string rootPath, string pathRel)
    {
        if (string.IsNullOrWhiteSpace(pathRel))
            return false;

        var fullPath = IsRootedPath(pathRel)
            ? pathRel
            : Path.Combine(rootPath, pathRel);

        return Directory.Exists(fullPath) || File.Exists(fullPath);
    }

    private static string NormalizeBackupPathRel(string pathRel)
    {
        if (string.IsNullOrWhiteSpace(pathRel))
            return string.Empty;

        var normalized = pathRel
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        return IsRootedPath(normalized)
            ? normalized
            : normalized.TrimStart(Path.DirectorySeparatorChar);
    }

    private static bool IsRootedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (Path.IsPathRooted(path))
            return true;

        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            return true;

        return path.StartsWith("\\\\", StringComparison.Ordinal) ||
               path.StartsWith("//", StringComparison.Ordinal);
    }

    private static void TryExportMissingBackupTombstones(string rootPath, IReadOnlyCollection<string> missingExternalIds)
    {
        if (missingExternalIds.Count == 0)
            return;

        var store = new MetadataStore(rootPath);
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Missing backup tombstone export failed: store init error at '{rootPath}': {ex.Message}");
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
                WriterAppVersion = "unknown",
                WriterMachineId = Environment.MachineName
            };
        }
        else
        {
            metaInfo.LastWriteUtc = now;
            metaInfo.WriterMachineId = Environment.MachineName;
        }

        try
        {
            store.UpsertMetaInfo(metaInfo);
            foreach (var externalId in missingExternalIds)
            {
                if (string.IsNullOrWhiteSpace(externalId))
                    continue;

                store.AddTombstone(new MetaTombstone
                {
                    EntityType = "backup",
                    EntityId = externalId,
                    DeletedUtc = now,
                    OriginMachineId = Environment.MachineName
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Missing backup tombstone export failed writing store '{rootPath}': {ex.Message}");
        }
    }

    private static void TryExportMissingSnapshotTombstones(string rootPath, IReadOnlyCollection<string> missingExternalIds)
    {
        if (missingExternalIds.Count == 0)
            return;

        var store = new MetadataStore(rootPath);
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Missing snapshot tombstone export failed: store init error at '{rootPath}': {ex.Message}");
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
                WriterAppVersion = "unknown",
                WriterMachineId = Environment.MachineName
            };
        }
        else
        {
            metaInfo.LastWriteUtc = now;
            metaInfo.WriterMachineId = Environment.MachineName;
        }

        try
        {
            store.UpsertMetaInfo(metaInfo);
            foreach (var externalId in missingExternalIds)
            {
                if (string.IsNullOrWhiteSpace(externalId))
                    continue;

                store.AddTombstone(new MetaTombstone
                {
                    EntityType = "snapshot",
                    EntityId = externalId,
                    DeletedUtc = now,
                    OriginMachineId = Environment.MachineName
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Missing snapshot tombstone export failed writing store '{rootPath}': {ex.Message}");
        }
    }

    public MetadataSyncResult ExportBackupToStore(string rootPath, int backupId, string appVersion, string machineId, bool forceBackfill = false)
    {
        MetadataIoGate.Wait();
        try
        {
            WaitForNetworkReady(rootPath);
            var retryDelays = new[]
            {
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(1000)
            };

            for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                try
                {
                    return ExportBackupToStoreInternal(rootPath, backupId, appVersion, machineId, forceBackfill);
                }
                catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
                {
                    if (attempt >= retryDelays.Length)
                    {
                        Console.WriteLine($"[MetadataSync] Export failed after retries: {ex.Message}");
                        return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
                    }

                    var delay = retryDelays[attempt];
                    Console.WriteLine($"[MetadataSync] Export store locked; retrying in {delay.TotalMilliseconds:0}ms.");
                    Thread.Sleep(delay);
                }
            }

            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, "Export failed after retries.");
        }
        finally
        {
            MetadataIoGate.Release();
        }
    }

    private MetadataSyncResult ExportBackupToStoreInternal(string rootPath, int backupId, string appVersion, string machineId, bool forceBackfill)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.WriteLine("[MetadataSync] Export failed: root path is empty.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, "Root path is empty.");
        }

        TryFlushDeferredExport(rootPath);

        var storeRoot = rootPath;
        var isDeferred = false;
        var destMetaDir = GetMetaDir(rootPath);
        if (!TryEnsureMetadataDirWritable(destMetaDir))
        {
            storeRoot = GetDeferredExportRoot(rootPath);
            isDeferred = true;
        }

        var store = new MetadataStore(storeRoot);
        Console.WriteLine($"[MetadataSync] Export target store: '{store.DatabasePath}'.");
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
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
            var counts = ExportProjectHistory(store, project, projectExternalId, now, machineId);
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
                    SettingsJson = BuildProjectSettingsJson(project),
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
                var descriptor = BackupCryptoDescriptor.FromMetadata(backup.IsEncrypted, backup.CryptoDescriptorJson);
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
                    OriginMachineName = machineId,
                    IsProtected = backup.IsProtected,
                    IsEncrypted = backup.IsEncrypted,
                    KdfParamsJson = descriptor.ToMetadataJson(backup.IsEncrypted)
                });
                exportedBackups = 1;
            }
        }
        catch (Exception ex)
        {
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
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
            ? $"[MetadataSync] Export complete (backfill) for project '{project.Name}' to '{storeRoot}': snapshots={exportedSnapshots}, backups={exportedBackups}."
            : $"[MetadataSync] Export complete for backup {backupId} to '{storeRoot}'.");
        LogStoreCounts(store);
        if (isDeferred)
        {
            if (TryFlushDeferredExport(rootPath))
                return exportResult;

            return MetadataSyncResult.Failure(
                MetadataSyncStatus.WriteFailed,
                "Export queued: destination not writable. Will retry when available.");
        }

        return exportResult;
    }

    public void ExportBackupTombstoneToStore(string rootPath, string backupExternalId, string appVersion, string machineId)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(backupExternalId))
            return;

        MetadataIoGate.Wait();
        try
        {
            WaitForNetworkReady(rootPath);
            var retryDelays = new[]
            {
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(1000)
            };

            for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                try
                {
                    ExportBackupTombstoneInternal(rootPath, backupExternalId, appVersion, machineId);
                    return;
                }
                catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
                {
                    if (attempt >= retryDelays.Length)
                    {
                        Console.WriteLine($"[MetadataSync] Tombstone export failed after retries: {ex.Message}");
                        return;
                    }

                    var delay = retryDelays[attempt];
                    Console.WriteLine($"[MetadataSync] Tombstone store locked; retrying in {delay.TotalMilliseconds:0}ms.");
                    Thread.Sleep(delay);
                }
            }
        }
        finally
        {
            MetadataIoGate.Release();
        }
    }

    private void ExportBackupTombstoneInternal(string rootPath, string backupExternalId, string appVersion, string machineId)
    {
        TryFlushDeferredExport(rootPath);
        var storeRoot = rootPath;
        var isDeferred = false;
        var destMetaDir = GetMetaDir(rootPath);
        if (!TryEnsureMetadataDirWritable(destMetaDir))
        {
            storeRoot = GetDeferredExportRoot(rootPath);
            isDeferred = true;
        }

        var store = new MetadataStore(storeRoot);
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
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
            if (ex is SqliteException sqliteEx && IsCannotOpenOrLocked(sqliteEx))
                throw;
            Console.WriteLine($"[MetadataSync] Tombstone export failed writing store '{rootPath}': {ex.Message}");
            return;
        }

        if (isDeferred)
        {
            TryFlushDeferredExport(rootPath);
        }
    }

    private static string GetMetaDir(string rootPath) =>
        Path.Combine(rootPath, ".vaultsync", "meta");

    private static string GetDeferredExportRoot(string rootPath)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootPath)));
        return Path.Combine(Path.GetTempPath(), "vaultsync-meta-export", hash.ToLowerInvariant());
    }

    private static void WaitForNetworkReady(string rootPath)
    {
        if (!IsLikelyNetworkPath(rootPath))
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Directory.Exists(rootPath))
                return;

            Thread.Sleep(200 * (attempt + 1));
        }
    }

    private static bool IsLikelyNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("//", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.Contains("/Library/Application Support/VaultSync/mounts/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool TryEnsureMetadataDirWritable(string metaDir)
    {
        try
        {
            var rootDir = Directory.GetParent(Directory.GetParent(metaDir)?.FullName ?? string.Empty)?.FullName;
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
                return false;

            Directory.CreateDirectory(metaDir);
            var probe = Path.Combine(metaDir, ".write_test");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFlushDeferredExport(string rootPath)
    {
        var deferredRoot = GetDeferredExportRoot(rootPath);
        return TryCopyStoreFiles(deferredRoot, rootPath);
    }

    private static bool TryCopyStoreFiles(string fromRoot, string toRoot)
    {
        try
        {
            var sourceDir = GetMetaDir(fromRoot);
            if (!Directory.Exists(sourceDir))
                return false;

            if (!Directory.Exists(toRoot))
                return false;

            var destDir = GetMetaDir(toRoot);
            Directory.CreateDirectory(destDir);

            var copied = false;
            foreach (var suffix in new[] { "vaultsync.meta.db", "vaultsync.meta.db-wal", "vaultsync.meta.db-shm" })
            {
                var src = Path.Combine(sourceDir, suffix);
                if (!File.Exists(src))
                    continue;

                var dst = Path.Combine(destDir, suffix);
                File.Copy(src, dst, overwrite: true);
                if (suffix.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    copied = true;
            }

            return copied;
        }
        catch
        {
            return false;
        }
    }

    private (int snapshots, int backups) ExportProjectHistory(
        MetadataStore store,
        Project project,
        string projectExternalId,
        DateTime now,
        string machineId)
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
            SettingsJson = BuildProjectSettingsJson(project),
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
            var descriptor = BackupCryptoDescriptor.FromMetadata(backup.IsEncrypted, backup.CryptoDescriptorJson);
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
                OriginMachineName = machineId,
                IsProtected = backup.IsProtected,
                IsEncrypted = backup.IsEncrypted,
                KdfParamsJson = descriptor.ToMetadataJson(backup.IsEncrypted)
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

    private static string BuildProjectSettingsJson(Project project)
    {
        try
        {
            var color = ProjectColorResolver?.Invoke(project);
            var settings = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(color))
            {
                settings["avatarColor"] = color;
            }

            settings["encryptionPolicy"] = ProjectEncryptionPolicy.Normalize(project.EncryptionPolicy);

            if (!string.IsNullOrWhiteSpace(project.EncryptionKeyRef))
            {
                settings["encryptionKeyRef"] = project.EncryptionKeyRef;
            }

            return settings.Count == 0 ? "{}" : JsonSerializer.Serialize(settings);
        }
        catch
        {
            return "{}";
        }
    }

    private static (string EncryptionPolicy, string? EncryptionKeyRef) ParseProjectSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return (ProjectEncryptionPolicy.Inherit, null);

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            var policy = ProjectEncryptionPolicy.Inherit;
            string? keyRef = null;

            if (doc.RootElement.TryGetProperty("encryptionPolicy", out var policyProp))
            {
                policy = ProjectEncryptionPolicy.Normalize(policyProp.GetString());
            }

            if (doc.RootElement.TryGetProperty("encryptionKeyRef", out var keyRefProp))
            {
                var rawKeyRef = keyRefProp.GetString();
                keyRef = string.IsNullOrWhiteSpace(rawKeyRef) ? null : rawKeyRef;
            }

            return (policy, keyRef);
        }
        catch
        {
            return (ProjectEncryptionPolicy.Inherit, null);
        }
    }

    private static void TryApplyProjectColor(MetaProject metaProject)
    {
        if (ProjectColorApplier is null || string.IsNullOrWhiteSpace(metaProject.ExternalId))
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(metaProject.SettingsJson))
                return;

            using var doc = JsonDocument.Parse(metaProject.SettingsJson);
            if (!doc.RootElement.TryGetProperty("avatarColor", out var colorProp))
                return;

            var color = colorProp.GetString();
            if (string.IsNullOrWhiteSpace(color))
                return;

            ProjectColorApplier(metaProject.ExternalId, color);
        }
        catch
        {
            // ignore malformed settings json
        }
    }
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
    public IReadOnlyCollection<int> AffectedProjectIds { get; init; } = Array.Empty<int>();

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
