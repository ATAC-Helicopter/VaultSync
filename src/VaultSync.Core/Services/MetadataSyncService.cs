using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace VaultSync.Core.Services;

public sealed class MetadataSyncService(SqliteRepository repo, IAppConfigStore? configStore = null)
{
    private readonly SqliteRepository _repo = repo;
    private readonly IAppConfigStore _configStore = configStore ?? StaticAppConfigStore.Instance;
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, MetadataSyncPreview Preview)> _previewCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim MetadataIoGate = new(1, 1);

    public static Func<Project, string?>? ProjectColorResolver { get; set; }
    public static Action<string, string>? ProjectColorApplier { get; set; }

    public MetadataSyncResult ImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        return ImportFromStoreAsync(rootPath, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<MetadataSyncResult> ImportFromStoreAsync(string rootPath, MetadataSyncOptions? options = null, CancellationToken ct = default)
    {
        using var totalTiming = RuntimeTiming.Measure("Metadata import total");
        await MetadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using (RuntimeTiming.Measure("Metadata import network wait"))
            {
                await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            }
            MetadataSyncOptions opts = options ?? MetadataSyncOptions.Default;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                Console.WriteLine("[MetadataSync] Import failed: root path is empty.");
                return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, "Root path is empty.");
            }

            var store = new MetadataStore(rootPath);
            if (!File.Exists(store.DatabasePath))
            {
                Console.WriteLine($"[MetadataSync] Import skipped: store not found at '{store.DatabasePath}'.");
                MetadataSyncResult legacyResult = ImportBackupFoldersFromDestination(rootPath, opts, _configStore.Load());
                if (legacyResult.Status == MetadataSyncStatus.Success &&
                    (legacyResult.ImportedProjects > 0 ||
                     legacyResult.ImportedSnapshots > 0 ||
                     legacyResult.ImportedBackups > 0 ||
                     legacyResult.RepairedBackups > 0))
                {
                    return legacyResult;
                }

                return MetadataSyncResult.Failure(MetadataSyncStatus.NoStore, "Metadata store not found.");
            }

            if (TrySkipUnchangedSourceFromFileStamp(rootPath, store.DatabasePath, opts, out MetadataSyncResult? unchangedResult))
                return unchangedResult!;

            string? walTempRoot = null;
            bool copiedWalStore = false;
            if (ShouldUseTempCopy(store.DatabasePath))
            {
                using var copyTiming = RuntimeTiming.Measure("Metadata import temp copy");
                copiedWalStore = TryCopyStoreForRead(store.DatabasePath, out walTempRoot);
            }

            if (copiedWalStore && !string.IsNullOrWhiteSpace(walTempRoot))
            {
                Console.WriteLine($"[MetadataSync] Import using temp copy (SQLite sidecar detected): '{walTempRoot}'.");
                try
                {
                    return ImportFromStoreInternal(rootPath, new MetadataStore(walTempRoot, allowReadRecovery: true), opts, store.DatabasePath);
                }
                finally
                {
                    TryDeleteTempStore(walTempRoot);
                }
            }

            try
            {
                return ImportFromStoreInternal(rootPath, store, opts, store.DatabasePath);
            }
            catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
            {
                Console.WriteLine($"[MetadataSync] Import failed opening store at '{store.DatabasePath}': {ex.Message}");
                if (!TryCopyStoreForRead(store.DatabasePath, out string? tempRoot))
                    return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, ex.Message);

                Console.WriteLine($"[MetadataSync] Import retrying from temp copy: '{tempRoot}'.");
                try
                {
                    return ImportFromStoreInternal(rootPath, new MetadataStore(tempRoot, allowReadRecovery: true), opts, store.DatabasePath);
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
        return PreviewImportFromStoreAsync(rootPath, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<MetadataSyncPreview> PreviewImportFromStoreAsync(string rootPath, MetadataSyncOptions? options = null, CancellationToken ct = default)
    {
        await MetadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            MetadataSyncOptions opts = options ?? MetadataSyncOptions.Default;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                Console.WriteLine("[MetadataSync] Preview failed: root path is empty.");
                return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidPath, rootPath, string.Empty, "Root path is empty.");
            }

            var store = new MetadataStore(rootPath);
            if (!File.Exists(store.DatabasePath))
            {
                Console.WriteLine($"[MetadataSync] Preview skipped: store not found at '{store.DatabasePath}'.");
                MetadataSyncPreview legacyPreview = PreviewBackupFoldersFromDestination(rootPath, opts, store.DatabasePath);
                if (legacyPreview.Status == MetadataSyncStatus.Success &&
                    (legacyPreview.NewProjects > 0 ||
                     legacyPreview.NewSnapshots > 0 ||
                     legacyPreview.NewBackups > 0))
                {
                    return legacyPreview;
                }

                return MetadataSyncPreview.Failure(MetadataSyncStatus.NoStore, rootPath, store.DatabasePath, "Metadata store not found.");
            }

            if (ShouldUseTempCopy(store.DatabasePath) && TryCopyStoreForRead(store.DatabasePath, out string? walTempRoot))
            {
                Console.WriteLine($"[MetadataSync] Preview using temp copy (SQLite sidecar detected): '{walTempRoot}'.");
                try
                {
                    return PreviewImportFromStoreInternal(rootPath, new MetadataStore(walTempRoot, allowReadRecovery: true), opts);
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
                if (!TryCopyStoreForRead(store.DatabasePath, out string? tempRoot))
                    return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidStore, rootPath, store.DatabasePath, ex.Message);

                Console.WriteLine($"[MetadataSync] Preview retrying from temp copy: '{tempRoot}'.");
                try
                {
                    return PreviewImportFromStoreInternal(rootPath, new MetadataStore(tempRoot, allowReadRecovery: true), opts);
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

    private MetadataSyncResult ImportFromStoreInternal(
        string rootPath,
        MetadataStore store,
        MetadataSyncOptions opts,
        string sourceDatabasePath)
    {
        using var totalTiming = RuntimeTiming.Measure("Metadata import apply");
        MetaInfo? metaInfo;
        try
        {
            using var timing = RuntimeTiming.Measure("Metadata import read meta info");
            metaInfo = store.GetMetaInfo();
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Import failed: invalid store at '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, ex.Message);
        }

        if (metaInfo?.SchemaVersion > MetadataStore.CurrentSchemaVersion)
        {
            Console.WriteLine($"[MetadataSync] Import blocked: schema {metaInfo.SchemaVersion} > supported {MetadataStore.CurrentSchemaVersion}.");
            return MetadataSyncResult.Failure(
                MetadataSyncStatus.Incompatible,
                $"Metadata schema {metaInfo.SchemaVersion} is newer than supported {MetadataStore.CurrentSchemaVersion}.");
        }

        if (opts.SkipUnchangedReadOnlySource &&
            !opts.ExportMissingTombstonesOnImport &&
            TryGetMetadataSourceStamp(rootPath, sourceDatabasePath, metaInfo, out MetadataImportSourceStamp sourceStamp) &&
            HasSuccessfulUnchangedImport(sourceStamp))
        {
            Console.WriteLine($"[MetadataSync] Auto import skipped for unchanged store '{rootPath}'.");
            return new MetadataSyncResult(MetadataSyncStatus.Success, 0, 0, 0, 0, "Metadata source unchanged.");
        }

        int importedProjects = 0;
        int importedSnapshots = 0;
        int importedBackups = 0;
        int appliedTombstones = 0;
        var affectedProjectIds = new HashSet<int>();
        var projectMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var snapshotMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        AppConfig config;
        List<Project> localProjects;
        using (RuntimeTiming.Measure("Metadata import load local state"))
        {
            config = _configStore.Load();
            localProjects = _repo.GetAllProjects().ToList();
        }
        List<ProjectMetadataConflictRecord> pendingConflicts = config.Advanced.ProjectMetadataConflicts ??= [];
        bool metadataConflictChanged = false;

        IReadOnlyDictionary<string, int> projectExternalMap = _repo.GetProjectExternalIdMap();
        foreach (KeyValuePair<string, int> pair in projectExternalMap)
        {
            projectMap[pair.Key] = pair.Value;
        }

        IEnumerable<MetaProject> metaProjects;
        IEnumerable<MetaSnapshot> metaSnapshots;
        IEnumerable<MetaBackup> metaBackups;
        IEnumerable<MetaTombstone> metaTombstones;

        try
        {
            using var timing = RuntimeTiming.Measure("Metadata import read store rows");
            metaProjects = [.. store.ListProjects()];
            metaSnapshots = [.. store.ListSnapshots()];
            metaBackups = [.. store.ListBackups()];
            metaTombstones = [.. store.ListTombstones()];
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Import failed while reading store '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, ex.Message);
        }

        var tombstonedProjectIds = metaTombstones
            .Where(t => string.Equals(t.EntityType, "project", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string? tombstonedProjectId in tombstonedProjectIds)
        {
            if (!projectMap.TryGetValue(tombstonedProjectId, out int existingId))
                continue;

            _repo.RemoveProject(existingId);
            config.Backups.AutoBackupDisabledProjects?.Remove(existingId);
            RemoveProjectMetadataConflict(existingId, pendingConflicts);
            projectMap.Remove(tombstonedProjectId);
            localProjects.RemoveAll(project => project.Id == existingId);
            appliedTombstones++;
            metadataConflictChanged = true;
        }

        foreach (MetaProject metaProject in metaProjects)
        {
            if (string.IsNullOrWhiteSpace(metaProject.ExternalId))
                continue;

            if (tombstonedProjectIds.Contains(metaProject.ExternalId))
                continue;

            ParsedProjectSettings parsedSettings = ParseProjectSettings(metaProject.SettingsJson);
            TryApplyProjectColor(metaProject);

            if (projectMap.TryGetValue(metaProject.ExternalId, out int mappedProjectId))
            {
                metadataConflictChanged |= ApplyImportedProjectSettings(
                    mappedProjectId,
                    config,
                    metaProject,
                    metaInfo?.WriterMachineId,
                    parsedSettings,
                    pendingConflicts);
                continue;
            }

            Project? existingByName = localProjects.FirstOrDefault(p =>
                string.Equals(p.Name, metaProject.Name, StringComparison.OrdinalIgnoreCase));

            if (existingByName != null)
            {
                string importedRoot = ResolveImportedProjectRoot(
                    metaProject.RootPathHint,
                    config.ProjectsRoot,
                    metaProject.Name,
                    metaProject.ExternalId);

                if (string.IsNullOrWhiteSpace(existingByName.ExternalId))
                {
                    _repo.UpdateProjectExternalId(existingByName.Id, metaProject.ExternalId);
                }

                if (!string.IsNullOrWhiteSpace(importedRoot) &&
                    ShouldRepairImportedProjectRoot(existingByName.RootPath, importedRoot))
                {
                    _repo.UpdateProjectPath(existingByName.Name, importedRoot, out _);
                    existingByName = existingByName with { RootPath = importedRoot };
                }

                metadataConflictChanged |= ApplyImportedProjectSettings(
                    existingByName.Id,
                    config,
                    metaProject,
                    metaInfo?.WriterMachineId,
                    parsedSettings,
                    pendingConflicts);
                projectMap[metaProject.ExternalId] = existingByName.Id;
                continue;
            }

            if (!opts.AllowCreateProjects)
                continue;

            string projectRoot = ResolveImportedProjectRoot(
                metaProject.RootPathHint,
                config.ProjectsRoot,
                metaProject.Name,
                metaProject.ExternalId);

            var project = new Project
            {
                ExternalId = metaProject.ExternalId,
                Name = metaProject.Name,
                RootPath = projectRoot,
                Preset = metaProject.Preset,
                CreatedUtc = metaProject.CreatedUtc,
                NeedsRestore = false,
                EncryptionPolicy = parsedSettings.HasEncryptionPolicy
                    ? parsedSettings.EncryptionPolicy
                    : ProjectEncryptionPolicy.Inherit,
                EncryptionKeyRef = parsedSettings.HasEncryptionKeyRef
                    ? parsedSettings.EncryptionKeyRef
                    : null,
                VerificationPolicy = parsedSettings.HasVerificationPolicy
                    ? parsedSettings.VerificationPolicy
                    : ProjectVerificationPolicy.Always,
                PreferredDestinationId = parsedSettings.HasPreferredDestinationId
                    ? parsedSettings.PreferredDestinationId
                    : string.Empty,
                RestoreMode = parsedSettings.HasRestoreMode
                    ? parsedSettings.RestoreMode
                    : ProjectRestoreMode.Direct,
                Tags = parsedSettings.HasTags
                    ? parsedSettings.Tags
                    : string.Empty
            };

            int newId = _repo.AddProject(project);
            if (parsedSettings.HasAutoBackupEnabled)
                metadataConflictChanged |= ApplyImportedProjectAutoBackupSetting(config, newId, parsedSettings.AutoBackupEnabled);
            projectMap[metaProject.ExternalId] = newId;
            importedProjects++;
        }

        IReadOnlyDictionary<string, int> snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        foreach (KeyValuePair<string, int> pair in snapshotExternalMap)
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
                .Where(b => !string.IsNullOrWhiteSpace(b.SnapshotExternalId) && !tombstonedBackupIds.Contains(b.ExternalId))
                .Select(b => b.SnapshotExternalId),
            StringComparer.OrdinalIgnoreCase);
        var missingSnapshotExternalIds = metaSnapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.ExternalId) && !liveSnapshotExternalIds.Contains(s.ExternalId))
            .Select(s => s.ExternalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string? missingSnapshotId in missingSnapshotExternalIds)
        {
            tombstonedSnapshotIds.Add(missingSnapshotId);
        }

        foreach (MetaSnapshot metaSnapshot in metaSnapshots)
        {
            if (string.IsNullOrWhiteSpace(metaSnapshot.ExternalId))
                continue;

            if (tombstonedSnapshotIds.Contains(metaSnapshot.ExternalId))
                continue;

            if (!projectMap.TryGetValue(metaSnapshot.ProjectExternalId, out int projectId))
                continue;

            if (snapshotMap.ContainsKey(metaSnapshot.ExternalId))
                continue;

            int id = _repo.CreateSnapshotFromMetadata(
                metaSnapshot.ExternalId,
                projectId,
                metaSnapshot.CreatedUtc,
                metaSnapshot.FileCount,
                metaSnapshot.TotalBytes,
                new SnapshotDiffSummary(
                    metaSnapshot.DiffAdded,
                    metaSnapshot.DiffModified,
                    metaSnapshot.DiffDeleted,
                    metaSnapshot.DiffNetBytes,
                    SnapshotDiffSummary.ParseTopChangedPaths(metaSnapshot.DiffTopPathsJson)));

            snapshotMap[metaSnapshot.ExternalId] = id;
            importedSnapshots++;
        }

        IReadOnlyDictionary<string, int> backupExternalMap = _repo.GetBackupExternalIdMap();
        var missingBackupExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<int, DateTime> localLatestByProjectBeforeBackupImport = _repo.GetLatestBackupUtcByProject();

        using (RuntimeTiming.Measure("Metadata import apply backups"))
        {
            foreach (MetaBackup metaBackup in metaBackups)
            {
                if (string.IsNullOrWhiteSpace(metaBackup.ExternalId))
                    continue;

                if (tombstonedBackupIds.Contains(metaBackup.ExternalId))
                    continue;

                if (!TryResolveBackupPath(rootPath, metaBackup.PathRel, out string? normalizedPathRel))
                {
                    missingBackupExternalIds.Add(metaBackup.ExternalId);
                    tombstonedBackupIds.Add(metaBackup.ExternalId);
                    continue;
                }

                if (!projectMap.TryGetValue(metaBackup.ProjectExternalId, out int projectId))
                    continue;

                if (!snapshotMap.TryGetValue(metaBackup.SnapshotExternalId, out int snapshotId))
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
                    backupMode: metaBackup.BackupMode,
                    originMachineName: metaBackup.OriginMachineName,
                    isEncrypted: metaBackup.IsEncrypted,
                    cryptoDescriptorJson: metaBackup.KdfParamsJson);
                importedBackups++;
                affectedProjectIds.Add(projectId);
            }
        }

        foreach (MetaTombstone tombstone in metaTombstones)
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityId))
                continue;

            if (string.Equals(tombstone.EntityType, "backup", StringComparison.OrdinalIgnoreCase))
            {
                if (backupExternalMap.TryGetValue(tombstone.EntityId, out int existingId))
                {
                    _repo.DeleteBackupById(existingId);
                    appliedTombstones++;
                }
            }
        }

        if (missingBackupExternalIds.Count > 0)
        {
            foreach (string missingExternalId in missingBackupExternalIds)
            {
                if (backupExternalMap.TryGetValue(missingExternalId, out int existingId))
                {
                    _repo.DeleteBackupById(existingId);
                    appliedTombstones++;
                }
            }

            if (opts.ExportMissingTombstonesOnImport)
            {
                TryExportMissingBackupTombstones(rootPath, missingBackupExternalIds);
            }
        }

        if (missingSnapshotExternalIds.Count > 0)
        {
            int removedSnapshots = 0;
            foreach (string? missingExternalId in missingSnapshotExternalIds)
            {
                Snapshot? snapshot = _repo.GetSnapshotByExternalId(missingExternalId);
                if (snapshot == null)
                    continue;

                if (_repo.HasBackupForSnapshot(snapshot.ProjectId, snapshot.Id))
                    continue;

                Project? project = _repo.GetProjectById(snapshot.ProjectId);
                if (project == null)
                    continue;

                (int Snapshots, int Files) = _repo.DeleteSnapshotsById(project.Name, [snapshot.Id]);
                removedSnapshots += Snapshots;
            }

            if (removedSnapshots > 0)
            {
                if (opts.ExportMissingTombstonesOnImport)
                {
                    TryExportMissingSnapshotTombstones(rootPath, missingSnapshotExternalIds);
                }
            }
        }

        MetadataSyncResult filesystemResult;
        using (RuntimeTiming.Measure("Metadata import legacy folder scan"))
        {
            filesystemResult = ImportBackupFoldersFromDestination(rootPath, opts, config);
        }
        importedProjects += filesystemResult.ImportedProjects;
        importedSnapshots += filesystemResult.ImportedSnapshots;
        importedBackups += filesystemResult.ImportedBackups;
        foreach (int projectId in filesystemResult.AffectedProjectIds)
        {
            affectedProjectIds.Add(projectId);
        }

        if (metadataConflictChanged)
        {
            _configStore.Save(config);
        }

        var result = new MetadataSyncResult(
            MetadataSyncStatus.Success,
            importedProjects,
            importedSnapshots,
            importedBackups,
            appliedTombstones,
            string.Empty)
        {
            AffectedProjectIds = [.. affectedProjectIds]
        };
        if (opts.MarkNeedsRestoreOnImport)
        {
            IEnumerable<MetaBackup> liveBackups = metaBackups
                .Where(b => !string.IsNullOrWhiteSpace(b.ExternalId) && !tombstonedBackupIds.Contains(b.ExternalId));
            using (RuntimeTiming.Measure("Metadata import restore flags"))
            {
                UpdateNeedsRestoreFlags(projectMap, liveBackups, localLatestByProjectBeforeBackupImport);
            }
        }
        if (opts.SkipUnchangedReadOnlySource &&
            !opts.ExportMissingTombstonesOnImport &&
            missingBackupExternalIds.Count == 0 &&
            TryGetMetadataSourceStamp(rootPath, sourceDatabasePath, metaInfo, out MetadataImportSourceStamp completedStamp))
        {
            List<string> importedProjectExternalIds = GetLiveExternalIds(metaProjects, tombstonedProjectIds, project => project.ExternalId);
            List<string> importedSnapshotExternalIds = GetLiveExternalIds(metaSnapshots, tombstonedSnapshotIds, snapshot => snapshot.ExternalId);
            List<string> importedBackupExternalIds = GetLiveExternalIds(metaBackups, tombstonedBackupIds, backup => backup.ExternalId);
            SaveSuccessfulImportStamp(
                completedStamp,
                importedProjectExternalIds,
                importedSnapshotExternalIds,
                importedBackupExternalIds,
                metaTombstones.Count());
        }
        Console.WriteLine($"[MetadataSync] Import complete from '{rootPath}': projects={importedProjects}, snapshots={importedSnapshots}, backups={importedBackups}, tombstones={appliedTombstones}.");
        return result;
    }

    private bool HasSuccessfulUnchangedImport(MetadataImportSourceStamp current)
    {
        try
        {
            MetadataImportSourceStamp? cached = _configStore.Load()
                .Advanced
                .MetadataImportCache
                .Sources
                .FirstOrDefault(source => string.Equals(source.SourceKey, current.SourceKey, StringComparison.OrdinalIgnoreCase));

            return cached != null &&
                   !string.IsNullOrWhiteSpace(cached.ImportedUtc) &&
                   string.Equals(cached.SourceMachineId, current.SourceMachineId, StringComparison.Ordinal) &&
                   string.Equals(cached.StoreUpdatedUtc, current.StoreUpdatedUtc, StringComparison.Ordinal) &&
                   cached.StoreSchemaVersion == current.StoreSchemaVersion &&
                   cached.StoreFileLengthBytes == current.StoreFileLengthBytes &&
                   string.Equals(cached.StoreFileUpdatedUtc, current.StoreFileUpdatedUtc, StringComparison.Ordinal) &&
                   string.Equals(cached.StoreSidecarStamp, current.StoreSidecarStamp, StringComparison.Ordinal) &&
                   LocalRepositoryHasImportCoverage(cached);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Import cache check failed: {ex.Message}");
            return false;
        }
    }

    private void SaveSuccessfulImportStamp(
        MetadataImportSourceStamp stamp,
        IReadOnlyCollection<string> projectExternalIds,
        IReadOnlyCollection<string> snapshotExternalIds,
        IReadOnlyCollection<string> backupExternalIds,
        int tombstoneCount)
    {
        try
        {
            AppConfig config = _configStore.Load();
            List<MetadataImportSourceStamp> sources = config.Advanced.MetadataImportCache.Sources;
            sources.RemoveAll(source => string.Equals(source.SourceKey, stamp.SourceKey, StringComparison.OrdinalIgnoreCase));
            stamp.ImportedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            stamp.ProjectExternalIds = [.. projectExternalIds];
            stamp.SnapshotExternalIds = [.. snapshotExternalIds];
            stamp.BackupExternalIds = [.. backupExternalIds];
            stamp.ProjectCount = stamp.ProjectExternalIds.Count;
            stamp.SnapshotCount = stamp.SnapshotExternalIds.Count;
            stamp.BackupCount = stamp.BackupExternalIds.Count;
            stamp.TombstoneCount = tombstoneCount;
            sources.Insert(0, stamp);
            if (sources.Count > 32)
            {
                sources.RemoveRange(32, sources.Count - 32);
            }

            _configStore.Save(config);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Import cache save failed: {ex.Message}");
        }
    }

    private static bool TryGetMetadataSourceStamp(
        string rootPath,
        string sourceDatabasePath,
        MetaInfo? metaInfo,
        out MetadataImportSourceStamp stamp)
    {
        stamp = new MetadataImportSourceStamp();
        if (metaInfo == null || string.IsNullOrWhiteSpace(sourceDatabasePath))
            return false;

        try
        {
            var db = new FileInfo(sourceDatabasePath);
            if (!db.Exists)
                return false;

            string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string sourceKey = BuildMetadataSourceKey(normalizedRoot);
            stamp = new MetadataImportSourceStamp
            {
                SourceKey = sourceKey,
                SourcePath = normalizedRoot,
                SourceMachineId = metaInfo.WriterMachineId ?? string.Empty,
                StoreUpdatedUtc = metaInfo.LastWriteUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                StoreSchemaVersion = metaInfo.SchemaVersion,
                StoreFileLengthBytes = db.Length,
                StoreFileUpdatedUtc = db.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                StoreSidecarStamp = BuildStoreSidecarStamp(sourceDatabasePath)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildMetadataSourceKey(string normalizedRoot)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot.Replace('\\', '/')));
        return "metadata:" + HashService.FormatHexLower(hash);
    }

    private bool TrySkipUnchangedSourceFromFileStamp(
        string rootPath,
        string sourceDatabasePath,
        MetadataSyncOptions opts,
        out MetadataSyncResult? result)
    {
        result = null;
        if (!opts.SkipUnchangedReadOnlySource || opts.ExportMissingTombstonesOnImport)
            return false;

        try
        {
            var db = new FileInfo(sourceDatabasePath);
            if (!db.Exists)
                return false;

            string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string sourceKey = BuildMetadataSourceKey(normalizedRoot);
            MetadataImportSourceStamp? cached = _configStore.Load()
                .Advanced
                .MetadataImportCache
                .Sources
                .FirstOrDefault(source => string.Equals(source.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));

            if (cached == null || string.IsNullOrWhiteSpace(cached.ImportedUtc))
                return false;

            bool unchanged =
                cached.StoreFileLengthBytes == db.Length &&
                string.Equals(cached.StoreFileUpdatedUtc, db.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                string.Equals(cached.StoreSidecarStamp, BuildStoreSidecarStamp(sourceDatabasePath), StringComparison.Ordinal);
            if (!unchanged)
                return false;
            if (!LocalRepositoryHasImportCoverage(cached))
                return false;

            Console.WriteLine($"[MetadataSync] Auto import skipped for unchanged store files '{rootPath}'.");
            result = new MetadataSyncResult(MetadataSyncStatus.Success, 0, 0, 0, 0, "Metadata source unchanged.");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Import file-stamp cache check failed: {ex.Message}");
            return false;
        }
    }

    private bool LocalRepositoryHasImportCoverage(MetadataImportSourceStamp cached)
    {
        try
        {
            if (!HasCachedExternalIds(cached))
                return false;

            IReadOnlyDictionary<string, int> projectExternalIds = _repo.GetProjectExternalIdMap();
            IReadOnlyDictionary<string, int> snapshotExternalIds = _repo.GetSnapshotExternalIdMap();
            IReadOnlyDictionary<string, int> backupExternalIds = _repo.GetBackupExternalIdMap();
            return ContainsAll(projectExternalIds, cached.ProjectExternalIds) &&
                   ContainsAll(snapshotExternalIds, cached.SnapshotExternalIds) &&
                   ContainsAll(backupExternalIds, cached.BackupExternalIds);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Import cache local coverage check failed: {ex.Message}");
            return false;
        }
    }

    private static bool HasCachedExternalIds(MetadataImportSourceStamp cached)
    {
        return cached.ProjectExternalIds.Count >= cached.ProjectCount &&
               cached.SnapshotExternalIds.Count >= cached.SnapshotCount &&
               cached.BackupExternalIds.Count >= cached.BackupCount;
    }

    private static bool ContainsAll(IReadOnlyDictionary<string, int> localExternalIds, IEnumerable<string> cachedExternalIds)
    {
        foreach (string externalId in cachedExternalIds)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                continue;

            if (!localExternalIds.ContainsKey(externalId))
                return false;
        }

        return true;
    }

    private static List<string> GetLiveExternalIds<T>(
        IEnumerable<T> rows,
        ISet<string> tombstonedExternalIds,
        Func<T, string> getExternalId)
    {
        return rows
            .Select(getExternalId)
            .Where(id => !string.IsNullOrWhiteSpace(id) && !tombstonedExternalIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildStoreSidecarStamp(string sourceDatabasePath)
    {
        var parts = new List<string>();
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var file = new FileInfo(sourceDatabasePath + suffix);
            if (!file.Exists)
                continue;

            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{suffix}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
        }

        return parts.Count == 0 ? "none" : string.Join("|", parts);
    }

    private void UpdateNeedsRestoreFlags(
        IReadOnlyDictionary<string, int> projectMap,
        IEnumerable<MetaBackup> metaBackups,
        IReadOnlyDictionary<int, DateTime> localLatestByProject)
    {
        if (projectMap.Count == 0)
            return;

        var importedLatestByExternalId = metaBackups
            .Where(b => !string.IsNullOrWhiteSpace(b.ProjectExternalId))
            .GroupBy(b => b.ProjectExternalId)
            .ToDictionary(g => g.Key, g => g.Max(b => b.CreatedUtc), StringComparer.OrdinalIgnoreCase);

        foreach ((string externalId, int projectId) in projectMap)
        {
            if (!importedLatestByExternalId.TryGetValue(externalId, out DateTime importedLatest))
                continue;

            localLatestByProject.TryGetValue(projectId, out DateTime localLatest);
            bool needsRestore = importedLatest > localLatest;
            if (needsRestore)
            {
                Project? project = _repo.GetProjectById(projectId);
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
                string current = stack.Pop();

                IEnumerable<string> dirs;
                try
                {
                    dirs = Directory.EnumerateDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir);
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

                foreach (string file in files)
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
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Preview failed: invalid store at '{rootPath}': {ex.Message}");
            return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidStore, rootPath, store.DatabasePath, ex.Message);
        }

        if (metaInfo?.SchemaVersion > MetadataStore.CurrentSchemaVersion)
        {
            Console.WriteLine($"[MetadataSync] Preview blocked: schema {metaInfo.SchemaVersion} > supported {MetadataStore.CurrentSchemaVersion}.");
            return MetadataSyncPreview.Failure(
                MetadataSyncStatus.Incompatible,
                rootPath,
                store.DatabasePath,
                $"Metadata schema {metaInfo.SchemaVersion} is newer than supported {MetadataStore.CurrentSchemaVersion}.");
        }

        if (metaInfo != null &&
            _previewCache.TryGetValue(rootPath, out (DateTime LastWriteUtc, MetadataSyncPreview Preview) cached) &&
            cached.LastWriteUtc == metaInfo.LastWriteUtc)
        {
            return cached.Preview;
        }

        int addProjects = 0;
        int linkProjects = 0;
        int addSnapshots = 0;
        int addBackups = 0;
        int deleteBackups = 0;

        var projectMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var localProjects = _repo.GetAllProjects().ToList();
        IReadOnlyDictionary<string, int> projectExternalMap = _repo.GetProjectExternalIdMap();
        foreach (KeyValuePair<string, int> pair in projectExternalMap)
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
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Preview failed while reading store '{rootPath}': {ex.Message}");
            return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidStore, rootPath, store.DatabasePath, ex.Message);
        }

        foreach (MetaProject metaProject in metaProjects)
        {
            if (string.IsNullOrWhiteSpace(metaProject.ExternalId))
                continue;

            if (projectMap.ContainsKey(metaProject.ExternalId))
                continue;

            Project? existingByName = localProjects.FirstOrDefault(p =>
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

        IReadOnlyDictionary<string, int> snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        IReadOnlyDictionary<string, int> backupExternalMap = _repo.GetBackupExternalIdMap();
        var existingBackupPaths = _repo
            .GetAllProjects()
            .SelectMany(project => _repo.GetBackupsForProject(project.Id))
            .Select(backup => NormalizeStablePath(NormalizeBackupPathRel(backup.Path)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tombstonedBackupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tombstonedSnapshotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveSnapshotExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (MetaTombstone tombstone in metaTombstones)
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

        foreach (MetaBackup metaBackup in metaBackups)
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

            if (!TryResolveBackupPath(rootPath, metaBackup.PathRel, out _))
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

        foreach (MetaSnapshot metaSnapshot in metaSnapshots)
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

        MetadataSyncPreview filesystemPreview = PreviewBackupFoldersFromDestination(rootPath, opts, store.DatabasePath);
        addProjects += filesystemPreview.NewProjects;
        addSnapshots += filesystemPreview.NewSnapshots;
        addBackups += filesystemPreview.NewBackups;

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

    private MetadataSyncResult ImportBackupFoldersFromDestination(string rootPath, MetadataSyncOptions opts, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return new MetadataSyncResult(MetadataSyncStatus.Success, 0, 0, 0, 0, string.Empty);
        }

        IReadOnlyList<LegacyBackupFolder> discovered = DiscoverLegacyBackupFolders(rootPath);
        if (discovered.Count == 0)
        {
            return new MetadataSyncResult(MetadataSyncStatus.Success, 0, 0, 0, 0, string.Empty);
        }

        int importedProjects = 0;
        int importedSnapshots = 0;
        int importedBackups = 0;
        int repairedBackups = 0;
        var affectedProjectIds = new HashSet<int>();
        var projectsByName = _repo
            .GetAllProjects()
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        IReadOnlyDictionary<string, int> backupExternalMap = _repo.GetBackupExternalIdMap();
        var existingBackupByPath = _repo
            .GetAllProjects()
            .SelectMany(project => _repo.GetBackupsForProject(project.Id))
            .Select(backup => new
            {
                Backup = backup,
                Path = NormalizeStablePath(NormalizeBackupPathRel(backup.Path))
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Backup, StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, LegacyBackupFolder> projectGroup in discovered.GroupBy(folder => folder.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            List<LegacyBackupFolder> importableFolders = [.. projectGroup
                .Where(folder => !existingBackupByPath.ContainsKey(NormalizeStablePath(folder.RelativePath)))
                .OrderBy(folder => folder.CreatedUtc)];
            List<LegacyBackupFolder> repairableFolders = [.. projectGroup
                .Where(folder =>
                    existingBackupByPath.TryGetValue(NormalizeStablePath(folder.RelativePath), out Backup? backup) &&
                    backup.IsImported &&
                    backup.TotalBytes <= 0)
                .OrderBy(folder => folder.CreatedUtc)];
            if (importableFolders.Count == 0 && repairableFolders.Count == 0)
                continue;

            string projectName = projectGroup.Key;
            string projectExternalId = BuildStableExternalId("legacy-project", rootPath, projectName);

            if (!projectsByName.TryGetValue(projectName, out Project? project))
            {
                if (!opts.AllowCreateProjects)
                    continue;

                string projectRoot = ResolveImportedProjectRoot(null, config.ProjectsRoot, projectName, projectExternalId);
                int projectId = _repo.AddProject(new Project
                {
                    ExternalId = projectExternalId,
                    Name = projectName,
                    RootPath = projectRoot,
                    Preset = "generic",
                    CreatedUtc = projectGroup.Min(folder => folder.CreatedUtc),
                    NeedsRestore = false
                });

                project = _repo.GetProjectById(projectId);
                if (project is null)
                    continue;

                projectsByName[projectName] = project;
                importedProjects++;
            }
            else if (string.IsNullOrWhiteSpace(project.ExternalId))
            {
                _repo.UpdateProjectExternalId(project.Id, projectExternalId);
                project = project with { ExternalId = projectExternalId };
                projectsByName[projectName] = project;
            }

            foreach (LegacyBackupFolder folder in repairableFolders)
            {
                string normalizedRelativePath = NormalizeStablePath(folder.RelativePath);
                if (!existingBackupByPath.TryGetValue(normalizedRelativePath, out Backup? existingBackup))
                    continue;

                long sizeBytes = GetLegacyBackupFolderSize(rootPath, folder.RelativePath);
                if (sizeBytes <= 0)
                    continue;

                _repo.UpdateBackupTotalBytes(existingBackup.Id, sizeBytes);
                Snapshot? existingSnapshot = _repo.GetSnapshotById(existingBackup.SnapshotId);
                if (existingSnapshot is not null && existingSnapshot.TotalBytes <= 0)
                    _repo.UpdateSnapshotTotalBytes(existingSnapshot.Id, sizeBytes);

                repairedBackups++;
                affectedProjectIds.Add(existingBackup.ProjectId);
            }

            foreach (LegacyBackupFolder folder in importableFolders)
            {
                string normalizedRelativePath = NormalizeStablePath(folder.RelativePath);
                string snapshotExternalId = BuildStableExternalId("legacy-snapshot", rootPath, folder.RelativePath);
                string backupExternalId = BuildStableExternalId("legacy-backup", rootPath, folder.RelativePath);
                long sizeBytes = GetLegacyBackupFolderSize(rootPath, folder.RelativePath);

                if (!snapshotExternalMap.TryGetValue(snapshotExternalId, out int snapshotId))
                {
                    snapshotId = _repo.CreateSnapshotFromMetadata(
                        snapshotExternalId,
                        project.Id,
                        folder.CreatedUtc,
                        fileCount: 0,
                        totalBytes: sizeBytes);
                    importedSnapshots++;
                }

                if (backupExternalMap.ContainsKey(backupExternalId))
                    continue;

                _repo.CreateBackupFromMetadata(
                    backupExternalId,
                    project.Id,
                    snapshotId,
                    folder.CreatedUtc,
                    "manual",
                    sizeBytes,
                    folder.RelativePath,
                    rootPath,
                    string.Empty,
                    isProtected: false,
                    isImported: true,
                    backupMode: BackupModes.Full);

                importedBackups++;
                affectedProjectIds.Add(project.Id);
                existingBackupByPath[normalizedRelativePath] = _repo.GetBackupByExternalId(backupExternalId)
                    ?? new Backup { Id = 0, ProjectId = project.Id, SnapshotId = snapshotId, Path = folder.RelativePath, TotalBytes = sizeBytes, IsImported = true };
            }

            if (opts.MarkNeedsRestoreOnImport && affectedProjectIds.Contains(project.Id))
            {
                _repo.UpdateProjectNeedsRestore(project.Id, true);
            }
        }

        return new MetadataSyncResult(
            MetadataSyncStatus.Success,
            importedProjects,
            importedSnapshots,
            importedBackups,
            0,
            string.Empty)
        {
            AffectedProjectIds = [.. affectedProjectIds],
            RepairedBackups = repairedBackups
        };
    }

    private MetadataSyncPreview PreviewBackupFoldersFromDestination(string rootPath, MetadataSyncOptions opts, string databasePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return new MetadataSyncPreview(MetadataSyncStatus.Success, rootPath, databasePath, 0, 0, 0, 0, 0, string.Empty);
        }

        IReadOnlyList<LegacyBackupFolder> discovered = DiscoverLegacyBackupFolders(rootPath);
        if (discovered.Count == 0)
        {
            return new MetadataSyncPreview(MetadataSyncStatus.Success, rootPath, databasePath, 0, 0, 0, 0, 0, string.Empty);
        }

        var projectsByName = _repo
            .GetAllProjects()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        IReadOnlyDictionary<string, int> backupExternalMap = _repo.GetBackupExternalIdMap();
        var existingBackupPaths = _repo
            .GetAllProjects()
            .SelectMany(project => _repo.GetBackupsForProject(project.Id))
            .Select(backup => NormalizeStablePath(NormalizeBackupPathRel(backup.Path)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int addProjects = 0;
        int addSnapshots = 0;
        int addBackups = 0;
        var previewedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewedSnapshots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewedBackups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (LegacyBackupFolder folder in discovered)
        {
            string normalizedRelativePath = NormalizeStablePath(folder.RelativePath);
            if (existingBackupPaths.Contains(normalizedRelativePath))
                continue;

            bool projectExists = projectsByName.Contains(folder.ProjectName);
            if (!projectExists && !opts.AllowCreateProjects)
                continue;

            if (!projectExists && previewedProjects.Add(folder.ProjectName))
            {
                addProjects++;
            }

            string snapshotExternalId = BuildStableExternalId("legacy-snapshot", rootPath, folder.RelativePath);
            if (!snapshotExternalMap.ContainsKey(snapshotExternalId) && previewedSnapshots.Add(snapshotExternalId))
                addSnapshots++;

            string backupExternalId = BuildStableExternalId("legacy-backup", rootPath, folder.RelativePath);
            if (!backupExternalMap.ContainsKey(backupExternalId) && previewedBackups.Add(backupExternalId))
                addBackups++;
        }

        return new MetadataSyncPreview(
            MetadataSyncStatus.Success,
            rootPath,
            databasePath,
            addProjects,
            0,
            addSnapshots,
            addBackups,
            0,
            string.Empty);
    }

    private static IReadOnlyList<LegacyBackupFolder> DiscoverLegacyBackupFolders(string rootPath)
    {
        var result = new List<LegacyBackupFolder>();

        IEnumerable<string> projectDirs;
        try
        {
            projectDirs = Directory.EnumerateDirectories(rootPath);
        }
        catch
        {
            return result;
        }

        foreach (string projectDir in projectDirs)
        {
            string projectName = Path.GetFileName(projectDir);
            if (string.IsNullOrWhiteSpace(projectName) ||
                string.Equals(projectName, ".vaultsync", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IEnumerable<string> backupDirs;
            try
            {
                backupDirs = Directory.EnumerateDirectories(projectDir);
            }
            catch
            {
                continue;
            }

            foreach (string backupDir in backupDirs)
            {
                string folderName = Path.GetFileName(backupDir);
                if (!TryParseBackupFolderTimestamp(folderName, out DateTime createdUtc))
                    continue;

                string relativePath = Path.Combine(projectName, folderName);
                result.Add(new LegacyBackupFolder(projectName, relativePath, createdUtc));
            }
        }

        return result;
    }

    private static bool TryParseBackupFolderTimestamp(string folderName, out DateTime createdUtc)
    {
        createdUtc = default;
        if (string.IsNullOrWhiteSpace(folderName))
            return false;

        return DateTime.TryParseExact(
            folderName,
            "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out createdUtc);
    }

    private static long GetLegacyBackupFolderSize(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(relativePath))
            return 0;

        try
        {
            string backupFolder = Path.Combine(rootPath, relativePath);
            long archiveSize = BackupArchiveCryptoService.GetStoredArchiveSize(backupFolder);
            return archiveSize > 0 ? archiveSize : GetDirectorySize(backupFolder);
        }
        catch
        {
            return 0;
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return 0;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            try
            {
                foreach (string file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        total += Math.Max(0, new FileInfo(file).Length);
                    }
                    catch
                    {
                        // Best-effort size recovery for legacy imports.
                    }
                }

                foreach (string directory in Directory.EnumerateDirectories(current))
                {
                    pending.Push(directory);
                }
            }
            catch
            {
                // Skip folders that disappear or cannot be read.
            }
        }

        return total;
    }

    private static string BuildStableExternalId(string prefix, string rootPath, string relativePath)
    {
        _ = rootPath;
        string normalized = $"{prefix}|{NormalizeStablePath(relativePath)}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"{prefix}-{HashService.FormatHexLower(hash)[..32]}";
    }

    private static string NormalizeStablePath(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace('\\', '/')
            .TrimEnd('/')
            .ToLowerInvariant();
    }

    private sealed record LegacyBackupFolder(string ProjectName, string RelativePath, DateTime CreatedUtc);

    private static bool IsCannotOpenOrLocked(SqliteException ex)
    {
        return ex.SqliteErrorCode == 14 || ex.SqliteErrorCode == 5;
    }

    private static bool TryCopyStoreForRead(string databasePath, out string tempRoot)
    {
        tempRoot = string.Empty;
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "vaultsync-meta-import", Guid.NewGuid().ToString("N"));
            string tempDir = Path.Combine(root, ".vaultsync", "meta");
            Directory.CreateDirectory(tempDir);
            string destPath = Path.Combine(tempDir, Path.GetFileName(databasePath));
            File.Copy(databasePath, destPath, overwrite: true);
            TryCopySidecar(databasePath, destPath, "-wal");
            TryCopySidecar(databasePath, destPath, "-shm");
            TryCopySidecar(databasePath, destPath, "-journal");
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
            string source = sourceDbPath + suffix;
            if (!File.Exists(source))
                return;

            string dest = destDbPath + suffix;
            File.Copy(source, dest, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Temp copy missing sidecar '{suffix}': {ex.Message}");
        }
    }

    private static bool ShouldUseTempCopy(string databasePath)
    {
        return File.Exists(databasePath + "-wal") ||
               File.Exists(databasePath + "-shm") ||
               File.Exists(databasePath + "-journal");
    }

    private static bool BackupPathExists(string rootPath, string pathRel)
    {
        if (string.IsNullOrWhiteSpace(pathRel))
            return false;

        string fullPath = IsRootedPath(pathRel)
            ? pathRel
            : Path.Combine(rootPath, pathRel);

        return Directory.Exists(fullPath) || File.Exists(fullPath);
    }

    private static bool TryResolveBackupPath(string rootPath, string pathRel, out string normalizedPathRel)
    {
        normalizedPathRel = NormalizeBackupPathRel(pathRel);
        if (string.IsNullOrWhiteSpace(normalizedPathRel))
            return false;

        if (BackupPathExists(rootPath, normalizedPathRel))
            return true;

        if (TryResolveRootedBackupPathUnderRoot(rootPath, normalizedPathRel, out string? remappedPathRel))
        {
            normalizedPathRel = remappedPathRel;
            return true;
        }

        return false;
    }

    private static bool TryResolveRootedBackupPathUnderRoot(string rootPath, string pathRel, out string remappedPathRel)
    {
        remappedPathRel = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) || !IsRootedPath(pathRel))
            return false;

        string[] segments = [.. pathRel
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(static segment => !segment.EndsWith(':'))];

        // Rooted paths from another machine may include that machine's destination root.
        // Try suffixes under the configured destination, but keep at least project/timestamp.
        for (int start = 0; start <= segments.Length - 2; start++)
        {
            string candidateRel = Path.Combine(segments[start..]);
            string candidateFull = Path.Combine(rootPath, candidateRel);
            if (Directory.Exists(candidateFull) || File.Exists(candidateFull))
            {
                remappedPathRel = candidateRel;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeBackupPathRel(string pathRel)
    {
        if (string.IsNullOrWhiteSpace(pathRel))
            return string.Empty;

        string normalized = pathRel
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

    private static string ResolveImportedProjectRoot(
        string? rootPathHint,
        string? projectsRoot,
        string? projectName,
        string? externalId)
    {
        if (IsAcceptableImportedProjectRoot(rootPathHint))
            return Path.GetFullPath(rootPathHint!);

        if (ProjectRootResolver.TryResolveExistingProjectRoot(
                projectsRoot,
                projectName,
                rootPathHint,
                out string mappedRoot))
        {
            return mappedRoot;
        }

        if (IsAcceptableProjectsRoot(projectsRoot))
        {
            string folderName = BuildImportedProjectFolderName(projectName, externalId);
            return Path.Combine(Path.GetFullPath(projectsRoot!), folderName);
        }

        return PreserveImportedProjectRootHint(rootPathHint);
    }

    private static bool ShouldRepairImportedProjectRoot(string? existingRoot, string importedRoot)
    {
        if (string.IsNullOrWhiteSpace(existingRoot))
            return true;

        try
        {
            if (Directory.Exists(existingRoot))
                return false;
        }
        catch
        {
        }

        return Directory.Exists(importedRoot);
    }

    private static string PreserveImportedProjectRootHint(string? rootPathHint)
    {
        if (string.IsNullOrWhiteSpace(rootPathHint))
            return string.Empty;

        string trimmed = rootPathHint.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        if (IsVaultSyncTransientTempPath(trimmed))
            return string.Empty;

        // Preserve the original imported hint when we cannot safely map it
        // to a local existing directory, so the path is never silently erased.
        return trimmed;
    }

    private static bool IsAcceptableImportedProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsRootedPath(path))
            return false;

        if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (IsVaultSyncTransientTempPath(fullPath))
                return false;

            return Directory.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAcceptableProjectsRoot(string? projectsRoot)
    {
        if (string.IsNullOrWhiteSpace(projectsRoot) || !IsRootedPath(projectsRoot))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(projectsRoot);
            return Directory.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildImportedProjectFolderName(string? projectName, string? externalId)
    {
        string? source = string.IsNullOrWhiteSpace(projectName) ? externalId : projectName;
        if (string.IsNullOrWhiteSpace(source))
            return "ImportedProject";

        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        string cleaned = new string([.. source
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '-' : ch)])
            .Trim(' ', '.');

        return string.IsNullOrWhiteSpace(cleaned)
            ? "ImportedProject"
            : cleaned;
    }

    private static bool IsVaultSyncTransientTempPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!fullPath.StartsWith(tempRoot + Path.DirectorySeparatorChar, comparison))
                return false;

            var relative = Path.GetRelativePath(tempRoot, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return false;

            var firstSegment = relative
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment));

            return firstSegment is not null &&
                   (string.Equals(firstSegment, "vaultsync-meta-import", comparison) ||
                    string.Equals(firstSegment, "vaultsync-meta-export", comparison) ||
                    string.Equals(firstSegment, "vaultsync-archive-root", comparison) ||
                    firstSegment.StartsWith("vaultsync-open-", comparison) ||
                    firstSegment.StartsWith("vaultsync-restore-", comparison));
        }
        catch
        {
            return false;
        }
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

        DateTime now = DateTime.UtcNow;
        MetaInfo? metaInfo = store.GetMetaInfo();
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
            foreach (string externalId in missingExternalIds)
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

        DateTime now = DateTime.UtcNow;
        MetaInfo? metaInfo = store.GetMetaInfo();
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
            foreach (string externalId in missingExternalIds)
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

    public static void TryExportProjectTombstone(string rootPath, string projectExternalId, string? originMachineId = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(projectExternalId))
            return;

        var store = new MetadataStore(rootPath);
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Project tombstone export failed: store init error at '{rootPath}': {ex.Message}");
            return;
        }

        DateTime now = DateTime.UtcNow;
        string machineId = string.IsNullOrWhiteSpace(originMachineId) ? Environment.MachineName : originMachineId;
        MetaInfo? metaInfo = store.GetMetaInfo();
        if (metaInfo == null)
        {
            metaInfo = new MetaInfo
            {
                SchemaVersion = MetadataStore.CurrentSchemaVersion,
                CreatedUtc = now,
                LastWriteUtc = now,
                WriterAppVersion = "unknown",
                WriterMachineId = machineId
            };
        }
        else
        {
            metaInfo.LastWriteUtc = now;
            metaInfo.WriterMachineId = machineId;
        }

        try
        {
            store.UpsertMetaInfo(metaInfo);
            store.AddTombstone(new MetaTombstone
            {
                EntityType = "project",
                EntityId = projectExternalId,
                DeletedUtc = now,
                OriginMachineId = machineId
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Project tombstone export failed writing store '{rootPath}': {ex.Message}");
        }
    }

    public MetadataSyncResult ExportBackupToStore(string rootPath, int backupId, string appVersion, string machineId, bool forceBackfill = false)
    {
        return ExportBackupToStoreAsync(rootPath, backupId, appVersion, machineId, forceBackfill, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public MetadataSyncResult ExportProjectToStore(string rootPath, int projectId, string appVersion, string machineId)
    {
        return ExportProjectToStoreAsync(rootPath, projectId, appVersion, machineId, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public async Task<MetadataSyncResult> ExportProjectToStoreAsync(
        string rootPath,
        int projectId,
        string appVersion,
        string machineId,
        CancellationToken ct = default)
    {
        await MetadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            TimeSpan[] retryDelays =
            [
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5)
            ];

            for (int attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                try
                {
                    return ExportProjectToStoreInternal(rootPath, projectId, appVersion, machineId);
                }
                catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
                {
                    if (attempt >= retryDelays.Length)
                    {
                        Console.WriteLine($"[MetadataSync] Project export failed after retries: {ex.Message}");
                        return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
                    }

                    TimeSpan delay = retryDelays[attempt];
                    Console.WriteLine($"[MetadataSync] Project export store locked; retrying in {delay.TotalMilliseconds:0}ms.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, "Project export failed after retries.");
        }
        finally
        {
            MetadataIoGate.Release();
        }
    }

    private MetadataSyncResult ExportProjectToStoreInternal(string rootPath, int projectId, string appVersion, string machineId)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.WriteLine("[MetadataSync] Project export failed: root path is empty.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, "Root path is empty.");
        }

        TryFlushDeferredExport(rootPath);

        string storeRoot = rootPath;
        bool isDeferred = false;
        string destMetaDir = GetMetaDir(rootPath);
        if (!TryEnsureMetadataDirWritable(destMetaDir))
        {
            storeRoot = GetDeferredExportRoot(rootPath);
            isDeferred = true;
        }

        var store = new MetadataStore(storeRoot);
        Console.WriteLine($"[MetadataSync] Project export target store: '{store.DatabasePath}'.");
        try
        {
            store.EnsureSchema();
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Project export failed: store init error at '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        Project? project = _repo.GetProjectById(projectId);
        if (project == null)
        {
            Console.WriteLine($"[MetadataSync] Project export failed: project {projectId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Project not found.");
        }

        string projectExternalId = EnsureProjectExternalId(project);
        DateTime now = DateTime.UtcNow;
        MetaInfo? metaInfo = store.GetMetaInfo();
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
                SettingsJson = BuildProjectSettingsJson(project),
                UpdatedUtc = now
            });
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Project export failed writing store '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        var exportResult = new MetadataSyncResult(
            MetadataSyncStatus.Success,
            1,
            0,
            0,
            0,
            string.Empty);
        Console.WriteLine($"[MetadataSync] Project export complete for project '{project.Name}' to '{storeRoot}'.");
        LogStoreCounts(store);

        if (isDeferred)
        {
            if (TryFlushDeferredExport(rootPath))
                return exportResult;

            return MetadataSyncResult.Failure(
                MetadataSyncStatus.WriteFailed,
                "Project export queued: destination not writable. Will retry when available.");
        }

        return exportResult;
    }

    public async Task<MetadataSyncResult> ExportBackupToStoreAsync(
        string rootPath,
        int backupId,
        string appVersion,
        string machineId,
        bool forceBackfill = false,
        CancellationToken ct = default)
    {
        await MetadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            TimeSpan[] retryDelays =
            [
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5)
            ];

            for (int attempt = 0; attempt <= retryDelays.Length; attempt++)
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

                    TimeSpan delay = retryDelays[attempt];
                    Console.WriteLine($"[MetadataSync] Export store locked; retrying in {delay.TotalMilliseconds:0}ms.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
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

        string storeRoot = rootPath;
        bool isDeferred = false;
        string destMetaDir = GetMetaDir(rootPath);
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
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Export failed: store init error at '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        Backup? backup = _repo.GetBackupById(backupId);
        if (backup == null)
        {
            Console.WriteLine($"[MetadataSync] Export failed: backup {backupId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Backup not found.");
        }

        Project? project = _repo.GetProjectById(backup.ProjectId);
        if (project == null)
        {
            Console.WriteLine($"[MetadataSync] Export failed: project {backup.ProjectId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Project not found.");
        }

        Snapshot? snapshot = _repo.GetSnapshotById(backup.SnapshotId);
        if (snapshot == null)
        {
            Console.WriteLine($"[MetadataSync] Export failed: snapshot {backup.SnapshotId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Snapshot not found.");
        }

        string projectExternalId = EnsureProjectExternalId(project);
        string snapshotExternalId = EnsureSnapshotExternalId(snapshot);
        string backupExternalId = EnsureBackupExternalId(backup);

        DateTime now = DateTime.UtcNow;
        MetaInfo? metaInfo = store.GetMetaInfo();
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

        int exportedProjects = 0;
        int exportedSnapshots = 0;
        int exportedBackups = 0;
        bool backfilled = false;

        try
        {
            store.UpsertMetaInfo(metaInfo);
        if (forceBackfill || !store.HasProject(projectExternalId))
        {
            backfilled = true;
                (int snapshots, int backups) = ExportProjectHistory(store, project, projectExternalId, now, machineId);
            exportedProjects = 1;
            exportedSnapshots = snapshots;
            exportedBackups = backups;
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
                    TotalBytes = snapshot.TotalBytes,
                    DiffAdded = snapshot.DiffAdded,
                    DiffModified = snapshot.DiffModified,
                    DiffDeleted = snapshot.DiffDeleted,
                    DiffNetBytes = snapshot.DiffNetBytes,
                    DiffTopPathsJson = string.IsNullOrWhiteSpace(snapshot.DiffTopPathsJson) ? "[]" : snapshot.DiffTopPathsJson
                });
                var descriptor = BackupCryptoDescriptor.FromMetadata(backup.IsEncrypted, backup.CryptoDescriptorJson);
                store.UpsertBackup(new MetaBackup
                {
                    ExternalId = backupExternalId,
                    ProjectExternalId = projectExternalId,
                    SnapshotExternalId = snapshotExternalId,
                    CreatedUtc = backup.CreatedUtc,
                    Type = backup.Type,
                    BackupMode = BackupModes.Normalize(backup.BackupMode),
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
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
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

    public static void ExportBackupTombstoneToStore(string rootPath, string backupExternalId, string appVersion, string machineId)
    {
        ExportBackupTombstoneToStoreAsync(rootPath, backupExternalId, appVersion, machineId, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public static async Task ExportBackupTombstoneToStoreAsync(
        string rootPath,
        string backupExternalId,
        string appVersion,
        string machineId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(backupExternalId))
            return;

        await MetadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            TimeSpan[] retryDelays =
            [
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5)
            ];

            for (int attempt = 0; attempt <= retryDelays.Length; attempt++)
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
                        TryExportBackupTombstoneToDeferred(rootPath, backupExternalId, appVersion, machineId);
                        return;
                    }

                    TimeSpan delay = retryDelays[attempt];
                    Console.WriteLine($"[MetadataSync] Tombstone store locked; retrying in {delay.TotalMilliseconds:0}ms.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            MetadataIoGate.Release();
        }
    }

    private static void TryExportBackupTombstoneToDeferred(
        string rootPath,
        string backupExternalId,
        string appVersion,
        string machineId)
    {
        try
        {
            string deferredRoot = GetDeferredExportRoot(rootPath);
            var store = new MetadataStore(deferredRoot);
            store.EnsureSchema();

            DateTime now = DateTime.UtcNow;
            MetaInfo? metaInfo = store.GetMetaInfo();
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

            store.UpsertMetaInfo(metaInfo);
            store.AddTombstone(new MetaTombstone
            {
                EntityType = "backup",
                EntityId = backupExternalId,
                DeletedUtc = now,
                OriginMachineId = machineId
            });

            Console.WriteLine($"[MetadataSync] Tombstone export deferred locally for '{rootPath}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Tombstone defer failed for '{rootPath}': {ex.Message}");
        }
    }

    private static void ExportBackupTombstoneInternal(string rootPath, string backupExternalId, string appVersion, string machineId)
    {
        TryFlushDeferredExport(rootPath);
        string storeRoot = rootPath;
        bool isDeferred = false;
        string destMetaDir = GetMetaDir(rootPath);
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
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Tombstone export failed: store init error at '{rootPath}': {ex.Message}");
            return;
        }

        DateTime now = DateTime.UtcNow;
        MetaInfo? metaInfo = store.GetMetaInfo();
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
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
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
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootPath));
        return Path.Combine(Path.GetTempPath(), "vaultsync-meta-export", HashService.FormatHexLower(hash));
    }

    private static async Task WaitForNetworkReadyAsync(string rootPath, CancellationToken ct)
    {
        if (!IsLikelyNetworkPath(rootPath))
            return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(rootPath))
                return;

            await Task.Delay(200 * (attempt + 1), ct).ConfigureAwait(false);
        }
    }

    private static bool IsLikelyNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("//", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/run/media/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.Contains("/Library/Application Support/VaultSync/mounts/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool TryEnsureMetadataDirWritable(string metaDir)
    {
        try
        {
            string? rootDir = Directory.GetParent(Directory.GetParent(metaDir)?.FullName ?? string.Empty)?.FullName;
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
                return false;

            _ = Directory.CreateDirectory(metaDir);
            string probe = Path.Combine(metaDir, ".write_test");
            using var fs = new FileStream(probe, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            fs.WriteByte(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFlushDeferredExport(string rootPath)
    {
        string deferredRoot = GetDeferredExportRoot(rootPath);
        return TryCopyStoreFiles(deferredRoot, rootPath);
    }

    private static bool TryCopyStoreFiles(string fromRoot, string toRoot)
    {
        try
        {
            string sourceDir = GetMetaDir(fromRoot);
            if (!Directory.Exists(sourceDir))
                return false;

            if (!Directory.Exists(toRoot))
                return false;

            string destDir = GetMetaDir(toRoot);
            Directory.CreateDirectory(destDir);

            bool copied = false;
            foreach (string? suffix in new[] { "vaultsync.meta.db", "vaultsync.meta.db-wal", "vaultsync.meta.db-shm", "vaultsync.meta.db-journal" })
            {
                string src = Path.Combine(sourceDir, suffix);
                if (!File.Exists(src))
                    continue;

                string dst = Path.Combine(destDir, suffix);
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

        foreach (Snapshot? snap in snapshots)
        {
            string snapExternal = EnsureSnapshotExternalId(snap);
            snapshotExternalIds[snap.Id] = snapExternal;
            store.UpsertSnapshot(new MetaSnapshot
            {
                ExternalId = snapExternal,
                ProjectExternalId = projectExternalId,
                CreatedUtc = snap.CreatedUtc,
                FileCount = snap.FileCount,
                TotalBytes = snap.TotalBytes,
                DiffAdded = snap.DiffAdded,
                DiffModified = snap.DiffModified,
                DiffDeleted = snap.DiffDeleted,
                DiffNetBytes = snap.DiffNetBytes,
                DiffTopPathsJson = string.IsNullOrWhiteSpace(snap.DiffTopPathsJson) ? "[]" : snap.DiffTopPathsJson
            });
        }

        int exportedBackups = 0;
        int skippedBackups = 0;
        foreach (Backup? backup in backups)
        {
            if (!snapshotExternalIds.TryGetValue(backup.SnapshotId, out string? snapshotExternalId))
            {
                Snapshot? snap = _repo.GetSnapshotById(backup.SnapshotId);
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
                    TotalBytes = snap.TotalBytes,
                    DiffAdded = snap.DiffAdded,
                    DiffModified = snap.DiffModified,
                    DiffDeleted = snap.DiffDeleted,
                    DiffNetBytes = snap.DiffNetBytes,
                    DiffTopPathsJson = string.IsNullOrWhiteSpace(snap.DiffTopPathsJson) ? "[]" : snap.DiffTopPathsJson
                });
            }

            string backupExternalId = EnsureBackupExternalId(backup);
            var descriptor = BackupCryptoDescriptor.FromMetadata(backup.IsEncrypted, backup.CryptoDescriptorJson);
            store.UpsertBackup(new MetaBackup
            {
                ExternalId = backupExternalId,
                ProjectExternalId = projectExternalId,
                SnapshotExternalId = snapshotExternalId,
                CreatedUtc = backup.CreatedUtc,
                Type = backup.Type,
                BackupMode = BackupModes.Normalize(backup.BackupMode),
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
            int projects = store.ListProjects().Count();
            int snapshots = store.ListSnapshots().Count();
            int backups = store.ListBackups().Count();
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

        string id = NewExternalId();
        _repo.UpdateProjectExternalId(project.Id, id);
        return id;
    }

    private string EnsureSnapshotExternalId(Snapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ExternalId))
            return snapshot.ExternalId;

        string id = NewExternalId();
        _repo.UpdateSnapshotExternalId(snapshot.Id, id);
        return id;
    }

    private string EnsureBackupExternalId(Backup backup)
    {
        if (!string.IsNullOrWhiteSpace(backup.ExternalId))
            return backup.ExternalId;

        string id = NewExternalId();
        _repo.UpdateBackupExternalId(backup.Id, id);
        return id;
    }

    private static string NewExternalId() => Guid.NewGuid().ToString("N");

    private string BuildProjectSettingsJson(Project project)
    {
        try
        {
            string? color = ProjectColorResolver?.Invoke(project);
            var settings = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(color))
            {
                settings["avatarColor"] = color;
            }

            settings["encryptionPolicy"] = ProjectEncryptionPolicy.Normalize(project.EncryptionPolicy);
            settings["encryptionKeyRef"] = string.IsNullOrWhiteSpace(project.EncryptionKeyRef)
                ? null
                : project.EncryptionKeyRef;
            settings["preferredDestinationId"] = string.IsNullOrWhiteSpace(project.PreferredDestinationId)
                ? null
                : project.PreferredDestinationId;
            settings["restoreMode"] = ProjectRestoreMode.Normalize(project.RestoreMode);
            settings["verificationPolicy"] = ProjectVerificationPolicy.Normalize(project.VerificationPolicy);
            List<int> disabledProjects = _configStore.GetSnapshot().Backups.AutoBackupDisabledProjects ?? [];
            settings["autoBackupEnabled"] = !disabledProjects.Contains(project.Id);
            settings["tags"] = string.IsNullOrWhiteSpace(project.Tags)
                ? string.Empty
                : project.Tags.Trim();

            return settings.Count == 0 ? "{}" : JsonSerializer.Serialize(settings);
        }
        catch
        {
            return "{}";
        }
    }

    private readonly record struct ParsedProjectSettings(
        string EncryptionPolicy,
        string? EncryptionKeyRef,
        string PreferredDestinationId,
        string RestoreMode,
        string VerificationPolicy,
        bool AutoBackupEnabled,
        string Tags,
        bool HasEncryptionPolicy,
        bool HasEncryptionKeyRef,
        bool HasPreferredDestinationId,
        bool HasRestoreMode,
        bool HasVerificationPolicy,
        bool HasAutoBackupEnabled,
        bool HasTags);

    private ParsedProjectSettings ParseProjectSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new ParsedProjectSettings(
                ProjectEncryptionPolicy.Inherit,
                null,
                string.Empty,
                ProjectRestoreMode.Direct,
                ProjectVerificationPolicy.Always,
                true,
                string.Empty,
                HasEncryptionPolicy: false,
                HasEncryptionKeyRef: false,
                HasPreferredDestinationId: false,
                HasRestoreMode: false,
                HasVerificationPolicy: false,
                HasAutoBackupEnabled: false,
                HasTags: false);
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            string policy = ProjectEncryptionPolicy.Inherit;
            string? keyRef = null;
            string preferredDestinationId = string.Empty;
            string restoreMode = ProjectRestoreMode.Direct;
            string verificationPolicy = ProjectVerificationPolicy.Always;
            bool autoBackupEnabled = true;
            string tags = string.Empty;
            bool hasPolicy = false;
            bool hasKeyRef = false;
            bool hasPreferredDestinationId = false;
            bool hasRestoreMode = false;
            bool hasVerificationPolicy = false;
            bool hasAutoBackupEnabled = false;
            bool hasTags = false;

            if (doc.RootElement.TryGetProperty("encryptionPolicy", out JsonElement policyProp))
            {
                policy = ProjectEncryptionPolicy.Normalize(policyProp.GetString());
                hasPolicy = true;
            }

            if (doc.RootElement.TryGetProperty("encryptionKeyRef", out JsonElement keyRefProp))
            {
                string? rawKeyRef = keyRefProp.GetString();
                keyRef = string.IsNullOrWhiteSpace(rawKeyRef) ? null : rawKeyRef;
                hasKeyRef = true;
            }

            if (doc.RootElement.TryGetProperty("verificationPolicy", out JsonElement verificationProp))
            {
                verificationPolicy = ProjectVerificationPolicy.Normalize(verificationProp.GetString());
                hasVerificationPolicy = true;
            }

            if (doc.RootElement.TryGetProperty("preferredDestinationId", out JsonElement destinationProp))
            {
                preferredDestinationId = NormalizePreferredDestinationId(
                    destinationProp.GetString(),
                    _configStore.Load().Backups.Destinations);
                hasPreferredDestinationId = true;
            }

            if (doc.RootElement.TryGetProperty("restoreMode", out JsonElement restoreModeProp))
            {
                restoreMode = ProjectRestoreMode.Normalize(restoreModeProp.GetString());
                hasRestoreMode = true;
            }

            if (doc.RootElement.TryGetProperty("autoBackupEnabled", out JsonElement autoBackupEnabledProp))
            {
                autoBackupEnabled = autoBackupEnabledProp.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => autoBackupEnabled
                };
                hasAutoBackupEnabled = autoBackupEnabledProp.ValueKind is JsonValueKind.True or JsonValueKind.False;
            }

            if (doc.RootElement.TryGetProperty("tags", out JsonElement tagsProp))
            {
                string? rawTags = tagsProp.GetString();
                tags = string.IsNullOrWhiteSpace(rawTags) ? string.Empty : rawTags.Trim();
                hasTags = true;
            }

            return new ParsedProjectSettings(
                policy,
                keyRef,
                preferredDestinationId,
                restoreMode,
                verificationPolicy,
                autoBackupEnabled,
                tags,
                HasEncryptionPolicy: hasPolicy,
                HasEncryptionKeyRef: hasKeyRef,
                HasPreferredDestinationId: hasPreferredDestinationId,
                HasRestoreMode: hasRestoreMode,
                HasVerificationPolicy: hasVerificationPolicy,
                HasAutoBackupEnabled: hasAutoBackupEnabled,
                HasTags: hasTags);
        }
        catch
        {
            return new ParsedProjectSettings(
                ProjectEncryptionPolicy.Inherit,
                null,
                string.Empty,
                ProjectRestoreMode.Direct,
                ProjectVerificationPolicy.Always,
                true,
                string.Empty,
                HasEncryptionPolicy: false,
                HasEncryptionKeyRef: false,
                HasPreferredDestinationId: false,
                HasRestoreMode: false,
                HasVerificationPolicy: false,
                HasAutoBackupEnabled: false,
                HasTags: false);
        }
    }

    private bool ApplyImportedProjectSettings(
        int projectId,
        AppConfig config,
        MetaProject metaProject,
        string? sourceMachineId,
        ParsedProjectSettings parsedSettings,
        IList<ProjectMetadataConflictRecord> pendingConflicts)
    {
        if (!parsedSettings.HasEncryptionPolicy &&
            !parsedSettings.HasEncryptionKeyRef &&
            !parsedSettings.HasPreferredDestinationId &&
            !parsedSettings.HasRestoreMode &&
            !parsedSettings.HasVerificationPolicy &&
            !parsedSettings.HasAutoBackupEnabled &&
            !parsedSettings.HasTags)
        {
            return false;
        }

        Project? current = _repo.GetProjectById(projectId);
        if (current is null)
            return false;

        string currentPolicy = ProjectEncryptionPolicy.Normalize(current.EncryptionPolicy);
        string incomingPolicy = parsedSettings.HasEncryptionPolicy
            ? ProjectEncryptionPolicy.Normalize(parsedSettings.EncryptionPolicy)
            : currentPolicy;

        // Do not downgrade an explicit local policy to "inherit" from stale metadata.
        bool applyPolicy = parsedSettings.HasEncryptionPolicy
            && !string.Equals(incomingPolicy, currentPolicy, StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(incomingPolicy, ProjectEncryptionPolicy.Inherit, StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(currentPolicy, ProjectEncryptionPolicy.Inherit, StringComparison.OrdinalIgnoreCase));

        string nextPolicy = applyPolicy ? incomingPolicy : currentPolicy;
        string? nextKeyRef = parsedSettings.HasEncryptionKeyRef
            ? parsedSettings.EncryptionKeyRef
            : current.EncryptionKeyRef;
        string currentVerificationPolicy = ProjectVerificationPolicy.Normalize(current.VerificationPolicy);
        string nextVerificationPolicy = parsedSettings.HasVerificationPolicy
            ? ProjectVerificationPolicy.Normalize(parsedSettings.VerificationPolicy)
            : currentVerificationPolicy;
        List<BackupDestination> destinations = _configStore.Load().Backups.Destinations;
        string currentPreferredDestinationId = NormalizePreferredDestinationId(current.PreferredDestinationId, destinations);
        string nextPreferredDestinationId = parsedSettings.HasPreferredDestinationId
            ? NormalizePreferredDestinationId(parsedSettings.PreferredDestinationId, destinations)
            : currentPreferredDestinationId;
        string currentRestoreMode = ProjectRestoreMode.Normalize(current.RestoreMode);
        string nextRestoreMode = parsedSettings.HasRestoreMode
            ? ProjectRestoreMode.Normalize(parsedSettings.RestoreMode)
            : currentRestoreMode;
        config.Backups.AutoBackupDisabledProjects ??= [];
        bool currentAutoBackupEnabled = !config.Backups.AutoBackupDisabledProjects.Contains(projectId);
        bool nextAutoBackupEnabled = parsedSettings.HasAutoBackupEnabled
            ? parsedSettings.AutoBackupEnabled
            : currentAutoBackupEnabled;
        string currentTags = current.Tags?.Trim() ?? string.Empty;
        string nextTags = parsedSettings.HasTags
            ? (parsedSettings.Tags?.Trim() ?? string.Empty)
            : currentTags;

        string? currentKeyRef = string.IsNullOrWhiteSpace(current.EncryptionKeyRef) ? null : current.EncryptionKeyRef;
        string? normalizedNextKeyRef = string.IsNullOrWhiteSpace(nextKeyRef) ? null : nextKeyRef;
        if (string.Equals(nextPolicy, currentPolicy, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedNextKeyRef, currentKeyRef, StringComparison.Ordinal) &&
            string.Equals(nextVerificationPolicy, currentVerificationPolicy, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(nextPreferredDestinationId, currentPreferredDestinationId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(nextRestoreMode, currentRestoreMode, StringComparison.OrdinalIgnoreCase) &&
            nextAutoBackupEnabled == currentAutoBackupEnabled &&
            string.Equals(nextTags, currentTags, StringComparison.Ordinal))
        {
            return RemoveProjectMetadataConflict(projectId, pendingConflicts);
        }

        _repo.UpdateProjectEncryptionSettings(projectId, nextPolicy, normalizedNextKeyRef);

        bool conflictValuesDiffer =
            !string.Equals(nextPreferredDestinationId, currentPreferredDestinationId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextRestoreMode, currentRestoreMode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextVerificationPolicy, currentVerificationPolicy, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextTags, currentTags, StringComparison.Ordinal);

        if (!conflictValuesDiffer)
        {
            _repo.UpdateProjectPreferredDestination(projectId, nextPreferredDestinationId);
            _repo.UpdateProjectRestoreMode(projectId, nextRestoreMode);
            _repo.UpdateProjectVerificationPolicy(projectId, nextVerificationPolicy);
            _repo.UpdateProjectTags(projectId, nextTags);
            if (parsedSettings.HasAutoBackupEnabled)
            {
                ApplyImportedProjectAutoBackupSetting(config, projectId, nextAutoBackupEnabled);
            }
            return RemoveProjectMetadataConflict(projectId, pendingConflicts);
        }

        return UpsertProjectMetadataConflict(
            current,
            metaProject,
            sourceMachineId,
            currentPreferredDestinationId,
            currentRestoreMode,
            currentVerificationPolicy,
            currentTags,
            nextPreferredDestinationId,
            nextRestoreMode,
            nextVerificationPolicy,
            nextTags,
            pendingConflicts);
    }

    private static bool ApplyImportedProjectAutoBackupSetting(AppConfig config, int projectId, bool enabled)
    {
        config.Backups.AutoBackupDisabledProjects ??= [];
        List<int> disabled = config.Backups.AutoBackupDisabledProjects;

        if (enabled)
        {
            return disabled.Remove(projectId);
        }
        else if (!disabled.Contains(projectId))
        {
            disabled.Add(projectId);
            return true;
        }

        return false;
    }

    private static bool RemoveProjectMetadataConflict(int projectId, IList<ProjectMetadataConflictRecord> pendingConflicts)
    {
        ProjectMetadataConflictRecord? existing = pendingConflicts.FirstOrDefault(conflict => conflict.ProjectId == projectId);
        if (existing is null)
            return false;

        pendingConflicts.Remove(existing);
        return true;
    }

    private static bool UpsertProjectMetadataConflict(
        Project current,
        MetaProject metaProject,
        string? sourceMachineId,
        string currentPreferredDestinationId,
        string currentRestoreMode,
        string currentVerificationPolicy,
        string currentTags,
        string importedPreferredDestinationId,
        string importedRestoreMode,
        string importedVerificationPolicy,
        string importedTags,
        IList<ProjectMetadataConflictRecord> pendingConflicts)
    {
        var next = new ProjectMetadataConflictRecord
        {
            ProjectId = current.Id,
            ProjectExternalId = string.IsNullOrWhiteSpace(current.ExternalId) ? metaProject.ExternalId : current.ExternalId,
            ProjectName = current.Name,
            SourceMachineId = string.IsNullOrWhiteSpace(sourceMachineId) ? "unknown" : sourceMachineId,
            SourceUpdatedUtc = metaProject.UpdatedUtc == default
                ? string.Empty
                : metaProject.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Local = new ProjectMetadataConflictValues
            {
                PreferredDestinationId = currentPreferredDestinationId,
                RestoreMode = currentRestoreMode,
                VerificationPolicy = currentVerificationPolicy,
                Tags = currentTags
            },
            Imported = new ProjectMetadataConflictValues
            {
                PreferredDestinationId = importedPreferredDestinationId,
                RestoreMode = importedRestoreMode,
                VerificationPolicy = importedVerificationPolicy,
                Tags = importedTags
            }
        };

        ProjectMetadataConflictRecord? existing = pendingConflicts.FirstOrDefault(conflict =>
            conflict.ProjectId == current.Id ||
            (!string.IsNullOrWhiteSpace(conflict.ProjectExternalId) &&
             string.Equals(conflict.ProjectExternalId, next.ProjectExternalId, StringComparison.OrdinalIgnoreCase)));

        if (existing is null)
        {
            pendingConflicts.Add(next);
            return true;
        }

        if (ProjectMetadataConflictEquals(existing, next))
            return false;

        existing.ProjectId = next.ProjectId;
        existing.ProjectExternalId = next.ProjectExternalId;
        existing.ProjectName = next.ProjectName;
        existing.SourceMachineId = next.SourceMachineId;
        existing.SourceUpdatedUtc = next.SourceUpdatedUtc;
        existing.Local = next.Local;
        existing.Imported = next.Imported;
        return true;
    }

    private static bool ProjectMetadataConflictEquals(ProjectMetadataConflictRecord left, ProjectMetadataConflictRecord right)
    {
        return left.ProjectId == right.ProjectId &&
               string.Equals(left.ProjectExternalId, right.ProjectExternalId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ProjectName, right.ProjectName, StringComparison.Ordinal) &&
               string.Equals(left.SourceMachineId, right.SourceMachineId, StringComparison.Ordinal) &&
               string.Equals(left.SourceUpdatedUtc, right.SourceUpdatedUtc, StringComparison.Ordinal) &&
               ProjectMetadataConflictValuesEqual(left.Local, right.Local) &&
               ProjectMetadataConflictValuesEqual(left.Imported, right.Imported);
    }

    private static bool ProjectMetadataConflictValuesEqual(ProjectMetadataConflictValues left, ProjectMetadataConflictValues right)
    {
        return string.Equals(left.PreferredDestinationId, right.PreferredDestinationId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.RestoreMode, right.RestoreMode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.VerificationPolicy, right.VerificationPolicy, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Tags, right.Tags, StringComparison.Ordinal);
    }

    private static string NormalizePreferredDestinationId(string? preferredDestinationId, IReadOnlyCollection<BackupDestination> destinations)
        => DestinationIdentityService.NormalizePreferredDestinationId(preferredDestinationId, destinations);

    private static void TryApplyProjectColor(MetaProject metaProject)
    {
        if (ProjectColorApplier is null || string.IsNullOrWhiteSpace(metaProject.ExternalId))
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(metaProject.SettingsJson))
                return;

            using var doc = JsonDocument.Parse(metaProject.SettingsJson);
            if (!doc.RootElement.TryGetProperty("avatarColor", out JsonElement colorProp))
                return;

            string? color = colorProp.GetString();
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

public sealed record MetadataSyncOptions(
    bool AllowCreateProjects,
    bool MarkNeedsRestoreOnImport,
    bool ExportMissingTombstonesOnImport = true,
    bool SkipUnchangedReadOnlySource = false)
{
    public static MetadataSyncOptions Default => new(true, true);
    public MetadataSyncOptions AsReadOnlySource() => this with { ExportMissingTombstonesOnImport = false };
    public MetadataSyncOptions WithUnchangedSourceSkip() => this with { SkipUnchangedReadOnlySource = true };
}

public sealed record MetadataSyncResult(
    MetadataSyncStatus Status,
    int ImportedProjects,
    int ImportedSnapshots,
    int ImportedBackups,
    int AppliedTombstones,
    string Message)
{
    public IReadOnlyCollection<int> AffectedProjectIds { get; init; } = [];
    public int RepairedBackups { get; init; }

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
