using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;

namespace VaultSync.Core.Services;

public sealed class BackupService(
    SqliteRepository repo,
    BackupEncryptionSecretService? backupEncryptionSecretService = null,
    IAppConfigStore? configStore = null)
{
    private readonly BackupCancellationRegistry _cancellationRegistry = new();
    private readonly SqliteRepository _repo = repo;
    private readonly BackupEncryptionSecretService _backupEncryptionSecretService = backupEncryptionSecretService ?? new BackupEncryptionSecretService();
    private readonly IAppConfigStore _configStore = configStore ?? StaticAppConfigStore.Instance;
    private const string InProgressMarkerFileName = ".vaultsync_inprogress";
    private const string CompletedMarkerFileName = ".vaultsync_complete";
    private const string ArchiveResumeCheckpointFileName = ".vaultsync_resume.json";
    private const string RsyncExecutableName = "rsync";
    private const string RsyncWindowsExecutableName = "rsync.exe";
    private const string ToolsDirectoryName = "tools";

    private static readonly JsonSerializerOptions ResumeCheckpointJsonOptions = new() { WriteIndented = true };
    public event Action<Backup>? BackupRetentionDeleted;
    public sealed record BackupRetentionPreflightResult(
        bool CanPrune,
        string Code,
        string Message,
        int ValidRestorePointCount,
        int DeletionQuota);

    internal sealed record BackupRetentionCandidateDecision(
        int BackupId,
        bool Selected,
        string Code,
        string Message);

    internal sealed record ArchiveResumeCheckpoint(
        int Version,
        string Mode,
        string SourceFingerprint,
        long ArchiveSizeBytes,
        DateTime LastUpdatedUtc,
        bool UsesParallelUpload = false,
        long ChunkSizeBytes = 0,
        int Parallelism = 0,
        List<int>? CompletedChunkIndexes = null,
        string ArtifactFileName = BackupArchiveCryptoService.PlainArchiveFileName);

    internal sealed record CheckpointResumeTelemetryUpdate
    {
        public required string Status { get; init; }
        public required string ProjectName { get; init; }
        public required string BackupFolder { get; init; }
        public required string ArchivePath { get; init; }
        public long ResumeOffsetBytes { get; init; }
        public long ArchiveSizeBytes { get; init; }
        public required string SourceFingerprint { get; init; }
        public required string Message { get; init; }
    }

    private sealed record ArchiveBackupResult(
        string BackupFolder,
        bool IsEncrypted,
        BackupCryptoDescriptor Descriptor);

    internal static void UpdateCheckpointResumeTelemetry(
        AppConfig config,
        CheckpointResumeTelemetryUpdate update)
    {
        config.Advanced.CheckpointResumeTelemetry ??= new CheckpointResumeTelemetry();
        CheckpointResumeTelemetry telemetry = config.Advanced.CheckpointResumeTelemetry;
        telemetry.LastUpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        telemetry.LastStatus = update.Status;
        telemetry.LastProjectName = update.ProjectName;
        telemetry.LastBackupFolder = update.BackupFolder;
        telemetry.LastArchivePath = update.ArchivePath;
        telemetry.LastResumeOffsetBytes = Math.Max(0, update.ResumeOffsetBytes);
        telemetry.LastArchiveSizeBytes = Math.Max(0, update.ArchiveSizeBytes);
        telemetry.LastSourceFingerprint = update.SourceFingerprint;
        telemetry.LastMessage = update.Message;
    }

    private static void PersistCheckpointResumeTelemetry(
        CheckpointResumeTelemetryUpdate update,
        IAppConfigStore? configStore = null)
    {
        try
        {
            IAppConfigStore store = configStore ?? StaticAppConfigStore.Instance;
            AppConfig cfg = store.Load();
            UpdateCheckpointResumeTelemetry(cfg, update);
            store.Save(cfg);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[BackupService] Failed to persist checkpoint resume telemetry: {ex.Message}");
        }
    }

    public sealed record BackupRunResult(int BackupId, bool SkippedForNoChanges, bool Cancelled);
    public sealed record BackupPreflightResult(
        long TotalBytes,
        int TotalFiles,
        long? VolumeTotalBytes,
        long? VolumeFreeBytes,
        bool HasEnoughSpace,
        double? EstimatedSeconds,
        double EstimatedThroughputMbSec,
        string? WarningMessage,
        bool UsedCache);

    private static readonly ConcurrentDictionary<string, (DateTime TimestampUtc, BackupPreflightResult Result)> PreflightCache = new();
    private static readonly ConcurrentDictionary<string, (DateTime TimestampUtc, int TotalFiles, long TotalBytes)> StatsCache = new();
    private const int PreflightCacheLimit = 128;
    private const int StatsCacheLimit = 128;
    private static readonly HashSet<string> ArchiveNoCompressionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".cab", ".iso",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff",
        ".mp3", ".aac", ".ogg", ".flac", ".m4a", ".wav",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm",
        ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp",
        ".sqlite", ".db", ".parquet", ".feather"
    };
    private static readonly HashSet<string> ArchiveOptimalCompressionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".csv", ".tsv", ".log", ".sql",
        ".cs", ".fs", ".vb", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".kt", ".swift",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh", ".rs", ".go", ".php", ".rb", ".sh",
        ".html", ".css", ".scss", ".less"
    };

    public void CancelBackup(int projectId)
    {
        RuntimeLog.WriteVerbose($"[BackupService] Cancel requested for projectId={projectId}.");
        if (!_cancellationRegistry.Cancel(projectId))
        {
            RuntimeLog.WriteVerbose(
                $"[BackupService] Cancel ignored; no active registration exists for projectId={projectId}.");
        }
    }

    private static void DeletePartialBackup(string backupFolder)
    {
        try
        {
            if (Directory.Exists(backupFolder))
                Directory.Delete(backupFolder, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to delete partial backup '{backupFolder}': {ex.Message}");
        }
    }

    private static void WriteMarkerFile(string backupFolder, string fileName, string contents)
    {
        try
        {
            string markerPath = Path.Combine(backupFolder, fileName);
            File.WriteAllText(markerPath, contents);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to write marker '{fileName}' in '{backupFolder}': {ex.Message}");
        }
    }

    private static void RemoveMarkerFile(string backupFolder, string fileName)
    {
        try
        {
            string markerPath = Path.Combine(backupFolder, fileName);
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to remove marker '{fileName}' in '{backupFolder}': {ex.Message}");
        }
    }

    private static string GetArchiveResumeCheckpointPath(string backupFolder)
        => Path.Combine(backupFolder, ArchiveResumeCheckpointFileName);

    private static void WriteArchiveResumeCheckpoint(string backupFolder, ArchiveResumeCheckpoint checkpoint)
    {
        try
        {
            string path = GetArchiveResumeCheckpointPath(backupFolder);
            string json = JsonSerializer.Serialize(checkpoint, ResumeCheckpointJsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to write archive resume checkpoint in '{backupFolder}': {ex.Message}");
        }
    }

    private static ArchiveResumeCheckpoint? TryReadArchiveResumeCheckpoint(string backupFolder)
    {
        try
        {
            string path = GetArchiveResumeCheckpointPath(backupFolder);
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ArchiveResumeCheckpoint>(json, ResumeCheckpointJsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to read archive resume checkpoint in '{backupFolder}': {ex.Message}");
            return null;
        }
    }

    private static void RemoveArchiveResumeCheckpoint(string backupFolder)
    {
        try
        {
            string path = GetArchiveResumeCheckpointPath(backupFolder);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to remove archive resume checkpoint in '{backupFolder}': {ex.Message}");
        }
    }

    private static bool ValidateArchiveRange(
        string localArchive,
        string destinationArchive,
        long startOffset,
        long length,
        int bufferSize,
        CancellationToken ct)
    {
        if (length <= 0)
        {
            return true;
        }

        long remaining = length;
        int compareBuffer = Math.Max(64 * 1024, bufferSize);
        byte[] left = new byte[compareBuffer];
        byte[] right = new byte[compareBuffer];

        using var src = new FileStream(localArchive, FileMode.Open, FileAccess.Read, FileShare.Read, compareBuffer, FileOptions.SequentialScan);
        using var dst = new FileStream(destinationArchive, FileMode.Open, FileAccess.Read, FileShare.Read, compareBuffer, FileOptions.SequentialScan);

        src.Seek(startOffset, SeekOrigin.Begin);
        dst.Seek(startOffset, SeekOrigin.Begin);

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(compareBuffer, remaining);
            int srcRead = src.Read(left, 0, toRead);
            int dstRead = dst.Read(right, 0, toRead);
            if (srcRead != dstRead || srcRead != toRead)
            {
                return false;
            }

            for (int i = 0; i < toRead; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            remaining -= toRead;
        }

        return true;
    }

    internal static string BuildArchiveResumeFingerprint(string sourceDir, IEnumerable<string> files)
    {
        using var sha = SHA256.Create();
        IOrderedEnumerable<string> orderedFiles = files
            .Select(path => Path.GetFullPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (string? file in orderedFiles)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                {
                    RuntimeLog.WriteVerbose($"[BackupService] Skipping fingerprint entry for missing file '{file}'.");
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                RuntimeLog.WriteVerbose($"[BackupService] Skipping fingerprint entry for inaccessible file '{file}': {ex.Message}");
                continue;
            }

            string relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            string line;
            try
            {
                line = $"{relative}|{info.Length.ToString(CultureInfo.InvariantCulture)}|{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}\n";
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
            {
                RuntimeLog.WriteVerbose($"[BackupService] Skipping fingerprint entry after stat failure for '{file}': {ex.Message}");
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(line);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return HashService.FormatHex(sha.Hash ?? []);
    }

    private static bool IsResumableArchiveBackup(string backupDir)
    {
        if (!Directory.Exists(backupDir))
        {
            return false;
        }

        if (!File.Exists(Path.Combine(backupDir, InProgressMarkerFileName)))
        {
            return false;
        }

        ArchiveResumeCheckpoint? checkpoint = TryReadArchiveResumeCheckpoint(backupDir);
        if (!IsValidArchiveResumeCheckpoint(checkpoint))
        {
            return false;
        }

        string artifactFileName = checkpoint!.ArtifactFileName;
        return File.Exists(Path.Combine(backupDir, artifactFileName));
    }

    private static bool IsValidArchiveResumeCheckpoint(ArchiveResumeCheckpoint? checkpoint)
    {
        if (checkpoint is null ||
            checkpoint.Version != 1 ||
            !string.Equals(checkpoint.Mode, "archive", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(checkpoint.SourceFingerprint) ||
            checkpoint.ArchiveSizeBytes < 0 ||
            checkpoint.LastUpdatedUtc == default)
        {
            return false;
        }

        return string.Equals(
                   checkpoint.ArtifactFileName,
                   BackupArchiveCryptoService.PlainArchiveFileName,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   checkpoint.ArtifactFileName,
                   BackupArchiveCryptoService.EncryptedArchiveFileName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryFindResumableArchiveBackupFolder(string candidateDestDir, string sourceFingerprint)
    {
        string? projectDir = Path.GetDirectoryName(candidateDestDir);
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return null;
        }

        string? match = SafeEnumerateDirectories(projectDir)
            .Where(dir => !string.Equals(dir, candidateDestDir, StringComparison.OrdinalIgnoreCase))
            .Select(dir => new { Dir = dir, Checkpoint = TryReadArchiveResumeCheckpoint(dir) })
            .Where(item => item.Checkpoint is not null
                           && string.Equals(item.Checkpoint.Mode, "archive", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(item.Checkpoint.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
                           && IsResumableArchiveBackup(item.Dir))
            .OrderByDescending(item => item.Checkpoint!.LastUpdatedUtc)
            .Select(item => item.Dir)
            .FirstOrDefault();

        return match;
    }

    internal static bool ValidateArchiveResumePrefix(string localArchive, string destinationArchive, long length, int bufferSize, CancellationToken ct)
    {
        if (length <= 0)
        {
            return true;
        }

        long remaining = length;
        int compareBuffer = Math.Max(64 * 1024, bufferSize);
        byte[] left = new byte[compareBuffer];
        byte[] right = new byte[compareBuffer];

        using var src = new FileStream(localArchive, FileMode.Open, FileAccess.Read, FileShare.Read, compareBuffer, FileOptions.SequentialScan);
        using var dst = new FileStream(destinationArchive, FileMode.Open, FileAccess.Read, FileShare.Read, compareBuffer, FileOptions.SequentialScan);

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(compareBuffer, remaining);
            int srcRead = src.Read(left, 0, toRead);
            int dstRead = dst.Read(right, 0, toRead);
            if (srcRead != dstRead || srcRead != toRead)
            {
                return false;
            }

            for (int i = 0; i < toRead; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            remaining -= toRead;
        }

        return true;
    }

    private static bool IsNetworkPathOrDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("//", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var drive = new DriveInfo(root);
            if (OperatingSystem.IsWindows() && drive.DriveType == DriveType.Network)
                return true;

            if (OperatingSystem.IsMacOS() && root.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            return false;
        }

        return false;
    }

    public async Task<BackupPreflightResult> PreflightBackupAsync(
        Project project,
        string backupRoot,
        double? throughputMbSec = null,
        bool useArchiveMode = false,
        TimeSpan? cacheTtl = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");
        if (string.IsNullOrWhiteSpace(backupRoot))
            throw new InvalidOperationException("Backup root is empty. Configure a backup location in Settings.");

        backupRoot = Path.GetFullPath(backupRoot);
        if (!Directory.Exists(backupRoot))
        {
            throw new InvalidOperationException(
                $"Backup root '{backupRoot}' does not exist or is not accessible. " +
                "Make sure the path exists and any network share is mounted.");
        }

        TimeSpan ttl = cacheTtl ?? TimeSpan.FromSeconds(30);
        string cacheKey = $"{project.Id}|{backupRoot}|{project.Preset}|{useArchiveMode}";
        if (TryGetCachedPreflight(cacheKey, ttl, out BackupPreflightResult? cached))
        {
            return cached with
            {
                UsedCache = true
            };
        }

        (int totalFiles, long totalBytes) = await Task.Run(() => GetBackupStats(project, ttl, ct), ct);
        (long totalBytes, long freeBytes)? space = TryGetDiskSpace(backupRoot);
        long? volumeTotal = space?.totalBytes;
        long? volumeFree = space?.freeBytes;
        long requiredBytes = totalBytes;
        if (useArchiveMode && requiredBytes > 0)
        {
            requiredBytes = (long)Math.Ceiling(requiredBytes * 1.05d);
        }
        bool hasEnoughSpace = volumeFree is not long freeBytes || freeBytes >= requiredBytes;
        string? warning = volumeFree is long knownFreeBytes && knownFreeBytes < requiredBytes
            ? $"Backup may not fit on the target. Required={requiredBytes} bytes, Free={knownFreeBytes} bytes."
            : null;

        double fallbackThroughput = GetFallbackThroughputMbSec(backupRoot, useArchiveMode);
        double usedThroughput = throughputMbSec > 0
            ? throughputMbSec.Value
            : fallbackThroughput;

        double? estimatedSeconds = totalBytes > 0 && usedThroughput > 0
            ? totalBytes / (usedThroughput * 1024d * 1024d)
            : (double?)null;

        var result = new BackupPreflightResult(
            totalBytes,
            totalFiles,
            volumeTotal,
            volumeFree,
            hasEnoughSpace,
            estimatedSeconds,
            usedThroughput,
            warning,
            UsedCache: false);

        PreflightCache[cacheKey] = (DateTime.UtcNow, result);
        TrimPreflightCache();
        return result;
    }

    private static bool TryGetCachedPreflight(
        string cacheKey,
        TimeSpan ttl,
        out BackupPreflightResult result)
    {
        if (PreflightCache.TryGetValue(cacheKey, out (DateTime TimestampUtc, BackupPreflightResult Result) cached) &&
            (DateTime.UtcNow - cached.TimestampUtc) <= ttl)
        {
            result = cached.Result;
            return true;
        }

        result = default!;
        return false;
    }

    private (int totalFiles, long totalBytes) GetBackupStats(
        Project project,
        TimeSpan ttl,
        CancellationToken ct)
    {
        Snapshot? snapshot = _repo.GetLatestLocalSnapshotForProject(project.Id);
        if (snapshot is not null)
        {
            int fileCount = Convert.ToInt32(snapshot.FileCount);
            long totalBytes = snapshot.TotalBytes;
            return (fileCount, totalBytes);
        }

        string statsKey = $"{project.RootPath}|{project.Preset}";
        if (StatsCache.TryGetValue(statsKey, out (DateTime TimestampUtc, int TotalFiles, long TotalBytes) cached) &&
            (DateTime.UtcNow - cached.TimestampUtc) <= ttl)
        {
            return (cached.TotalFiles, cached.TotalBytes);
        }

        (int totalFiles, long totalBytes) computed = ComputeBackupStats(project.RootPath, project.Preset, ct);
        StatsCache[statsKey] = (DateTime.UtcNow, computed.totalFiles, computed.totalBytes);
        TrimStatsCache();
        return computed;
    }

    private static void TrimPreflightCache()
    {
        if (PreflightCache.Count <= PreflightCacheLimit)
            return;

        foreach (KeyValuePair<string, (DateTime TimestampUtc, BackupPreflightResult Result)> entry in PreflightCache
                     .OrderBy(kvp => kvp.Value.TimestampUtc)
                     .Take(Math.Max(1, PreflightCache.Count - PreflightCacheLimit)))
        {
            PreflightCache.TryRemove(entry.Key, out _);
        }
    }

    private static void TrimStatsCache()
    {
        if (StatsCache.Count <= StatsCacheLimit)
            return;

        foreach (KeyValuePair<string, (DateTime TimestampUtc, int TotalFiles, long TotalBytes)> entry in StatsCache
                     .OrderBy(kvp => kvp.Value.TimestampUtc)
                     .Take(Math.Max(1, StatsCache.Count - StatsCacheLimit)))
        {
            StatsCache.TryRemove(entry.Key, out _);
        }
    }

    private static double GetFallbackThroughputMbSec(string backupRoot, bool useArchiveMode)
    {
        bool isNetwork = IsNetworkPathOrDrive(backupRoot);
        if (useArchiveMode)
        {
            return isNetwork ? 18d : 60d;
        }

        return isNetwork ? 25d : 80d;
    }

    public static int CleanupIncompleteBackups(
        string backupRoot,
        IEnumerable<string>? projectFolderNames = null,
        IAppConfigStore? configStore = null)
    {
        if (string.IsNullOrWhiteSpace(backupRoot) || !Directory.Exists(backupRoot))
            return 0;

        int removed = 0;
        var projectDirs = projectFolderNames?.ToList();
        if (projectDirs is { Count: > 0 })
        {
            foreach (string? folder in projectDirs)
            {
                if (string.IsNullOrWhiteSpace(folder))
                    continue;

                string projectDir = Path.Combine(backupRoot, folder);
                if (!Directory.Exists(projectDir))
                    continue;

                removed += CleanupIncompleteBackupsUnderProject(projectDir, configStore);
            }

            return removed;
        }

        foreach (string projectDir in SafeEnumerateDirectories(backupRoot))
        {
            removed += CleanupIncompleteBackupsUnderProject(projectDir, configStore);
        }

        return removed;
    }

    private static int CleanupIncompleteBackupsUnderProject(string projectDir, IAppConfigStore? configStore)
    {
        int removed = 0;
        foreach (string backupDir in SafeEnumerateDirectories(projectDir))
        {
            try
            {
                string markerPath = Path.Combine(backupDir, InProgressMarkerFileName);
                if (!File.Exists(markerPath))
                    continue;

                if (IsResumableArchiveBackup(backupDir))
                {
                    RuntimeLog.WriteVerbose($"[BackupService] Preserving resumable incomplete archive '{backupDir}' for checkpoint retry.");
                    ArchiveResumeCheckpoint? checkpoint = TryReadArchiveResumeCheckpoint(backupDir);
                    string artifactFileName = string.IsNullOrWhiteSpace(checkpoint?.ArtifactFileName)
                        ? BackupArchiveCryptoService.PlainArchiveFileName
                        : Path.GetFileName(checkpoint.ArtifactFileName);
                    string artifactPath = Path.Combine(backupDir, artifactFileName);
                    PersistCheckpointResumeTelemetry(
                        new CheckpointResumeTelemetryUpdate
                        {
                            Status = "cleanup-preserved",
                            ProjectName = Path.GetFileName(projectDir),
                            BackupFolder = backupDir,
                            ArchivePath = artifactPath,
                            ResumeOffsetBytes = File.Exists(artifactPath)
                                ? new FileInfo(artifactPath).Length
                                : 0,
                            ArchiveSizeBytes = checkpoint?.ArchiveSizeBytes ?? 0,
                            SourceFingerprint = checkpoint?.SourceFingerprint ?? string.Empty,
                            Message = "Preserved interrupted archive backup because checkpoint resume metadata is valid."
                        },
                        configStore);
                    continue;
                }

                DeletePartialBackup(backupDir);
                removed++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                Console.WriteLine($"[BackupService] Skipping incomplete backup cleanup for '{backupDir}': {ex.Message}");
            }
        }

        return removed;
    }

    private static string[] SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
        {
            RuntimeLog.WriteVerbose($"[BackupService] Skipping directory scan for '{root}': {ex.Message}");
            return [];
        }
    }

    public void EnforceRetentionForProject(int projectId, string backupRoot, int? maxSnapshotsToKeep)
    {
        ApplyBackupRetention(projectId, backupRoot, maxSnapshotsToKeep);
    }

    /// <summary>
    /// Creates a full backup of the given project under the specified backup root.
    /// The backup is associated with the latest snapshot for the project.
    /// </summary>
    /// <param name="project">Project to back up.</param>
    /// <param name="backupRoot">
    /// Root folder where backups are stored (e.g. from AppConfig.Backups.BackupLocation).
    /// </param>
    /// <param name="isAuto">
    /// Whether this backup should be marked as "auto" or "manual" in the database.
    /// </param>
    /// <param name="progressCallback">Callback for progress updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="useArchiveMode">Whether to create a compressed archive instead of using native sync tools.</param>
    /// <param name="fullSnapshotHash">Whether to hash all files when creating the pre-backup snapshot.</param>
    /// <param name="preferredFinalBackupRoot">
    /// Optional final backup root to move into after creation (e.g., NAS path). If provided and different
    /// from <paramref name="backupRoot"/>, a best-effort move will be attempted after the backup completes.
    /// </param>
    /// <param name="reuseSnapshotId">
    /// Optional snapshot ID to reuse when writing the backup metadata. When null, a fresh
    /// snapshot is created before the backup.
    /// </param>
    /// <param name="writeMetadata">
    /// When false, backup data is written but database metadata/retention is skipped.
    /// Useful for mirror destinations that should not create duplicate history entries.
    /// </param>
    /// <returns>Backup run metadata including the created backup ID.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the project has no snapshots yet, or backupRoot is not configured.
    /// </exception>
    public async Task<BackupRunResult> RunBackupAsync(
        Project project,
        string backupRoot,
        bool isAuto,
        Action<double, string, string>? progressCallback = null,
        bool useArchiveMode = false,
        bool fullSnapshotHash = true,
        int? maxSnapshotsToKeep = null,
        double? minimumFreeSpacePercent = null,
        string? preferredFinalBackupRoot = null,
        int? reuseSnapshotId = null,
        bool writeMetadata = true,
        string? destinationPath = null,
        string? destinationAlias = null,
        bool skipIfNoChanges = false,
        bool useRsyncDelta = false,
        bool useIncrementalBackups = false,
        int? archiveUploadBufferBytes = null,
        bool preferRunnerProgressOnly = false,
        bool preferParallelArchiveUpload = false,
        bool useScanCache = false,
        bool aggressiveScanCache = false,
        bool enableCheckpointedRetry = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");

        if (string.IsNullOrWhiteSpace(backupRoot))
            throw new InvalidOperationException("Backup root is empty. Configure a backup location in Settings.");

        AppConfig configSnapshot = _configStore.Load();
        BackupEncryptionConfig encryptionConfig = configSnapshot.Backups.Encryption ?? new BackupEncryptionConfig();
        BackupEncryptionPolicyResolver.ResolvedPolicy resolvedEncryption = BackupEncryptionPolicyResolver.Resolve(project, encryptionConfig);
        bool encryptionRequested = resolvedEncryption.EncryptionRequested;
        if (encryptionRequested && !useArchiveMode)
        {
            // Encrypted backups currently require archive artifacts.
            useArchiveMode = true;
            RuntimeLog.WriteVerbose($"[BackupService] Encryption enabled; forcing archive mode for project '{project.Name}'.");
        }

        bool backupIsEncrypted = false;
        string backupCryptoDescriptorJson = BackupCryptoDescriptor.PlainMetadataJson;
        string? encryptionPassword = null;
        if (encryptionRequested)
        {
            if (string.IsNullOrWhiteSpace(resolvedEncryption.EffectiveKeyRef))
            {
                throw new InvalidOperationException(
                    "Backup encryption is enabled for this project but no encryption key reference is configured.");
            }

            encryptionPassword = _backupEncryptionSecretService.GetSecret(
                resolvedEncryption.EffectiveKeyRef,
                username: BackupEncryptionCredentialIdentity.AccountName);
            if (string.IsNullOrWhiteSpace(encryptionPassword))
            {
                throw new InvalidOperationException(
                    $"Backup encryption is enabled but no encryption secret is available for the resolved {resolvedEncryption.KeySource} key.");
            }
        }

        using BackupCancellationRegistry.Registration cancellationRegistration =
            _cancellationRegistry.Register(project.Id, ct);
        CancellationToken linkedToken = cancellationRegistration.Token;
        await using CancellationTokenRegistration cancelLog = linkedToken.Register(() =>
            RuntimeLog.WriteVerbose($"[BackupService] Cancellation observed for project '{project.Name}' (Id={project.Id})."));

        linkedToken.ThrowIfCancellationRequested();

        RuntimeLog.WriteVerbose($"[BackupService] RunBackupAsync entered for project '{project.Name}' (Id={project.Id}), backupRoot='{backupRoot}', isAuto={isAuto}, useArchiveMode={useArchiveMode}");
        progressCallback?.Invoke(0, "Preparing backup...", string.Empty);

        // Normalise backup root and ensure it exists (e.g. mounted NAS/share).
        backupRoot = Path.GetFullPath(backupRoot);
        if (ShouldRejectUnbackedManagedMount(
                OperatingSystem.IsMacOS(),
                IsMacManagedMountPath(backupRoot),
                IsNetworkMountPath(backupRoot)))
        {
            throw new InvalidOperationException(
                $"Backup root '{backupRoot}' is a VaultSync-managed network mount point, but its share is not mounted. " +
                "The backup was stopped to avoid writing remote backup data onto the local system drive.");
        }
        if (!Directory.Exists(backupRoot))
        {
            throw new InvalidOperationException(
                $"Backup root '{backupRoot}' does not exist or is not accessible. " +
                "Make sure the path exists and any network share is mounted.");
        }

        // Project-specific backup root: <backupRoot>/project-slug/
        string projectSlug = Slugify(project.Name);
        string projectBackupRoot = Path.Combine(backupRoot, projectSlug);
        Directory.CreateDirectory(projectBackupRoot);

        // Optional low-disk protection: check free space on the backup target volume
        if (minimumFreeSpacePercent is > 0)
        {
            (long totalBytes, long freeBytes)? space = TryGetDiskSpace(projectBackupRoot);
            if (space is not null)
            {
                (long volumeTotalBytes, long volumeFreeBytes) = space.Value;
                if (TryCalculateFreeSpacePercent(volumeTotalBytes, volumeFreeBytes, out double freePercent))
                {
                    RuntimeLog.WriteVerbose($"[BackupService] Backup target free space for '{project.Name}': {freePercent:0.0}% remaining (threshold={minimumFreeSpacePercent.Value:0.0}%).");

                    if (freePercent < minimumFreeSpacePercent.Value)
                    {
                        throw new InvalidOperationException(
                            $"Backup target for '{project.Name}' does not have enough free space. Free={freePercent:0.0}% (threshold={minimumFreeSpacePercent.Value:0.0}%).");
                    }
                }
            }
        }

        // Timestamped folder name: 2025-11-16_15-47-30
        DateTime timestamp = DateTime.UtcNow;
        string folderName = timestamp.ToString("yyyy-MM-dd_HH-mm-ss");
        string backupFolder = GetAvailableBackupFolder(projectBackupRoot, folderName);
        Directory.CreateDirectory(backupFolder);
        WriteMarkerFile(backupFolder, InProgressMarkerFileName, $"started:{DateTime.UtcNow:O}");
        string backupRootUsed = backupRoot;
        string backupFolderUsed = backupFolder;

        long totalBytes = 0;

        int snapshotId;
        int totalFilesForProgress = 0;
        List<FileEntry>? filesForProgress = null;
        if (reuseSnapshotId.HasValue)
        {
            snapshotId = reuseSnapshotId.Value;
            progressCallback?.Invoke(0, "Reusing existing snapshot...", string.Empty);
        }
        else
        {
            // Always create a fresh snapshot before backing up so history stays aligned.
            progressCallback?.Invoke(0, "Preparing backup...", string.Empty);
            var snapshotService = new SnapshotService(_repo, new HashService());
            snapshotId = await snapshotService.CreateSnapshotAsync(
                project,
                fullSnapshotHash,
                new SnapshotCreationOptions
                {
                    HashNow = false,
                    MaxSnapshotsToKeep = maxSnapshotsToKeep,
                    ProgressCallback = (percent, currentFile, etaText) =>
                        progressCallback?.Invoke(percent, currentFile, etaText),
                    UseScanCache = useScanCache,
                    AggressiveScanCache = aggressiveScanCache
                },
                linkedToken);

            SnapshotOutcome? outcome = snapshotService.LastCreatedOutcome;
            if (skipIfNoChanges &&
                !reuseSnapshotId.HasValue &&
                outcome is { Added: 0, Modified: 0, Deleted: 0 } &&
                HasUsableBackupForDestination(project.Id, backupRoot))
            {
                // No file changes: remove the empty backup folder and snapshot, then skip.
                DeletePartialBackup(backupFolderUsed);
                _repo.DeleteSnapshotsById(project.Name, [snapshotId]);
                progressCallback?.Invoke(100, string.Empty, "No changes detected; backup skipped.");
                return new BackupRunResult(0, true, false);
            }
        }

        Snapshot? snapshot = _repo.GetSnapshotById(snapshotId);
        if (snapshot is not null)
        {
            totalFilesForProgress = Convert.ToInt32(snapshot.FileCount);
            totalBytes = snapshot.TotalBytes;
        }

        bool needFileList = useArchiveMode || (progressCallback is not null && !preferRunnerProgressOnly);
        if (needFileList)
        {
            try
            {
                filesForProgress = await _repo.GetFilesForSnapshotAsync(snapshotId, linkedToken).ConfigureAwait(false);
                if (snapshot is null)
                {
                    totalFilesForProgress = filesForProgress.Count;
                    totalBytes = filesForProgress.Sum(f => f.Size);
                }
                else if (totalFilesForProgress <= 0)
                {
                    totalFilesForProgress = filesForProgress.Count;
                }
            }
            catch
            {
                totalFilesForProgress = 0;
                filesForProgress = null;
                if (snapshot is null)
                {
                    totalBytes = 0;
                }
            }
        }

        string[]? filesForBackup = null;
        if (filesForProgress is { Count: > 0 })
        {
            filesForBackup = [.. filesForProgress
                .Select(f => ResolveSnapshotSourceFile(project.RootPath, f.RelPath))
            ];
        }

        RuntimeLog.WriteVerbose($"[BackupService] Starting backup for '{project.Name}' ({project.RootPath}), totalBytes={totalBytes}.");

        string? linkDest = null;
        if (!useArchiveMode && useIncrementalBackups)
        {
            linkDest = TryGetPreviousBackupFolder(projectBackupRoot, backupFolder);
            if (linkDest is null)
            {
                RuntimeLog.WriteVerbose($"[BackupService] Incremental enabled but no previous backup found for '{project.Name}'.");
            }
            else
            {
                RuntimeLog.WriteVerbose($"[BackupService] Using incremental link-dest '{linkDest}'.");
            }
        }

        try
        {
            linkedToken.ThrowIfCancellationRequested();

            if (useArchiveMode)
            {
                progressCallback?.Invoke(0, "Preparing archive backup...", string.Empty);

                int uploadBufferBytes = NormalizeArchiveUploadBufferBytes(archiveUploadBufferBytes);
                ArchiveBackupResult archiveResult = await RunArchiveBackupAsync(
                    project,
                    backupFolder,
                    totalBytes,
                    totalFilesForProgress,
                    filesForBackup,
                    progressCallback,
                    uploadBufferBytes,
                    preferParallelArchiveUpload,
                    enableCheckpointedRetry,
                    _configStore,
                    encryptionPassword,
                    encryptionConfig,
                    linkedToken);
                backupFolderUsed = archiveResult.BackupFolder;
                backupIsEncrypted = archiveResult.IsEncrypted;
                backupCryptoDescriptorJson = archiveResult.Descriptor.ToMetadataJson(backupIsEncrypted);
            }
            else
            {
                progressCallback?.Invoke(0, "Copying files...", string.Empty);

                await RunNativeBackupAsync(new NativeBackupRequest
                {
                    Project = project,
                    DestinationDirectory = backupFolder,
                    TotalBytes = totalBytes,
                    TotalFiles = totalFilesForProgress,
                    FilesForProgress = filesForProgress,
                    ProgressCallback = progressCallback,
                    UseRsyncDelta = useRsyncDelta,
                    UseIncrementalBackups = useIncrementalBackups,
                    LinkDestination = linkDest,
                    MaxBandwidthMbps = TransferPolicy.NormalizeBandwidthLimitMbps(
                        configSnapshot.Backups.EnableBandwidthLimit,
                        configSnapshot.Backups.MaxBandwidthMbps),
                    PreferRunnerProgressOnly = preferRunnerProgressOnly,
                    CancellationToken = linkedToken
                });
            }
        }
        catch (Exception ex)
        {
            if (linkedToken.IsCancellationRequested)
            {
                RuntimeLog.WriteVerbose($"[BackupService] Backup cancelled for '{project.Name}'. Cleaning up.");
                DeletePartialBackup(backupFolderUsed);
                if (!string.Equals(backupFolderUsed, backupFolder, StringComparison.OrdinalIgnoreCase))
                    DeletePartialBackup(backupFolder);
                RemoveMarkerFile(backupFolderUsed, InProgressMarkerFileName);
                RemoveMarkerFile(backupFolder, InProgressMarkerFileName);
                return new BackupRunResult(0, false, true);
            }

            if (useArchiveMode)
            {
                Console.WriteLine($"[BackupService] Archive backup failed for '{project.Name}': {ex}");
                throw;
            }

            // If the native tool is not available or fails unexpectedly, fall back
            // to the managed File.Copy-based implementation so the backup still
            // succeeds (albeit more slowly).
            Console.WriteLine($"[BackupService] Backup phase failed for '{project.Name}', falling back to managed copy. Exception: {ex}");

            totalBytes = await Task.Run(() =>
            {
                long bytes = 0;
                try
                {
                    CopyDirectoryRecursive(project.RootPath, backupFolder, project.Preset, filesForBackup, ref bytes, progressCallback, linkedToken);
                }
                catch (OperationCanceledException)
                {
                    DeletePartialBackup(backupFolder);
                    throw;
                }
                return bytes;
            }, linkedToken);

            progressCallback?.Invoke(100, string.Empty, "Backup completed (fallback).");
        }

        // If we created the backup in a temporary location (e.g., NAS unreachable) but the preferred
        // final backup root is available now, move it before writing metadata.
        if (!string.IsNullOrWhiteSpace(preferredFinalBackupRoot) &&
            !string.Equals(preferredFinalBackupRoot, backupRoot, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(preferredFinalBackupRoot))
                {
                    string destProjectRoot = Path.Combine(preferredFinalBackupRoot, projectSlug);
                    Directory.CreateDirectory(destProjectRoot);

                    string destFolder = Path.Combine(destProjectRoot, Path.GetFileName(backupFolder));
                    if (Directory.Exists(destFolder))
                        Directory.Delete(destFolder, recursive: true);

                    Directory.Move(backupFolder, destFolder);
                    backupRootUsed = preferredFinalBackupRoot;
                    backupFolderUsed = destFolder;

                    // Clean up empty temp project root if applicable
                    try
                    {
                        string? tempProjectRoot = Path.GetDirectoryName(backupFolder);
                        if (!string.IsNullOrWhiteSpace(tempProjectRoot) &&
                            Directory.Exists(tempProjectRoot) &&
                            !Directory.EnumerateFileSystemEntries(tempProjectRoot).Any())
                        {
                            Directory.Delete(tempProjectRoot, recursive: true);
                        }
                    }
                    catch
                    {
                        // ignore cleanup failures
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupService] Failed to move backup from temp to preferred root: {ex.Message}");
            }
        }

        if (linkedToken.IsCancellationRequested)
        {
            RuntimeLog.WriteVerbose($"[BackupService] Backup cancelled before metadata publication for '{project.Name}'. Cleaning up.");
            DeletePartialBackup(backupFolderUsed);
            if (!string.Equals(backupFolderUsed, backupFolder, StringComparison.OrdinalIgnoreCase))
                DeletePartialBackup(backupFolder);
            return new BackupRunResult(0, false, true);
        }

        try
        {
            EnsureBackupDataReadyForCommit(backupFolderUsed, useArchiveMode, backupIsEncrypted);
        }
        catch
        {
            DeletePartialBackup(backupFolderUsed);
            if (!string.Equals(backupFolderUsed, backupFolder, StringComparison.OrdinalIgnoreCase))
                DeletePartialBackup(backupFolder);
            throw;
        }

        // Store relative path so if backupRoot moves, paths are still valid.
        string relativePath = Path.GetRelativePath(backupRootUsed, backupFolderUsed);
        string backupType = isAuto ? "auto" : "manual";
        string backupMode = !useArchiveMode &&
                         useIncrementalBackups &&
                         !string.IsNullOrWhiteSpace(linkDest)
            ? BackupModes.Incremental
            : BackupModes.Full;

        RuntimeLog.WriteVerbose($"[BackupService] Backup data written for '{project.Name}', creating backup metadata in database...");

        int backupId = 0;

        if (writeMetadata)
        {
            // Persist metadata in the backups table
            string metadataRoot = !string.IsNullOrWhiteSpace(destinationPath)
                ? destinationPath
                : backupRootUsed;
            string metadataAlias = !string.IsNullOrWhiteSpace(destinationAlias)
                ? destinationAlias
                : string.Empty;

            backupId = _repo.CreateBackup(new BackupWriteRequest(
                ProjectId: project.Id,
                SnapshotId: snapshotId,
                Type: backupType,
                TotalBytes: totalBytes,
                RelativePath: relativePath,
                DestinationPath: metadataRoot,
                DestinationAlias: metadataAlias,
                BackupMode: backupMode,
                IsEncrypted: backupIsEncrypted,
                CryptoDescriptorJson: backupCryptoDescriptorJson));

            RuntimeLog.WriteVerbose($"[BackupService] Backup metadata created successfully for '{project.Name}' (backupId={backupId}).");

            // Apply simple retention: keep only the most recent N backups per project, if configured.
            try
            {
                ApplyBackupRetention(project.Id, backupRootUsed, maxSnapshotsToKeep);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupService] Retention step failed for project '{project.Name}': {ex}");
            }
        }

        RemoveMarkerFile(backupFolderUsed, InProgressMarkerFileName);
        if (!IsNetworkPathOrDrive(backupFolderUsed))
        {
            WriteMarkerFile(backupFolderUsed, CompletedMarkerFileName, $"completed:{DateTime.UtcNow:O}");
        }
        else
        {
            RuntimeLog.WriteVerbose($"[BackupService] Skipping completion marker on network destination '{backupFolderUsed}'.");
        }

        string completedMessage;
        if (backupIsEncrypted)
            completedMessage = "Backup completed (encrypted).";
        else if (useArchiveMode)
            completedMessage = "Backup completed (archive).";
        else
            completedMessage = "Backup completed.";
        progressCallback?.Invoke(100, string.Empty, completedMessage);

        // Metadata and the completion marker are the success commit point. A
        // cancellation requested by the final progress notification must not
        // invalidate or delete the already committed backup.
        return new BackupRunResult(backupId, false, false);
    }

    private static void EnsureBackupDataReadyForCommit(
        string backupFolder,
        bool useArchiveMode,
        bool isEncrypted)
    {
        if (!Directory.Exists(backupFolder))
        {
            throw new IOException(
                $"Backup destination disappeared before metadata could be committed: '{backupFolder}'.");
        }

        if (!useArchiveMode)
            return;

        string artifactName = isEncrypted
            ? BackupArchiveCryptoService.EncryptedArchiveFileName
            : BackupArchiveCryptoService.PlainArchiveFileName;
        string artifactPath = Path.Combine(backupFolder, artifactName);
        if (!File.Exists(artifactPath) || new FileInfo(artifactPath).Length <= 0)
        {
            throw new IOException(
                $"Backup archive disappeared or is empty before metadata could be committed: '{artifactPath}'.");
        }

        if (isEncrypted && !File.Exists(Path.Combine(backupFolder, BackupArchiveCryptoService.MetadataFileName)))
        {
            throw new IOException(
                $"Encrypted backup metadata disappeared before the backup could be committed: '{backupFolder}'.");
        }
    }

    /// <summary>
    /// Uses the platform-specific sync runner (rsync on macOS/Linux, robocopy on Windows)
    /// to perform a fast backup of the project into the given destination folder.
    /// Throws if the tool is missing or returns a failure exit code.
    /// </summary>
    private static async Task RunNativeBackupAsync(NativeBackupRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();

        // Normalise destination trailing separator for the runners.
        string destDir = request.DestinationDirectory;
        if (!destDir.EndsWith(Path.DirectorySeparatorChar))
            destDir += Path.DirectorySeparatorChar;

        await using NativeBackupProgressSession progress = StartNativeBackupProgress(request, destDir);
        bool isNetworkDestination = IsNetworkPath(destDir);
        bool effectiveUseRsyncDelta = ShouldUseRsyncDelta(request, isNetworkDestination);

        if (OperatingSystem.IsWindows())
            await RunWindowsNativeBackupAsync(request, destDir, progress.RunnerCallback, isNetworkDestination, effectiveUseRsyncDelta);
        else
            await RunRsyncBackupAsync(request, destDir, progress.RunnerCallback, effectiveUseRsyncDelta);
    }

    private static bool ShouldUseRsyncDelta(NativeBackupRequest request, bool isNetworkDestination) =>
        request.UseRsyncDelta ||
        (OperatingSystem.IsWindows() &&
         isNetworkDestination &&
         !request.UseIncrementalBackups &&
         (TryGetBundledRsyncPath() is not null || IsOnPath(RsyncExecutableName)));

    private static async Task RunWindowsNativeBackupAsync(
        NativeBackupRequest request,
        string destDir,
        Action<double, string, string>? progressCallback,
        bool isNetworkDestination,
        bool useRsyncDelta)
    {
        string? bundledRsync = TryGetBundledRsyncPath();
        bool rsyncAvailable = bundledRsync is not null || IsOnPath(RsyncExecutableName);
        if ((useRsyncDelta || request.UseIncrementalBackups) && rsyncAvailable)
        {
            await RunRsyncBackupAsync(request, destDir, progressCallback, useRsyncDelta, bundledRsync);
            return;
        }

        if ((useRsyncDelta || request.UseIncrementalBackups) && !rsyncAvailable)
            RuntimeLog.WriteVerbose("[BackupService] rsync not found on PATH; falling back to robocopy.");

        int threads = isNetworkDestination
            ? Math.Min(32, Math.Max(4, Environment.ProcessorCount))
            : Math.Min(128, Math.Max(8, Environment.ProcessorCount * 2));
        RuntimeLog.WriteVerbose($"[BackupService] Starting robocopy backup (threads={threads}, bw={(request.MaxBandwidthMbps is > 0 ? $"{request.MaxBandwidthMbps}Mbps" : "unlimited")}).");
        var runner = new RobocopyRunner(isNetworkDestination);
        int exitCode = await runner.SyncAsync(
            request.Project,
            destDir,
            dryRun: false,
            progressCallback,
            request.MaxBandwidthMbps,
            request.CancellationToken);

        if (exitCode != 0)
            throw new InvalidOperationException($"robocopy backup failed with exit code {exitCode}. See RobocopyRunner logs above for stdout/stderr.");
    }

    private static async Task RunRsyncBackupAsync(
        NativeBackupRequest request,
        string destDir,
        Action<double, string, string>? progressCallback,
        bool useRsyncDelta,
        string? bundledRsync = null)
    {
        string source = bundledRsync is null ? "PATH" : "bundled";
        int? bandwidthLimit = request.MaxBandwidthMbps is > 0
            ? TransferPolicy.ToRsyncBwLimitKbps(request.MaxBandwidthMbps.Value)
            : null;
        string sourceDetail = OperatingSystem.IsWindows() ? $"source={source}, " : string.Empty;
        RuntimeLog.WriteVerbose($"[BackupService] Starting rsync backup ({sourceDetail}delta={useRsyncDelta}, incremental={request.UseIncrementalBackups}, bw={(bandwidthLimit is > 0 ? $"{bandwidthLimit}KB/s" : "unlimited")}).");
        if (OperatingSystem.IsWindows())
            RuntimeLog.WriteVerbose($"[BackupService] Using rsync on Windows ({source}).");

        var runner = new RsyncRunner(useWholeFile: !useRsyncDelta, rsyncPath: bundledRsync ?? RsyncExecutableName);
        int exitCode = await runner.SyncAsync(
            request.Project,
            destDir,
            dryRun: false,
            progressCallback,
            request.UseIncrementalBackups ? request.LinkDestination : null,
            bandwidthLimit,
            request.CancellationToken);

        if (exitCode != 0)
            throw new InvalidOperationException($"rsync backup failed with exit code {exitCode}.");
    }

    private static NativeBackupProgressSession StartNativeBackupProgress(
        NativeBackupRequest request,
        string destDir)
    {
        bool useHybridMonitor = request.ProgressCallback is not null
            && request.TotalBytes > 0
            && request.FilesForProgress is { Count: > 0 }
            && !request.PreferRunnerProgressOnly;

        if (useHybridMonitor)
        {
            RuntimeLog.WriteVerbose("[BackupService] Progress monitor enabled (destination scans for progress).");
            var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
            var runnerState = new RunnerProgressState();
            Task monitorTask = MonitorCopyProgressAsync(
                destDir,
                request.FilesForProgress!,
                request.TotalBytes,
                request.ProgressCallback!,
                runnerState,
                monitorCts.Token);
            return new NativeBackupProgressSession(runnerState.Update, monitorCts, monitorTask);
        }

        if (request.ProgressCallback is null || request.TotalBytes <= 0)
            return new NativeBackupProgressSession(request.ProgressCallback);

        var decorator = new RunnerProgressDecorator(
            request.TotalBytes,
            request.TotalFiles,
            request.ProgressCallback);
        return new NativeBackupProgressSession(
            (percent, currentFile, _) => decorator.Report(percent, currentFile));
    }

    private static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private static async Task MonitorCopyProgressAsync(
        string destDir,
        List<FileEntry> filesForProgress,
        long totalBytes,
        Action<double, string, string> progressCallback,
        RunnerProgressState? runnerState,
        CancellationToken ct)
    {
        DateTime startTime = DateTime.UtcNow;
        DateTime lastSample = startTime;
        long lastBytes = 0;
        int totalEntries = filesForProgress.Count;
        if (totalEntries == 0)
            return;

        RuntimeLog.WriteVerbose($"[BackupService] Progress monitor started for '{destDir}' (entries={totalEntries}).");

        long[] observedSizes = new long[totalEntries];
        bool[] completed = new bool[totalEntries];
        int completedFiles = 0;
        long copiedBytes = 0;

        TimeSpan minInterval = totalEntries > 4000
            ? TimeSpan.FromMilliseconds(1000)
            : TimeSpan.FromMilliseconds(500);
        var logInterval = TimeSpan.FromSeconds(5);
        DateTime lastLog = startTime;

        int chunkSize = totalEntries switch
        {
            > 20000 => 150,
            > 10000 => 250,
            > 4000 => 400,
            > 1000 => 600,
            _ => 900
        };

        int scanIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            int scanned = 0;
            string? lastTouched = null;

            while (scanned < chunkSize && totalEntries > 0)
            {
                int index = scanIndex++ % totalEntries;
                FileEntry entry = filesForProgress[index];
                if (!BackupSafetyService.TryCombinePathUnderRoot(destDir, entry.RelPath, out string targetPath))
                {
                    scanned++;
                    continue;
                }

                long size = 0;
                try
                {
                    var info = new FileInfo(targetPath);
                    if (!info.Exists)
                    {
                        scanned++;
                        continue;
                    }

                    size = info.Length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Console.WriteLine($"[BackupService] Progress scan skipped '{targetPath}': {ex.Message}");
                    scanned++;
                    continue;
                }

                size = Math.Min(size, entry.Size);
                if (size <= 0)
                {
                    scanned++;
                    continue;
                }

                long previousSize = observedSizes[index];
                if (size != previousSize)
                {
                    observedSizes[index] = size;
                    long delta = size - previousSize;
                    if (delta > 0)
                        copiedBytes += delta;
                }

                if (!completed[index] && entry.Size > 0 && size >= entry.Size)
                {
                    completed[index] = true;
                    completedFiles++;
                }

                lastTouched = entry.RelPath;
                scanned++;
            }

            DateTime now = DateTime.UtcNow;
            TimeSpan elapsed = now - lastSample;
            if (elapsed.TotalSeconds < 0.1)
                elapsed = TimeSpan.FromSeconds(0.1);

            long deltaBytes = copiedBytes - lastBytes;
            if (deltaBytes < 0)
                deltaBytes = 0;

            lastBytes = copiedBytes;
            lastSample = now;

            double percent = totalBytes > 0
                ? Math.Min(99.5d, copiedBytes * 100d / totalBytes)
                : 0d;

            double speedBytesSec = deltaBytes / elapsed.TotalSeconds;
            double speedMbSec = speedBytesSec / (1024 * 1024);

            string etaText;
            if (speedBytesSec > 0 && totalBytes > 0)
            {
                long remainingBytes = Math.Max(0, totalBytes - copiedBytes);
                double etaSeconds = remainingBytes / speedBytesSec;
                var eta = TimeSpan.FromSeconds(etaSeconds);
                etaText = $"Copying {completedFiles}/{totalEntries} files - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
            }
            else if (runnerState is not null && runnerState.HasRecentUpdate(TimeSpan.FromSeconds(10)))
            {
                string? fallback = runnerState.LastEtaText;
                if (string.IsNullOrWhiteSpace(fallback) && !string.IsNullOrWhiteSpace(runnerState.LastFile))
                {
                    etaText = "Copying files (robocopy)...";
                }
                else
                {
                    etaText = string.IsNullOrWhiteSpace(fallback)
                        ? $"Copying {completedFiles}/{totalEntries} files - Estimating..."
                        : fallback;
                }
            }
            else
            {
                etaText = $"Copying {completedFiles}/{totalEntries} files - Waiting for first file...";
            }

            string currentFile = string.IsNullOrWhiteSpace(lastTouched) ? string.Empty : lastTouched;
            if (string.IsNullOrWhiteSpace(currentFile) && runnerState is not null)
            {
                currentFile = runnerState.LastFile ?? string.Empty;
            }

            if (copiedBytes <= 0 && runnerState is not null)
            {
                double fallbackPercent = runnerState.LastPercent;
                if (fallbackPercent > 0)
                    percent = Math.Min(99.0d, fallbackPercent);
            }
            progressCallback(percent, currentFile, etaText);

            if ((now - lastLog) >= logInterval)
            {
                RuntimeLog.WriteVerbose($"[BackupService] Copy progress: {completedFiles}/{totalEntries} files, {percent:0.0}% ({speedMbSec:0.0} MB/s).");
                lastLog = now;
            }

            await Task.Delay(minInterval, ct);
        }

        RuntimeLog.WriteVerbose($"[BackupService] Progress monitor stopped for '{destDir}'.");
    }

    /// <summary>
    /// Creates a compressed zip archive of the filtered project contents into the given
    /// destination directory. The archive contains only files that pass the same filter
    /// rules used by snapshots.
    /// </summary>
    private static int NormalizeArchiveUploadBufferBytes(int? requestedBytes)
    {
        const int defaultBytes = 4 * 1024 * 1024;
        const int minBytes = 256 * 1024;
        const int maxBytes = 64 * 1024 * 1024;

        if (requestedBytes is null || requestedBytes.Value <= 0)
            return defaultBytes;

        int value = requestedBytes.Value;
        if (value < minBytes)
            return minBytes;
        if (value > maxBytes)
            return maxBytes;

        return value;
    }

    private const int ArchiveCopyBufferBytes = 1024 * 1024;
    private const int ArchiveFileStreamBufferBytes = 1024 * 1024;

    private static async Task<ArchiveBackupResult> RunArchiveBackupAsync(
        Project project,
        string destDir,
        long totalBytes,
        int totalFiles,
        IReadOnlyList<string>? filesForBackup,
        Action<double, string, string>? progressCallback,
        int uploadBufferBytes,
        bool preferParallelUpload,
        bool enableCheckpointedRetry,
        IAppConfigStore configStore,
        string? encryptionPassword,
        BackupEncryptionConfig encryptionConfig,
        CancellationToken ct)
    {
        string sourceDir = project.RootPath;
        var srcInfo = new DirectoryInfo(sourceDir);
        if (!srcInfo.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");

        RuntimeLog.WriteVerbose($"[BackupService] RunArchiveBackupAsync (LOCAL ZIP MODE, 2-PHASE) started for '{project.Name}', destDir='{destDir}', totalBytes={totalBytes}.");

        // 1. Build filter identical to snapshots.
        var filter = FilterService.FromPresetAndLocal(sourceDir, project.Preset);

        // 2. Gather all files that will be archived.
        string[] allFiles = filesForBackup?.ToArray() ?? BuildFilteredFileList(sourceDir, filter, ct);
        int archiveTotalFiles = totalFiles > 0 ? totalFiles : allFiles.Length;
        bool encryptBeforeUpload = !string.IsNullOrWhiteSpace(encryptionPassword);
        string artifactFileName = encryptBeforeUpload
            ? BackupArchiveCryptoService.EncryptedArchiveFileName
            : BackupArchiveCryptoService.PlainArchiveFileName;

        string workingDestDir = destDir;
        ArchiveResumeCheckpoint? resumeCheckpoint = null;
        if (enableCheckpointedRetry && allFiles.Length > 0)
        {
            string fingerprint = BuildArchiveResumeFingerprint(sourceDir, allFiles);
            resumeCheckpoint = new ArchiveResumeCheckpoint(
                Version: 1,
                Mode: "archive",
                SourceFingerprint: fingerprint,
                ArchiveSizeBytes: 0,
                LastUpdatedUtc: DateTime.UtcNow,
                ArtifactFileName: artifactFileName);

            string? resumableDir = TryFindResumableArchiveBackupFolder(destDir, fingerprint);
            if (!string.IsNullOrWhiteSpace(resumableDir) &&
                !string.Equals(resumableDir, destDir, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.WriteVerbose($"[BackupService] Resuming interrupted archive upload for '{project.Name}' from '{resumableDir}'.");
                DeletePartialBackup(destDir);
                workingDestDir = resumableDir;
                PersistCheckpointResumeTelemetry(
                    new CheckpointResumeTelemetryUpdate
                    {
                        Status = "resume-discovered",
                        ProjectName = project.Name,
                        BackupFolder = workingDestDir,
                        ArchivePath = Path.Combine(workingDestDir, artifactFileName),
                        ResumeOffsetBytes = File.Exists(Path.Combine(workingDestDir, artifactFileName))
                            ? new FileInfo(Path.Combine(workingDestDir, artifactFileName)).Length
                            : 0,
                        ArchiveSizeBytes = 0,
                        SourceFingerprint = fingerprint,
                        Message = "Found interrupted archive backup with matching fingerprint and will attempt resume."
                    },
                    configStore);
            }
        }

        // 3. Prepare destination and local temp folder.
        Directory.CreateDirectory(workingDestDir);
        string finalArchivePath = Path.Combine(workingDestDir, artifactFileName);

        string localTempRoot = Path.Combine(Path.GetTempPath(), "vaultsync_archive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localTempRoot);
        string localArchive = Path.Combine(localTempRoot, BackupArchiveCryptoService.PlainArchiveFileName);

        try
        {
            // 4. If nothing to back up, create a valid empty ZIP and copy it.
            if (allFiles.Length == 0)
            {
                using (var fs = new FileStream(
                    localArchive,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    ArchiveFileStreamBufferBytes,
                    FileOptions.SequentialScan))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    // no entries
                }

                RuntimeLog.WriteVerbose($"[BackupService] Created empty local archive for '{project.Name}'.");
            }

            // --------------------
            // PHASE 1: Compress files into local ZIP
            // --------------------
            long processedBytes = 0;
            int processedFiles = 0;
            DateTime startTime = DateTime.UtcNow;
            DateTime lastUiUpdate = startTime;
            var minUiInterval = TimeSpan.FromMilliseconds(100);
            byte[] archiveCopyBuffer = new byte[ArchiveCopyBufferBytes];

            using (var fs = new FileStream(
                localArchive,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                ArchiveFileStreamBufferBytes,
                FileOptions.SequentialScan))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (string? filePath in allFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    string relative = Path.GetRelativePath(sourceDir, filePath);
                    CompressionLevel compressionLevel = ResolveArchiveCompressionLevel(filePath);
                    ZipArchiveEntry entry = zip.CreateEntry(relative, compressionLevel);

                    try
                    {
                        using (Stream entryStream = await entry.OpenAsync(ct).ConfigureAwait(false))
                        using (var input = new FileStream(
                            filePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            ArchiveFileStreamBufferBytes,
                            FileOptions.SequentialScan))
                        {
                            int read;
                            while ((read = await input.ReadAsync(archiveCopyBuffer.AsMemory(), ct)) > 0)
                            {
                                await entryStream.WriteAsync(archiveCopyBuffer.AsMemory(0, read), ct);
                                processedBytes += read;

                                if (progressCallback is null)
                                    continue;

                                double compressPercent = (totalBytes > 0)
                                    ? Math.Min(100d, (processedBytes * 100d / totalBytes))
                                    : 0d;

                                double overallPercent = compressPercent * 0.9;
                                DateTime now = DateTime.UtcNow;
                                if (overallPercent < 90d && (now - lastUiUpdate) < minUiInterval)
                                    continue;

                                lastUiUpdate = now;

                                TimeSpan elapsed = now - startTime;
                                double elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                                double speedBytesSec = processedBytes / elapsedSeconds;
                                double speedMbSec = speedBytesSec / (1024 * 1024);

                                string etaText;
                                if (overallPercent > 0 && overallPercent < 90)
                                {
                                    double remainingFraction = (90d - overallPercent) / overallPercent;
                                    double remainingSeconds = elapsedSeconds * remainingFraction;
                                    var eta = TimeSpan.FromSeconds(remainingSeconds);
                                    etaText = archiveTotalFiles > 0
                                        ? $"Compressing {processedFiles + 1}/{archiveTotalFiles} files - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}"
                                        : $"{speedMbSec:0.0} MB/s - Compressing - ETA {eta:mm\\:ss}";
                                }
                                else if (overallPercent >= 90)
                                {
                                    etaText = archiveTotalFiles > 0
                                        ? $"Compressing {processedFiles + 1}/{archiveTotalFiles} files - {speedMbSec:0.0} MB/s"
                                        : $"{speedMbSec:0.0} MB/s - Compressing";
                                }
                                else
                                {
                                    etaText = string.Empty;
                                }

                                progressCallback(overallPercent, relative, etaText);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new IOException(
                            $"Archive backup could not read required source file '{relative}'.",
                            ex);
                    }

                    processedFiles++;

                    if (progressCallback is not null)
                    {
                        // Map compression progress into 0-90% of overall progress.
                        double compressPercent = (totalBytes > 0)
                            ? Math.Min(100d, (processedBytes * 100d / totalBytes))
                            : 0d;

                        double overallPercent = compressPercent * 0.9; // 0-90%

                        DateTime now = DateTime.UtcNow;
                        if (overallPercent < 90d && (now - lastUiUpdate) < minUiInterval)
                            continue;

                        lastUiUpdate = now;

                        TimeSpan elapsed = now - startTime;
                        double elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                        double speedBytesSec = processedBytes / elapsedSeconds;
                        double speedMbSec = speedBytesSec / (1024 * 1024);

                        string etaText;
                        if (overallPercent > 0 && overallPercent < 90)
                        {
                            double remainingFraction = (90d - overallPercent) / overallPercent;
                            double remainingSeconds = elapsedSeconds * remainingFraction;
                            var eta = TimeSpan.FromSeconds(remainingSeconds);
                            if (archiveTotalFiles > 0)
                            {
                                etaText = $"Compressing {processedFiles}/{archiveTotalFiles} files - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
                            }
                            else
                            {
                                etaText = $"{speedMbSec:0.0} MB/s - Compressing - ETA {eta:mm\\:ss}";
                            }
                        }
                        else if (overallPercent >= 90)
                        {
                            if (archiveTotalFiles > 0)
                            {
                                etaText = $"Compressing {processedFiles}/{archiveTotalFiles} files - {speedMbSec:0.0} MB/s";
                            }
                            else
                            {
                                etaText = $"{speedMbSec:0.0} MB/s - Compressing";
                            }
                        }
                        else
                        {
                            etaText = string.Empty;
                        }

                        progressCallback(overallPercent, relative, etaText);
                    }
                }
            }

            BackupCryptoDescriptor descriptor = BackupCryptoDescriptor.Plain();
            if (encryptBeforeUpload)
            {
                progressCallback?.Invoke(88, string.Empty, "Encrypting archive before upload...");
                BackupArchiveCryptoService.EncryptionResult encryptionResult =
                    BackupArchiveCryptoService.EncryptArchiveInPlace(
                        localTempRoot,
                        encryptionPassword!,
                        encryptionConfig,
                        ct);
                localArchive = encryptionResult.EncryptedArchivePath;
                descriptor = encryptionResult.Descriptor;
            }

            // --------------------
            // PHASE 2: Upload the final local artifact to the destination (90-100%).
            // Encrypted backups never place data.zip on the destination.
            // --------------------
            ct.ThrowIfCancellationRequested();

            var zipInfo = new FileInfo(localArchive);
            long zipSize = zipInfo.Length;
            int bufferSize = uploadBufferBytes;
            TimeSpan stallTimeout = ComputeArchiveUploadStallTimeout(bufferSize);

            if (resumeCheckpoint is not null)
            {
                WriteArchiveResumeCheckpoint(
                    workingDestDir,
                    resumeCheckpoint with
                    {
                        ArchiveSizeBytes = zipSize,
                        LastUpdatedUtc = DateTime.UtcNow
                    });
            }

            async Task UploadSingleAttemptAsync(long startOffset)
            {
                long uploaded = 0;
                DateTime uploadStart = DateTime.UtcNow;
                DateTime lastLogTime = uploadStart;
                long lastLogBytes = 0;
                DateTime lastUiUpdate = uploadStart;
                long lastUiBytes = startOffset;
                byte[] buffer = new byte[bufferSize];
                long lastProgressTicks = uploadStart.Ticks;
                int stalled = 0;

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                async Task MonitorUploadAsync()
                {
                    while (!linkedCts.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), linkedCts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }

                        var lastProgress = new DateTime(Interlocked.Read(ref lastProgressTicks), DateTimeKind.Utc);
                        if (DateTime.UtcNow - lastProgress > stallTimeout)
                        {
                            Interlocked.Exchange(ref stalled, 1);
                            await linkedCts.CancelAsync();
                            return;
                        }
                    }
                }

                Task monitor = MonitorUploadAsync();

                try
                {
                    using (var src = new FileStream(
                               localArchive,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.Read,
                               bufferSize,
                               FileOptions.SequentialScan | FileOptions.Asynchronous))
                    using (var dst = new FileStream(
                               finalArchivePath,
                               FileMode.OpenOrCreate,
                               FileAccess.Write,
                               FileShare.None,
                               bufferSize,
                               FileOptions.SequentialScan | FileOptions.Asynchronous))
                    {
                        if (startOffset > 0)
                        {
                            src.Seek(startOffset, SeekOrigin.Begin);
                            dst.Seek(startOffset, SeekOrigin.Begin);
                            uploaded = startOffset;
                        }

                        int read;
                        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token)) > 0)
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();

                            await dst.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token);
                            uploaded += read;
                            Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);

                            if (progressCallback is not null && zipSize > 0)
                            {
                                double uploadPercent = Math.Min(100d, (uploaded * 100d / zipSize));
                                double overallPercent = 90d + uploadPercent * 0.1; // map 0-100% upload into 90-100%
                                if (overallPercent > 100d)
                                    overallPercent = 100d;

                                DateTime now = DateTime.UtcNow;
                                double intervalSeconds = Math.Max(0.1, (now - lastUiUpdate).TotalSeconds);
                                long intervalBytes = Math.Max(0, uploaded - lastUiBytes);
                                double speedBytesSec = intervalBytes / intervalSeconds;
                                double speedMbSec = speedBytesSec / (1024 * 1024);
                                lastUiUpdate = now;
                                lastUiBytes = uploaded;

                                double uploadedMb = uploaded / (1024d * 1024d);
                                double totalMb = zipSize / (1024d * 1024d);

                                string etaText;
                                if (uploadPercent >= 100)
                                {
                                    etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - Finalizing";
                                }
                                else if (speedBytesSec < 1024)
                                {
                                    etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - Waiting for network...";
                                }
                                else
                                {
                                    long remainingBytes = Math.Max(0, zipSize - uploaded);
                                    double remainingSeconds = remainingBytes / speedBytesSec;
                                    var eta = TimeSpan.FromSeconds(remainingSeconds);
                                    etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - ETA {eta:mm\\:ss}";
                                }

                                progressCallback(overallPercent, Path.GetFileName(finalArchivePath), etaText);
                            }

                            if ((DateTime.UtcNow - lastLogTime) >= TimeSpan.FromSeconds(5))
                            {
                                DateTime now = DateTime.UtcNow;
                                double intervalSeconds = Math.Max(0.1, (now - lastLogTime).TotalSeconds);
                                long intervalBytes = uploaded - lastLogBytes;
                                double intervalMbSec = (intervalBytes / intervalSeconds) / (1024d * 1024d);
                                RuntimeLog.WriteVerbose($"[BackupService] Archive upload (single) {uploaded}/{zipSize} bytes ({intervalMbSec:0.0} MB/s).");
                                lastLogTime = now;
                                lastLogBytes = uploaded;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (
                    Interlocked.CompareExchange(ref stalled, 0, 0) == 1 &&
                    !ct.IsCancellationRequested)
                {
                    throw new TimeoutException("No upload progress detected during single archive upload.");
                }
                finally
                {
                    await linkedCts.CancelAsync();
                    await monitor;
                }

                if (Interlocked.CompareExchange(ref stalled, 0, 0) == 1)
                {
                    throw new TimeoutException("No upload progress detected during single archive upload.");
                }
            }

            async Task UploadSingleWithResumeAsync(int maxRetries)
            {
                int attempt = 0;
                while (true)
                {
                    attempt++;
                    long existingLength = 0;

                    try
                    {
                        if (File.Exists(finalArchivePath))
                        {
                            existingLength = new FileInfo(finalArchivePath).Length;
                            if (existingLength > zipSize)
                            {
                                using var truncate = new FileStream(finalArchivePath, FileMode.Open, FileAccess.Write, FileShare.None);
                                truncate.SetLength(zipSize);
                                existingLength = zipSize;
                            }
                        }

                        if (existingLength > 0 &&
                            !ValidateArchiveResumePrefix(localArchive, finalArchivePath, existingLength, bufferSize, ct))
                        {
                            RuntimeLog.WriteVerbose($"[BackupService] Existing archive checkpoint for '{finalArchivePath}' did not match the local archive prefix. Restarting upload from 0 bytes.");
                            PersistCheckpointResumeTelemetry(
                                new CheckpointResumeTelemetryUpdate
                                {
                                    Status = "resume-prefix-mismatch",
                                    ProjectName = project.Name,
                                    BackupFolder = workingDestDir,
                                    ArchivePath = finalArchivePath,
                                    ResumeOffsetBytes = existingLength,
                                    ArchiveSizeBytes = zipSize,
                                    SourceFingerprint = resumeCheckpoint?.SourceFingerprint ?? string.Empty,
                                    Message = "Discarded partial archive because the existing destination prefix no longer matched the rebuilt local archive."
                                },
                                configStore);
                            using var truncate = new FileStream(finalArchivePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                            truncate.SetLength(0);
                            existingLength = 0;
                        }

                        if (existingLength > 0)
                        {
                            PersistCheckpointResumeTelemetry(
                                new CheckpointResumeTelemetryUpdate
                                {
                                    Status = "resume-attempt",
                                    ProjectName = project.Name,
                                    BackupFolder = workingDestDir,
                                    ArchivePath = finalArchivePath,
                                    ResumeOffsetBytes = existingLength,
                                    ArchiveSizeBytes = zipSize,
                                    SourceFingerprint = resumeCheckpoint?.SourceFingerprint ?? string.Empty,
                                    Message = "Resuming archive upload from a validated existing prefix."
                                },
                                configStore);
                        }

                        await UploadSingleAttemptAsync(existingLength);
                        return;
                    }
                    catch (TimeoutException) when (attempt <= maxRetries)
                    {
                        RuntimeLog.WriteVerbose($"[BackupService] Single archive upload stalled (attempt {attempt}/{maxRetries}). Retrying from {existingLength} bytes.");
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                }
            }

            await EnsureDestinationWriteReadyAsync(workingDestDir, ct);

            if (preferParallelUpload && zipSize >= bufferSize * 8L)
            {
                RuntimeLog.WriteVerbose($"[BackupService] Uploading archive with parallel writer (parts={Math.Clamp(Environment.ProcessorCount / 2, 2, 4)}, buffer={bufferSize / (1024 * 1024)} MB).");
                try
                {
                    await UploadArchiveParallelAsync(
                        localArchive,
                        finalArchivePath,
                        zipSize,
                        bufferSize,
                        progressCallback,
                        workingDestDir,
                        project.Name,
                        enableCheckpointedRetry,
                        resumeCheckpoint,
                        configStore,
                        ct);
                }
                catch (TimeoutException ex)
                {
                    Console.WriteLine($"[BackupService] Parallel archive upload stalled: {ex.Message}. Falling back to single stream.");
                    await UploadSingleWithResumeAsync(2);
                }
            }
            else
            {
                RuntimeLog.WriteVerbose($"[BackupService] Uploading archive with single writer (buffer={bufferSize / (1024 * 1024)} MB).");
                await UploadSingleWithResumeAsync(2);
            }

            if (encryptBeforeUpload)
            {
                string localMetadataPath = Path.Combine(localTempRoot, BackupArchiveCryptoService.MetadataFileName);
                string destinationMetadataPath = Path.Combine(workingDestDir, BackupArchiveCryptoService.MetadataFileName);
                File.Copy(localMetadataPath, destinationMetadataPath, overwrite: true);
            }
            RemoveArchiveResumeCheckpoint(workingDestDir);
            PersistCheckpointResumeTelemetry(
                new CheckpointResumeTelemetryUpdate
                {
                    Status = "resume-complete",
                    ProjectName = project.Name,
                    BackupFolder = workingDestDir,
                    ArchivePath = finalArchivePath,
                    ResumeOffsetBytes = zipSize,
                    ArchiveSizeBytes = zipSize,
                    SourceFingerprint = resumeCheckpoint?.SourceFingerprint ?? string.Empty,
                    Message = "Archive upload completed and checkpoint metadata was cleared."
                },
                configStore);
            RuntimeLog.WriteVerbose($"[BackupService] RunArchiveBackupAsync completed for '{project.Name}'. LocalArtifactSize={zipSize} bytes, encrypted={encryptBeforeUpload}.");
            return new ArchiveBackupResult(workingDestDir, encryptBeforeUpload, descriptor);
        }
        catch
        {
            if (enableCheckpointedRetry && File.Exists(finalArchivePath))
            {
                RuntimeLog.WriteVerbose($"[BackupService] Preserving incomplete archive checkpoint in '{workingDestDir}' for later retry.");
                PersistCheckpointResumeTelemetry(
                    new CheckpointResumeTelemetryUpdate
                    {
                        Status = "resume-preserved-after-failure",
                        ProjectName = project.Name,
                        BackupFolder = workingDestDir,
                        ArchivePath = finalArchivePath,
                        ResumeOffsetBytes = new FileInfo(finalArchivePath).Length,
                        ArchiveSizeBytes = resumeCheckpoint?.ArchiveSizeBytes ?? 0,
                        SourceFingerprint = resumeCheckpoint?.SourceFingerprint ?? string.Empty,
                        Message = "Preserved interrupted archive upload for a future checkpointed retry."
                    },
                    configStore);
            }
            else
            {
                // Cleanup: remove incomplete destination folder and rethrow.
                PersistCheckpointResumeTelemetry(
                    new CheckpointResumeTelemetryUpdate
                    {
                        Status = "resume-discarded-after-failure",
                        ProjectName = project.Name,
                        BackupFolder = workingDestDir,
                        ArchivePath = finalArchivePath,
                        ResumeOffsetBytes = File.Exists(finalArchivePath) ? new FileInfo(finalArchivePath).Length : 0,
                        ArchiveSizeBytes = resumeCheckpoint?.ArchiveSizeBytes ?? 0,
                        SourceFingerprint = resumeCheckpoint?.SourceFingerprint ?? string.Empty,
                        Message = "Discarded incomplete archive upload because checkpointed retry was not active or no resumable archive was present."
                    },
                    configStore);
                DeletePartialBackup(workingDestDir);
            }

            throw;
        }
        finally
        {
            // Always remove local temp folder.
            try
            {
                if (Directory.Exists(localTempRoot))
                    Directory.Delete(localTempRoot, recursive: true);
            }
            catch
            {
                // ignore cleanup errors
            }
        }
    }

    private static CompressionLevel ResolveArchiveCompressionLevel(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(ext))
            return CompressionLevel.Fastest;

        if (ArchiveNoCompressionExtensions.Contains(ext))
            return CompressionLevel.NoCompression;

        if (ArchiveOptimalCompressionExtensions.Contains(ext))
            return CompressionLevel.Optimal;

        return CompressionLevel.Fastest;
    }

    private static async Task UploadArchiveParallelAsync(
        string localArchive,
        string finalArchivePath,
        long zipSize,
        int bufferSize,
        Action<double, string, string>? progressCallback,
        string backupFolder,
        string projectName,
        bool enableCheckpointedRetry,
        ArchiveResumeCheckpoint? resumeCheckpoint,
        IAppConfigStore configStore,
        CancellationToken ct)
    {
        if (zipSize <= 0)
            return;

        string? finalDir = Path.GetDirectoryName(finalArchivePath);
        if (string.IsNullOrWhiteSpace(finalDir))
            throw new DirectoryNotFoundException($"Archive destination directory is missing for '{finalArchivePath}'.");

        Directory.CreateDirectory(finalDir);

        int parallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        long chunkSize = (long)Math.Ceiling(zipSize / (double)parallelism);
        var resumableChunkIndexes = new HashSet<int>();
        string fileName = Path.GetFileName(finalArchivePath);
        DateTime uploadStart = DateTime.UtcNow;
        var minUiInterval = TimeSpan.FromMilliseconds(150);
        object progressLock = new object();
        object checkpointLock = new object();
        DateTime lastUiUpdate = uploadStart;
        long lastUiBytes = 0;
        long uploaded = 0;
        DateTime lastLogTime = uploadStart;
        long lastLogBytes = 0;
        object logLock = new object();
        TimeSpan stallTimeout = ComputeArchiveUploadStallTimeout(bufferSize);
        long lastProgressTicks = uploadStart.Ticks;
        int stalled = 0;
        bool destinationHasExpectedSize = File.Exists(finalArchivePath) && new FileInfo(finalArchivePath).Length == zipSize;

        if (enableCheckpointedRetry &&
            resumeCheckpoint is not null &&
            destinationHasExpectedSize &&
            resumeCheckpoint.UsesParallelUpload &&
            resumeCheckpoint.ArchiveSizeBytes == zipSize &&
            resumeCheckpoint.ChunkSizeBytes == chunkSize &&
            resumeCheckpoint.Parallelism == parallelism &&
            resumeCheckpoint.CompletedChunkIndexes is { Count: > 0 })
        {
            foreach (int chunkIndex in resumeCheckpoint.CompletedChunkIndexes
                         .Where(index => index >= 0 && index < parallelism)
                         .Distinct()
                         .Order())
            {
                long start = chunkSize * chunkIndex;
                if (start >= zipSize)
                    continue;

                long length = Math.Min(chunkSize, zipSize - start);
                if (ValidateArchiveRange(localArchive, finalArchivePath, start, length, bufferSize, ct))
                {
                    resumableChunkIndexes.Add(chunkIndex);
                }
                else
                {
                    RuntimeLog.WriteVerbose($"[BackupService] Parallel archive checkpoint chunk {chunkIndex} for '{projectName}' did not validate and will be re-uploaded.");
                }
            }
        }

        if (!destinationHasExpectedSize || resumableChunkIndexes.Count == 0)
        {
            using var init = new FileStream(
                finalArchivePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Write,
                1,
                FileOptions.Asynchronous);
            init.SetLength(zipSize);
            resumableChunkIndexes.Clear();
        }
        else if (resumableChunkIndexes.Count > 0)
        {
            uploaded = resumableChunkIndexes.Sum(index => Math.Min(chunkSize, zipSize - (chunkSize * index)));
            lastUiBytes = uploaded;
            PersistCheckpointResumeTelemetry(
                new CheckpointResumeTelemetryUpdate
                {
                    Status = "parallel-resume-attempt",
                    ProjectName = projectName,
                    BackupFolder = backupFolder,
                    ArchivePath = finalArchivePath,
                    ResumeOffsetBytes = uploaded,
                    ArchiveSizeBytes = zipSize,
                    SourceFingerprint = resumeCheckpoint?.SourceFingerprint ?? string.Empty,
                    Message = $"Resuming parallel archive upload with {resumableChunkIndexes.Count} validated chunks already present."
                },
                configStore);
        }

        void PersistParallelCheckpoint(HashSet<int> completedIndexes)
        {
            if (!enableCheckpointedRetry || resumeCheckpoint is null)
                return;

            WriteArchiveResumeCheckpoint(
                backupFolder,
                resumeCheckpoint with
                {
                    Version = 2,
                    ArchiveSizeBytes = zipSize,
                    LastUpdatedUtc = DateTime.UtcNow,
                    UsesParallelUpload = true,
                    ChunkSizeBytes = chunkSize,
                    Parallelism = parallelism,
                    CompletedChunkIndexes = [.. completedIndexes.Order()]
                });
        }

        PersistParallelCheckpoint(resumableChunkIndexes);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task MonitorUploadAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    var lastProgress = new DateTime(Interlocked.Read(ref lastProgressTicks), DateTimeKind.Utc);
                    if (DateTime.UtcNow - lastProgress > stallTimeout)
                    {
                        Interlocked.Exchange(ref stalled, 1);
                        await cts.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The upload completed, failed, or was cancelled.
            }
        }

        async Task ReportHeartbeatAsync()
        {
            if (progressCallback is null)
                return;

            try
            {
                var heartbeatInterval = TimeSpan.FromSeconds(5);
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(heartbeatInterval, cts.Token);

                    long snapshotUploaded = Interlocked.Read(ref uploaded);
                    DateTime now = DateTime.UtcNow;
                    double intervalSeconds;
                    long intervalBytes;
                    lock (progressLock)
                    {
                        if ((now - lastUiUpdate) < heartbeatInterval)
                            continue;

                        intervalSeconds = Math.Max(0.1, (now - lastUiUpdate).TotalSeconds);
                        intervalBytes = Math.Max(0, snapshotUploaded - lastUiBytes);
                        lastUiUpdate = now;
                        lastUiBytes = snapshotUploaded;
                    }

                    double speedBytesSec = intervalBytes / intervalSeconds;
                    double speedMbSec = speedBytesSec / (1024 * 1024);
                    double uploadPercent = Math.Min(100d, snapshotUploaded * 100d / zipSize);
                    double overallPercent = 90d + uploadPercent * 0.1;
                    if (overallPercent > 100d)
                        overallPercent = 100d;

                    double uploadedMb = snapshotUploaded / (1024d * 1024d);
                    double totalMb = zipSize / (1024d * 1024d);

                    string etaText;
                    if (uploadPercent >= 100)
                    {
                        etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - Finalizing";
                    }
                    else if (speedBytesSec < 1024)
                    {
                        etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - Waiting for network...";
                    }
                    else
                    {
                        long remainingBytes = Math.Max(0, zipSize - snapshotUploaded);
                        double remainingSeconds = remainingBytes / speedBytesSec;
                        var eta = TimeSpan.FromSeconds(remainingSeconds);
                        etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - ETA {eta:mm\\:ss}";
                    }

                    progressCallback(overallPercent, fileName, etaText);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The upload completed, failed, or was cancelled.
            }
        }

        Task monitor = MonitorUploadAsync();
        Task heartbeat = ReportHeartbeatAsync();
        var tasks = Enumerable.Range(0, parallelism)
            .Select(index =>
            {
                long start = chunkSize * index;
                if (start >= zipSize)
                    return Task.CompletedTask;

                if (resumableChunkIndexes.Contains(index))
                    return Task.CompletedTask;

                long length = Math.Min(chunkSize, zipSize - start);
                return Task.Run(async () =>
                {
                    byte[] buffer = new byte[bufferSize];
                    using var src = new FileStream(
                        localArchive,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize,
                        FileOptions.SequentialScan | FileOptions.Asynchronous);
                    using var dst = new FileStream(
                        finalArchivePath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.Write,
                        bufferSize,
                        FileOptions.Asynchronous);

                    src.Seek(start, SeekOrigin.Begin);
                    dst.Seek(start, SeekOrigin.Begin);

                    long remaining = length;
                    while (remaining > 0)
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await src.ReadAsync(buffer.AsMemory(0, toRead), cts.Token);
                        if (read == 0)
                            throw new EndOfStreamException("The local archive ended before its upload chunk was complete.");

                        await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        remaining -= read;

                        if (progressCallback is null)
                        {
                            Interlocked.Add(ref uploaded, read);
                            Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                            continue;
                        }

                        long totalUploaded = Interlocked.Add(ref uploaded, read);
                        Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                        double uploadPercent = Math.Min(100d, totalUploaded * 100d / zipSize);
                        double overallPercent = 90d + uploadPercent * 0.1;
                        if (overallPercent > 100d)
                            overallPercent = 100d;

                        DateTime now = DateTime.UtcNow;
                        bool shouldUpdate = true;
                        double intervalSeconds = 0d;
                        long intervalBytes = 0;
                        lock (progressLock)
                        {
                            if (uploadPercent < 100 && (now - lastUiUpdate) < minUiInterval)
                            {
                                shouldUpdate = false;
                            }
                            else
                            {
                                intervalSeconds = Math.Max(0.1, (now - lastUiUpdate).TotalSeconds);
                                intervalBytes = Math.Max(0, totalUploaded - lastUiBytes);
                                lastUiUpdate = now;
                                lastUiBytes = totalUploaded;
                            }
                        }

                        if (!shouldUpdate)
                            continue;

                        double speedBytesSec = intervalBytes / intervalSeconds;
                        double speedMbSec = speedBytesSec / (1024 * 1024);
                        double uploadedMb = totalUploaded / (1024d * 1024d);
                        double totalMb = zipSize / (1024d * 1024d);

                        string etaText;
                        if (uploadPercent >= 100)
                        {
                            etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - Finalizing";
                        }
                        else if (speedBytesSec < 1024)
                        {
                            etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - Waiting for network...";
                        }
                        else
                        {
                            long remainingBytes = Math.Max(0, zipSize - totalUploaded);
                            double remainingSeconds = remainingBytes / speedBytesSec;
                            var eta = TimeSpan.FromSeconds(remainingSeconds);
                            etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - ETA {eta:mm\\:ss}";
                        }

                        progressCallback(overallPercent, fileName, etaText);
                    }

                    if (enableCheckpointedRetry && resumeCheckpoint is not null)
                    {
                        lock (checkpointLock)
                        {
                            resumableChunkIndexes.Add(index);
                            PersistParallelCheckpoint(resumableChunkIndexes);
                        }
                    }

                    DateTime logNow = DateTime.UtcNow;
                    long snapshotUploaded = Interlocked.Read(ref uploaded);
                    lock (logLock)
                    {
                        if ((logNow - lastLogTime) >= TimeSpan.FromSeconds(5))
                        {
                            double intervalSeconds = Math.Max(0.1, (logNow - lastLogTime).TotalSeconds);
                            long intervalBytes = snapshotUploaded - lastLogBytes;
                            double intervalMbSec = (intervalBytes / intervalSeconds) / (1024d * 1024d);
                            RuntimeLog.WriteVerbose($"[BackupService] Archive upload (parallel) {snapshotUploaded}/{zipSize} bytes ({intervalMbSec:0.0} MB/s).");
                            lastLogTime = logNow;
                            lastLogBytes = snapshotUploaded;
                        }
                    }
                }, cts.Token);
            })
            .ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (
            Interlocked.CompareExchange(ref stalled, 0, 0) == 1 &&
            !ct.IsCancellationRequested)
        {
            throw new TimeoutException("No upload progress detected during parallel archive upload.");
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(monitor, heartbeat);
        }

        if (Interlocked.CompareExchange(ref stalled, 0, 0) == 1)
        {
            throw new TimeoutException("No upload progress detected during parallel archive upload.");
        }
    }

    private static TimeSpan ComputeArchiveUploadStallTimeout(int bufferSize)
    {
        const long minBytesPerSec = 16 * 1024;
        const int minSeconds = 120;
        const int maxSeconds = 1800;

        int seconds = (int)Math.Ceiling(bufferSize / (double)minBytesPerSec * 2d);
        seconds = Math.Clamp(seconds, minSeconds, maxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task EnsureDestinationWriteReadyAsync(string destDir, CancellationToken ct)
    {
        string probePath = Path.Combine(destDir, ".vaultsync_upload_probe");
        var timeout = TimeSpan.FromSeconds(15);

        var task = Task.Run(() =>
        {
            Directory.CreateDirectory(destDir);
            using (var fs = new FileStream(
                       probePath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       1,
                       FileOptions.WriteThrough))
            {
                fs.WriteByte(0);
                fs.Flush(true);
            }
            File.Delete(probePath);
        }, ct);

        Task completed = await Task.WhenAny(task, Task.Delay(timeout, ct));
        if (completed != task)
        {
            throw new TimeoutException($"Destination write probe timed out for '{destDir}'.");
        }

        await task;
    }

    private static (int totalFiles, long totalBytes) ComputeBackupStats(string sourceDir, string preset, CancellationToken ct)
    {
        var dirInfo = new DirectoryInfo(sourceDir);
        if (!dirInfo.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");

        var filter = FilterService.FromPresetAndLocal(sourceDir, preset);
        long totalBytes = 0;
        int totalFiles = 0;

        try
        {
            string[] allFiles = BuildFilteredFileList(sourceDir, filter, ct);
            foreach (string filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fi = new FileInfo(filePath);
                    totalBytes += fi.Length;
                    totalFiles++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Console.WriteLine($"[BackupService] Skipping file while computing size '{filePath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.WriteLine($"[BackupService] Failed to enumerate files for size computation in '{sourceDir}': {ex.Message}");
            throw;
        }

        RuntimeLog.WriteVerbose($"[BackupService] Computed backup size for '{sourceDir}': {totalBytes} bytes across {totalFiles} files.");
        return (totalFiles, totalBytes);
    }

    private static string[] BuildFilteredFileList(string sourceDir, FilterService filter, CancellationToken ct)
    {
        var files = new List<string>();
        foreach (string filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(sourceDir, filePath);
            if (filter.ShouldExclude(sourceDir, filePath) ||
                !BackupSafetyService.TryResolveExistingFileUnderRoot(sourceDir, relative, out string safePath))
            {
                continue;
            }

            files.Add(safePath);
        }

        return [.. files];
    }

    internal static string ResolveSnapshotSourceFile(string sourceRoot, string relativePath)
    {
        if (!BackupSafetyService.TryResolveExistingFileUnderRoot(sourceRoot, relativePath, out string sourcePath))
        {
            throw new InvalidDataException(
                $"Snapshot path '{relativePath}' is missing, unsafe, or escapes the project root.");
        }

        return sourcePath;
    }

    private static void CopyDirectoryRecursive(
        string sourceDir,
        string destDir,
        string preset,
        IReadOnlyList<string>? filesForBackup,
        ref long totalBytes,
        Action<double, string, string>? progressCallback,
        CancellationToken ct)
    {
        var srcInfo = new DirectoryInfo(sourceDir);
        if (!srcInfo.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");

        // Build a path filter based on the project's preset plus any local .vaultsyncignore-style rules.
        var filter = FilterService.FromPresetAndLocal(sourceDir, preset);

        // Get all files up front so we can compute a simple percent and ETA, applying
        // the same vaultsyncignore-style filtering used by SnapshotService.
        string[] allFiles = filesForBackup?.ToArray() ?? BuildFilteredFileList(sourceDir, filter, ct);
        int totalFiles = allFiles.Length;
        int processedFiles = 0;
        DateTime startTime = DateTime.UtcNow;
        long copiedBytes = 0;

        foreach (string? filePath in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            var fileInfo = new FileInfo(filePath);
            string relative = Path.GetRelativePath(sourceDir, filePath);
            if (!BackupSafetyService.TryCombinePathUnderRoot(destDir, relative, out string targetPath))
                throw new InvalidDataException($"Snapshot path '{relative}' escapes the backup destination.");
            string? targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            try
            {
                const int bufferSize = 1024 * 1024; // 1 MB
                byte[] buffer = new byte[bufferSize];

                long copiedForThisFile = 0;

                using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        output.Write(buffer, 0, read);
                        copiedForThisFile += read;
                        totalBytes += read;
                        copiedBytes += read;

                        if (progressCallback is not null)
                        {
                            double percent;
                            if (totalFiles == 0)
                            {
                                percent = 100d;
                            }
                            else
                            {
                                // Combine completed files with partial progress on the current file.
                                double filesCompletedPortion = processedFiles * 100d / totalFiles;
                                double currentFilePortion = (double)copiedForThisFile / Math.Max(1L, fileInfo.Length) * (100d / totalFiles);
                                percent = filesCompletedPortion + currentFilePortion;
                                if (percent > 100d)
                                    percent = 100d;
                            }

                            TimeSpan elapsed = DateTime.UtcNow - startTime;
                            double elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                            string etaText = string.Empty;

                            if (percent > 0d && percent < 100d)
                            {
                                double remainingFraction = (100d - percent) / percent;
                                double remainingSeconds = elapsedSeconds * remainingFraction;
                                var eta = TimeSpan.FromSeconds(remainingSeconds);
                                double speedBytesSec = copiedBytes / elapsedSeconds;
                                double speedMbSec = speedBytesSec / (1024 * 1024);
                                etaText = $"Copying {processedFiles + 1}/{totalFiles} files - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
                            }

                            progressCallback(percent, relative, etaText);
                        }
                    }
                }

                processedFiles++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Managed backup could not copy required source file '{relative}'.",
                    ex);
            }
        }
    }

    private static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "project";

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] cleanedChars = [.. name
            .Trim()
            .ToLowerInvariant()
            .Select(ch => invalid.Contains(ch) ? '-' : ch)];

        string cleaned = new string(cleanedChars);

        // Collapse multiple '-' to a single '-'
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        return cleaned.Trim('-');
    }

    public static string GetProjectBackupFolderName(string name) => Slugify(name);

    private static string GetAvailableBackupFolder(string projectBackupRoot, string folderName)
    {
        string candidate = Path.Combine(projectBackupRoot, folderName);
        if (!Directory.Exists(candidate))
            return candidate;

        for (int i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(projectBackupRoot, $"{folderName}-{i}");
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(projectBackupRoot, $"{folderName}-{Guid.NewGuid():N}");
    }

    private bool HasUsableBackupForDestination(int projectId, string backupRoot)
    {
        List<Backup> backups;
        try
        {
            backups = [.. _repo.GetBackupsForProject(projectId)];
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[BackupService] Cannot verify existing backups for projectId={projectId}: {ex.Message}");
            return false;
        }

        foreach (Backup backup in backups)
        {
            string root = string.IsNullOrWhiteSpace(backup.DestinationPath)
                ? backupRoot
                : backup.DestinationPath;
            if (!PathsEqual(root, backupRoot))
                continue;

            string backupPath = Path.IsPathRooted(backup.Path)
                ? backup.Path
                : Path.Combine(root, backup.Path);
            if (DirectoryHasBackupContent(backupPath))
                return true;
        }

        return false;
    }

    private static bool DirectoryHasBackupContent(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            left = Path.GetFullPath(left);
            right = Path.GetFullPath(right);
        }
        catch
        {
            left = left.Trim();
            right = right.Trim();
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private void ApplyBackupRetention(int projectId, string backupRoot, int? maxSnapshotsToKeep)
    {
        if (!maxSnapshotsToKeep.HasValue || maxSnapshotsToKeep.Value <= 0)
        {
            // Retention disabled or not configured.
            return;
        }

        int maxToKeep = Math.Max(1, maxSnapshotsToKeep.Value);

        // Load all backups for this project, newest first.
        List<Backup> backups;
        try
        {
            backups = [.. _repo
                .GetBackupsForProject(projectId)
                .OrderByDescending(b => b.CreatedUtc)];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to load backups for retention (projectId={projectId}): {ex}");
            return;
        }

        var metadataBySnapshotId = _repo.GetSnapshotHistoryMetadataBySnapshotIds(backups.Select(backup => backup.SnapshotId));
        var protectedSnapshotIds = metadataBySnapshotId
            .Where(static entry => entry.Value.IsProtected)
            .Select(static entry => entry.Key)
            .ToHashSet();

        // Keep protected backups and protected snapshots; apply the cap only to eligible restore points.
        var unprotected = backups
            .Where(backup => !backup.IsProtected && !protectedSnapshotIds.Contains(backup.SnapshotId))
            .ToList();
        int deleteQuota = Math.Max(0, unprotected.Count - maxToKeep);
        // Retention candidates are oldest first.
        var candidates = unprotected
            .OrderBy(b => b.CreatedUtc)
            .ToList();
        var projectSnapshots = _repo.GetAllSnapshots()
            .Where(snapshot => snapshot.ProjectId == projectId)
            .ToDictionary(snapshot => snapshot.Id);
        BackupRetentionPreflightResult preflight = EvaluateRetentionPreflight(projectId, backups, candidates, projectSnapshots, deleteQuota);
        if (!preflight.CanPrune)
        {
            RuntimeLog.WriteVerbose(
                $"[BackupService] Retention preflight blocked for projectId={projectId}: code={preflight.Code}; validRestorePoints={preflight.ValidRestorePointCount}; deleteQuota={preflight.DeletionQuota}; message={preflight.Message}");
            return;
        }

        Project? project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
        string? projectName = project?.Name;
        var snapshotRefs = new Dictionary<int, int>();
        foreach (Backup backup in backups)
        {
            if (snapshotRefs.TryGetValue(backup.SnapshotId, out int count))
                snapshotRefs[backup.SnapshotId] = count + 1;
            else
                snapshotRefs[backup.SnapshotId] = 1;
        }

        var byteVerifiedBackupIds = _repo.GetRecoveryDrills()
            .Where(drill => drill.ProjectId == projectId)
            .GroupBy(drill => drill.BackupId)
            .Select(group => group.OrderByDescending(drill => drill.RunUtc).ThenByDescending(drill => drill.Id).First())
            .Where(RecoveryDrillService.HasPassedByteIntegrity)
            .Select(drill => drill.BackupId)
            .ToHashSet();
        IReadOnlyList<BackupRetentionCandidateDecision> retentionPlan = BuildRetentionDeletionPlan(
            projectId,
            backups,
            candidates,
            projectSnapshots,
            deleteQuota,
            byteVerifiedBackupIds);
        foreach (BackupRetentionCandidateDecision? skipped in retentionPlan.Where(static decision => !decision.Selected))
        {
            RuntimeLog.WriteVerbose(
                $"[BackupService] Retention candidate skipped for projectId={projectId}: backupId={skipped.BackupId}; code={skipped.Code}; message={skipped.Message}");
        }

        var plannedCandidateIds = retentionPlan
            .Where(static decision => decision.Selected)
            .Select(static decision => decision.BackupId)
            .ToHashSet();
        var attempted = new HashSet<int>();
        int deleted = 0;

        while (deleted < deleteQuota)
        {
            // Try the next oldest unprotected candidate that has not been attempted yet.
            Backup? backup = candidates.FirstOrDefault(b => plannedCandidateIds.Contains(b.Id) && !attempted.Contains(b.Id));
            if (backup is null)
                break;

            attempted.Add(backup.Id);

            bool canDeleteDbRow = true;
            bool diskDeleteSucceeded = true;

            try
            {
                string baseRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath)
                    ? backup.DestinationPath
                    : backupRoot;
                string relativePath = string.IsNullOrWhiteSpace(backup.Path)
                    ? string.Empty
                    : backup.Path
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar);
                string? fullPath = string.Empty;
                if (string.IsNullOrWhiteSpace(baseRoot) || !Directory.Exists(baseRoot))
                {
                    RuntimeLog.WriteVerbose(
                        $"[BackupService] Retention deferred because destination root '{baseRoot}' is unavailable (backupId={backup.Id}); code=destination-unavailable.");
                    canDeleteDbRow = false;
                    diskDeleteSucceeded = false;
                }
                else if (ShouldRejectUnbackedManagedMount(
                             OperatingSystem.IsMacOS(),
                             IsMacManagedMountPath(baseRoot),
                             IsNetworkMountPath(baseRoot)))
                {
                    RuntimeLog.WriteVerbose(
                        $"[BackupService] Retention deferred because managed destination '{baseRoot}' is not mounted (backupId={backup.Id}); code=destination-unmounted.");
                    canDeleteDbRow = false;
                    diskDeleteSucceeded = false;
                }
                else if (!BackupSafetyService.TryCombinePathUnderRoot(baseRoot, relativePath, out fullPath))
                {
                    RuntimeLog.WriteVerbose(
                        $"[BackupService] Retention skipped out-of-root backup path '{backup.Path}' (backupId={backup.Id}); code=out-of-root.");
                    canDeleteDbRow = false;
                    diskDeleteSucceeded = false;
                }
                else if (!string.IsNullOrWhiteSpace(fullPath) && Directory.Exists(fullPath))
                {
                    RuntimeLog.WriteVerbose($"[BackupService] Retention deleting old backup folder '{fullPath}' (backupId={backup.Id}).");
                    RetentionDeleteAttemptResult deleteResult = TryDeleteBackupFolder(fullPath, backup.Id);
                    diskDeleteSucceeded = deleteResult.Success;
                    if (!diskDeleteSucceeded)
                    {
                        RuntimeLog.WriteVerbose($"[BackupService] Retention delete failed for backupId={backup.Id}; code={deleteResult.Code}; trying next eligible unprotected candidate.");
                    }
                }
                else
                {
                    if (!Directory.Exists(baseRoot))
                    {
                        RuntimeLog.WriteVerbose(
                            $"[BackupService] Retention deferred because destination root '{baseRoot}' disappeared during inspection (backupId={backup.Id}); code=destination-lost.");
                        canDeleteDbRow = false;
                        diskDeleteSucceeded = false;
                    }
                    else
                    {
                        RuntimeLog.WriteVerbose($"[BackupService] Retention could not find backup folder '{fullPath}' on an accessible destination (backupId={backup.Id}), continuing with DB cleanup.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupService] Failed to delete old backup (backupId={backup.Id}): {ex}");
                canDeleteDbRow = false;
                diskDeleteSucceeded = false;
            }

            // If we failed to remove this candidate from disk, do NOT drop its DB row;
            // move to the next oldest unprotected candidate.
            if (!diskDeleteSucceeded || !canDeleteDbRow)
            {
                continue;
            }

            BackupRetentionDeleted?.Invoke(backup);
            _repo.DeleteBackupById(backup.Id);
            if (projectName != null &&
                snapshotRefs.TryGetValue(backup.SnapshotId, out int remaining) &&
                remaining <= 1)
            {
                _repo.DeleteSnapshotsById(projectName, new[] { backup.SnapshotId });
                snapshotRefs.Remove(backup.SnapshotId);
            }
            else if (snapshotRefs.TryGetValue(backup.SnapshotId, out int count) && count > 1)
            {
                snapshotRefs[backup.SnapshotId] = count - 1;
            }

            deleted++;
        }
    }

    internal static BackupRetentionPreflightResult EvaluateRetentionPreflight(
        int projectId,
        IReadOnlyList<Backup> backups,
        IReadOnlyList<Backup> candidates,
        IReadOnlyDictionary<int, Snapshot> snapshotsById,
        int deleteQuota)
    {
        if (deleteQuota <= 0 || backups.Count == 0)
        {
            return new BackupRetentionPreflightResult(true, "ok", "Retention preflight passed.", CountValidRestorePoints(projectId, backups, snapshotsById), deleteQuota);
        }

        int validRestorePoints = CountValidRestorePoints(projectId, backups, snapshotsById);
        if (validRestorePoints == 0)
        {
            return new BackupRetentionPreflightResult(
                false,
                "retention-no-restorable-point",
                "Retention blocked because the project currently has no metadata-valid restore point.",
                0,
                deleteQuota);
        }

        var simulatedDeletedIds = candidates
            .Take(deleteQuota)
            .Select(static backup => backup.Id)
            .ToHashSet();
        int remainingValidRestorePoints = backups
            .Where(backup => !simulatedDeletedIds.Contains(backup.Id))
            .Count(backup => IsMetadataValidRestorePoint(projectId, backup, snapshotsById));

        if (remainingValidRestorePoints <= 0)
        {
            return new BackupRetentionPreflightResult(
                false,
                "retention-last-restorable-point",
                "Retention blocked because pruning would remove the last metadata-valid restore point for this project.",
                validRestorePoints,
                deleteQuota);
        }

        return new BackupRetentionPreflightResult(true, "ok", "Retention preflight passed.", validRestorePoints, deleteQuota);
    }

    internal static IReadOnlyList<BackupRetentionCandidateDecision> BuildRetentionDeletionPlan(
        int projectId,
        IReadOnlyList<Backup> backups,
        IReadOnlyList<Backup> candidates,
        IReadOnlyDictionary<int, Snapshot> snapshotsById,
        int deleteQuota,
        IReadOnlySet<int>? byteVerifiedBackupIds = null)
    {
        var decisions = new List<BackupRetentionCandidateDecision>();
        if (deleteQuota <= 0 || candidates.Count == 0)
            return decisions;

        int remainingValidRestorePoints = CountValidRestorePoints(projectId, backups, snapshotsById);
        int remainingByteVerifiedPoints = byteVerifiedBackupIds is null
            ? 0
            : backups.Count(backup => byteVerifiedBackupIds.Contains(backup.Id));
        int selected = 0;

        foreach (Backup? candidate in candidates.OrderBy(static backup => backup.CreatedUtc).ThenBy(static backup => backup.Id))
        {
            if (selected >= deleteQuota)
            {
                decisions.Add(new BackupRetentionCandidateDecision(
                    candidate.Id,
                    false,
                    "quota-satisfied",
                    "Deletion quota already satisfied by older eligible candidates."));
                continue;
            }

            bool isValidRestorePoint = IsMetadataValidRestorePoint(projectId, candidate, snapshotsById);
            bool isByteVerified = byteVerifiedBackupIds?.Contains(candidate.Id) == true;
            if (isValidRestorePoint && remainingValidRestorePoints <= 1)
            {
                decisions.Add(new BackupRetentionCandidateDecision(
                    candidate.Id,
                    false,
                    "preserve-last-restorable-point",
                    "Deleting this backup would remove the last metadata-valid restore point for the project."));
                continue;
            }

            if (isByteVerified && remainingByteVerifiedPoints <= 1)
            {
                decisions.Add(new BackupRetentionCandidateDecision(
                    candidate.Id,
                    false,
                    "preserve-last-byte-verified-point",
                    "Deleting this backup would remove the last recovery point that passed a byte-level recovery proof."));
                continue;
            }

            decisions.Add(new BackupRetentionCandidateDecision(
                candidate.Id,
                true,
                "selected",
                "Eligible for retention deletion."));
            selected++;
            if (isValidRestorePoint)
                remainingValidRestorePoints--;
            if (isByteVerified)
                remainingByteVerifiedPoints--;
        }

        return decisions;
    }

    private static int CountValidRestorePoints(
        int projectId,
        IEnumerable<Backup> backups,
        IReadOnlyDictionary<int, Snapshot> snapshotsById)
    {
        return backups.Count(backup => IsMetadataValidRestorePoint(projectId, backup, snapshotsById));
    }

    private static bool IsMetadataValidRestorePoint(
        int projectId,
        Backup backup,
        IReadOnlyDictionary<int, Snapshot> snapshotsById)
    {
        if (backup.ProjectId != projectId)
            return false;

        if (!snapshotsById.TryGetValue(backup.SnapshotId, out Snapshot? snapshot))
            return false;

        return snapshot.ProjectId == projectId;
    }

    private RetentionDeleteAttemptResult TryDeleteBackupFolder(string fullPath, int backupId)
    {
        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                return new RetentionDeleteAttemptResult(
                    false,
                    "linked-root",
                    "Retention refused to delete a backup folder that is itself a filesystem link.");
            }

            ClearAttributesRecursive(fullPath);
            DeleteKnownMarkerFiles(fullPath);
            Directory.Delete(fullPath, recursive: true);
            return new RetentionDeleteAttemptResult(true, "deleted", "Retention delete succeeded.");
        }
        catch (Exception firstEx)
        {
            Console.WriteLine($"[BackupService] Retention recursive delete failed for '{fullPath}' (backupId={backupId}), attempting fallback delete: {firstEx.Message}");
            try
            {
                FallbackDeleteDirectory(fullPath);
                return new RetentionDeleteAttemptResult(true, "deleted-fallback", "Retention delete succeeded via fallback path.");
            }
            catch (Exception fallbackEx)
            {
                RuntimeLog.WriteVerbose(
                    $"[BackupService] Retention fallback delete failed for '{fullPath}' (backupId={backupId}): {fallbackEx.GetType().Name} - {fallbackEx.Message}");
                return new RetentionDeleteAttemptResult(false, ClassifyRetentionDeleteFailure(fallbackEx), fallbackEx.Message);
            }
        }
    }

    private sealed record RetentionDeleteAttemptResult(bool Success, string Code, string Message);

    private static string ClassifyRetentionDeleteFailure(Exception ex)
    {
        return ex switch
        {
            UnauthorizedAccessException => "permission-denied",
            IOException ioEx when ioEx.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase) => "locked",
            IOException => "io-error",
            _ => "delete-failed"
        };
    }

    private static void DeleteKnownMarkerFiles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return;

        string[] markerFiles = new[]
        {
            Path.Combine(rootPath, InProgressMarkerFileName),
            Path.Combine(rootPath, CompletedMarkerFileName),
            Path.Combine(rootPath, ".vaultsync_keep"),
        };

        var failedMarkers = new List<string>();
        foreach (string? markerPath in markerFiles)
        {
            try
            {
                if (!File.Exists(markerPath))
                    continue;

                File.SetAttributes(markerPath, FileAttributes.Normal);
                File.Delete(markerPath);
            }
            catch (Exception ex)
            {
                failedMarkers.Add($"{Path.GetFileName(markerPath)} ({ex.GetType().Name})");
            }
        }

        if (failedMarkers.Count > 0)
        {
            RuntimeLog.WriteVerbose(
                $"[BackupService] Retention could not remove {failedMarkers.Count} marker file(s) under '{rootPath}': {string.Join(", ", failedMarkers)}.");
        }
    }

    private static void ClearAttributesRecursive(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return;

        try
        {
            File.SetAttributes(rootPath, FileAttributes.Normal);
        }
        catch
        {
            // Best effort; continue with children.
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(rootPath))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((attributes & FileAttributes.Directory) != 0)
                    ClearAttributesRecursive(entry);
                else
                    File.SetAttributes(entry, FileAttributes.Normal);
            }
            catch
            {
                // Best effort; delete phase will handle failures.
            }
        }
    }

    internal static void FallbackDeleteDirectory(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return;

        var failures = new DirectoryDeletionFailures();
        DeleteDirectoryContentsWithoutFollowingLinks(rootPath, failures);

        if (failures.Files > 0 || failures.Directories > 0)
        {
            RuntimeLog.WriteVerbose(
                $"[BackupService] Retention fallback cleanup for '{rootPath}' had {failures.Files} file failure(s) and {failures.Directories} directory failure(s). Samples: {string.Join(", ", failures.Samples)}.");
        }

        File.SetAttributes(rootPath, FileAttributes.Normal);
        Directory.Delete(rootPath, recursive: false);
    }

    private static void DeleteDirectoryContentsWithoutFollowingLinks(
        string rootPath,
        DirectoryDeletionFailures failures)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(rootPath))
        {
            try
            {
                DeleteEntryWithoutFollowingLinks(entry, failures);
            }
            catch (Exception ex)
            {
                RecordDirectoryDeletionFailure(entry, ex, failures);
            }
        }
    }

    private static void DeleteEntryWithoutFollowingLinks(
        string entry,
        DirectoryDeletionFailures failures)
    {
        FileAttributes attributes = File.GetAttributes(entry);
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        bool isLink = (attributes & FileAttributes.ReparsePoint) != 0;

        if (isDirectory && !isLink)
        {
            DeleteDirectoryContentsWithoutFollowingLinks(entry, failures);
            File.SetAttributes(entry, FileAttributes.Normal);
            Directory.Delete(entry, recursive: false);
            return;
        }

        if (isDirectory)
        {
            Directory.Delete(entry, recursive: false);
            return;
        }

        if (!isLink)
            File.SetAttributes(entry, FileAttributes.Normal);
        File.Delete(entry);
    }

    private static void RecordDirectoryDeletionFailure(
        string entry,
        Exception exception,
        DirectoryDeletionFailures failures)
    {
        bool isDirectory;
        try
        {
            isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
        }
        catch
        {
            isDirectory = Directory.Exists(entry);
        }

        if (isDirectory)
            failures.Directories++;
        else
            failures.Files++;

        if (failures.Samples.Count < 3)
            failures.Samples.Add($"{Path.GetFileName(entry)} ({exception.GetType().Name})");
    }

    private sealed class DirectoryDeletionFailures
    {
        public int Files { get; set; }
        public int Directories { get; set; }
        public List<string> Samples { get; } = [];
    }

    private sealed class NativeBackupRequest
    {
        public required Project Project { get; init; }
        public required string DestinationDirectory { get; init; }
        public long TotalBytes { get; init; }
        public int TotalFiles { get; init; }
        public List<FileEntry>? FilesForProgress { get; init; }
        public Action<double, string, string>? ProgressCallback { get; init; }
        public bool UseRsyncDelta { get; init; }
        public bool UseIncrementalBackups { get; init; }
        public string? LinkDestination { get; init; }
        public int? MaxBandwidthMbps { get; init; }
        public bool PreferRunnerProgressOnly { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    private sealed class NativeBackupProgressSession : IAsyncDisposable
    {
        private readonly CancellationTokenSource? _monitorCts;
        private readonly Task? _monitorTask;

        public NativeBackupProgressSession(
            Action<double, string, string>? runnerCallback,
            CancellationTokenSource? monitorCts = null,
            Task? monitorTask = null)
        {
            RunnerCallback = runnerCallback;
            _monitorCts = monitorCts;
            _monitorTask = monitorTask;
        }

        public Action<double, string, string>? RunnerCallback { get; }

        public async ValueTask DisposeAsync()
        {
            if (_monitorCts is null)
                return;

            await _monitorCts.CancelAsync().ConfigureAwait(false);
            try
            {
                if (_monitorTask is not null)
                    await _monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the native copy completes and stops its monitor.
            }

            RuntimeLog.WriteVerbose("[BackupService] Progress monitor cancelled.");
            _monitorCts.Dispose();
        }
    }

    private sealed class RunnerProgressDecorator
    {
        private static readonly TimeSpan MinUiInterval = TimeSpan.FromMilliseconds(100);
        private readonly long _totalBytes;
        private readonly int _totalFiles;
        private readonly Action<double, string, string> _progressCallback;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private DateTime _lastUiUpdate = DateTime.UtcNow;

        public RunnerProgressDecorator(
            long totalBytes,
            int totalFiles,
            Action<double, string, string> progressCallback)
        {
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _progressCallback = progressCallback;
        }

        public void Report(double percent, string currentFile)
        {
            DateTime now = DateTime.UtcNow;
            if (percent < 100 && (now - _lastUiUpdate) < MinUiInterval)
                return;

            _lastUiUpdate = now;
            TimeSpan elapsed = now - _startTime;
            string progressText = BuildProgressText(percent, elapsed);
            _progressCallback(percent, currentFile, progressText);
        }

        private string BuildProgressText(double percent, TimeSpan elapsed)
        {
            if (percent > 0 && percent < 100)
            {
                double elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                double speedMbSec = (_totalBytes * (percent / 100.0) / elapsedSeconds) / (1024 * 1024);
                var eta = TimeSpan.FromSeconds(elapsedSeconds * ((100.0 - percent) / percent));
                return _totalFiles > 0
                    ? $"Copying ~{(int)Math.Round(_totalFiles * (percent / 100.0))}/{_totalFiles} files - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}"
                    : $"Copying - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
            }

            if (percent >= 100 && elapsed.TotalSeconds > 0)
            {
                double speedMbSec = (_totalBytes / elapsed.TotalSeconds) / (1024 * 1024);
                return _totalFiles > 0
                    ? $"Copying ~{_totalFiles}/{_totalFiles} files - {speedMbSec:0.0} MB/s - Finalizing"
                    : $"Copying - {speedMbSec:0.0} MB/s - Finalizing";
            }

            return string.Empty;
        }
    }

    private sealed class RunnerProgressState
    {
        private readonly object _lock = new();
        private DateTime _lastUpdateUtc = DateTime.MinValue;

        public double LastPercent
        {
            get; private set;
        }
        public string? LastFile
        {
            get; private set;
        }
        public string? LastEtaText
        {
            get; private set;
        }

        public void Update(double percent, string currentFile, string etaText)
        {
            lock (_lock)
            {
                if (percent > 0)
                    LastPercent = percent;

                if (!string.IsNullOrWhiteSpace(currentFile))
                    LastFile = currentFile;

                if (!string.IsNullOrWhiteSpace(etaText))
                    LastEtaText = etaText;

                _lastUpdateUtc = DateTime.UtcNow;
            }
        }

        public bool HasRecentUpdate(TimeSpan window)
        {
            lock (_lock)
            {
                return (DateTime.UtcNow - _lastUpdateUtc) <= window;
            }
        }
    }

    private static (long totalBytes, long freeBytes)? TryGetDiskSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string fullPath = Path.GetFullPath(path);

            if (OperatingSystem.IsWindows())
            {
                if (!GetDiskFreeSpaceEx(
                        fullPath,
                        out ulong freeBytesAvailable,
                        out ulong totalNumberOfBytes,
                        out _))
                {
                    return null;
                }

                if (totalNumberOfBytes > long.MaxValue || freeBytesAvailable > long.MaxValue)
                    return null;

                long totalBytes = (long)totalNumberOfBytes;
                long freeBytes = (long)freeBytesAvailable;
                return IsValidDiskSpace(totalBytes, freeBytes)
                    ? (totalBytes, freeBytes)
                    : null;
            }

            if (OperatingSystem.IsMacOS() && IsMacManagedMountPath(fullPath) && !IsNetworkMountPath(fullPath))
            {
                Console.WriteLine($"[BackupService] Skipping free-space check for '{fullPath}': network mount not detected.");
                return null;
            }

            // Let the runtime query Unix filesystems. Hand-maintained statvfs layouts
            // are ABI-sensitive and previously produced impossible negative capacity
            // values on macOS.
            var driveInfo = new DriveInfo(fullPath);
            if (!driveInfo.IsReady)
                return null;

            long driveTotalBytes = driveInfo.TotalSize;
            long driveFreeBytes = driveInfo.AvailableFreeSpace;
            return IsValidDiskSpace(driveTotalBytes, driveFreeBytes)
                ? (driveTotalBytes, driveFreeBytes)
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to read disk space for '{path}': {ex.Message}");
            return null;
        }
    }

    internal static bool TryCalculateFreeSpacePercent(
        long totalBytes,
        long freeBytes,
        out double freePercent)
    {
        freePercent = 0d;
        if (!IsValidDiskSpace(totalBytes, freeBytes))
            return false;

        freePercent = (double)freeBytes / totalBytes * 100d;
        return double.IsFinite(freePercent) && freePercent is >= 0d and <= 100d;
    }

    private static bool IsValidDiskSpace(long totalBytes, long freeBytes) =>
        totalBytes > 0 && freeBytes >= 0 && freeBytes <= totalBytes;

    private static bool IsMacManagedMountPath(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string mountRoot = Path.Combine(home, "Library", "Application Support", "VaultSync", "mounts");
        return path.StartsWith(mountRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSmbfsMountPath(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/sbin/mount",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            proc.WaitForExit(3_000);
            string output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string line in lines)
            {
                if (!line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                int onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                string rest = line[(onIndex + 4)..];
                string mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                if (string.IsNullOrWhiteSpace(mountedAt))
                    continue;

                if (path.StartsWith(mountedAt, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsNfsMountPath(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/sbin/mount",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            proc.WaitForExit(3_000);
            string output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string line in lines)
            {
                if (!line.Contains(" nfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                int onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                string rest = line[(onIndex + 4)..];
                string mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                if (string.IsNullOrWhiteSpace(mountedAt))
                    continue;

                if (path.StartsWith(mountedAt, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsNetworkMountPath(string path)
        => IsSmbfsMountPath(path) || IsNfsMountPath(path);

    internal static bool ShouldRejectUnbackedManagedMount(
        bool isMacOs,
        bool isManagedMountPath,
        bool isNetworkMountPath) =>
        isMacOs && isManagedMountPath && !isNetworkMountPath;

    private static bool IsOnPath(string tool)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        char sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (string dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{tool}.exe" : tool);
                if (File.Exists(candidate))
                    return true;
            }
            catch { /* ignore */ }
        }
        return false;
    }

    private static string? TryGetBundledRsyncPath()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (OperatingSystem.IsWindows())
            {
                string direct = Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, RsyncWindowsExecutableName);
                if (File.Exists(direct))
                    return direct;

                string bin = Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "bin", RsyncWindowsExecutableName);
                return File.Exists(bin) ? bin : null;
            }

            if (OperatingSystem.IsMacOS())
            {
                var candidates = new List<string>();
                Architecture arch = RuntimeInformation.OSArchitecture;
                if (arch == Architecture.Arm64)
                {
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "arm64", "bin", RsyncExecutableName));
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "arm64", RsyncExecutableName));
                }
                else if (arch == Architecture.X64)
                {
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "x64", "bin", RsyncExecutableName));
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "x64", RsyncExecutableName));
                }
                else
                {
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "arm64", "bin", RsyncExecutableName));
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "x64", "bin", RsyncExecutableName));
                }

                candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, RsyncExecutableName));
                candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "bin", RsyncExecutableName));

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }

                return null;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryGetPreviousBackupFolder(string projectBackupRoot, string currentBackupFolder)
    {
        try
        {
            if (!Directory.Exists(projectBackupRoot))
                return null;

            var folders = Directory.EnumerateDirectories(projectBackupRoot)
                .Where(path => !string.Equals(path, currentBackupFolder, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return folders.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
