using System;
using System.Buffers;
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

public sealed class MetadataSyncService
{
    private const string BackupEntityType = "backup";
    private const string InvalidRootPathMessage = "Root path is empty.";
    private const string VaultSyncDirectoryName = ".vaultsync";
    private const string UnknownAppVersion = "unknown";
    private const string AvatarColorField = "avatarColor";
    private const string EncryptionPolicyField = "encryptionPolicy";
    private const string PreferredDestinationIdField = "preferredDestinationId";
    private const string RestoreModeField = "restoreMode";
    private const string VerificationPolicyField = "verificationPolicy";
    private const string AutoBackupEnabledField = "autoBackupEnabled";
    private const string TagsField = "tags";
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly TimeSpan[] StoreRetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    private sealed record TombstoneExportContext(
        string RootPath,
        string EntityType,
        IReadOnlyCollection<string> ExternalIds,
        string MachineId,
        string LogLabel,
        string AppVersion,
        string LeaseOwnerId);

    private sealed record BackupExportEntities(Backup Backup, Project Project, Snapshot Snapshot);

    private sealed record BackupExportCounts(int Projects, int Snapshots, int Backups, bool Backfilled);

    private sealed record BackupExportWriteContext(
        string ProjectExternalId,
        string SnapshotExternalId,
        string BackupExternalId,
        DateTime Now,
        string MachineId,
        bool ForceBackfill,
        MetaProject ProjectRecord,
        long ExpectedProjectRevision);

    private sealed class ExportStoreContext(
        string storeRoot,
        RepositoryLeaseHandle? deferredLease,
        RepositoryLeaseHandle? activeLease,
        MetadataStore? store,
        MetadataSyncResult? failure) : IDisposable
    {
        public string StoreRoot { get; } = storeRoot;
        public RepositoryLeaseHandle? ActiveLease { get; } = activeLease;
        public MetadataStore? Store { get; } = store;
        public MetadataSyncResult? Failure { get; } = failure;

        public static ExportStoreContext Failed(string storeRoot, MetadataSyncResult failure) =>
            new(storeRoot, null, null, null, failure);

        public void Dispose() => deferredLease?.Dispose();
    }

    private sealed record GuardedProjectWrite(
        MetaProject Record,
        long ExpectedRevision,
        ProjectMetadataConflictValues Values);

    private sealed record GuardedProjectWriteRequest(
        string DestinationRoot,
        Project Project,
        string ProjectExternalId,
        DateTime UpdatedUtc,
        string MachineId);

    private sealed class MetadataRevisionConflictException(string message) : InvalidOperationException(message);

    private sealed record LegacyPreviewContext(
        string RootPath,
        bool AllowCreateProjects,
        LegacyPreviewIndexes Indexes,
        LegacyPreviewSeen Seen);

    private sealed record LegacyPreviewIndexes(
        IReadOnlySet<string> ProjectsByName,
        IReadOnlyDictionary<string, int> SnapshotExternalMap,
        IReadOnlyDictionary<string, int> BackupExternalMap,
        IReadOnlySet<string> ExistingBackupPaths);

    private sealed record LegacyPreviewSeen(
        ISet<string> Projects,
        ISet<string> Snapshots,
        ISet<string> Backups);

    private sealed record PreviewProjectCounts(int Add, int Link);

    private sealed record PreviewTombstoneAnalysis(
        HashSet<string> BackupIds,
        HashSet<string> SnapshotIds,
        int DeleteProjects,
        int DeleteSnapshots,
        int DeleteBackups);

    private sealed record PreviewBackupAnalysis(HashSet<string> LiveSnapshotIds, int Add, int Delete);

    private sealed record ProjectMetadataConflictContext(
        Project Current,
        MetaProject Imported,
        string SourceKey,
        string? SourceMachineId,
        long BaseRevision,
        string BaseMachineId,
        string BaseUpdatedUtc,
        string LocalMachineId,
        string DetectedUtc,
        ProjectMetadataConflictValues Base,
        ProjectMetadataConflictValues Local,
        ProjectMetadataConflictValues Incoming,
        ProjectMetadataMergePlan Plan);

    private sealed class LegacyImportState
    {
        public required Dictionary<string, Project> ProjectsByName { get; init; }
        public required IReadOnlyDictionary<string, int> SnapshotExternalMap { get; init; }
        public required IReadOnlyDictionary<string, int> BackupExternalMap { get; init; }
        public required Dictionary<string, Backup> ExistingBackupByPath { get; init; }
        public HashSet<int> AffectedProjectIds { get; } = [];
        public int ImportedProjects { get; set; }
        public int ImportedSnapshots { get; set; }
        public int ImportedBackups { get; set; }
        public int RepairedBackups { get; set; }
    }

    private readonly SqliteRepository _repo;
    private readonly IAppConfigStore _configStore;
    private readonly IInstallationIdentityProvider? _installationIdentityProvider;
    private readonly RepositoryLeaseService _repositoryLeaseService;
    private readonly Func<Project, string?>? _projectColorResolver;
    private readonly Action<string, string>? _projectColorApplier;
    private readonly Action<string>? _operationCheckpoint;
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, MetadataSyncPreview Preview)> _previewCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MetadataIoGates =
        new(GetPathComparer());

    public MetadataSyncService(
        SqliteRepository repo,
        IAppConfigStore? configStore = null,
        Func<Project, string?>? projectColorResolver = null,
        Action<string, string>? projectColorApplier = null,
        IInstallationIdentityProvider? installationIdentityProvider = null,
        RepositoryLeaseService? repositoryLeaseService = null)
        : this(
            repo,
            configStore,
            projectColorResolver,
            projectColorApplier,
            installationIdentityProvider,
            repositoryLeaseService,
            operationCheckpoint: null)
    {
    }

    internal MetadataSyncService(
        SqliteRepository repo,
        IAppConfigStore? configStore,
        Func<Project, string?>? projectColorResolver,
        Action<string, string>? projectColorApplier,
        IInstallationIdentityProvider? installationIdentityProvider,
        RepositoryLeaseService? repositoryLeaseService,
        Action<string>? operationCheckpoint)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _configStore = configStore ?? StaticAppConfigStore.Instance;
        _projectColorResolver = projectColorResolver;
        _projectColorApplier = projectColorApplier;
        _installationIdentityProvider = installationIdentityProvider;
        _repositoryLeaseService = repositoryLeaseService ?? new RepositoryLeaseService();
        _operationCheckpoint = operationCheckpoint;
    }

    public MetadataSyncResult ImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        return ImportFromStoreAsync(rootPath, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<MetadataSyncResult> ImportFromStoreAsync(string rootPath, MetadataSyncOptions? options = null, CancellationToken ct = default)
    {
        using var totalTiming = RuntimeTiming.Measure("Metadata import total");
        SemaphoreSlim metadataIoGate = GetMetadataIoGate(rootPath);
        await metadataIoGate.WaitAsync(ct).ConfigureAwait(false);
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
                return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, InvalidRootPathMessage);
            }

            RepositoryLeaseInspection leaseInspection = _repositoryLeaseService.Inspect(rootPath);
            if (leaseInspection.State is RepositoryLeaseState.Active or
                RepositoryLeaseState.Stale or
                RepositoryLeaseState.Invalid or
                RepositoryLeaseState.Unavailable)
            {
                opts = opts.AsReadOnlySource();
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
            metadataIoGate.Release();
        }
    }

    public MetadataSyncPreview PreviewImportFromStore(string rootPath, MetadataSyncOptions? options = null)
    {
        return PreviewImportFromStoreAsync(rootPath, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<MetadataSyncPreview> PreviewImportFromStoreAsync(string rootPath, MetadataSyncOptions? options = null, CancellationToken ct = default)
    {
        SemaphoreSlim metadataIoGate = GetMetadataIoGate(rootPath);
        await metadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            MetadataSyncOptions opts = options ?? MetadataSyncOptions.Default;

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                Console.WriteLine("[MetadataSync] Preview failed: root path is empty.");
                return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidPath, rootPath, string.Empty, InvalidRootPathMessage);
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
            metadataIoGate.Release();
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
        string sourceKey = BuildMetadataSourceKey(
            Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
            if (!opts.ApplyDestructiveTombstones)
                continue;

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

            if (projectMap.TryGetValue(metaProject.ExternalId, out int mappedProjectId))
            {
                metadataConflictChanged |= ApplyImportedProjectSettings(
                    mappedProjectId,
                    config,
                    metaProject,
                    sourceKey,
                    ResolveProjectWriterMachineId(metaProject, metaInfo),
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
                    sourceKey,
                    ResolveProjectWriterMachineId(metaProject, metaInfo),
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
                // Encryption key references are installation-local secrets. A
                // repository may describe the policy, but never selects a key
                // that may not exist on this machine.
                EncryptionKeyRef = null,
                VerificationPolicy = parsedSettings.HasVerificationPolicy
                    ? parsedSettings.VerificationPolicy
                    : ProjectVerificationPolicy.Always,
                PreferredDestinationId = parsedSettings.HasPreferredDestinationId
                    ? NormalizeImportedPreferredDestinationId(parsedSettings.PreferredDestinationId, config.Backups.Destinations)
                    : string.Empty,
                RestoreMode = parsedSettings.HasRestoreMode
                    ? parsedSettings.RestoreMode
                    : ProjectRestoreMode.Direct,
                Tags = parsedSettings.HasTags
                    ? parsedSettings.Tags
                    : string.Empty
            };

            int newId = _repo.AddProject(project);
            if (parsedSettings.HasAvatarColor)
                TryApplyProjectColor(metaProject.ExternalId, parsedSettings.AvatarColor);
            if (parsedSettings.HasAutoBackupEnabled)
                metadataConflictChanged |= ApplyImportedProjectAutoBackupSetting(config, newId, parsedSettings.AutoBackupEnabled);
            metadataConflictChanged |= UpsertProjectMetadataMergeBase(
                config,
                sourceKey,
                metaProject,
                ResolveProjectWriterMachineId(metaProject, metaInfo),
                BuildImportedValues(project, parsedSettings, config, newId));
            projectMap[metaProject.ExternalId] = newId;
            importedProjects++;
        }

        IReadOnlyDictionary<string, int> snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        foreach (KeyValuePair<string, int> pair in snapshotExternalMap)
        {
            snapshotMap[pair.Key] = pair.Value;
        }

        var tombstonedBackupIds = metaTombstones
            .Where(t => string.Equals(t.EntityType, BackupEntityType, StringComparison.OrdinalIgnoreCase))
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

        if (opts.ApplyDestructiveTombstones)
        {
            foreach (MetaTombstone tombstone in metaTombstones)
            {
                if (string.IsNullOrWhiteSpace(tombstone.EntityId))
                    continue;

                if (string.Equals(tombstone.EntityType, BackupEntityType, StringComparison.OrdinalIgnoreCase) &&
                    backupExternalMap.TryGetValue(tombstone.EntityId, out int existingId))
                {
                    _repo.DeleteBackupById(existingId);
                    appliedTombstones++;
                }
            }
        }

        if (missingBackupExternalIds.Count > 0 && opts.ApplyDestructiveTombstones)
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

        if (missingSnapshotExternalIds.Count > 0 && opts.ApplyDestructiveTombstones)
        {
            var removedSnapshotExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                (int snapshots, _) = _repo.DeleteSnapshotsById(project.Name, [snapshot.Id]);
                if (snapshots > 0)
                    removedSnapshotExternalIds.Add(missingExternalId);
            }

            if (removedSnapshotExternalIds.Count > 0 && opts.ExportMissingTombstonesOnImport)
            {
                TryExportMissingSnapshotTombstones(rootPath, removedSnapshotExternalIds);
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
            var stack = new Stack<string>([rootPath]);

            while (stack.Count > 0)
            {
                string current = stack.Pop();
                foreach (string directory in GetTraversableDirectories(current))
                    stack.Push(directory);

                if (ContainsFileNewerThan(current, importedLatestUtc))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static List<string> GetTraversableDirectories(string path)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(path);
        }
        catch
        {
            return [];
        }

        return directories.Where(IsTraversableDirectory).ToList();
    }

    private static bool IsTraversableDirectory(string path)
    {
        if (string.Equals(Path.GetFileName(path), VaultSyncDirectoryName, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsFileNewerThan(string path, DateTime timestampUtc)
    {
        try
        {
            return Directory.EnumerateFiles(path).Any(file => IsFileNewerThan(file, timestampUtc));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileNewerThan(string path, DateTime timestampUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) > timestampUtc;
        }
        catch
        {
            return false;
        }
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

        var projectMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var localProjects = _repo.GetAllProjects().ToList();
        IReadOnlyDictionary<string, int> projectExternalMap = _repo.GetProjectExternalIdMap();
        foreach (KeyValuePair<string, int> pair in projectExternalMap)
        {
            projectMap[pair.Key] = pair.Value;
        }

        IReadOnlyList<MetaProject> metaProjects;
        IReadOnlyList<MetaSnapshot> metaSnapshots;
        IReadOnlyList<MetaBackup> metaBackups;
        IReadOnlyList<MetaTombstone> metaTombstones;

        try
        {
            metaProjects = [.. store.ListProjects()];
            metaSnapshots = [.. store.ListSnapshots()];
            metaBackups = [.. store.ListBackups()];
            metaTombstones = [.. store.ListTombstones()];
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Preview failed while reading store '{rootPath}': {ex.Message}");
            return MetadataSyncPreview.Failure(MetadataSyncStatus.InvalidStore, rootPath, store.DatabasePath, ex.Message);
        }

        PreviewProjectCounts projectCounts = CountPreviewProjects(
            metaProjects,
            projectMap,
            localProjects,
            opts.AllowCreateProjects);

        IReadOnlyDictionary<string, int> snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        IReadOnlyDictionary<string, int> backupExternalMap = _repo.GetBackupExternalIdMap();
        PreviewTombstoneAnalysis tombstones = AnalyzePreviewTombstones(
            metaTombstones,
            projectExternalMap,
            snapshotExternalMap,
            backupExternalMap);
        PreviewBackupAnalysis backups = AnalyzePreviewBackups(
            metaBackups,
            rootPath,
            projectMap,
            backupExternalMap,
            tombstones.BackupIds);
        int addSnapshots = CountPreviewSnapshots(
            metaSnapshots,
            projectMap,
            snapshotExternalMap,
            tombstones.SnapshotIds,
            backups.LiveSnapshotIds);

        HashSet<string> metadataProjectNames = metaProjects
            .Where(project => !string.IsNullOrWhiteSpace(project.Name))
            .Select(project => project.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> metadataBackupPaths = metaBackups
            .Where(backup =>
                !string.IsNullOrWhiteSpace(backup.PathRel) &&
                !tombstones.BackupIds.Contains(backup.ExternalId) &&
                TryResolveBackupPath(rootPath, backup.PathRel, out _))
            .Select(backup => NormalizeStablePath(NormalizeBackupPathRel(backup.PathRel)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        MetadataSyncPreview filesystemPreview = PreviewBackupFoldersFromDestination(
            rootPath,
            opts,
            store.DatabasePath,
            metadataProjectNames,
            metadataBackupPaths);
        int addProjects = projectCounts.Add + filesystemPreview.NewProjects;
        addSnapshots += filesystemPreview.NewSnapshots;
        int addBackups = backups.Add + filesystemPreview.NewBackups;

        var preview = new MetadataSyncPreview(
            MetadataSyncStatus.Success,
            rootPath,
            store.DatabasePath,
            addProjects,
            projectCounts.Link,
            addSnapshots,
            addBackups,
            tombstones.DeleteBackups + backups.Delete,
            string.Empty)
        {
            DeletedProjects = tombstones.DeleteProjects,
            DeletedSnapshots = tombstones.DeleteSnapshots
        };

        if (metaInfo != null)
        {
            _previewCache[rootPath] = (metaInfo.LastWriteUtc, preview);
        }

        return preview;
    }

    private static PreviewProjectCounts CountPreviewProjects(
        IEnumerable<MetaProject> projects,
        Dictionary<string, int> projectMap,
        IReadOnlyCollection<Project> localProjects,
        bool allowCreateProjects)
    {
        int add = 0;
        int link = 0;
        foreach (MetaProject project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.ExternalId) || projectMap.ContainsKey(project.ExternalId))
                continue;

            Project? local = localProjects.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, project.Name, StringComparison.OrdinalIgnoreCase));
            if (local is not null)
            {
                if (string.IsNullOrWhiteSpace(local.ExternalId))
                    link++;
                projectMap[project.ExternalId] = local.Id;
            }
            else if (allowCreateProjects)
            {
                add++;
                projectMap[project.ExternalId] = -1;
            }
        }

        return new PreviewProjectCounts(add, link);
    }

    private static PreviewTombstoneAnalysis AnalyzePreviewTombstones(
        IEnumerable<MetaTombstone> tombstones,
        IReadOnlyDictionary<string, int> projectExternalMap,
        IReadOnlyDictionary<string, int> snapshotExternalMap,
        IReadOnlyDictionary<string, int> backupExternalMap)
    {
        var backupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int deleteProjects = 0;
        int deleteSnapshots = 0;
        int deleteBackups = 0;
        foreach (MetaTombstone tombstone in tombstones.Where(tombstone => !string.IsNullOrWhiteSpace(tombstone.EntityId)))
        {
            if (string.Equals(tombstone.EntityType, "project", StringComparison.OrdinalIgnoreCase))
            {
                deleteProjects += projectExternalMap.ContainsKey(tombstone.EntityId) ? 1 : 0;
            }
            else if (string.Equals(tombstone.EntityType, BackupEntityType, StringComparison.OrdinalIgnoreCase))
            {
                backupIds.Add(tombstone.EntityId);
                deleteBackups += backupExternalMap.ContainsKey(tombstone.EntityId) ? 1 : 0;
            }
            else if (string.Equals(tombstone.EntityType, "snapshot", StringComparison.OrdinalIgnoreCase))
            {
                snapshotIds.Add(tombstone.EntityId);
                deleteSnapshots += snapshotExternalMap.ContainsKey(tombstone.EntityId) ? 1 : 0;
            }
        }

        return new PreviewTombstoneAnalysis(backupIds, snapshotIds, deleteProjects, deleteSnapshots, deleteBackups);
    }

    private static PreviewBackupAnalysis AnalyzePreviewBackups(
        IEnumerable<MetaBackup> backups,
        string rootPath,
        Dictionary<string, int> projectMap,
        IReadOnlyDictionary<string, int> backupExternalMap,
        HashSet<string> tombstonedBackupIds)
    {
        var liveSnapshotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int add = 0;
        int delete = 0;
        foreach (MetaBackup backup in backups)
        {
            (int backupAdd, int backupDelete) = AnalyzePreviewBackup(
                backup,
                rootPath,
                projectMap,
                backupExternalMap,
                tombstonedBackupIds,
                liveSnapshotIds);
            add += backupAdd;
            delete += backupDelete;
        }

        return new PreviewBackupAnalysis(liveSnapshotIds, add, delete);
    }

    private static (int Add, int Delete) AnalyzePreviewBackup(
        MetaBackup backup,
        string rootPath,
        Dictionary<string, int> projectMap,
        IReadOnlyDictionary<string, int> backupExternalMap,
        HashSet<string> tombstonedBackupIds,
        HashSet<string> liveSnapshotIds)
    {
        if (string.IsNullOrWhiteSpace(backup.ExternalId))
            return default;

        bool isTombstoned = tombstonedBackupIds.Contains(backup.ExternalId);
        if (!string.IsNullOrWhiteSpace(backup.SnapshotExternalId) && !isTombstoned)
            liveSnapshotIds.Add(backup.SnapshotExternalId);
        if (isTombstoned)
            return default;
        if (!TryResolveBackupPath(rootPath, backup.PathRel, out _))
        {
            tombstonedBackupIds.Add(backup.ExternalId);
            return (0, backupExternalMap.ContainsKey(backup.ExternalId) ? 1 : 0);
        }

        bool isNew = projectMap.ContainsKey(backup.ProjectExternalId) &&
                     !backupExternalMap.ContainsKey(backup.ExternalId);
        return (isNew ? 1 : 0, 0);
    }

    private static int CountPreviewSnapshots(
        IEnumerable<MetaSnapshot> snapshots,
        Dictionary<string, int> projectMap,
        IReadOnlyDictionary<string, int> snapshotExternalMap,
        HashSet<string> tombstonedSnapshotIds,
        HashSet<string> liveSnapshotIds)
    {
        int add = 0;
        foreach (MetaSnapshot snapshot in snapshots.Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.ExternalId)))
        {
            if (!liveSnapshotIds.Contains(snapshot.ExternalId))
            {
                tombstonedSnapshotIds.Add(snapshot.ExternalId);
                continue;
            }
            if (!tombstonedSnapshotIds.Contains(snapshot.ExternalId) &&
                projectMap.ContainsKey(snapshot.ProjectExternalId) &&
                !snapshotExternalMap.ContainsKey(snapshot.ExternalId))
            {
                add++;
            }
        }

        return add;
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

        var state = new LegacyImportState
        {
            ProjectsByName = projectsByName,
            SnapshotExternalMap = snapshotExternalMap,
            BackupExternalMap = backupExternalMap,
            ExistingBackupByPath = existingBackupByPath
        };
        foreach (IGrouping<string, LegacyBackupFolder> projectGroup in discovered.GroupBy(folder => folder.ProjectName, StringComparer.OrdinalIgnoreCase))
            ImportLegacyProjectGroup(projectGroup, rootPath, opts, config, state);

        return new MetadataSyncResult(
            MetadataSyncStatus.Success,
            state.ImportedProjects,
            state.ImportedSnapshots,
            state.ImportedBackups,
            0,
            string.Empty)
        {
            AffectedProjectIds = [.. state.AffectedProjectIds],
            RepairedBackups = state.RepairedBackups
        };
    }

    private void ImportLegacyProjectGroup(
        IGrouping<string, LegacyBackupFolder> projectGroup,
        string rootPath,
        MetadataSyncOptions options,
        AppConfig config,
        LegacyImportState state)
    {
        List<LegacyBackupFolder> importableFolders = [.. projectGroup
            .Where(folder => !state.ExistingBackupByPath.ContainsKey(NormalizeStablePath(folder.RelativePath)))
            .OrderBy(folder => folder.CreatedUtc)];
        List<LegacyBackupFolder> repairableFolders = [.. projectGroup
            .Where(folder => IsRepairableLegacyBackup(folder, state.ExistingBackupByPath))
            .OrderBy(folder => folder.CreatedUtc)];
        if (importableFolders.Count == 0 && repairableFolders.Count == 0)
            return;

        Project? project = ResolveLegacyProject(projectGroup, rootPath, options, config, state);
        if (project is null)
            return;

        RepairLegacyBackups(repairableFolders, rootPath, state);
        ImportLegacyBackups(importableFolders, rootPath, project, state);
        if (options.MarkNeedsRestoreOnImport && state.AffectedProjectIds.Contains(project.Id))
            _repo.UpdateProjectNeedsRestore(project.Id, true);
    }

    private static bool IsRepairableLegacyBackup(
        LegacyBackupFolder folder,
        Dictionary<string, Backup> existingBackupByPath) =>
        existingBackupByPath.TryGetValue(NormalizeStablePath(folder.RelativePath), out Backup? backup) &&
        backup.IsImported &&
        backup.TotalBytes <= 0;

    private Project? ResolveLegacyProject(
        IGrouping<string, LegacyBackupFolder> projectGroup,
        string rootPath,
        MetadataSyncOptions options,
        AppConfig config,
        LegacyImportState state)
    {
        string projectName = projectGroup.Key;
        string externalId = BuildStableExternalId("legacy-project", rootPath, projectName);
        if (!state.ProjectsByName.TryGetValue(projectName, out Project? project))
            return options.AllowCreateProjects
                ? CreateLegacyProject(projectGroup, config, externalId, state)
                : null;

        if (string.IsNullOrWhiteSpace(project.ExternalId))
        {
            _repo.UpdateProjectExternalId(project.Id, externalId);
            project = project with { ExternalId = externalId };
            state.ProjectsByName[projectName] = project;
        }

        return project;
    }

    private Project? CreateLegacyProject(
        IGrouping<string, LegacyBackupFolder> projectGroup,
        AppConfig config,
        string externalId,
        LegacyImportState state)
    {
        string projectRoot = ResolveImportedProjectRoot(null, config.ProjectsRoot, projectGroup.Key, externalId);
        int projectId = _repo.AddProject(new Project
        {
            ExternalId = externalId,
            Name = projectGroup.Key,
            RootPath = projectRoot,
            Preset = "generic",
            CreatedUtc = projectGroup.Min(folder => folder.CreatedUtc),
            NeedsRestore = false
        });
        Project? project = _repo.GetProjectById(projectId);
        if (project is null)
            return null;

        state.ProjectsByName[projectGroup.Key] = project;
        state.ImportedProjects++;
        return project;
    }

    private void RepairLegacyBackups(
        IEnumerable<LegacyBackupFolder> folders,
        string rootPath,
        LegacyImportState state)
    {
        foreach (string relativePath in folders.Select(folder => folder.RelativePath))
        {
            string path = NormalizeStablePath(relativePath);
            if (!state.ExistingBackupByPath.TryGetValue(path, out Backup? backup))
                continue;

            long sizeBytes = GetLegacyBackupFolderSize(rootPath, relativePath);
            if (sizeBytes <= 0)
                continue;

            _repo.UpdateBackupTotalBytes(backup.Id, sizeBytes);
            Snapshot? snapshot = _repo.GetSnapshotById(backup.SnapshotId);
            if (snapshot is not null && snapshot.TotalBytes <= 0)
                _repo.UpdateSnapshotTotalBytes(snapshot.Id, sizeBytes);

            state.RepairedBackups++;
            state.AffectedProjectIds.Add(backup.ProjectId);
        }
    }

    private void ImportLegacyBackups(
        IEnumerable<LegacyBackupFolder> folders,
        string rootPath,
        Project project,
        LegacyImportState state)
    {
        foreach (LegacyBackupFolder folder in folders)
            ImportLegacyBackup(folder, rootPath, project, state);
    }

    private void ImportLegacyBackup(
        LegacyBackupFolder folder,
        string rootPath,
        Project project,
        LegacyImportState state)
    {
        string normalizedPath = NormalizeStablePath(folder.RelativePath);
        string snapshotExternalId = BuildStableExternalId("legacy-snapshot", rootPath, folder.RelativePath);
        string backupExternalId = BuildStableExternalId("legacy-backup", rootPath, folder.RelativePath);
        long sizeBytes = GetLegacyBackupFolderSize(rootPath, folder.RelativePath);
        int snapshotId = ResolveLegacySnapshot(snapshotExternalId, project.Id, folder, sizeBytes, state);
        if (state.BackupExternalMap.ContainsKey(backupExternalId))
            return;

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
        state.ImportedBackups++;
        state.AffectedProjectIds.Add(project.Id);
        state.ExistingBackupByPath[normalizedPath] = _repo.GetBackupByExternalId(backupExternalId)
            ?? new Backup { Id = 0, ProjectId = project.Id, SnapshotId = snapshotId, Path = folder.RelativePath, TotalBytes = sizeBytes, IsImported = true };
    }

    private int ResolveLegacySnapshot(
        string externalId,
        int projectId,
        LegacyBackupFolder folder,
        long sizeBytes,
        LegacyImportState state)
    {
        if (state.SnapshotExternalMap.TryGetValue(externalId, out int snapshotId))
            return snapshotId;

        state.ImportedSnapshots++;
        return _repo.CreateSnapshotFromMetadata(
            externalId,
            projectId,
            folder.CreatedUtc,
            fileCount: 0,
            totalBytes: sizeBytes);
    }

    private MetadataSyncPreview PreviewBackupFoldersFromDestination(
        string rootPath,
        MetadataSyncOptions opts,
        string databasePath,
        IReadOnlySet<string>? representedProjectNames = null,
        IReadOnlySet<string>? representedBackupPaths = null)
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
        if (representedProjectNames is not null)
            projectsByName.UnionWith(representedProjectNames);
        IReadOnlyDictionary<string, int> snapshotExternalMap = _repo.GetSnapshotExternalIdMap();
        IReadOnlyDictionary<string, int> backupExternalMap = _repo.GetBackupExternalIdMap();
        var existingBackupPaths = _repo
            .GetAllProjects()
            .SelectMany(project => _repo.GetBackupsForProject(project.Id))
            .Select(backup => NormalizeStablePath(NormalizeBackupPathRel(backup.Path)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (representedBackupPaths is not null)
            existingBackupPaths.UnionWith(representedBackupPaths);

        int addProjects = 0;
        int addSnapshots = 0;
        int addBackups = 0;
        var previewedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewedSnapshots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewedBackups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewContext = new LegacyPreviewContext(
            rootPath,
            opts.AllowCreateProjects,
            new LegacyPreviewIndexes(projectsByName, snapshotExternalMap, backupExternalMap, existingBackupPaths),
            new LegacyPreviewSeen(previewedProjects, previewedSnapshots, previewedBackups));

        foreach (LegacyBackupFolder folder in discovered)
        {
            (int projects, int snapshots, int backups) = CountLegacyFolderPreview(folder, previewContext);
            addProjects += projects;
            addSnapshots += snapshots;
            addBackups += backups;
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

    private static (int Projects, int Snapshots, int Backups) CountLegacyFolderPreview(
        LegacyBackupFolder folder,
        LegacyPreviewContext context)
    {
        if (context.Indexes.ExistingBackupPaths.Contains(NormalizeStablePath(folder.RelativePath)))
            return default;

        bool projectExists = context.Indexes.ProjectsByName.Contains(folder.ProjectName);
        if (!projectExists && !context.AllowCreateProjects)
            return default;

        int projects = !projectExists && context.Seen.Projects.Add(folder.ProjectName) ? 1 : 0;
        string snapshotExternalId = BuildStableExternalId("legacy-snapshot", context.RootPath, folder.RelativePath);
        int snapshots = !context.Indexes.SnapshotExternalMap.ContainsKey(snapshotExternalId) && context.Seen.Snapshots.Add(snapshotExternalId) ? 1 : 0;
        string backupExternalId = BuildStableExternalId("legacy-backup", context.RootPath, folder.RelativePath);
        int backups = !context.Indexes.BackupExternalMap.ContainsKey(backupExternalId) && context.Seen.Backups.Add(backupExternalId) ? 1 : 0;
        return (projects, snapshots, backups);
    }

    private static IReadOnlyList<LegacyBackupFolder> DiscoverLegacyBackupFolders(string rootPath)
    {
        var result = new List<LegacyBackupFolder>();

        IEnumerable<string> projectDirs;
        try
        {
            projectDirs = Directory.EnumerateDirectories(rootPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Legacy backup root could not be enumerated: {ex.Message}");
            return result;
        }

        foreach (string projectDir in projectDirs)
        {
            string projectName = Path.GetFileName(projectDir);
            if (string.IsNullOrWhiteSpace(projectName) ||
                string.Equals(projectName, VaultSyncDirectoryName, StringComparison.OrdinalIgnoreCase))
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
            string tempDir = Path.Combine(root, VaultSyncDirectoryName, "meta");
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
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Existing project root could not be inspected: {ex.Message}");
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

            char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
            var firstSegment = relative
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
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

    private void TryExportMissingBackupTombstones(string rootPath, IReadOnlyCollection<string> missingExternalIds)
    {
        string machineId = Environment.MachineName;
        TryExportTombstonesCore(
            new TombstoneExportContext(
                rootPath,
                BackupEntityType,
                missingExternalIds,
                machineId,
                "Missing backup tombstone export",
                UnknownAppVersion,
                ResolveLeaseOwnerId(machineId)),
            _repositoryLeaseService);
    }

    private void TryExportMissingSnapshotTombstones(string rootPath, IReadOnlyCollection<string> missingExternalIds)
    {
        string machineId = Environment.MachineName;
        TryExportTombstonesCore(
            new TombstoneExportContext(
                rootPath,
                "snapshot",
                missingExternalIds,
                machineId,
                "Missing snapshot tombstone export",
                UnknownAppVersion,
                ResolveLeaseOwnerId(machineId)),
            _repositoryLeaseService);
    }

    public static void TryExportProjectTombstone(
        string rootPath,
        string projectExternalId,
        string? originMachineId = null,
        string? leaseOwnerId = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(projectExternalId))
            return;

        string machineId = string.IsNullOrWhiteSpace(originMachineId) ? Environment.MachineName : originMachineId;
        var leaseService = new RepositoryLeaseService();
        TryExportTombstonesCore(
            new TombstoneExportContext(
                rootPath,
                "project",
                [projectExternalId],
                machineId,
                "Project tombstone export",
                UnknownAppVersion,
                leaseOwnerId ?? CreateCompatibilityInstallationId(machineId)),
            leaseService);
    }

    private static void TryExportTombstonesCore(
        TombstoneExportContext context,
        RepositoryLeaseService leaseService)
    {
        if (string.IsNullOrWhiteSpace(context.RootPath) || context.ExternalIds.Count == 0)
            return;

        RepositoryLeaseAcquireResult leaseResult = leaseService.TryAcquire(
            context.RootPath,
            CreateLeaseRequest(context.LeaseOwnerId, context.MachineId, context.LogLabel, context.AppVersion));
        if (!leaseResult.Acquired)
        {
            Console.WriteLine($"[MetadataSync] {context.LogLabel} skipped: {leaseResult.Inspection.Message}");
            return;
        }

        using RepositoryLeaseHandle lease = leaseResult.Handle!;
        var store = new MetadataStore(context.RootPath);
        try
        {
            if (!lease.IsOwner)
                return;
            store.EnsureSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] {context.LogLabel} failed: store init error at '{context.RootPath}': {ex.Message}");
            return;
        }

        DateTime now = DateTime.UtcNow;
        MetaInfo metaInfo = BuildUpdatedTombstoneMetaInfo(
            store,
            now,
            context.AppVersion,
            context.MachineId,
            updateExistingAppVersion: false);

        try
        {
            if (!lease.IsOwner)
                return;
            store.ExecuteWriteBatch(() =>
            {
                store.UpsertMetaInfo(metaInfo);
                AddTombstones(store, context.ExternalIds, context.EntityType, now, context.MachineId);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] {context.LogLabel} failed writing store '{context.RootPath}': {ex.Message}");
        }
    }

    private static MetaInfo BuildUpdatedTombstoneMetaInfo(
        MetadataStore store,
        DateTime now,
        string appVersion,
        string machineId,
        bool updateExistingAppVersion)
    {
        MetaInfo? metaInfo = store.GetMetaInfo();
        if (metaInfo == null)
        {
            return new MetaInfo
            {
                SchemaVersion = MetadataStore.CurrentSchemaVersion,
                CreatedUtc = now,
                LastWriteUtc = now,
                WriterAppVersion = appVersion,
                WriterMachineId = machineId
            };
        }

        metaInfo.SchemaVersion = MetadataStore.CurrentSchemaVersion;
        metaInfo.LastWriteUtc = now;
        metaInfo.WriterMachineId = machineId;
        if (updateExistingAppVersion)
            metaInfo.WriterAppVersion = appVersion;

        return metaInfo;
    }

    private static void AddTombstones(
        MetadataStore store,
        IEnumerable<string> externalIds,
        string entityType,
        DateTime deletedUtc,
        string originMachineId)
    {
        foreach (string externalId in externalIds)
        {
            if (string.IsNullOrWhiteSpace(externalId))
                continue;

            store.AddTombstone(new MetaTombstone
            {
                EntityType = entityType,
                EntityId = externalId,
                DeletedUtc = deletedUtc,
                OriginMachineId = originMachineId
            });
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
        return await ExecuteExportWithLeaseAsync(
                rootPath,
                appVersion,
                machineId,
                "project-metadata-export",
                "Project export",
                (destinationLease, useDeferredStore) => ExportProjectToStoreInternal(
                    rootPath,
                    projectId,
                    appVersion,
                    machineId,
                    destinationLease,
                    useDeferredStore,
                    ct),
                ct)
            .ConfigureAwait(false);
    }

    private async Task<MetadataSyncResult> ExecuteExportWithLeaseAsync(
        string rootPath,
        string appVersion,
        string machineId,
        string leaseOperation,
        string retryLabel,
        Func<RepositoryLeaseHandle?, bool, MetadataSyncResult> export,
        CancellationToken ct)
    {
        SemaphoreSlim metadataIoGate = GetMetadataIoGate(rootPath);
        await metadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rootPath))
                return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, InvalidRootPathMessage);

            RepositoryLeaseAcquireResult leaseResult = TryAcquireRepositoryLease(
                rootPath,
                leaseOperation,
                appVersion,
                machineId);
            bool useDeferredStore = leaseResult.Status == RepositoryLeaseAcquireStatus.Unavailable;
            if (!leaseResult.Acquired && !useDeferredStore)
                return LeaseFailure(leaseResult);

            using RepositoryLeaseHandle? destinationLease = leaseResult.Handle;
            return await ExecuteStoreWriteWithRetryAsync(
                    () => export(destinationLease, useDeferredStore),
                    retryLabel,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            metadataIoGate.Release();
        }
    }

    private static async Task<MetadataSyncResult> ExecuteStoreWriteWithRetryAsync(
        Func<MetadataSyncResult> write,
        string operationLabel,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt <= StoreRetryDelays.Length; attempt++)
        {
            try
            {
                return write();
            }
            catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
            {
                if (attempt >= StoreRetryDelays.Length)
                {
                    Console.WriteLine($"[MetadataSync] {operationLabel} failed after retries: {ex.Message}");
                    return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
                }

                TimeSpan delay = StoreRetryDelays[attempt];
                Console.WriteLine($"[MetadataSync] {operationLabel} store locked; retrying in {delay.TotalMilliseconds:0}ms.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, $"{operationLabel} failed after retries.");
    }

    private MetadataSyncResult? ValidateDestinationLease(
        string rootPath,
        string appVersion,
        string machineId,
        RepositoryLeaseHandle? destinationLease)
    {
        if (destinationLease is null)
            return null;
        if (!destinationLease.IsOwner)
            return LostLeaseFailure();
        if (!HasDeferredExport(rootPath))
            return null;
        if (TryFlushDeferredExport(
                rootPath,
                appVersion,
                machineId,
                ResolveLeaseOwnerId(machineId),
                _repositoryLeaseService,
                _operationCheckpoint))
        {
            return null;
        }

        return MetadataSyncResult.Failure(
            MetadataSyncStatus.RepositoryBusy,
            "Deferred metadata was preserved because destination metadata already exists or the queue could not be locked safely.");
    }

    private ExportStoreContext PrepareExportStore(
        string rootPath,
        string appVersion,
        string machineId,
        RepositoryLeaseHandle? destinationLease,
        bool useDeferredStore,
        string deferredLeaseOperation,
        string operationLabel)
    {
        MetadataSyncResult? destinationFailure = ValidateDestinationLease(
            rootPath, appVersion, machineId, destinationLease);
        if (destinationFailure is not null)
            return ExportStoreContext.Failed(rootPath, destinationFailure);

        string storeRoot = useDeferredStore ? GetDeferredExportRoot(rootPath) : rootPath;
        RepositoryLeaseHandle? deferredLease = useDeferredStore
            ? TryAcquireDeferredLease(storeRoot, appVersion, machineId, deferredLeaseOperation)
            : null;
        RepositoryLeaseHandle? activeLease = destinationLease ?? deferredLease;
        if (activeLease is null || !activeLease.IsOwner)
        {
            return new ExportStoreContext(
                storeRoot,
                deferredLease,
                null,
                null,
                MetadataSyncResult.Failure(
                    MetadataSyncStatus.WriteFailed,
                    "Metadata export could not acquire its deferred writer lease."));
        }

        var store = new MetadataStore(storeRoot);
        MetadataSyncResult? initializationFailure = TryInitializeExportStore(
            store, activeLease, rootPath, operationLabel);
        return new ExportStoreContext(
            storeRoot,
            deferredLease,
            activeLease,
            store,
            initializationFailure);
    }

    private MetadataSyncResult ExportProjectToStoreInternal(
        string rootPath,
        int projectId,
        string appVersion,
        string machineId,
        RepositoryLeaseHandle? destinationLease,
        bool useDeferredStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.WriteLine("[MetadataSync] Project export failed: root path is empty.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, InvalidRootPathMessage);
        }

        using ExportStoreContext context = PrepareExportStore(
            rootPath,
            appVersion,
            machineId,
            destinationLease,
            useDeferredStore,
            "deferred-project-metadata-export",
            "Project export");
        if (context.Failure is not null)
            return context.Failure;

        string storeRoot = context.StoreRoot;
        RepositoryLeaseHandle activeLease = context.ActiveLease!;
        MetadataStore store = context.Store!;

        Project? project = _repo.GetProjectById(projectId);
        if (project == null)
        {
            Console.WriteLine($"[MetadataSync] Project export failed: project {projectId} not found.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Project not found.");
        }

        string projectExternalId = EnsureProjectExternalId(project);
        DateTime now = DateTime.UtcNow;
        MetaInfo metaInfo = BuildUpdatedTombstoneMetaInfo(
            store,
            now,
            appVersion,
            machineId,
            updateExistingAppVersion: true);
        if (!TryPrepareGuardedProjectWrite(
                store,
                new GuardedProjectWriteRequest(rootPath, project, projectExternalId, now, machineId),
                out GuardedProjectWrite? guardedWrite,
                out string revisionFailure))
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.RepositoryBusy, revisionFailure);
        }

        try
        {
            if (!activeLease.IsOwner)
                return LostLeaseFailure();
            store.ExecuteWriteBatch(batchCt =>
            {
                batchCt.ThrowIfCancellationRequested();
                store.UpsertMetaInfo(metaInfo);
                ObserveCheckpoint("project-export-meta-info");
                batchCt.ThrowIfCancellationRequested();
                if (!store.TryUpsertProject(guardedWrite!.Record, guardedWrite.ExpectedRevision))
                    throw new MetadataRevisionConflictException("Project metadata changed after its revision was inspected.");
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataRevisionConflictException ex)
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.RepositoryBusy, ex.Message);
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

        return CompleteExport(
            rootPath,
            guardedWrite!,
            useDeferredStore,
            exportResult,
            "Project export queued: destination not writable. Will retry when available.");
    }

    public async Task<MetadataSyncResult> ExportBackupToStoreAsync(
        string rootPath,
        int backupId,
        string appVersion,
        string machineId,
        bool forceBackfill = false,
        CancellationToken ct = default)
    {
        return await ExecuteExportWithLeaseAsync(
                rootPath,
                appVersion,
                machineId,
                "backup-metadata-export",
                "Backup export",
                (destinationLease, useDeferredStore) => ExportBackupToStoreInternal(
                    rootPath,
                    backupId,
                    appVersion,
                    machineId,
                    forceBackfill,
                    destinationLease,
                    useDeferredStore,
                    ct),
                ct)
            .ConfigureAwait(false);
    }

    private MetadataSyncResult ExportBackupToStoreInternal(
        string rootPath,
        int backupId,
        string appVersion,
        string machineId,
        bool forceBackfill,
        RepositoryLeaseHandle? destinationLease,
        bool useDeferredStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            Console.WriteLine("[MetadataSync] Export failed: root path is empty.");
            return MetadataSyncResult.Failure(MetadataSyncStatus.InvalidPath, InvalidRootPathMessage);
        }

        if (!TryResolveBackupExportEntities(backupId, out BackupExportEntities? entities, out MetadataSyncResult? entityFailure))
            return entityFailure!;

        Backup backup = entities!.Backup;
        Project project = entities.Project;
        Snapshot snapshot = entities.Snapshot;

        using ExportStoreContext context = PrepareExportStore(
            rootPath,
            appVersion,
            machineId,
            destinationLease,
            useDeferredStore,
            "deferred-backup-metadata-export",
            "Backup export");
        if (context.Failure is not null)
            return context.Failure;

        string storeRoot = context.StoreRoot;
        RepositoryLeaseHandle activeLease = context.ActiveLease!;
        MetadataStore store = context.Store!;

        string projectExternalId = EnsureProjectExternalId(project);
        string snapshotExternalId = EnsureSnapshotExternalId(snapshot);
        string backupExternalId = EnsureBackupExternalId(backup);

        DateTime now = DateTime.UtcNow;
        MetaInfo metaInfo = BuildUpdatedTombstoneMetaInfo(
            store,
            now,
            appVersion,
            machineId,
            updateExistingAppVersion: true);
        if (!TryPrepareGuardedProjectWrite(
                store,
                new GuardedProjectWriteRequest(rootPath, project, projectExternalId, now, machineId),
                out GuardedProjectWrite? guardedWrite,
                out string revisionFailure))
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.RepositoryBusy, revisionFailure);
        }

        BackupExportCounts counts;
        try
        {
            if (!activeLease.IsOwner)
                return LostLeaseFailure();
            counts = WriteBackupExport(
                store,
                metaInfo,
                entities,
                new BackupExportWriteContext(
                    projectExternalId,
                    snapshotExternalId,
                    backupExternalId,
                    now,
                    machineId,
                    forceBackfill,
                    guardedWrite!.Record,
                    guardedWrite.ExpectedRevision),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataRevisionConflictException ex)
        {
            return MetadataSyncResult.Failure(MetadataSyncStatus.RepositoryBusy, ex.Message);
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Export failed writing store '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }

        var exportResult = new MetadataSyncResult(
            MetadataSyncStatus.Success,
            counts.Projects,
            counts.Snapshots,
            counts.Backups,
            0,
            string.Empty);
        Console.WriteLine(counts.Backfilled
            ? $"[MetadataSync] Export complete (backfill) for project '{project.Name}' to '{storeRoot}': snapshots={counts.Snapshots}, backups={counts.Backups}."
            : $"[MetadataSync] Export complete for backup {backupId} to '{storeRoot}'.");
        LogStoreCounts(store);
        return CompleteExport(
            rootPath,
            guardedWrite,
            useDeferredStore,
            exportResult,
            "Export queued: destination not writable. Will retry when available.");
    }

    private MetadataSyncResult CompleteExport(
        string rootPath,
        GuardedProjectWrite write,
        bool useDeferredStore,
        MetadataSyncResult success,
        string deferredMessage)
    {
        if (useDeferredStore)
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, deferredMessage);
        SaveSuccessfulProjectWriteBase(rootPath, write);
        return success;
    }

    private bool TryResolveBackupExportEntities(
        int backupId,
        out BackupExportEntities? entities,
        out MetadataSyncResult? failure)
    {
        entities = null;
        Backup? backup = _repo.GetBackupById(backupId);
        if (backup is null)
        {
            Console.WriteLine($"[MetadataSync] Export skipped: backup {backupId} no longer exists.");
            failure = SuccessfulSkip("Backup no longer exists; metadata export skipped.");
            return false;
        }

        Project? project = _repo.GetProjectById(backup.ProjectId);
        if (project is null)
        {
            Console.WriteLine($"[MetadataSync] Export failed: project {backup.ProjectId} not found.");
            failure = MetadataSyncResult.Failure(MetadataSyncStatus.InvalidStore, "Project not found.");
            return false;
        }

        Snapshot? snapshot = _repo.GetSnapshotById(backup.SnapshotId);
        if (snapshot is null)
        {
            Console.WriteLine($"[MetadataSync] Export skipped: snapshot {backup.SnapshotId} no longer exists.");
            failure = SuccessfulSkip("Snapshot no longer exists; metadata export skipped.");
            return false;
        }

        entities = new BackupExportEntities(backup, project, snapshot);
        failure = null;
        return true;
    }

    private static MetadataSyncResult? TryInitializeExportStore(
        MetadataStore store,
        RepositoryLeaseHandle activeLease,
        string rootPath,
        string operationLabel)
    {
        Console.WriteLine($"[MetadataSync] {operationLabel} target store: '{store.DatabasePath}'.");
        try
        {
            if (!activeLease.IsOwner)
                return LostLeaseFailure();
            store.EnsureSchema();
            return null;
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] {operationLabel} failed: store init error at '{rootPath}': {ex.Message}");
            return MetadataSyncResult.Failure(MetadataSyncStatus.WriteFailed, ex.Message);
        }
    }

    private static MetadataSyncResult SuccessfulSkip(string message) =>
        new(MetadataSyncStatus.Success, 0, 0, 0, 0, message);

    private BackupExportCounts WriteBackupExport(
        MetadataStore store,
        MetaInfo metaInfo,
        BackupExportEntities entities,
        BackupExportWriteContext context,
        CancellationToken ct)
    {
        int exportedProjects = 0;
        int exportedSnapshots = 0;
        int exportedBackups = 0;
        bool backfilled = context.ForceBackfill || !store.HasProject(context.ProjectExternalId);

        store.ExecuteWriteBatch(batchCt =>
        {
            batchCt.ThrowIfCancellationRequested();
            store.UpsertMetaInfo(metaInfo);
            ObserveCheckpoint("backup-export-meta-info");
            batchCt.ThrowIfCancellationRequested();
            if (backfilled)
            {
                (int snapshots, int backups) = ExportProjectHistory(
                    store,
                    entities.Project,
                    context.ProjectExternalId,
                    context.MachineId,
                    context.ProjectRecord,
                    context.ExpectedProjectRevision,
                    batchCt);
                exportedProjects = 1;
                exportedSnapshots = snapshots;
                exportedBackups = backups;
                return;
            }

            if (!store.TryUpsertProject(context.ProjectRecord, context.ExpectedProjectRevision))
                throw new MetadataRevisionConflictException("Project metadata changed after its revision was inspected.");
            ObserveCheckpoint("backup-export-project");
            batchCt.ThrowIfCancellationRequested();
            store.UpsertSnapshot(new MetaSnapshot
            {
                ExternalId = context.SnapshotExternalId,
                ProjectExternalId = context.ProjectExternalId,
                CreatedUtc = entities.Snapshot.CreatedUtc,
                FileCount = entities.Snapshot.FileCount,
                TotalBytes = entities.Snapshot.TotalBytes,
                DiffAdded = entities.Snapshot.DiffAdded,
                DiffModified = entities.Snapshot.DiffModified,
                DiffDeleted = entities.Snapshot.DiffDeleted,
                DiffNetBytes = entities.Snapshot.DiffNetBytes,
                DiffTopPathsJson = string.IsNullOrWhiteSpace(entities.Snapshot.DiffTopPathsJson)
                    ? "[]"
                    : entities.Snapshot.DiffTopPathsJson
            });
            ObserveCheckpoint("backup-export-snapshot");
            batchCt.ThrowIfCancellationRequested();
            store.UpsertBackup(CreateMetaBackup(
                entities.Backup,
                context.BackupExternalId,
                context.ProjectExternalId,
                context.SnapshotExternalId,
                context.MachineId));
            exportedBackups = 1;
        }, ct);

        return new BackupExportCounts(exportedProjects, exportedSnapshots, exportedBackups, backfilled);
    }

    public static void ExportBackupTombstoneToStore(
        string rootPath,
        string backupExternalId,
        string appVersion,
        string machineId,
        string? leaseOwnerId = null)
    {
        ExportBackupTombstoneToStoreAsync(
                rootPath,
                backupExternalId,
                appVersion,
                machineId,
                leaseOwnerId,
                CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public static async Task ExportBackupTombstoneToStoreAsync(
        string rootPath,
        string backupExternalId,
        string appVersion,
        string machineId,
        string? leaseOwnerId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(backupExternalId))
            return;

        SemaphoreSlim metadataIoGate = GetMetadataIoGate(rootPath);
        await metadataIoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForNetworkReadyAsync(rootPath, ct).ConfigureAwait(false);
            var leaseService = new RepositoryLeaseService();
            string ownerId = leaseOwnerId ?? CreateCompatibilityInstallationId(machineId);
            var context = new TombstoneExportContext(
                rootPath,
                BackupEntityType,
                [backupExternalId],
                machineId,
                "Backup tombstone export",
                appVersion,
                ownerId);
            RepositoryLeaseAcquireResult leaseResult = leaseService.TryAcquire(
                rootPath,
                CreateLeaseRequest(ownerId, machineId, "backup-tombstone-export", appVersion));
            bool useDeferredStore = leaseResult.Status == RepositoryLeaseAcquireStatus.Unavailable;
            if (!leaseResult.Acquired && !useDeferredStore)
            {
                Console.WriteLine($"[MetadataSync] Tombstone export skipped: {leaseResult.Inspection.Message}");
                return;
            }

            using RepositoryLeaseHandle? destinationLease = leaseResult.Handle;
            for (int attempt = 0; attempt <= StoreRetryDelays.Length; attempt++)
            {
                try
                {
                    ExportBackupTombstoneInternal(
                        context,
                        leaseService,
                        destinationLease,
                        useDeferredStore);
                    return;
                }
                catch (SqliteException ex) when (IsCannotOpenOrLocked(ex))
                {
                    if (attempt >= StoreRetryDelays.Length)
                    {
                        Console.WriteLine($"[MetadataSync] Tombstone export failed after retries: {ex.Message}");
                        TryExportBackupTombstoneToDeferred(
                            context,
                            leaseService);
                        return;
                    }

                    TimeSpan delay = StoreRetryDelays[attempt];
                    Console.WriteLine($"[MetadataSync] Tombstone store locked; retrying in {delay.TotalMilliseconds:0}ms.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            metadataIoGate.Release();
        }
    }

    private static SemaphoreSlim GetMetadataIoGate(string rootPath)
    {
        string key;
        try
        {
            key = string.IsNullOrWhiteSpace(rootPath)
                ? "<invalid-root>"
                : Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            key = rootPath?.Trim() ?? "<invalid-root>";
        }

        return MetadataIoGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static void TryExportBackupTombstoneToDeferred(
        TombstoneExportContext context,
        RepositoryLeaseService leaseService)
    {
        try
        {
            string deferredRoot = GetDeferredExportRoot(context.RootPath);
            Directory.CreateDirectory(deferredRoot);
            RepositoryLeaseAcquireResult leaseResult = leaseService.TryAcquire(
                deferredRoot,
                CreateLeaseRequest(
                    context.LeaseOwnerId,
                    context.MachineId,
                    "deferred-backup-tombstone-export",
                    context.AppVersion));
            if (!leaseResult.Acquired)
                return;

            using RepositoryLeaseHandle lease = leaseResult.Handle!;
            var store = new MetadataStore(deferredRoot);
            if (!lease.IsOwner)
                return;
            store.EnsureSchema();

            DateTime now = DateTime.UtcNow;
            MetaInfo metaInfo = BuildUpdatedTombstoneMetaInfo(
                store,
                now,
                context.AppVersion,
                context.MachineId,
                updateExistingAppVersion: true);

            if (!lease.IsOwner)
                return;
            store.ExecuteWriteBatch(() =>
            {
                store.UpsertMetaInfo(metaInfo);
                AddTombstones(store, context.ExternalIds, context.EntityType, now, context.MachineId);
            });

            Console.WriteLine($"[MetadataSync] Tombstone export deferred locally for '{context.RootPath}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetadataSync] Tombstone defer failed for '{context.RootPath}': {ex.Message}");
        }
    }

    private static void ExportBackupTombstoneInternal(
        TombstoneExportContext context,
        RepositoryLeaseService leaseService,
        RepositoryLeaseHandle? destinationLease,
        bool useDeferredStore)
    {
        if (!CanWriteTombstoneDestination(context, leaseService, destinationLease))
            return;

        string storeRoot = useDeferredStore ? GetDeferredExportRoot(context.RootPath) : context.RootPath;
        if (useDeferredStore)
            Directory.CreateDirectory(storeRoot);
        RepositoryLeaseAcquireResult? deferredLeaseResult = useDeferredStore
            ? leaseService.TryAcquire(
                storeRoot,
                CreateLeaseRequest(
                    context.LeaseOwnerId,
                    context.MachineId,
                    "deferred-backup-tombstone-export",
                    context.AppVersion))
            : null;
        using RepositoryLeaseHandle? deferredLease = deferredLeaseResult?.Handle;
        RepositoryLeaseHandle? activeLease = destinationLease ?? deferredLease;
        if (activeLease is null || !activeLease.IsOwner)
            return;

        var store = new MetadataStore(storeRoot);
        try
        {
            if (!activeLease.IsOwner)
                return;
            store.EnsureSchema();
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Tombstone export failed: store init error at '{context.RootPath}': {ex.Message}");
            return;
        }

        DateTime now = DateTime.UtcNow;
        MetaInfo metaInfo = BuildUpdatedTombstoneMetaInfo(
            store,
            now,
            context.AppVersion,
            context.MachineId,
            updateExistingAppVersion: true);

        try
        {
            if (!activeLease.IsOwner)
                return;
            store.ExecuteWriteBatch(() =>
            {
                store.UpsertMetaInfo(metaInfo);
                AddTombstones(store, context.ExternalIds, context.EntityType, now, context.MachineId);
            });
        }
        catch (Exception ex) when (ex is not SqliteException sqliteEx || !IsCannotOpenOrLocked(sqliteEx))
        {
            Console.WriteLine($"[MetadataSync] Tombstone export failed writing store '{context.RootPath}': {ex.Message}");
            return;
        }

        if (useDeferredStore)
            Console.WriteLine($"[MetadataSync] Tombstone export queued locally for '{context.RootPath}'.");
    }

    private static bool CanWriteTombstoneDestination(
        TombstoneExportContext context,
        RepositoryLeaseService leaseService,
        RepositoryLeaseHandle? destinationLease)
    {
        if (destinationLease is null)
            return true;
        if (!destinationLease.IsOwner)
            return false;
        return !HasDeferredExport(context.RootPath) ||
               TryFlushDeferredExport(
                   context.RootPath,
                   context.AppVersion,
                   context.MachineId,
                   context.LeaseOwnerId,
                   leaseService);
    }

    private static string GetMetaDir(string rootPath) =>
        Path.Combine(rootPath, VaultSyncDirectoryName, "meta");

    private RepositoryLeaseAcquireResult TryAcquireRepositoryLease(
        string rootPath,
        string operation,
        string appVersion,
        string machineLabel) =>
        _repositoryLeaseService.TryAcquire(
            rootPath,
            CreateLeaseRequest(
                ResolveLeaseOwnerId(machineLabel),
                machineLabel,
                operation,
                appVersion));

    private RepositoryLeaseHandle? TryAcquireDeferredLease(
        string deferredRoot,
        string appVersion,
        string machineLabel,
        string operation)
    {
        try
        {
            Directory.CreateDirectory(deferredRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        RepositoryLeaseAcquireResult result = _repositoryLeaseService.TryAcquire(
            deferredRoot,
            CreateLeaseRequest(
                ResolveLeaseOwnerId(machineLabel),
                machineLabel,
                operation,
                appVersion));
        return result.Handle;
    }

    private string ResolveLeaseOwnerId(string machineLabel) =>
        _installationIdentityProvider?.GetOrCreate() ??
        CreateCompatibilityInstallationId(machineLabel);

    private static RepositoryLeaseRequest CreateLeaseRequest(
        string installationId,
        string machineLabel,
        string operation,
        string appVersion) =>
        new(
            installationId,
            string.IsNullOrWhiteSpace(machineLabel) ? "Unknown host" : machineLabel.Trim(),
            operation,
            string.IsNullOrWhiteSpace(appVersion) ? UnknownAppVersion : appVersion.Trim());

    private static string CreateCompatibilityInstallationId(string machineLabel)
    {
        string source = string.IsNullOrWhiteSpace(machineLabel) ? UnknownAppVersion : machineLabel.Trim();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"vaultsync-lease:{source}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }

    private static MetadataSyncResult LeaseFailure(RepositoryLeaseAcquireResult leaseResult) =>
        MetadataSyncResult.Failure(
            MetadataSyncStatus.RepositoryBusy,
            leaseResult.Inspection.Message);

    private static MetadataSyncResult LostLeaseFailure() =>
        MetadataSyncResult.Failure(
            MetadataSyncStatus.RepositoryBusy,
            "Repository writer ownership changed before the metadata update could commit.");

    private static string GetDeferredExportRoot(string rootPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootPath));
        return Path.Combine(Path.GetTempPath(), "vaultsync-meta-export", HashService.FormatHexLower(hash));
    }

    private static async Task WaitForNetworkReadyAsync(string rootPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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

    private static bool TryFlushDeferredExport(
        string rootPath,
        string appVersion,
        string machineLabel,
        string leaseOwnerId,
        RepositoryLeaseService leaseService,
        Action<string>? operationCheckpoint = null)
    {
        string deferredRoot = GetDeferredExportRoot(rootPath);
        if (!File.Exists(new MetadataStore(deferredRoot).DatabasePath))
            return false;
        if (File.Exists(new MetadataStore(rootPath).DatabasePath))
            return false;

        RepositoryLeaseAcquireResult leaseResult = leaseService.TryAcquire(
            deferredRoot,
            CreateLeaseRequest(
                leaseOwnerId,
                machineLabel,
                "deferred-metadata-flush",
                appVersion));
        if (!leaseResult.Acquired)
            return false;

        bool copied;
        using (RepositoryLeaseHandle lease = leaseResult.Handle!)
        {
            if (!lease.IsOwner)
                return false;
            copied = TryCopyStoreFiles(deferredRoot, rootPath);
        }

        if (!copied)
            return false;

        operationCheckpoint?.Invoke("deferred-export-copied");
        string destinationDatabase = new MetadataStore(rootPath).DatabasePath;
        if (!Directory.Exists(rootPath) || !File.Exists(destinationDatabase))
            return false;

        try
        {
            if (new MetadataStore(rootPath, allowReadRecovery: true).GetMetaInfo() is null)
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            RuntimeLog.WriteVerbose(
                $"[MetadataSync] Deferred export copy could not be validated at '{rootPath}': {ex.Message}");
            return false;
        }

        return TryRetireDeferredExport(deferredRoot);
    }

    private static bool HasDeferredExport(string rootPath) =>
        File.Exists(new MetadataStore(GetDeferredExportRoot(rootPath)).DatabasePath);

    private static bool TryRetireDeferredExport(string deferredRoot)
    {
        string retiredRoot = deferredRoot + ".consumed-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.Move(deferredRoot, retiredRoot);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        string machineId,
        MetaProject projectRecord,
        long expectedProjectRevision,
        CancellationToken ct)
    {
        var snapshots = _repo.GetSnapshotsForProject(project.Name).ToList();
        var backups = _repo.GetBackupsForProject(project.Id).ToList();
        Console.WriteLine($"[MetadataSync] Export history for '{project.Name}': snapshots={snapshots.Count}, backups={backups.Count}.");
        var snapshotExternalIds = new Dictionary<int, string>();

        if (!store.TryUpsertProject(projectRecord, expectedProjectRevision))
            throw new MetadataRevisionConflictException("Project metadata changed after its revision was inspected.");
        ObserveCheckpoint("backup-export-project");

        foreach (Snapshot? snap in snapshots)
        {
            ct.ThrowIfCancellationRequested();
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
            ObserveCheckpoint("backup-export-snapshot");
        }

        int exportedBackups = 0;
        int skippedBackups = 0;
        foreach (Backup? backup in backups)
        {
            ct.ThrowIfCancellationRequested();
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
            store.UpsertBackup(CreateMetaBackup(
                backup,
                backupExternalId,
                projectExternalId,
                snapshotExternalId,
                machineId));
            ObserveCheckpoint("backup-export-backup");
            exportedBackups++;
        }

        if (skippedBackups > 0)
        {
            Console.WriteLine($"[MetadataSync] Export history skipped {skippedBackups} backups without snapshots for '{project.Name}'.");
        }

        return (snapshots.Count, exportedBackups);
    }

    private void ObserveCheckpoint(string checkpoint) => _operationCheckpoint?.Invoke(checkpoint);

    private static MetaBackup CreateMetaBackup(
        Backup backup,
        string backupExternalId,
        string projectExternalId,
        string snapshotExternalId,
        string machineId)
    {
        var descriptor = BackupCryptoDescriptor.FromMetadata(backup.IsEncrypted, backup.CryptoDescriptorJson);
        return new MetaBackup
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
        };
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

    private bool TryPrepareGuardedProjectWrite(
        MetadataStore store,
        GuardedProjectWriteRequest request,
        out GuardedProjectWrite? write,
        out string failure)
    {
        write = null;
        failure = string.Empty;
        try
        {
            MetaProject? existing = store.GetProject(request.ProjectExternalId);
            AppConfig config = _configStore.Load();
            string sourceKey = BuildMetadataSourceKey(
                Path.GetFullPath(request.DestinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            ProjectMetadataMergeBaseRecord? mergeBase = (config.Advanced.ProjectMetadataMergeBases ?? [])
                .FirstOrDefault(item =>
                    string.Equals(item.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.ProjectExternalId, request.ProjectExternalId, StringComparison.OrdinalIgnoreCase));
            if (!TryResolveExpectedRevision(existing, mergeBase, request.MachineId, out long expectedRevision, out failure))
                return false;

            ProjectMetadataConflictValues values = BuildExportedProjectValues(request.Project, config);
            long nextRevision = checked(expectedRevision + 1);
            var record = new MetaProject
            {
                ExternalId = request.ProjectExternalId,
                Name = request.Project.Name,
                Preset = request.Project.Preset,
                RootPathHint = request.Project.RootPath,
                CreatedUtc = request.Project.CreatedUtc,
                SettingsJson = SerializeProjectSettings(values),
                UpdatedUtc = request.UpdatedUtc,
                WriterMachineId = request.MachineId,
                Revision = nextRevision,
                BaseRevision = expectedRevision,
                FieldProvenanceJson = BuildFieldProvenanceJson(existing, values, request.MachineId, nextRevision, request.UpdatedUtc),
                ResolutionJson = BuildResolutionJson(config, sourceKey, request.ProjectExternalId)
            };
            write = new GuardedProjectWrite(record, expectedRevision, values);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Could not prepare guarded project metadata: {ex.Message}");
            failure = "Project metadata could not be prepared safely for writing.";
            return false;
        }
    }

    private static bool TryResolveExpectedRevision(
        MetaProject? existing,
        ProjectMetadataMergeBaseRecord? mergeBase,
        string machineId,
        out long expectedRevision,
        out string failure)
    {
        failure = string.Empty;
        if (existing is null)
        {
            expectedRevision = 0;
            return true;
        }
        if (mergeBase is not null)
        {
            expectedRevision = mergeBase.Revision;
            if (mergeBase.Revision > 0 && mergeBase.Revision == existing.Revision)
                return true;
            failure = "Project metadata changed on another machine. Import and review that revision before writing.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(existing.WriterMachineId) &&
            string.Equals(existing.WriterMachineId, machineId, StringComparison.Ordinal))
        {
            expectedRevision = existing.Revision;
            return true;
        }

        expectedRevision = 0;
        failure = "Existing project metadata has no trusted local base. Import and review it before writing.";
        return false;
    }

    private void SaveSuccessfulProjectWriteBase(string destinationRoot, GuardedProjectWrite write)
    {
        try
        {
            AppConfig config = _configStore.Load();
            string sourceKey = BuildMetadataSourceKey(
                Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            bool changed = false;
            string supersededUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (ProjectMetadataResolutionRecord resolution in (config.Advanced.ProjectMetadataResolutions ?? [])
                         .Where(item => item.UndoAvailable &&
                             string.Equals(item.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(item.ProjectExternalId, write.Record.ExternalId, StringComparison.OrdinalIgnoreCase)))
            {
                resolution.UndoAvailable = false;
                resolution.SupersededUtc = supersededUtc;
                changed = true;
            }

            changed |= UpsertProjectMetadataMergeBase(
                    config,
                    sourceKey,
                    write.Record,
                    write.Record.WriterMachineId,
                    write.Values);
            if (changed)
                _configStore.Save(config);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[MetadataSync] Could not persist the successful project write base: {ex.Message}");
        }
    }

    private ProjectMetadataConflictValues BuildExportedProjectValues(Project project, AppConfig config)
    {
        string? color = _projectColorResolver?.Invoke(project);
        List<int> disabledProjects = config.Backups.AutoBackupDisabledProjects ?? [];
        return new ProjectMetadataConflictValues
        {
            AvatarColor = NormalizeAvatarColor(color),
            EncryptionPolicy = ProjectEncryptionPolicy.Normalize(project.EncryptionPolicy),
            PreferredDestinationId = project.PreferredDestinationId?.Trim() ?? string.Empty,
            RestoreMode = ProjectRestoreMode.Normalize(project.RestoreMode),
            VerificationPolicy = ProjectVerificationPolicy.Normalize(project.VerificationPolicy),
            AutoBackupEnabled = !disabledProjects.Contains(project.Id),
            Tags = project.Tags?.Trim() ?? string.Empty
        };
    }

    private static string SerializeProjectSettings(ProjectMetadataConflictValues values)
    {
        var settings = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(values.AvatarColor))
            settings[AvatarColorField] = values.AvatarColor;
        settings[EncryptionPolicyField] = values.EncryptionPolicy;
        settings[PreferredDestinationIdField] = string.IsNullOrWhiteSpace(values.PreferredDestinationId)
            ? null
            : values.PreferredDestinationId;
        settings[RestoreModeField] = values.RestoreMode;
        settings[VerificationPolicyField] = values.VerificationPolicy;
        settings[AutoBackupEnabledField] = values.AutoBackupEnabled;
        settings[TagsField] = values.Tags;
        return JsonSerializer.Serialize(settings);
    }

    private string BuildFieldProvenanceJson(
        MetaProject? existing,
        ProjectMetadataConflictValues values,
        string writerMachineId,
        long nextRevision,
        DateTime updatedUtc)
    {
        Dictionary<string, ProjectMetadataFieldProvenance> provenance = ParseFieldProvenance(existing?.FieldProvenanceJson);
        ProjectMetadataConflictValues? previous = existing is null
            ? null
            : ValuesFromParsedSettings(ParseProjectSettings(existing.SettingsJson));
        string timestamp = updatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        foreach (string field in ProjectMetadataFieldNames)
        {
            if (existing is null || previous is null || !ProjectMetadataFieldEquals(field, previous, values))
            {
                provenance[field] = new ProjectMetadataFieldProvenance
                {
                    WriterMachineId = writerMachineId,
                    Revision = nextRevision,
                    UpdatedUtc = timestamp
                };
                continue;
            }

            if (provenance.ContainsKey(field))
                continue;

            provenance[field] = new ProjectMetadataFieldProvenance
            {
                WriterMachineId = existing.WriterMachineId,
                Revision = Math.Max(0, existing.Revision),
                UpdatedUtc = existing.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            };
        }

        return JsonSerializer.Serialize(provenance);
    }

    private static string BuildResolutionJson(AppConfig config, string sourceKey, string projectExternalId)
    {
        ProjectMetadataResolutionRecord? resolution = (config.Advanced.ProjectMetadataResolutions ?? [])
            .Where(item =>
                string.Equals(item.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ProjectExternalId, projectExternalId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ResolvedUtc, StringComparer.Ordinal)
            .FirstOrDefault();
        return resolution is null ? string.Empty : JsonSerializer.Serialize(resolution);
    }

    private static Dictionary<string, ProjectMetadataFieldProvenance> ParseFieldProvenance(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, ProjectMetadataFieldProvenance>(StringComparer.Ordinal);

        try
        {
            Dictionary<string, ProjectMetadataFieldProvenance>? parsed =
                JsonSerializer.Deserialize<Dictionary<string, ProjectMetadataFieldProvenance>>(json);
            return parsed is null
                ? new Dictionary<string, ProjectMetadataFieldProvenance>(StringComparer.Ordinal)
                : new Dictionary<string, ProjectMetadataFieldProvenance>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, ProjectMetadataFieldProvenance>(StringComparer.Ordinal);
        }
    }

    private static readonly string[] ProjectMetadataFieldNames =
    [
        AvatarColorField,
        EncryptionPolicyField,
        PreferredDestinationIdField,
        RestoreModeField,
        VerificationPolicyField,
        AutoBackupEnabledField,
        TagsField
    ];

    private static ProjectMetadataConflictValues ValuesFromParsedSettings(ParsedProjectSettings parsed) => new()
    {
        AvatarColor = parsed.HasAvatarColor ? parsed.AvatarColor : string.Empty,
        EncryptionPolicy = parsed.HasEncryptionPolicy ? parsed.EncryptionPolicy : string.Empty,
        PreferredDestinationId = parsed.HasPreferredDestinationId ? parsed.PreferredDestinationId : string.Empty,
        RestoreMode = parsed.HasRestoreMode ? parsed.RestoreMode : string.Empty,
        VerificationPolicy = parsed.HasVerificationPolicy ? parsed.VerificationPolicy : string.Empty,
        AutoBackupEnabled = parsed.HasAutoBackupEnabled ? parsed.AutoBackupEnabled : null,
        Tags = parsed.HasTags ? parsed.Tags : string.Empty
    };

    private static bool ProjectMetadataFieldEquals(
        string field,
        ProjectMetadataConflictValues left,
        ProjectMetadataConflictValues right) => field switch
    {
        AvatarColorField => string.Equals(left.AvatarColor, right.AvatarColor, StringComparison.OrdinalIgnoreCase),
        EncryptionPolicyField => string.Equals(left.EncryptionPolicy, right.EncryptionPolicy, StringComparison.OrdinalIgnoreCase),
        PreferredDestinationIdField => string.Equals(left.PreferredDestinationId, right.PreferredDestinationId, StringComparison.OrdinalIgnoreCase),
        RestoreModeField => string.Equals(left.RestoreMode, right.RestoreMode, StringComparison.OrdinalIgnoreCase),
        VerificationPolicyField => string.Equals(left.VerificationPolicy, right.VerificationPolicy, StringComparison.OrdinalIgnoreCase),
        AutoBackupEnabledField => left.AutoBackupEnabled == right.AutoBackupEnabled,
        TagsField => string.Equals(left.Tags, right.Tags, StringComparison.Ordinal),
        _ => false
    };

    private readonly record struct ParsedProjectSettings(
        string AvatarColor,
        string EncryptionPolicy,
        string PreferredDestinationId,
        string RestoreMode,
        string VerificationPolicy,
        bool AutoBackupEnabled,
        string Tags,
        bool HasAvatarColor,
        bool HasEncryptionPolicy,
        bool HasPreferredDestinationId,
        bool HasRestoreMode,
        bool HasVerificationPolicy,
        bool HasAutoBackupEnabled,
        bool HasTags);

    private ParsedProjectSettings ParseProjectSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return EmptyParsedProjectSettings();

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            string avatarColor = string.Empty;
            string policy = ProjectEncryptionPolicy.Inherit;
            string preferredDestinationId = string.Empty;
            string restoreMode = ProjectRestoreMode.Direct;
            string verificationPolicy = ProjectVerificationPolicy.Always;
            bool autoBackupEnabled = true;
            string tags = string.Empty;
            bool hasPolicy = false;
            bool hasPreferredDestinationId = false;
            bool hasRestoreMode = false;
            bool hasVerificationPolicy = false;
            bool hasAutoBackupEnabled = false;
            bool hasTags = false;
            bool hasAvatarColor = false;

            if (doc.RootElement.TryGetProperty(AvatarColorField, out JsonElement avatarColorProp))
            {
                avatarColor = NormalizeAvatarColor(avatarColorProp.GetString());
                hasAvatarColor = !string.IsNullOrWhiteSpace(avatarColor);
            }

            if (doc.RootElement.TryGetProperty(EncryptionPolicyField, out JsonElement policyProp))
            {
                policy = ProjectEncryptionPolicy.Normalize(policyProp.GetString());
                hasPolicy = true;
            }

            if (doc.RootElement.TryGetProperty(VerificationPolicyField, out JsonElement verificationProp))
            {
                verificationPolicy = ProjectVerificationPolicy.Normalize(verificationProp.GetString());
                hasVerificationPolicy = true;
            }

            if (doc.RootElement.TryGetProperty(PreferredDestinationIdField, out JsonElement destinationProp))
            {
                preferredDestinationId = NormalizePreferredDestinationId(
                    destinationProp.GetString(),
                    _configStore.Load().Backups.Destinations);
                hasPreferredDestinationId = true;
            }

            if (doc.RootElement.TryGetProperty(RestoreModeField, out JsonElement restoreModeProp))
            {
                restoreMode = ProjectRestoreMode.Normalize(restoreModeProp.GetString());
                hasRestoreMode = true;
            }

            if (doc.RootElement.TryGetProperty(AutoBackupEnabledField, out JsonElement autoBackupEnabledProp))
            {
                autoBackupEnabled = autoBackupEnabledProp.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => autoBackupEnabled
                };
                hasAutoBackupEnabled = autoBackupEnabledProp.ValueKind is JsonValueKind.True or JsonValueKind.False;
            }

            if (doc.RootElement.TryGetProperty(TagsField, out JsonElement tagsProp))
            {
                string? rawTags = tagsProp.GetString();
                tags = string.IsNullOrWhiteSpace(rawTags) ? string.Empty : rawTags.Trim();
                hasTags = true;
            }

            return new ParsedProjectSettings(
                avatarColor,
                policy,
                preferredDestinationId,
                restoreMode,
                verificationPolicy,
                autoBackupEnabled,
                tags,
                HasAvatarColor: hasAvatarColor,
                HasEncryptionPolicy: hasPolicy,
                HasPreferredDestinationId: hasPreferredDestinationId,
                HasRestoreMode: hasRestoreMode,
                HasVerificationPolicy: hasVerificationPolicy,
                HasAutoBackupEnabled: hasAutoBackupEnabled,
                HasTags: hasTags);
        }
        catch
        {
            return EmptyParsedProjectSettings();
        }
    }

    private static ParsedProjectSettings EmptyParsedProjectSettings() =>
        new(
            string.Empty,
            ProjectEncryptionPolicy.Inherit,
            string.Empty,
            ProjectRestoreMode.Direct,
            ProjectVerificationPolicy.Always,
            true,
            string.Empty,
            HasAvatarColor: false,
            HasEncryptionPolicy: false,
            HasPreferredDestinationId: false,
            HasRestoreMode: false,
            HasVerificationPolicy: false,
            HasAutoBackupEnabled: false,
            HasTags: false);

    private bool ApplyImportedProjectSettings(
        int projectId,
        AppConfig config,
        MetaProject metaProject,
        string sourceKey,
        string? sourceMachineId,
        ParsedProjectSettings parsedSettings,
        IList<ProjectMetadataConflictRecord> pendingConflicts)
    {
        if (!parsedSettings.HasAvatarColor &&
            !parsedSettings.HasEncryptionPolicy &&
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

        string currentAvatarColor = NormalizeAvatarColor(_projectColorResolver?.Invoke(current));
        string nextAvatarColor = parsedSettings.HasAvatarColor
            ? parsedSettings.AvatarColor
            : currentAvatarColor;
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
        string currentVerificationPolicy = ProjectVerificationPolicy.Normalize(current.VerificationPolicy);
        string nextVerificationPolicy = parsedSettings.HasVerificationPolicy
            ? ProjectVerificationPolicy.Normalize(parsedSettings.VerificationPolicy)
            : currentVerificationPolicy;
        List<BackupDestination> destinations = _configStore.Load().Backups.Destinations;
        string currentPreferredDestinationId = NormalizePreferredDestinationId(current.PreferredDestinationId, destinations);
        string nextPreferredDestinationId = parsedSettings.HasPreferredDestinationId
            ? NormalizeImportedPreferredDestinationId(parsedSettings.PreferredDestinationId, destinations)
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

        bool conflictValuesDiffer =
            !string.Equals(nextAvatarColor, currentAvatarColor, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextPolicy, currentPolicy, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextPreferredDestinationId, currentPreferredDestinationId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextRestoreMode, currentRestoreMode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextVerificationPolicy, currentVerificationPolicy, StringComparison.OrdinalIgnoreCase) ||
            nextAutoBackupEnabled != currentAutoBackupEnabled ||
            !string.Equals(nextTags, currentTags, StringComparison.Ordinal);

        var localValues = new ProjectMetadataConflictValues
        {
            AvatarColor = currentAvatarColor,
            EncryptionPolicy = currentPolicy,
            PreferredDestinationId = currentPreferredDestinationId,
            RestoreMode = currentRestoreMode,
            VerificationPolicy = currentVerificationPolicy,
            AutoBackupEnabled = currentAutoBackupEnabled,
            Tags = currentTags
        };
        var incomingValues = new ProjectMetadataConflictValues
        {
            AvatarColor = nextAvatarColor,
            EncryptionPolicy = nextPolicy,
            PreferredDestinationId = nextPreferredDestinationId,
            RestoreMode = nextRestoreMode,
            VerificationPolicy = nextVerificationPolicy,
            AutoBackupEnabled = nextAutoBackupEnabled,
            Tags = nextTags
        };

        ProjectMetadataMergeBaseRecord? mergeBase = (config.Advanced.ProjectMetadataMergeBases ??= [])
            .FirstOrDefault(item =>
                string.Equals(item.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ProjectExternalId, metaProject.ExternalId, StringComparison.OrdinalIgnoreCase));
        ProjectMetadataConflictValues? trustedBase = mergeBase is { Revision: > 0 }
            ? mergeBase.Values
            : null;
        ProjectMetadataMergePlan plan = ProjectMetadataMergePlanner.Create(trustedBase, localValues, incomingValues);

        if (conflictValuesDiffer && HasDurableKeepLocalResolution(config, metaProject, sourceMachineId, incomingValues))
        {
            bool changed = RemoveProjectMetadataConflict(projectId, pendingConflicts);
            changed |= UpsertProjectMetadataMergeBase(config, sourceKey, metaProject, sourceMachineId, incomingValues);
            return changed;
        }

        if (!plan.HasConflicts)
        {
            ApplyProjectMetadataValues(config, current, metaProject.ExternalId, plan.Merged, currentKeyRef);
            bool changed = RemoveProjectMetadataConflict(projectId, pendingConflicts);
            changed |= UpsertProjectMetadataMergeBase(config, sourceKey, metaProject, sourceMachineId, incomingValues);
            return changed;
        }

        return UpsertProjectMetadataConflict(
            new ProjectMetadataConflictContext(
                current,
                metaProject,
                sourceKey,
                sourceMachineId,
                mergeBase?.Revision ?? 0,
                mergeBase?.WriterMachineId ?? string.Empty,
                mergeBase?.UpdatedUtc ?? string.Empty,
                ResolveLocalMetadataWriterId(),
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                trustedBase ?? new ProjectMetadataConflictValues(),
                localValues,
                incomingValues,
                plan),
            pendingConflicts);
    }

    private string ResolveLocalMetadataWriterId()
    {
        try
        {
            return _installationIdentityProvider?.GetOrCreate() ?? "this-installation";
        }
        catch
        {
            return "this-installation";
        }
    }

    private void ApplyProjectMetadataValues(
        AppConfig config,
        Project current,
        string externalId,
        ProjectMetadataConflictValues values,
        string? currentKeyRef)
    {
        TryApplyProjectColor(externalId, values.AvatarColor);
        _repo.UpdateProjectEncryptionSettings(current.Id, values.EncryptionPolicy, currentKeyRef);
        _repo.UpdateProjectPreferredDestination(current.Id, NullIfWhiteSpace(values.PreferredDestinationId));
        _repo.UpdateProjectRestoreMode(current.Id, NullIfWhiteSpace(values.RestoreMode));
        _repo.UpdateProjectVerificationPolicy(current.Id, NullIfWhiteSpace(values.VerificationPolicy));
        _repo.UpdateProjectTags(current.Id, NullIfWhiteSpace(values.Tags));
        if (values.AutoBackupEnabled.HasValue)
            ApplyImportedProjectAutoBackupSetting(config, current.Id, values.AutoBackupEnabled.Value);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProjectMetadataConflictValues BuildImportedValues(
        Project project,
        ParsedProjectSettings parsed,
        AppConfig config,
        int projectId) => new()
    {
        AvatarColor = parsed.HasAvatarColor ? parsed.AvatarColor : string.Empty,
        EncryptionPolicy = parsed.HasEncryptionPolicy ? parsed.EncryptionPolicy : project.EncryptionPolicy,
        PreferredDestinationId = parsed.HasPreferredDestinationId ? parsed.PreferredDestinationId : project.PreferredDestinationId ?? string.Empty,
        RestoreMode = parsed.HasRestoreMode ? parsed.RestoreMode : project.RestoreMode ?? string.Empty,
        VerificationPolicy = parsed.HasVerificationPolicy ? parsed.VerificationPolicy : project.VerificationPolicy ?? string.Empty,
        AutoBackupEnabled = parsed.HasAutoBackupEnabled
            ? parsed.AutoBackupEnabled
            : !(config.Backups.AutoBackupDisabledProjects ?? []).Contains(projectId),
        Tags = parsed.HasTags ? parsed.Tags : project.Tags ?? string.Empty
    };

    private static bool UpsertProjectMetadataMergeBase(
        AppConfig config,
        string sourceKey,
        MetaProject project,
        string? writerMachineId,
        ProjectMetadataConflictValues values)
    {
        config.Advanced.ProjectMetadataMergeBases ??= [];
        ProjectMetadataMergeBaseRecord? existing = config.Advanced.ProjectMetadataMergeBases.FirstOrDefault(item =>
            string.Equals(item.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ProjectExternalId, project.ExternalId, StringComparison.OrdinalIgnoreCase));
        string updatedUtc = project.UpdatedUtc == default
            ? string.Empty
            : project.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        Dictionary<string, ProjectMetadataFieldProvenance> fieldProvenance = ParseFieldProvenance(project.FieldProvenanceJson);
        if (existing is not null &&
            existing.Revision == project.Revision &&
            string.Equals(existing.WriterMachineId, writerMachineId, StringComparison.Ordinal) &&
            string.Equals(existing.UpdatedUtc, updatedUtc, StringComparison.Ordinal) &&
            ProjectMetadataConflictValuesEqual(existing.Values, values) &&
            ProjectMetadataFieldProvenanceEqual(existing.FieldProvenance, fieldProvenance))
        {
            return false;
        }

        existing ??= new ProjectMetadataMergeBaseRecord
        {
            SourceKey = sourceKey,
            ProjectExternalId = project.ExternalId
        };
        if (!config.Advanced.ProjectMetadataMergeBases.Contains(existing))
            config.Advanced.ProjectMetadataMergeBases.Add(existing);
        existing.Revision = project.Revision;
        existing.WriterMachineId = writerMachineId ?? string.Empty;
        existing.UpdatedUtc = updatedUtc;
        existing.Values = values;
        existing.FieldProvenance = fieldProvenance;
        return true;
    }

    private static bool ProjectMetadataFieldProvenanceEqual(
        Dictionary<string, ProjectMetadataFieldProvenance>? left,
        Dictionary<string, ProjectMetadataFieldProvenance>? right)
    {
        left ??= new Dictionary<string, ProjectMetadataFieldProvenance>();
        right ??= new Dictionary<string, ProjectMetadataFieldProvenance>();
        if (left.Count != right.Count)
            return false;
        return left.All(pair => right.TryGetValue(pair.Key, out ProjectMetadataFieldProvenance? value) &&
            string.Equals(pair.Value.WriterMachineId, value.WriterMachineId, StringComparison.Ordinal) &&
            pair.Value.Revision == value.Revision &&
            string.Equals(pair.Value.UpdatedUtc, value.UpdatedUtc, StringComparison.Ordinal));
    }

    private static bool HasDurableKeepLocalResolution(
        AppConfig config,
        MetaProject project,
        string? sourceMachineId,
        ProjectMetadataConflictValues incomingValues)
    {
        string sourceUpdatedUtc = project.UpdatedUtc == default
            ? string.Empty
            : project.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        string normalizedSourceMachineId = string.IsNullOrWhiteSpace(sourceMachineId)
            ? UnknownAppVersion
            : sourceMachineId;
        return (config.Advanced.ProjectMetadataResolutions ?? []).Any(resolution =>
            string.Equals(resolution.Decision, "keep-local", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(resolution.UndoneUtc) &&
            string.Equals(resolution.ProjectExternalId, project.ExternalId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(resolution.SourceMachineId, normalizedSourceMachineId, StringComparison.Ordinal) &&
            string.Equals(resolution.SourceUpdatedUtc, sourceUpdatedUtc, StringComparison.Ordinal) &&
            ProjectMetadataConflictValuesEqual(resolution.Imported, incomingValues));
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
        ProjectMetadataConflictContext context,
        IList<ProjectMetadataConflictRecord> pendingConflicts)
    {
        Project current = context.Current;
        MetaProject metaProject = context.Imported;
        var next = new ProjectMetadataConflictRecord
        {
            ProjectId = current.Id,
            ProjectExternalId = string.IsNullOrWhiteSpace(current.ExternalId) ? metaProject.ExternalId : current.ExternalId,
            ProjectName = current.Name,
            SourceMachineId = string.IsNullOrWhiteSpace(context.SourceMachineId) ? UnknownAppVersion : context.SourceMachineId,
            SourceKey = context.SourceKey,
            SourceRevision = metaProject.Revision,
            BaseRevision = context.BaseRevision,
            BaseMachineId = context.BaseMachineId,
            BaseUpdatedUtc = context.BaseUpdatedUtc,
            LocalMachineId = context.LocalMachineId,
            DetectedUtc = context.DetectedUtc,
            SourceUpdatedUtc = metaProject.UpdatedUtc == default
                ? string.Empty
                : metaProject.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ConflictingFields = [.. context.Plan.ConflictingFields],
            Base = context.Base,
            Local = context.Local,
            Imported = context.Incoming,
            KeepLocalResult = context.Plan.KeepLocalResult,
            AcceptImportedResult = context.Plan.AcceptImportedResult
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

        if (string.Equals(existing.SourceKey, next.SourceKey, StringComparison.OrdinalIgnoreCase) &&
            existing.SourceRevision == next.SourceRevision &&
            !string.IsNullOrWhiteSpace(existing.DetectedUtc))
        {
            next.DetectedUtc = existing.DetectedUtc;
        }

        if (ProjectMetadataConflictEquals(existing, next))
            return false;

        existing.ProjectId = next.ProjectId;
        existing.ProjectExternalId = next.ProjectExternalId;
        existing.ProjectName = next.ProjectName;
        existing.SourceMachineId = next.SourceMachineId;
        existing.SourceUpdatedUtc = next.SourceUpdatedUtc;
        existing.SourceKey = next.SourceKey;
        existing.SourceRevision = next.SourceRevision;
        existing.BaseRevision = next.BaseRevision;
        existing.BaseMachineId = next.BaseMachineId;
        existing.BaseUpdatedUtc = next.BaseUpdatedUtc;
        existing.LocalMachineId = next.LocalMachineId;
        existing.DetectedUtc = next.DetectedUtc;
        existing.ConflictingFields = next.ConflictingFields;
        existing.Base = next.Base;
        existing.Local = next.Local;
        existing.Imported = next.Imported;
        existing.KeepLocalResult = next.KeepLocalResult;
        existing.AcceptImportedResult = next.AcceptImportedResult;
        return true;
    }

    private static bool ProjectMetadataConflictEquals(ProjectMetadataConflictRecord left, ProjectMetadataConflictRecord right)
    {
        return left.ProjectId == right.ProjectId &&
               string.Equals(left.ProjectExternalId, right.ProjectExternalId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ProjectName, right.ProjectName, StringComparison.Ordinal) &&
               string.Equals(left.SourceMachineId, right.SourceMachineId, StringComparison.Ordinal) &&
               string.Equals(left.SourceUpdatedUtc, right.SourceUpdatedUtc, StringComparison.Ordinal) &&
               string.Equals(left.SourceKey, right.SourceKey, StringComparison.OrdinalIgnoreCase) &&
               left.SourceRevision == right.SourceRevision &&
               left.BaseRevision == right.BaseRevision &&
               string.Equals(left.BaseMachineId, right.BaseMachineId, StringComparison.Ordinal) &&
               string.Equals(left.BaseUpdatedUtc, right.BaseUpdatedUtc, StringComparison.Ordinal) &&
               string.Equals(left.LocalMachineId, right.LocalMachineId, StringComparison.Ordinal) &&
               string.Equals(left.DetectedUtc, right.DetectedUtc, StringComparison.Ordinal) &&
               left.ConflictingFields.SequenceEqual(right.ConflictingFields, StringComparer.Ordinal) &&
               ProjectMetadataConflictValuesEqual(left.Base, right.Base) &&
               ProjectMetadataConflictValuesEqual(left.Local, right.Local) &&
               ProjectMetadataConflictValuesEqual(left.Imported, right.Imported) &&
               ProjectMetadataConflictValuesEqual(left.KeepLocalResult, right.KeepLocalResult) &&
               ProjectMetadataConflictValuesEqual(left.AcceptImportedResult, right.AcceptImportedResult);
    }

    private static bool ProjectMetadataConflictValuesEqual(ProjectMetadataConflictValues left, ProjectMetadataConflictValues right)
    {
        return string.Equals(left.AvatarColor, right.AvatarColor, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.EncryptionPolicy, right.EncryptionPolicy, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.PreferredDestinationId, right.PreferredDestinationId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.RestoreMode, right.RestoreMode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.VerificationPolicy, right.VerificationPolicy, StringComparison.OrdinalIgnoreCase) &&
               left.AutoBackupEnabled == right.AutoBackupEnabled &&
               string.Equals(left.Tags, right.Tags, StringComparison.Ordinal);
    }

    private static string? ResolveProjectWriterMachineId(MetaProject project, MetaInfo? storeInfo)
        => string.IsNullOrWhiteSpace(project.WriterMachineId)
            ? storeInfo?.WriterMachineId
            : project.WriterMachineId;

    private static string NormalizePreferredDestinationId(string? preferredDestinationId, IReadOnlyCollection<BackupDestination> destinations)
        => DestinationIdentityService.NormalizePreferredDestinationId(preferredDestinationId, destinations);

    private static string NormalizeImportedPreferredDestinationId(
        string? preferredDestinationId,
        IReadOnlyCollection<BackupDestination> destinations)
    {
        string normalized = DestinationIdentityService.NormalizePreferredDestinationId(preferredDestinationId, destinations);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        BackupDestination? localDestination = DestinationIdentityService.FindByPreferredDestinationId(destinations, normalized);
        return localDestination is null ? string.Empty : DestinationIdentityService.GetId(localDestination);
    }

    private void TryApplyProjectColor(string projectExternalId, string color)
    {
        if (_projectColorApplier is null || string.IsNullOrWhiteSpace(projectExternalId))
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(color))
                return;

            _projectColorApplier(projectExternalId, color);
        }
        catch
        {
            // ignore malformed settings json
        }
    }

    private static string NormalizeAvatarColor(string? value)
    {
        string color = value?.Trim() ?? string.Empty;
        if (color.Length != 7 || color[0] != '#')
            return string.Empty;

        return color.AsSpan(1).IndexOfAnyExcept(HexCharacters) >= 0
            ? string.Empty
            : color.ToUpperInvariant();
    }
}

public sealed record MetadataSyncOptions(
    bool AllowCreateProjects,
    bool MarkNeedsRestoreOnImport,
    bool ExportMissingTombstonesOnImport = true,
    bool SkipUnchangedReadOnlySource = false,
    bool ApplyDestructiveTombstones = true)
{
    public static MetadataSyncOptions Default => new(true, true);
    public MetadataSyncOptions WithoutSourceWrites() => this with { ExportMissingTombstonesOnImport = false };
    public MetadataSyncOptions AsReadOnlySource() => this with
    {
        ExportMissingTombstonesOnImport = false,
        ApplyDestructiveTombstones = false
    };
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
    public int RepairedBackups
    {
        get; init;
    }

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
    public int DeletedProjects { get; init; }
    public int DeletedSnapshots { get; init; }
    public int TotalDeletes => DeletedProjects + DeletedSnapshots + DeletedBackups;

    public bool HasChanges =>
        NewProjects > 0 ||
        LinkedProjects > 0 ||
        NewSnapshots > 0 ||
        NewBackups > 0 ||
        TotalDeletes > 0;

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
    RepositoryBusy,
    WriteFailed
}
