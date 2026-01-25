using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;

namespace VaultSync.Core.Services;

public sealed class BackupService
{
    private readonly Dictionary<int, CancellationTokenSource> _cancelMap = new();
    private readonly SqliteRepository _repo;
    private const string InProgressMarkerFileName = ".vaultsync_inprogress";
    private const string CompletedMarkerFileName = ".vaultsync_complete";
    public event Action<Backup>? BackupRetentionDeleted;

    public sealed record BackupRunResult(int BackupId, bool SkippedForNoChanges, bool Cancelled);

    public BackupService(SqliteRepository repo)
    {
        _repo = repo;
    }

    public void CancelBackup(int projectId)
    {
        if (_cancelMap.TryGetValue(projectId, out var cts))
        {
            Console.WriteLine($"[BackupService] Cancel requested for projectId={projectId}.");
            try { cts.Cancel(); } catch { }
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
            var markerPath = Path.Combine(backupFolder, fileName);
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
            var markerPath = Path.Combine(backupFolder, fileName);
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to remove marker '{fileName}' in '{backupFolder}': {ex.Message}");
        }
    }

    private static bool IsNetworkPathOrDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(path);
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

    public int CleanupIncompleteBackups(string backupRoot, IEnumerable<string>? projectFolderNames = null)
    {
        if (string.IsNullOrWhiteSpace(backupRoot) || !Directory.Exists(backupRoot))
            return 0;

        var removed = 0;
        var projectDirs = projectFolderNames?.ToList();
        if (projectDirs is { Count: > 0 })
        {
            foreach (var folder in projectDirs)
            {
                if (string.IsNullOrWhiteSpace(folder))
                    continue;

                var projectDir = Path.Combine(backupRoot, folder);
                if (!Directory.Exists(projectDir))
                    continue;

                removed += CleanupIncompleteBackupsUnderProject(projectDir);
            }

            return removed;
        }

        foreach (var projectDir in SafeEnumerateDirectories(backupRoot))
        {
            removed += CleanupIncompleteBackupsUnderProject(projectDir);
        }

        return removed;
    }

    private int CleanupIncompleteBackupsUnderProject(string projectDir)
    {
        var removed = 0;
        {
            foreach (var backupDir in SafeEnumerateDirectories(projectDir))
            {
                try
                {
                    var markerPath = Path.Combine(backupDir, InProgressMarkerFileName);
                    if (!File.Exists(markerPath))
                        continue;

                    DeletePartialBackup(backupDir);
                    removed++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    Console.WriteLine($"[BackupService] Skipping incomplete backup cleanup for '{backupDir}': {ex.Message}");
                }
            }
        }

        return removed;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
        {
            Console.WriteLine($"[BackupService] Skipping directory scan for '{root}': {ex.Message}");
            return Array.Empty<string>();
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
        CancellationToken ct = default,
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
        bool preferParallelArchiveUpload = false)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");

        if (string.IsNullOrWhiteSpace(backupRoot))
            throw new InvalidOperationException("Backup root is empty. Configure a backup location in Settings.");

        // Create (or replace) a CTS for this project and link with caller token.
        if (_cancelMap.ContainsKey(project.Id))
        {
            try { _cancelMap[project.Id].Cancel(); } catch { }
            _cancelMap[project.Id].Dispose();
        }
        var projectCts = new CancellationTokenSource();
        _cancelMap[project.Id] = projectCts;

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, projectCts.Token);
        var linkedToken = linkedCts.Token;
        using var cancelLog = linkedToken.Register(() =>
            Console.WriteLine($"[BackupService] Cancellation observed for project '{project.Name}' (Id={project.Id})."));

        linkedToken.ThrowIfCancellationRequested();

        Console.WriteLine($"[BackupService] RunBackupAsync entered for project '{project.Name}' (Id={project.Id}), backupRoot='{backupRoot}', isAuto={isAuto}, useArchiveMode={useArchiveMode}");
        progressCallback?.Invoke(0, "Preparing backup...", string.Empty);

        // Normalise backup root and ensure it exists (e.g. mounted NAS/share).
        backupRoot = Path.GetFullPath(backupRoot);
        if (!Directory.Exists(backupRoot))
        {
            throw new InvalidOperationException(
                $"Backup root '{backupRoot}' does not exist or is not accessible. " +
                "Make sure the path exists and any network share is mounted.");
        }

        // Project-specific backup root: <backupRoot>/project-slug/
        var projectSlug = Slugify(project.Name);
        var projectBackupRoot = Path.Combine(backupRoot, projectSlug);
        Directory.CreateDirectory(projectBackupRoot);

        // Optional low-disk protection: check free space on the backup target volume
        if (minimumFreeSpacePercent.HasValue && minimumFreeSpacePercent.Value > 0)
        {
            var space = TryGetDiskSpace(projectBackupRoot);
            if (space is not null)
            {
                var (volumeTotalBytes, volumeFreeBytes) = space.Value;
                if (volumeTotalBytes > 0)
                {
                    var freePercent = (double)volumeFreeBytes / volumeTotalBytes * 100d;
                    Console.WriteLine($"[BackupService] Backup target free space for '{project.Name}': {freePercent:0.0}% remaining (threshold={minimumFreeSpacePercent.Value:0.0}%).");

                    if (freePercent < minimumFreeSpacePercent.Value)
                    {
                        throw new InvalidOperationException(
                            $"Backup target for '{project.Name}' does not have enough free space. Free={freePercent:0.0}% (threshold={minimumFreeSpacePercent.Value:0.0}%).");
                    }
                }
            }
        }

        // Timestamped folder name: 2025-11-16_15-47-30
        var timestamp = DateTime.UtcNow;
        var folderName = timestamp.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupFolder = Path.Combine(projectBackupRoot, folderName);
        Directory.CreateDirectory(backupFolder);
        WriteMarkerFile(backupFolder, InProgressMarkerFileName, $"started:{DateTime.UtcNow:O}");
        var backupRootUsed = backupRoot;
        var backupFolderUsed = backupFolder;

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
                hashNow: false,
                maxSnapshotsToKeep,
                linkedToken,
                (percent, currentFile, etaText) =>
                {
                    progressCallback?.Invoke(percent, currentFile, etaText);
                });

            var outcome = SnapshotService.LastOutcome;
            if (skipIfNoChanges &&
                !reuseSnapshotId.HasValue &&
                outcome is { Added: 0, Modified: 0, Deleted: 0 })
            {
                // No file changes: remove the empty backup folder and snapshot, then skip.
                DeletePartialBackup(backupFolderUsed);
                _repo.DeleteSnapshotsById(project.Name, new[] { snapshotId });
                progressCallback?.Invoke(100, string.Empty, "No changes detected; backup skipped.");
                return new BackupRunResult(0, true, false);
            }
        }

        var snapshot = _repo.GetSnapshotById(snapshotId);
        if (snapshot is not null)
        {
            totalFilesForProgress = Convert.ToInt32(snapshot.FileCount);
            totalBytes = snapshot.TotalBytes;
        }

        var needFileList = useArchiveMode || (progressCallback is not null && !preferRunnerProgressOnly);
        if (needFileList)
        {
            try
            {
                filesForProgress = _repo.GetFilesForSnapshot(snapshotId).ToList();
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
            filesForBackup = filesForProgress
                .Select(f => Path.Combine(project.RootPath, f.RelPath))
                .ToArray();
        }

        Console.WriteLine($"[BackupService] Starting backup for '{project.Name}' ({project.RootPath}), totalBytes={totalBytes}.");

        string? linkDest = null;
        if (!useArchiveMode && useIncrementalBackups)
        {
            linkDest = TryGetPreviousBackupFolder(projectBackupRoot, backupFolder);
            if (linkDest is null)
            {
                Console.WriteLine($"[BackupService] Incremental enabled but no previous backup found for '{project.Name}'.");
            }
            else
            {
                Console.WriteLine($"[BackupService] Using incremental link-dest '{linkDest}'.");
            }
        }

        try
        {
            linkedToken.ThrowIfCancellationRequested();

            if (useArchiveMode)
            {
                progressCallback?.Invoke(0, "Preparing archive backup...", string.Empty);

                var uploadBufferBytes = NormalizeArchiveUploadBufferBytes(archiveUploadBufferBytes);
                await RunArchiveBackupAsync(
                    project,
                    backupFolder,
                    totalBytes,
                    totalFilesForProgress,
                    filesForBackup,
                    progressCallback,
                    linkedToken,
                    uploadBufferBytes,
                    preferParallelArchiveUpload);
            }
            else
            {
                progressCallback?.Invoke(0, "Copying files...", string.Empty);

                await RunNativeBackupAsync(
                    project,
                    backupFolder,
                    totalBytes,
                    totalFilesForProgress,
                    filesForProgress,
                    progressCallback,
                    linkedToken,
                    useRsyncDelta,
                    useIncrementalBackups,
                    linkDest,
                    preferRunnerProgressOnly);
            }
        }
        catch (Exception ex)
        {
            if (linkedToken.IsCancellationRequested)
            {
                Console.WriteLine($"[BackupService] Backup cancelled for '{project.Name}'. Cleaning up.");
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
                    var destProjectRoot = Path.Combine(preferredFinalBackupRoot, projectSlug);
                    Directory.CreateDirectory(destProjectRoot);

                    var destFolder = Path.Combine(destProjectRoot, Path.GetFileName(backupFolder));
                    if (Directory.Exists(destFolder))
                        Directory.Delete(destFolder, recursive: true);

                    Directory.Move(backupFolder, destFolder);
                    backupRootUsed = preferredFinalBackupRoot;
                    backupFolderUsed = destFolder;

                    // Clean up empty temp project root if applicable
                    try
                    {
                        var tempProjectRoot = Path.GetDirectoryName(backupFolder);
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

        // Store relative path so if backupRoot moves, paths are still valid.
        var relativePath = Path.GetRelativePath(backupRootUsed, backupFolderUsed);
        var backupType = isAuto ? "auto" : "manual";

        Console.WriteLine($"[BackupService] Backup data written for '{project.Name}', creating backup metadata in database...");

        var backupId = 0;

        if (writeMetadata)
        {
            // Persist metadata in the backups table
            var metadataRoot = !string.IsNullOrWhiteSpace(destinationPath)
                ? destinationPath
                : backupRootUsed;
            var metadataAlias = !string.IsNullOrWhiteSpace(destinationAlias)
                ? destinationAlias
                : string.Empty;

            backupId = _repo.CreateBackup(
                projectId: project.Id,
                snapshotId: snapshotId,
                type: backupType,
                totalBytes: totalBytes,
                relativePath: relativePath,
                destinationPath: metadataRoot,
                destinationAlias: metadataAlias);

            Console.WriteLine($"[BackupService] Backup metadata created successfully for '{project.Name}' (backupId={backupId}).");

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
            Console.WriteLine($"[BackupService] Skipping completion marker on network destination '{backupFolderUsed}'.");
        }

        progressCallback?.Invoke(100, string.Empty, useArchiveMode ? "Backup completed (archive)." : "Backup completed.");

        if (_cancelMap.TryGetValue(project.Id, out var finishedCts))
        {
            finishedCts.Dispose();
            _cancelMap.Remove(project.Id);
        }
        // Ensure cancelled backups never leave partial folders
        if (linkedToken.IsCancellationRequested)
        {
            DeletePartialBackup(backupFolder);
        }
        return new BackupRunResult(backupId, false, false);
    }

    /// <summary>
    /// Uses the platform-specific sync runner (rsync on macOS/Linux, robocopy on Windows)
    /// to perform a fast backup of the project into the given destination folder.
    /// Throws if the tool is missing or returns a failure exit code.
    /// </summary>
    private static async Task RunNativeBackupAsync(
        Project project,
        string destDir,
        long totalBytes,
        int totalFiles,
        List<FileEntry>? filesForProgress,
        Action<double, string, string>? progressCallback,
        CancellationToken ct,
        bool useRsyncDelta,
        bool useIncrementalBackups,
        string? linkDest,
        bool preferRunnerProgressOnly)
    {
        ct.ThrowIfCancellationRequested();

        // Normalise destination trailing separator for the runners.
        if (!destDir.EndsWith(Path.DirectorySeparatorChar))
            destDir += Path.DirectorySeparatorChar;

        // Wrap the runner's raw percentage/file progress to compute ETA and speed
        // based on the totalBytes we computed up front.
        Action<double, string, string>? callbackForRunner;
        CancellationTokenSource? monitorCts = null;
        Task? monitorTask = null;
        RunnerProgressState? runnerState = null;
        var useHybridMonitor = progressCallback is not null
            && totalBytes > 0
            && filesForProgress is not null
            && filesForProgress.Count > 0
            && !preferRunnerProgressOnly;

        if (useHybridMonitor)
        {
            Console.WriteLine("[BackupService] Progress monitor enabled (destination scans for progress).");
            monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runnerState = new RunnerProgressState();
            monitorTask = MonitorCopyProgressAsync(destDir, filesForProgress!, totalBytes, progressCallback!, monitorCts.Token, runnerState);
            callbackForRunner = (percent, currentFile, etaText) =>
            {
                runnerState.Update(percent, currentFile, etaText);
            };
        }
        else if (progressCallback is null || totalBytes <= 0)
        {
            // Nothing to decorate; just pass through whatever the runner reports.
            callbackForRunner = progressCallback;
        }
        else
        {
            var startTime = DateTime.UtcNow;
            var lastUiUpdate = startTime;
            var minUiInterval = TimeSpan.FromMilliseconds(100);

            callbackForRunner = (percent, currentFile, _) =>
            {
                var now = DateTime.UtcNow;

                // Throttle UI updates a bit to avoid spamming the UI thread when
                // the native tool reports progress very frequently, especially
                // when multiple backups run in parallel.
                if (percent < 100 && (now - lastUiUpdate) < minUiInterval)
                    return;

                lastUiUpdate = now;

                var elapsed = now - startTime;
                var etaText = string.Empty;

                if (percent > 0 && percent < 100)
                {
                    var elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                    var doneBytes = totalBytes * (percent / 100.0);
                    var speedBytesSec = doneBytes / elapsedSeconds;
                    var speedMbSec = speedBytesSec / (1024 * 1024);

                    var remainingFraction = (100.0 - percent) / percent;
                    var remainingSeconds = elapsedSeconds * remainingFraction;
                    var eta = TimeSpan.FromSeconds(remainingSeconds);

                    if (totalFiles > 0)
                    {
                        var approxDone = (int)Math.Round(totalFiles * (percent / 100.0));
                        etaText = $"Copying ~{approxDone}/{totalFiles} files - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
                    }
                    else
                    {
                        etaText = $"Copying - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
                    }
                }
                else if (percent >= 100 && elapsed.TotalSeconds > 0)
                {
                    var elapsedSeconds = elapsed.TotalSeconds;
                    var speedBytesSec = totalBytes / elapsedSeconds;
                    var speedMbSec = speedBytesSec / (1024 * 1024);

                    if (totalFiles > 0)
                    {
                        etaText = $"Copying ~{totalFiles}/{totalFiles} files - {speedMbSec:0.0} MB/s - Finalizing";
                    }
                    else
                    {
                        etaText = $"Copying - {speedMbSec:0.0} MB/s - Finalizing";
                    }
                }

                progressCallback!(percent, currentFile, etaText);
            };
        }

        var isNetworkDestination = IsNetworkPath(destDir);
        var effectiveUseRsyncDelta = useRsyncDelta;
        if (OperatingSystem.IsWindows() && isNetworkDestination && !useRsyncDelta && !useIncrementalBackups)
        {
            if (TryGetBundledRsyncPath() is not null || IsOnPath("rsync"))
            {
                effectiveUseRsyncDelta = true;
            }
        }

        int exitCode;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bundledRsync = TryGetBundledRsyncPath();
                if ((effectiveUseRsyncDelta || useIncrementalBackups) && (bundledRsync is not null || IsOnPath("rsync")))
                {
                    // rsync-based backup on Windows (when installed)
                    var source = bundledRsync is null ? "PATH" : "bundled";
                    var rsyncPath = bundledRsync ?? "rsync";
                    Console.WriteLine($"[BackupService] Starting rsync backup (source={source}, delta={effectiveUseRsyncDelta}, incremental={useIncrementalBackups}).");
                    Console.WriteLine($"[BackupService] Using rsync on Windows ({source}).");
                    var runner = new RsyncRunner(useWholeFile: !effectiveUseRsyncDelta, rsyncPath: rsyncPath);
                    exitCode = await runner.SyncAsync(
                        project,
                        destDir,
                        dryRun: false,
                        callbackForRunner,
                        useIncrementalBackups ? linkDest : null,
                        ct);

                    if (exitCode != 0)
                        throw new InvalidOperationException($"rsync backup failed with exit code {exitCode}.");
                }
                else
                {
                    // robocopy-based backup (multi-threaded, robust on Windows)
                    if ((effectiveUseRsyncDelta || useIncrementalBackups) && bundledRsync is null && !IsOnPath("rsync"))
                        Console.WriteLine("[BackupService] rsync not found on PATH; falling back to robocopy.");

                    Console.WriteLine($"[BackupService] Starting robocopy backup (threads={(isNetworkDestination ? Math.Min(32, Math.Max(4, Environment.ProcessorCount)) : Math.Min(128, Math.Max(8, Environment.ProcessorCount * 2)))}).");
                    var runner = new RobocopyRunner(isNetworkDestination);
                    exitCode = await runner.SyncAsync(
                        project,
                        destDir,
                        dryRun: false,
                        callbackForRunner,
                        ct);

                    if (exitCode != 0)
                        throw new InvalidOperationException($"robocopy backup failed with exit code {exitCode}. See RobocopyRunner logs above for stdout/stderr.");
                }
            }
            else
            {
                // rsync-based backup (fast, incremental on macOS/Linux)
                Console.WriteLine($"[BackupService] Starting rsync backup (delta={effectiveUseRsyncDelta}, incremental={useIncrementalBackups}).");
                var runner = new RsyncRunner(useWholeFile: !effectiveUseRsyncDelta);
                exitCode = await runner.SyncAsync(
                    project,
                    destDir,
                    dryRun: false,
                    callbackForRunner,
                    useIncrementalBackups ? linkDest : null,
                    ct);

                if (exitCode != 0)
                    throw new InvalidOperationException($"rsync backup failed with exit code {exitCode}.");
            }
        }
        finally
        {
            if (monitorCts is not null)
            {
                monitorCts.Cancel();
                try
                {
                    if (monitorTask is not null)
                        await monitorTask;
                }
                catch (OperationCanceledException)
                {
                    // expected when stopping monitor
                }
                Console.WriteLine("[BackupService] Progress monitor cancelled.");
                monitorCts.Dispose();
            }
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var root = Path.GetPathRoot(path);
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
        CancellationToken ct,
        RunnerProgressState? runnerState)
    {
        var startTime = DateTime.UtcNow;
        var lastSample = startTime;
        long lastBytes = 0;
        var totalEntries = filesForProgress.Count;
        if (totalEntries == 0)
            return;

        Console.WriteLine($"[BackupService] Progress monitor started for '{destDir}' (entries={totalEntries}).");

        var observedSizes = new long[totalEntries];
        var completed = new bool[totalEntries];
        var completedFiles = 0;
        long copiedBytes = 0;

        var minInterval = totalEntries > 4000
            ? TimeSpan.FromMilliseconds(1000)
            : TimeSpan.FromMilliseconds(500);
        var logInterval = TimeSpan.FromSeconds(5);
        var lastLog = startTime;

        var chunkSize = totalEntries switch
        {
            > 20000 => 150,
            > 10000 => 250,
            > 4000 => 400,
            > 1000 => 600,
            _ => 900
        };

        var scanIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            var scanned = 0;
            string? lastTouched = null;

            while (scanned < chunkSize && totalEntries > 0)
            {
                var index = scanIndex++ % totalEntries;
                var entry = filesForProgress[index];
                var targetPath = Path.Combine(destDir, entry.RelPath);

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

                var previousSize = observedSizes[index];
                if (size != previousSize)
                {
                    observedSizes[index] = size;
                    var delta = size - previousSize;
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

            var now = DateTime.UtcNow;
            var elapsed = now - lastSample;
            if (elapsed.TotalSeconds < 0.1)
                elapsed = TimeSpan.FromSeconds(0.1);

            var deltaBytes = copiedBytes - lastBytes;
            if (deltaBytes < 0)
                deltaBytes = 0;

            lastBytes = copiedBytes;
            lastSample = now;

            double percent = totalBytes > 0
                ? Math.Min(99.5d, copiedBytes * 100d / totalBytes)
                : 0d;

            var speedBytesSec = deltaBytes / elapsed.TotalSeconds;
            var speedMbSec = speedBytesSec / (1024 * 1024);

            string etaText;
            if (speedBytesSec > 0 && totalBytes > 0)
            {
                var remainingBytes = Math.Max(0, totalBytes - copiedBytes);
                var etaSeconds = remainingBytes / speedBytesSec;
                var eta = TimeSpan.FromSeconds(etaSeconds);
                etaText = $"Copying {completedFiles}/{totalEntries} files - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
            }
            else
            {
                if (runnerState is not null && runnerState.HasRecentUpdate(TimeSpan.FromSeconds(10)))
                {
                    var fallback = runnerState.LastEtaText;
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
            }

            var currentFile = string.IsNullOrWhiteSpace(lastTouched) ? string.Empty : lastTouched;
            if (string.IsNullOrWhiteSpace(currentFile) && runnerState is not null)
            {
                currentFile = runnerState.LastFile ?? string.Empty;
            }

            if (copiedBytes <= 0 && runnerState is not null)
            {
                var fallbackPercent = runnerState.LastPercent;
                if (fallbackPercent > 0)
                    percent = Math.Min(99.0d, fallbackPercent);
            }
            progressCallback(percent, currentFile, etaText);

            if ((now - lastLog) >= logInterval)
            {
                Console.WriteLine($"[BackupService] Copy progress: {completedFiles}/{totalEntries} files, {percent:0.0}% ({speedMbSec:0.0} MB/s).");
                lastLog = now;
            }

            await Task.Delay(minInterval, ct);
        }

        Console.WriteLine($"[BackupService] Progress monitor stopped for '{destDir}'.");
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

        var value = requestedBytes.Value;
        if (value < minBytes)
            return minBytes;
        if (value > maxBytes)
            return maxBytes;

        return value;
    }

    private const int ArchiveCopyBufferBytes = 1024 * 1024;
    private const int ArchiveFileStreamBufferBytes = 1024 * 1024;

    private static async Task RunArchiveBackupAsync(
        Project project,
        string destDir,
        long totalBytes,
        int totalFiles,
        IReadOnlyList<string>? filesForBackup,
        Action<double, string, string>? progressCallback,
        CancellationToken ct,
        int uploadBufferBytes,
        bool preferParallelUpload)
    {
        var sourceDir = project.RootPath;
        var srcInfo = new DirectoryInfo(sourceDir);
        if (!srcInfo.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");

        Console.WriteLine($"[BackupService] RunArchiveBackupAsync (LOCAL ZIP MODE, 2-PHASE) started for '{project.Name}', destDir='{destDir}', totalBytes={totalBytes}.");

        // 1. Build filter identical to snapshots.
        var filter = FilterService.FromPresetAndLocal(sourceDir, project.Preset);

        // 2. Gather all files that will be archived.
        var allFiles = filesForBackup?.ToArray() ?? BuildFilteredFileList(sourceDir, filter, ct);
        var archiveTotalFiles = totalFiles > 0 ? totalFiles : allFiles.Length;

        // 3. Prepare destination and local temp folder.
        Directory.CreateDirectory(destDir);
        var finalArchivePath = Path.Combine(destDir, "data.zip");

        var localTempRoot = Path.Combine(Path.GetTempPath(), "vaultsync_archive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localTempRoot);
        var localArchive = Path.Combine(localTempRoot, "data.zip");

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

                File.Copy(localArchive, finalArchivePath, overwrite: true);
                progressCallback?.Invoke(100, string.Empty, "Archive empty (0 files).");

                var emptySize = new FileInfo(finalArchivePath).Length;
                Console.WriteLine($"[BackupService] Created empty archive for '{project.Name}', size={emptySize} bytes.");
                return;
            }

            // --------------------
            // PHASE 1: Compress files into local ZIP
            // --------------------
            long processedBytes = 0;
            var processedFiles = 0;
            var startTime = DateTime.UtcNow;
            var lastUiUpdate = startTime;
            var minUiInterval = TimeSpan.FromMilliseconds(100);

            using (var fs = new FileStream(
                localArchive,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                ArchiveFileStreamBufferBytes,
                FileOptions.SequentialScan))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var filePath in allFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(sourceDir, filePath);
                    var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);

                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var input = new FileStream(
                            filePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            ArchiveFileStreamBufferBytes,
                            FileOptions.SequentialScan))
                        {
                            await input.CopyToAsync(entryStream, ArchiveCopyBufferBytes, ct);
                            processedBytes += input.Length;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Console.WriteLine($"[BackupService] Failed to add '{filePath}' to archive: {ex.Message}");
                        continue;
                    }

                    if (progressCallback is not null)
                    {
                        processedFiles++;

                        // Map compression progress into 0-90% of overall progress.
                        double compressPercent = (totalBytes > 0)
                            ? Math.Min(100d, (processedBytes * 100d / totalBytes))
                            : 0d;

                        double overallPercent = compressPercent * 0.9; // 0-90%

                        var now = DateTime.UtcNow;
                        if (overallPercent < 90d && (now - lastUiUpdate) < minUiInterval)
                            continue;

                        lastUiUpdate = now;

                        var elapsed = now - startTime;
                        var elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                        var speedBytesSec = processedBytes / elapsedSeconds;
                        var speedMbSec = speedBytesSec / (1024 * 1024);

                        string etaText;
                        if (overallPercent > 0 && overallPercent < 90)
                        {
                            var remainingFraction = (90d - overallPercent) / overallPercent;
                            var remainingSeconds = elapsedSeconds * remainingFraction;
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

            // --------------------
            // PHASE 2: Upload local ZIP to destination with progress (90-100%)
            // --------------------
            ct.ThrowIfCancellationRequested();

            var zipInfo = new FileInfo(localArchive);
            var zipSize = zipInfo.Length;
            var bufferSize = uploadBufferBytes;
            var stallTimeout = ComputeArchiveUploadStallTimeout(bufferSize);

            async Task UploadSingleAttemptAsync(long startOffset)
            {
                long uploaded = 0;
                var uploadStart = DateTime.UtcNow;
                var lastLogTime = uploadStart;
                long lastLogBytes = 0;
                var lastUiUpdate = uploadStart;
                long lastUiBytes = startOffset;
                var buffer = new byte[bufferSize];
                var lastProgressTicks = uploadStart.Ticks;
                var stalled = 0;

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var monitor = Task.Run(async () =>
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
                            linkedCts.Cancel();
                            return;
                        }
                    }
                });

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
                            var uploadPercent = Math.Min(100d, (uploaded * 100d / zipSize));
                            var overallPercent = 90d + uploadPercent * 0.1; // map 0-100% upload into 90-100%
                            if (overallPercent > 100d) overallPercent = 100d;

                            var now = DateTime.UtcNow;
                            var intervalSeconds = Math.Max(0.1, (now - lastUiUpdate).TotalSeconds);
                            var intervalBytes = Math.Max(0, uploaded - lastUiBytes);
                            var speedBytesSec = intervalBytes / intervalSeconds;
                            var speedMbSec = speedBytesSec / (1024 * 1024);
                            lastUiUpdate = now;
                            lastUiBytes = uploaded;

                            var uploadedMb = uploaded / (1024d * 1024d);
                            var totalMb = zipSize / (1024d * 1024d);

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
                                var remainingBytes = Math.Max(0, zipSize - uploaded);
                                var remainingSeconds = remainingBytes / speedBytesSec;
                                var eta = TimeSpan.FromSeconds(remainingSeconds);
                                etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - ETA {eta:mm\\:ss}";
                            }

                            progressCallback(overallPercent, Path.GetFileName(finalArchivePath), etaText);
                        }

                        if ((DateTime.UtcNow - lastLogTime) >= TimeSpan.FromSeconds(5))
                        {
                            var now = DateTime.UtcNow;
                            var intervalSeconds = Math.Max(0.1, (now - lastLogTime).TotalSeconds);
                            var intervalBytes = uploaded - lastLogBytes;
                            var intervalMbSec = (intervalBytes / intervalSeconds) / (1024d * 1024d);
                            Console.WriteLine($"[BackupService] Archive upload (single) {uploaded}/{zipSize} bytes ({intervalMbSec:0.0} MB/s).");
                            lastLogTime = now;
                            lastLogBytes = uploaded;
                        }
                    }
                }

                linkedCts.Cancel();
                await monitor;

                if (Interlocked.CompareExchange(ref stalled, 0, 0) == 1)
                {
                    throw new TimeoutException("No upload progress detected during single archive upload.");
                }
            }

            async Task UploadSingleWithResumeAsync(int maxRetries)
            {
                var attempt = 0;
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

                        await UploadSingleAttemptAsync(existingLength);
                        return;
                    }
                    catch (TimeoutException ex) when (attempt <= maxRetries)
                    {
                        Console.WriteLine($"[BackupService] Single archive upload stalled (attempt {attempt}/{maxRetries}). Retrying from {existingLength} bytes.");
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                }
            }

            await EnsureDestinationWriteReadyAsync(destDir, ct);

            if (preferParallelUpload && zipSize >= bufferSize * 8L)
            {
                Console.WriteLine($"[BackupService] Uploading archive with parallel writer (parts={Math.Clamp(Environment.ProcessorCount / 2, 2, 4)}, buffer={bufferSize / (1024 * 1024)} MB).");
                try
                {
                    await UploadArchiveParallelAsync(
                        localArchive,
                        finalArchivePath,
                        zipSize,
                        bufferSize,
                        progressCallback,
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
                Console.WriteLine($"[BackupService] Uploading archive with single writer (buffer={bufferSize / (1024 * 1024)} MB).");
                await UploadSingleWithResumeAsync(2);
            }

            Console.WriteLine($"[BackupService] RunArchiveBackupAsync completed for '{project.Name}'. LocalZipSize={zipSize} bytes");
        }
        catch
        {
            // Cleanup: remove incomplete destination folder and rethrow.
            DeletePartialBackup(destDir);
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

    private static async Task UploadArchiveParallelAsync(
        string localArchive,
        string finalArchivePath,
        long zipSize,
        int bufferSize,
        Action<double, string, string>? progressCallback,
        CancellationToken ct)
    {
        if (zipSize <= 0)
            return;

        var finalDir = Path.GetDirectoryName(finalArchivePath);
        if (string.IsNullOrWhiteSpace(finalDir))
            throw new DirectoryNotFoundException($"Archive destination directory is missing for '{finalArchivePath}'.");

        Directory.CreateDirectory(finalDir);

        var parallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        var chunkSize = (long)Math.Ceiling(zipSize / (double)parallelism);
        var fileName = Path.GetFileName(finalArchivePath);
        var uploadStart = DateTime.UtcNow;
        var minUiInterval = TimeSpan.FromMilliseconds(150);
        var progressLock = new object();
        var lastUiUpdate = uploadStart;
        long lastUiBytes = 0;
        long uploaded = 0;
        var lastLogTime = uploadStart;
        long lastLogBytes = 0;
        var logLock = new object();
        var stallTimeout = ComputeArchiveUploadStallTimeout(bufferSize);
        var lastProgressTicks = uploadStart.Ticks;
        var stalled = 0;

        using (var init = new FileStream(
                   finalArchivePath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.Write,
                   1,
                   FileOptions.Asynchronous))
        {
            init.SetLength(zipSize);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                var lastProgress = new DateTime(Interlocked.Read(ref lastProgressTicks), DateTimeKind.Utc);
                if (DateTime.UtcNow - lastProgress > stallTimeout)
                {
                    Interlocked.Exchange(ref stalled, 1);
                    cts.Cancel();
                    return;
                }
            }
        });
        var heartbeat = Task.Run(async () =>
        {
            if (progressCallback is null)
                return;

            var heartbeatInterval = TimeSpan.FromSeconds(5);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(heartbeatInterval, cts.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                var snapshotUploaded = Interlocked.Read(ref uploaded);
                var now = DateTime.UtcNow;
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

                var speedBytesSec = intervalBytes / intervalSeconds;
                var speedMbSec = speedBytesSec / (1024 * 1024);
                var uploadPercent = Math.Min(100d, snapshotUploaded * 100d / zipSize);
                var overallPercent = 90d + uploadPercent * 0.1;
                if (overallPercent > 100d)
                    overallPercent = 100d;

                var uploadedMb = snapshotUploaded / (1024d * 1024d);
                var totalMb = zipSize / (1024d * 1024d);

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
                    var remainingBytes = Math.Max(0, zipSize - snapshotUploaded);
                    var remainingSeconds = remainingBytes / speedBytesSec;
                    var eta = TimeSpan.FromSeconds(remainingSeconds);
                    etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - ETA {eta:mm\\:ss}";
                }

                progressCallback(overallPercent, fileName, etaText);
            }
        });
        var tasks = Enumerable.Range(0, parallelism)
            .Select(index =>
            {
                var start = chunkSize * index;
                if (start >= zipSize)
                    return Task.CompletedTask;

                var length = Math.Min(chunkSize, zipSize - start);
                return Task.Run(async () =>
                {
                    var buffer = new byte[bufferSize];
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

                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = await src.ReadAsync(buffer.AsMemory(0, toRead), cts.Token);
                        if (read == 0)
                            break;

                        await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        remaining -= read;

                        if (progressCallback is null)
                        {
                            Interlocked.Add(ref uploaded, read);
                            Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                            continue;
                        }

                        var totalUploaded = Interlocked.Add(ref uploaded, read);
                        Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                        var uploadPercent = Math.Min(100d, totalUploaded * 100d / zipSize);
                        var overallPercent = 90d + uploadPercent * 0.1;
                        if (overallPercent > 100d)
                            overallPercent = 100d;

                        var now = DateTime.UtcNow;
                        var shouldUpdate = true;
                        var intervalSeconds = 0d;
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

                        var speedBytesSec = intervalBytes / intervalSeconds;
                        var speedMbSec = speedBytesSec / (1024 * 1024);
                        var uploadedMb = totalUploaded / (1024d * 1024d);
                        var totalMb = zipSize / (1024d * 1024d);

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
                            var remainingBytes = Math.Max(0, zipSize - totalUploaded);
                            var remainingSeconds = remainingBytes / speedBytesSec;
                            var eta = TimeSpan.FromSeconds(remainingSeconds);
                            etaText = $"{speedMbSec:0.0} MB/s - Uploading archive ({uploadedMb:0.0}/{totalMb:0.0} MB) - ETA {eta:mm\\:ss}";
                        }

                        progressCallback(overallPercent, fileName, etaText);
                    }

                    var logNow = DateTime.UtcNow;
                    var snapshotUploaded = Interlocked.Read(ref uploaded);
                    lock (logLock)
                    {
                        if ((logNow - lastLogTime) >= TimeSpan.FromSeconds(5))
                        {
                            var intervalSeconds = Math.Max(0.1, (logNow - lastLogTime).TotalSeconds);
                            var intervalBytes = snapshotUploaded - lastLogBytes;
                            var intervalMbSec = (intervalBytes / intervalSeconds) / (1024d * 1024d);
                            Console.WriteLine($"[BackupService] Archive upload (parallel) {snapshotUploaded}/{zipSize} bytes ({intervalMbSec:0.0} MB/s).");
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
            cts.Cancel();
            try
            {
                await Task.WhenAll(monitor, heartbeat);
            }
            catch (OperationCanceledException)
            {
                // expected once we cancel the monitor/heartbeat
            }
        }
        catch
        {
            cts.Cancel();
            throw;
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

        var seconds = (int)Math.Ceiling(bufferSize / (double)minBytesPerSec * 2d);
        seconds = Math.Clamp(seconds, minSeconds, maxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task EnsureDestinationWriteReadyAsync(string destDir, CancellationToken ct)
    {
        var probePath = Path.Combine(destDir, ".vaultsync_upload_probe");
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

        var completed = await Task.WhenAny(task, Task.Delay(timeout, ct));
        if (completed != task)
        {
            throw new TimeoutException($"Destination write probe timed out for '{destDir}'.");
        }

        await task;
    }

    /// <summary>
    /// Computes the total bytes that will be included in the backup, using the same
    /// filtering rules as SnapshotService.
    /// </summary>
    private static long ComputeBackupSize(string sourceDir, string preset, CancellationToken ct)
    {
        var dirInfo = new DirectoryInfo(sourceDir);
        if (!dirInfo.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");

        var filter = FilterService.FromPresetAndLocal(sourceDir, preset);
        long total = 0;

        try
        {
            var allFiles = BuildFilteredFileList(sourceDir, filter, ct);
            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fi = new FileInfo(filePath);
                    total += fi.Length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Skip files that cannot be accessed but do not abort the entire backup size computation.
                    Console.WriteLine($"[BackupService] Skipping file while computing size '{filePath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Log and rethrow so callers can decide whether to fall back or abort.
            Console.WriteLine($"[BackupService] Failed to enumerate files for size computation in '{sourceDir}': {ex.Message}");
            throw;
        }

        Console.WriteLine($"[BackupService] Computed backup size for '{sourceDir}': {total} bytes.");
        return total;
    }

    private static string[] BuildFilteredFileList(string sourceDir, FilterService filter, CancellationToken ct)
    {
        var files = new List<string>();
        foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if (filter.ShouldExclude(sourceDir, filePath))
                continue;

            files.Add(filePath);
        }

        return files.ToArray();
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
        var allFiles = filesForBackup?.ToArray() ?? BuildFilteredFileList(sourceDir, filter, ct);
        var totalFiles = allFiles.Length;
        var processedFiles = 0;
        var startTime = DateTime.UtcNow;
        long copiedBytes = 0;

        foreach (var filePath in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            var fileInfo = new FileInfo(filePath);
            var relative = Path.GetRelativePath(sourceDir, filePath);
            var targetPath = Path.Combine(destDir, relative);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            try
            {
                const int bufferSize = 1024 * 1024; // 1 MB
                var buffer = new byte[bufferSize];

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
                                var filesCompletedPortion = processedFiles * 100d / totalFiles;
                                var currentFilePortion = (double)copiedForThisFile / Math.Max(1L, fileInfo.Length) * (100d / totalFiles);
                                percent = filesCompletedPortion + currentFilePortion;
                                if (percent > 100d) percent = 100d;
                            }

                            var elapsed = DateTime.UtcNow - startTime;
                            var elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                            string etaText = string.Empty;

                            if (percent > 0d && percent < 100d)
                            {
                                var remainingFraction = (100d - percent) / percent;
                                var remainingSeconds = elapsedSeconds * remainingFraction;
                                var eta = TimeSpan.FromSeconds(remainingSeconds);
                                var speedBytesSec = copiedBytes / elapsedSeconds;
                                var speedMbSec = speedBytesSec / (1024 * 1024);
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
                // Log and skip the file rather than aborting the whole fallback backup.
                Console.WriteLine($"[BackupService] Failed to copy '{filePath}' to '{targetPath}': {ex.Message}");
                continue;
            }
        }
    }

    private static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "project";

        var invalid = Path.GetInvalidFileNameChars();
        var cleanedChars = name
            .Trim()
            .ToLowerInvariant()
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();

        var cleaned = new string(cleanedChars);

        // Collapse multiple '-' to a single '-'
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        return cleaned.Trim('-');
    }

    public static string GetProjectBackupFolderName(string name) => Slugify(name);
    private void ApplyBackupRetention(int projectId, string backupRoot, int? maxSnapshotsToKeep)
    {
        if (!maxSnapshotsToKeep.HasValue || maxSnapshotsToKeep.Value <= 0)
        {
            // Retention disabled or not configured.
            return;
        }

        var maxToKeep = Math.Max(1, maxSnapshotsToKeep.Value);

        // Load all backups for this project, newest first.
        List<Backup> backups;
        try
        {
            backups = _repo
                .GetBackupsForProject(projectId)
                .OrderByDescending(b => b.CreatedUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to load backups for retention (projectId={projectId}): {ex}");
            return;
        }

        // Keep all protected backups; apply the cap only to unprotected ones.
        var unprotected = backups.Where(b => !b.IsProtected).ToList();
        var toRemove = unprotected.Skip(maxToKeep).ToList();

        var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
        var projectName = project?.Name;
        var snapshotRefs = new Dictionary<int, int>();
        foreach (var backup in backups)
        {
            if (snapshotRefs.TryGetValue(backup.SnapshotId, out var count))
                snapshotRefs[backup.SnapshotId] = count + 1;
            else
                snapshotRefs[backup.SnapshotId] = 1;
        }

        foreach (var backup in toRemove)
        {
            try
            {
                var baseRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath)
                    ? backup.DestinationPath
                    : backupRoot;
                var relativePath = string.IsNullOrWhiteSpace(backup.Path)
                    ? string.Empty
                    : backup.Path
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar);
                var fullPath = string.IsNullOrWhiteSpace(baseRoot)
                    ? string.Empty
                    : Path.Combine(baseRoot, relativePath);

                if (!string.IsNullOrWhiteSpace(fullPath) && Directory.Exists(fullPath))
                {
                    Console.WriteLine($"[BackupService] Retention deleting old backup folder '{fullPath}' (backupId={backup.Id}).");
                    Directory.Delete(fullPath, recursive: true);
                }
                else
                {
                    Console.WriteLine($"[BackupService] Retention could not find backup folder '{fullPath}' on disk (backupId={backup.Id}), continuing with DB cleanup.");
                }
            }
            catch (Exception ex)
            {
                // Log and continue; retention should still drop the DB row even if disk cleanup fails.
                Console.WriteLine($"[BackupService] Failed to delete old backup (backupId={backup.Id}): {ex}");
            }
            finally
            {
                BackupRetentionDeleted?.Invoke(backup);
                _repo.DeleteBackupById(backup.Id);
                if (projectName != null &&
                    snapshotRefs.TryGetValue(backup.SnapshotId, out var remaining) &&
                    remaining <= 1)
                {
                    _repo.DeleteSnapshotsById(projectName, new[] { backup.SnapshotId });
                    snapshotRefs.Remove(backup.SnapshotId);
                }
                else if (snapshotRefs.TryGetValue(backup.SnapshotId, out var count) && count > 1)
                {
                    snapshotRefs[backup.SnapshotId] = count - 1;
                }
            }
        }
    }

    private sealed class RunnerProgressState
    {
        private readonly object _lock = new();
        private DateTime _lastUpdateUtc = DateTime.MinValue;

        public double LastPercent { get; private set; }
        public string? LastFile { get; private set; }
        public string? LastEtaText { get; private set; }

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

            var fullPath = Path.GetFullPath(path);

            if (OperatingSystem.IsWindows())
            {
                if (!GetDiskFreeSpaceEx(
                        fullPath,
                        out var freeBytesAvailable,
                        out var totalNumberOfBytes,
                        out _))
                {
                    return null;
                }

                return ((long)totalNumberOfBytes, (long)freeBytesAvailable);
            }

            if (OperatingSystem.IsMacOS() && IsMacManagedMountPath(fullPath) && !IsNetworkMountPath(fullPath))
            {
                Console.WriteLine($"[BackupService] Skipping free-space check for '{fullPath}': network mount not detected.");
                return null;
            }

            var unixSpace = TryGetUnixDiskSpace(fullPath);
            if (unixSpace is not null)
                return unixSpace.Value;

            // Non-Windows fallback: DriveInfo can handle full paths and mount points.
            var driveInfo = new DriveInfo(fullPath);
            if (!driveInfo.IsReady)
                return null;

            return (driveInfo.TotalSize, driveInfo.AvailableFreeSpace);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to read disk space for '{path}': {ex.Message}");
            return null;
        }
    }

    private static (long totalBytes, long freeBytes)? TryGetUnixDiskSpace(string path)
    {
        try
        {
            var stats = new Statvfs();
            if (statvfs(path, ref stats) != 0)
                return null;

            var blockSize = stats.f_frsize != 0 ? stats.f_frsize : stats.f_bsize;
            if (blockSize == 0)
                return null;

            var total = (long)stats.f_blocks * (long)blockSize;
            var free = (long)stats.f_bavail * (long)blockSize;
            if (total <= 0)
                return null;

            return (total, free);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMacManagedMountPath(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var mountRoot = Path.Combine(home, "Library", "Application Support", "VaultSync", "mounts");
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
            var output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                var rest = line[(onIndex + 4)..];
                var mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
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
            var output = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
                return false;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!line.Contains(" nfs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                if (onIndex <= 0)
                    continue;

                var rest = line[(onIndex + 4)..];
                var mountedAt = rest.Split(" (", StringSplitOptions.None)[0].Trim();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Statvfs
    {
        public ulong f_bsize;
        public ulong f_frsize;
        public ulong f_blocks;
        public ulong f_bfree;
        public ulong f_bavail;
        public ulong f_files;
        public ulong f_ffree;
        public ulong f_favail;
        public ulong f_fsid;
        public ulong f_flag;
        public ulong f_namemax;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statvfs(string path, ref Statvfs buf);

    private static bool IsOnPath(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{tool}.exe" : tool);
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
            var baseDir = AppContext.BaseDirectory;
            if (OperatingSystem.IsWindows())
            {
                var direct = Path.Combine(baseDir, "tools", "rsync", "rsync.exe");
                if (File.Exists(direct))
                    return direct;

                var bin = Path.Combine(baseDir, "tools", "rsync", "bin", "rsync.exe");
                return File.Exists(bin) ? bin : null;
            }

            if (OperatingSystem.IsMacOS())
            {
                var candidates = new List<string>();
                var arch = RuntimeInformation.OSArchitecture;
                if (arch == Architecture.Arm64)
                {
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "arm64", "bin", "rsync"));
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "arm64", "rsync"));
                }
                else if (arch == Architecture.X64)
                {
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "x64", "bin", "rsync"));
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "x64", "rsync"));
                }
                else
                {
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "arm64", "bin", "rsync"));
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "x64", "bin", "rsync"));
                }

                candidates.Add(Path.Combine(baseDir, "tools", "rsync", "rsync"));
                candidates.Add(Path.Combine(baseDir, "tools", "rsync", "bin", "rsync"));

                foreach (var candidate in candidates)
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
