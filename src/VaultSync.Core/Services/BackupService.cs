using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories; // adjust namespace if needed
using VaultSync.Core.Services;

namespace VaultSync.Core.Services;

public sealed class BackupService
{
    private readonly Dictionary<int, CancellationTokenSource> _cancelMap = new();
    private readonly SqliteRepository _repo;

    public sealed record BackupRunResult(int BackupId, bool SkippedForNoChanges, bool Cancelled);

    public BackupService(SqliteRepository repo)
    {
        _repo = repo;
    }

    public void CancelBackup(int projectId)
    {
        if (_cancelMap.TryGetValue(projectId, out var cts))
        {
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
        bool skipIfNoChanges = false)
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

        linkedToken.ThrowIfCancellationRequested();

        Console.WriteLine($"[BackupService] RunBackupAsync entered for project '{project.Name}' (Id={project.Id}), backupRoot='{backupRoot}', isAuto={isAuto}, useArchiveMode={useArchiveMode}");
        progressCallback?.Invoke(0, string.Empty, "Preparing backup...");

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
        var backupRootUsed   = backupRoot;
        var backupFolderUsed = backupFolder;

        // Compute total bytes off the UI thread, using the same filter logic as snapshots.
        long totalBytes;
        try
        {
            totalBytes = await Task.Run(
                () => ComputeBackupSize(project.RootPath, project.Preset, linkedToken),
                linkedToken);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.WriteLine($"[BackupService] Failed to compute backup size for '{project.Name}' ({project.RootPath}': {ex.Message}. Proceeding with totalBytes=0.");
            totalBytes = 0;
        }

        Console.WriteLine($"[BackupService] Starting backup for '{project.Name}' ({project.RootPath}), totalBytes={totalBytes}.");

        int snapshotId;
        if (reuseSnapshotId.HasValue)
        {
            snapshotId = reuseSnapshotId.Value;
            progressCallback?.Invoke(0, string.Empty, "Reusing existing snapshot...");
        }
        else
        {
            // Always create a fresh snapshot before backing up so history stays aligned.
            progressCallback?.Invoke(0, string.Empty, "Creating snapshot...");
            var snapshotService = new SnapshotService(_repo, new HashService());
            snapshotId = await snapshotService.CreateSnapshotAsync(project, fullSnapshotHash, maxSnapshotsToKeep, linkedToken);

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

        try
        {
            linkedToken.ThrowIfCancellationRequested();

            if (useArchiveMode)
            {
                progressCallback?.Invoke(0, string.Empty, "Preparing archive backup...");

                await RunArchiveBackupAsync(project, backupFolder, totalBytes, progressCallback, linkedToken);
            }
            else
            {
                progressCallback?.Invoke(0, string.Empty, "Running backup (rsync/robocopy)...");

                await RunNativeBackupAsync(project, backupFolder, totalBytes, progressCallback, linkedToken);
            }
        }
        catch (Exception ex)
        {
            if (linkedToken.IsCancellationRequested)
            {
                Console.WriteLine($"[BackupService] Backup cancelled for '{project.Name}'. Cleaning up.");
                DeletePartialBackup(backupFolder);
                return new BackupRunResult(0, false, true);
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
                    CopyDirectoryRecursive(project.RootPath, backupFolder, project.Preset, ref bytes, progressCallback, linkedToken);
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
                    backupRootUsed   = preferredFinalBackupRoot;
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
        var backupType   = isAuto ? "auto" : "manual";

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
                projectId:       project.Id,
                snapshotId:      snapshotId,
                type:            backupType,
                totalBytes:      totalBytes,
                relativePath:    relativePath,
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
        Action<double, string, string>? progressCallback,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Normalise destination trailing separator for the runners.
        if (!destDir.EndsWith(Path.DirectorySeparatorChar))
            destDir += Path.DirectorySeparatorChar;

        // Wrap the runner's raw percentage/file progress to compute ETA and speed
        // based on the totalBytes we computed up front.
        Action<double, string, string>? callbackForRunner;

        if (progressCallback is null || totalBytes <= 0)
        {
            // Nothing to decorate; just pass through whatever the runner reports.
            callbackForRunner = progressCallback;
        }
        else
        {
            var startTime     = DateTime.UtcNow;
            var lastUiUpdate  = startTime;
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
                    var doneBytes      = totalBytes * (percent / 100.0);
                    var speedBytesSec  = doneBytes / elapsedSeconds;
                    var speedMbSec     = speedBytesSec / (1024 * 1024);

                    var remainingFraction = (100.0 - percent) / percent;
                    var remainingSeconds  = elapsedSeconds * remainingFraction;
                    var eta               = TimeSpan.FromSeconds(remainingSeconds);

                    etaText = $"{speedMbSec:0.0} MB/s · ETA {eta:mm\\:ss}";
                }
                else if (percent >= 100 && elapsed.TotalSeconds > 0)
                {
                    var elapsedSeconds = elapsed.TotalSeconds;
                    var speedBytesSec  = totalBytes / elapsedSeconds;
                    var speedMbSec     = speedBytesSec / (1024 * 1024);

                    etaText = $"{speedMbSec:0.0} MB/s · Completed";
                }

                progressCallback(percent, currentFile, etaText);
            };
        }

        int exitCode;

        if (OperatingSystem.IsWindows())
        {
            // robocopy-based backup (multi-threaded, robust on Windows)
            var runner = new RobocopyRunner();
            exitCode   = await runner.SyncAsync(
                project,
                destDir,
                dryRun: false,
                callbackForRunner,
                ct);

            if (exitCode != 0)
                throw new InvalidOperationException($"robocopy backup failed with exit code {exitCode}. See RobocopyRunner logs above for stdout/stderr.");
        }
        else
        {
            // rsync-based backup (fast, incremental on macOS/Linux)
            var runner = new RsyncRunner();
            exitCode   = await runner.SyncAsync(
                project,
                destDir,
                dryRun: false,
                callbackForRunner,
                ct);

            if (exitCode != 0)
                throw new InvalidOperationException($"rsync backup failed with exit code {exitCode}.");
        }
    }

    /// <summary>
    /// Creates a compressed zip archive of the filtered project contents into the given
    /// destination directory. The archive contains only files that pass the same filter
    /// rules used by snapshots.
    /// </summary>
    private static async Task RunArchiveBackupAsync(
        Project project,
        string destDir,
        long totalBytes,
        Action<double, string, string>? progressCallback,
        CancellationToken ct)
    {
        var sourceDir = project.RootPath;
        var srcInfo   = new DirectoryInfo(sourceDir);
        if (!srcInfo.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");

        Console.WriteLine($"[BackupService] RunArchiveBackupAsync (LOCAL ZIP MODE, 2-PHASE) started for '{project.Name}', destDir='{destDir}', totalBytes={totalBytes}.");

        // 1. Build filter identical to snapshots.
        var filter = FilterService.FromPresetAndLocal(sourceDir, project.Preset);

        // 2. Gather all files that will be archived.
        var allFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(path => !filter.ShouldExclude(sourceDir, path))
            .ToArray();

        // 3. Prepare destination and local temp folder.
        Directory.CreateDirectory(destDir);
        var finalArchivePath = Path.Combine(destDir, "data.zip");

        var localTempRoot  = Path.Combine(Path.GetTempPath(), "vaultsync_archive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localTempRoot);
        var localArchive   = Path.Combine(localTempRoot, "data.zip");

        try
        {
            // 4. If nothing to back up, create a valid empty ZIP and copy it.
            if (allFiles.Length == 0)
            {
                using (var fs = new FileStream(localArchive, FileMode.Create, FileAccess.Write, FileShare.None))
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
            var  startTime      = DateTime.UtcNow;
            var  lastUiUpdate   = startTime;
            var  minUiInterval  = TimeSpan.FromMilliseconds(100);

            using (var fs = new FileStream(localArchive, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var filePath in allFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(sourceDir, filePath);
                    var entry    = zip.CreateEntry(relative, CompressionLevel.Fastest);

                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            await input.CopyToAsync(entryStream, 128 * 1024, ct);
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
                        // Map compression progress into 0-90% of overall progress.
                        double compressPercent = (totalBytes > 0)
                            ? Math.Min(100d, (processedBytes * 100d / totalBytes))
                            : 0d;

                        double overallPercent = compressPercent * 0.9; // 0-90%

                        var now = DateTime.UtcNow;
                        if (overallPercent < 90d && (now - lastUiUpdate) < minUiInterval)
                            continue;

                        lastUiUpdate = now;

                        var elapsed        = now - startTime;
                        var elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                        var speedBytesSec  = processedBytes / elapsedSeconds;
                        var speedMbSec     = speedBytesSec / (1024 * 1024);

                        string etaText;
                        if (overallPercent > 0 && overallPercent < 90)
                        {
                            var remainingFraction = (90d - overallPercent) / overallPercent;
                            var remainingSeconds  = elapsedSeconds * remainingFraction;
                            var eta               = TimeSpan.FromSeconds(remainingSeconds);
                            etaText               = $"{speedMbSec:0.0} MB/s · Compressing · ETA {eta:mm\\:ss}";
                        }
                        else if (overallPercent >= 90)
                        {
                            etaText = $"{speedMbSec:0.0} MB/s · Compressing";
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

            var zipInfo   = new FileInfo(localArchive);
            var zipSize   = zipInfo.Length;
            long uploaded = 0;

            var uploadStart = DateTime.UtcNow;

            const int bufferSize = 128 * 1024;
            var       buffer     = new byte[bufferSize];

            using (var src = new FileStream(localArchive, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(finalArchivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    uploaded += read;

                    if (progressCallback is not null && zipSize > 0)
                    {
                        var uploadPercent   = Math.Min(100d, (uploaded * 100d / zipSize));
                        var overallPercent  = 90d + uploadPercent * 0.1; // map 0-100% upload into 90-100%
                        if (overallPercent > 100d) overallPercent = 100d;

                        var now            = DateTime.UtcNow;
                        var elapsed        = now - uploadStart;
                        var elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                        var speedBytesSec  = uploaded / elapsedSeconds;
                        var speedMbSec     = speedBytesSec / (1024 * 1024);

                        string etaText;
                        if (uploadPercent > 0 && uploadPercent < 100)
                        {
                            var remainingFraction = (100d - uploadPercent) / uploadPercent;
                            var remainingSeconds  = elapsedSeconds * remainingFraction;
                            var eta               = TimeSpan.FromSeconds(remainingSeconds);
                            etaText               = $"{speedMbSec:0.0} MB/s · Uploading archive · ETA {eta:mm\\:ss}";
                        }
                        else
                        {
                            etaText = $"{speedMbSec:0.0} MB/s · Uploading archive · Completed";
                        }

                        progressCallback(overallPercent, Path.GetFileName(finalArchivePath), etaText);
                    }
                }
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
            foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                if (filter.ShouldExclude(sourceDir, filePath))
                    continue;

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

    private static void CopyDirectoryRecursive(
        string sourceDir,
        string destDir,
        string preset,
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
        var allFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(path => !filter.ShouldExclude(sourceDir, path))
            .ToArray();
        var totalFiles     = allFiles.Length;
        var processedFiles = 0;
        var startTime      = DateTime.UtcNow;

        foreach (var filePath in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            var fileInfo   = new FileInfo(filePath);
            var relative   = Path.GetRelativePath(sourceDir, filePath);
            var targetPath = Path.Combine(destDir, relative);
            var targetDir  = Path.GetDirectoryName(targetPath);
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
                                var currentFilePortion    = (double)copiedForThisFile / Math.Max(1L, fileInfo.Length) * (100d / totalFiles);
                                percent = filesCompletedPortion + currentFilePortion;
                                if (percent > 100d) percent = 100d;
                            }

                            var elapsed        = DateTime.UtcNow - startTime;
                            var elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                            string etaText     = string.Empty;

                            if (percent > 0d && percent < 100d)
                            {
                                var remainingFraction = (100d - percent) / percent;
                                var remainingSeconds  = elapsedSeconds * remainingFraction;
                                var eta               = TimeSpan.FromSeconds(remainingSeconds);
                                etaText               = $"ETA {eta:mm\\:ss}";
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

        foreach (var backup in toRemove)
        {
            try
            {
                var fullPath = Path.Combine(backupRoot, backup.Path);

                if (Directory.Exists(fullPath))
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
                _repo.DeleteBackupById(backup.Id);
                MaybeDeleteSnapshotIfOrphan(projectId, backup.SnapshotId);
            }
        }
    }

    private void MaybeDeleteSnapshotIfOrphan(int projectId, int snapshotId)
    {
        try
        {
            // If any other backup still references this snapshot, keep it.
            var remaining = _repo.GetBackupsForProject(projectId)
                .Any(b => b.SnapshotId == snapshotId);
            if (remaining)
                return;

            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
            if (project is null)
                return;

            _repo.DeleteSnapshotsById(project.Name, new[] { snapshotId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BackupService] Failed to delete orphan snapshot (projectId={projectId}, snapshotId={snapshotId}): {ex.Message}");
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

            // Non-Windows: DriveInfo can handle full paths and mount points.
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
