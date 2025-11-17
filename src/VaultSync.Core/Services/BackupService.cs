using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories; // adjust namespace if needed
using VaultSync.Core.Services;

namespace VaultSync.Core.Services;

public sealed class BackupService
{
    private readonly SqliteRepository _repo;

    public BackupService(SqliteRepository repo)
    {
        _repo = repo;
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
    /// <returns>The ID of the created backup row in the database.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the project has no snapshots yet, or backupRoot is not configured.
    /// </exception>
    public async Task<int> RunBackupAsync(
        Project project,
        string backupRoot,
        bool isAuto,
        Action<double, string, string>? progressCallback = null,
        CancellationToken ct = default,
        bool useArchiveMode = false)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");

        if (string.IsNullOrWhiteSpace(backupRoot))
            throw new InvalidOperationException("Backup root is empty. Configure a backup location in Settings.");

        // Make sure snapshot exists – we tie backups to snapshots for history.
        var latestSnapshot = _repo.GetLatestSnapshotForProject(project.Id);
        if (latestSnapshot is null)
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}' has no snapshots yet. Create a snapshot before running a backup.");
        }

        ct.ThrowIfCancellationRequested();

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

        // Timestamped folder name: 2025-11-16_15-47-30
        var timestamp = DateTime.UtcNow;
        var folderName = timestamp.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupFolder = Path.Combine(projectBackupRoot, folderName);
        Directory.CreateDirectory(backupFolder);

        // Compute total bytes up-front using the same filter logic as snapshots.
        // This is much cheaper than copying and gives us a consistent number even
        // when the actual copy is offloaded to rsync/robocopy.
        long totalBytes = ComputeBackupSize(project.RootPath, project.Preset, ct);

        try
        {
            ct.ThrowIfCancellationRequested();

            if (useArchiveMode)
            {
                progressCallback?.Invoke(0, string.Empty, "Preparing archive backup...");

                await RunArchiveBackupAsync(project, backupFolder, totalBytes, progressCallback, ct);

                progressCallback?.Invoke(100, string.Empty, "Backup completed (archive).");
            }
            else
            {
                progressCallback?.Invoke(0, string.Empty, "Running backup (rsync/robocopy)...");

                await RunNativeBackupAsync(project, backupFolder, totalBytes, progressCallback, ct);

                progressCallback?.Invoke(100, string.Empty, "Backup completed.");
            }
        }
        catch (Exception ex)
        {
            // If the native tool is not available or fails unexpectedly, fall back
            // to the managed File.Copy-based implementation so the backup still
            // succeeds (albeit more slowly).
            Console.WriteLine($"[BackupService] Native backup failed, falling back to managed copy: {ex}");

            totalBytes = 0; // recompute while copying
            CopyDirectoryRecursive(project.RootPath, backupFolder, project.Preset, ref totalBytes, progressCallback, ct);

            progressCallback?.Invoke(100, string.Empty, "Backup completed (fallback).");
        }

        // Store relative path so if backupRoot moves, paths are still valid.
        var relativePath = Path.GetRelativePath(backupRoot, backupFolder);
        var backupType   = isAuto ? "auto" : "manual";

        // Persist metadata in the backups table
        var backupId = _repo.CreateBackup(
            projectId:    project.Id,
            snapshotId:   latestSnapshot.Id,
            type:         backupType,
            totalBytes:   totalBytes,
            relativePath: relativePath);

        return backupId;
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
            var startTime = DateTime.UtcNow;

            callbackForRunner = (percent, currentFile, _) =>
            {
                var now     = DateTime.UtcNow;
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
                throw new InvalidOperationException($"robocopy backup failed with exit code {exitCode}.");
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

        // Build filter from preset + local ignore rules.
        var filter = FilterService.FromPresetAndLocal(sourceDir, project.Preset);

        // Collect all files that will be included in the archive.
        var allFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(path => !filter.ShouldExclude(sourceDir, path))
            .ToArray();

        if (allFiles.Length == 0)
        {
            // Nothing to back up; still create an empty archive for bookkeeping.
            Directory.CreateDirectory(destDir);
            var emptyArchivePath = Path.Combine(destDir, "data.zip");
            using (var fs = new FileStream(emptyArchivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
            }

            progressCallback?.Invoke(100, string.Empty, "Nothing to back up (archive is empty).");
            return;
        }

        Directory.CreateDirectory(destDir);
        var archivePath = Path.Combine(destDir, "data.zip");

        long processedBytes = 0;
        var  startTime      = DateTime.UtcNow;

        // Use a reasonably large buffer for efficient streaming.
        var buffer = new byte[128 * 1024];

        using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(sourceDir, filePath);
                var entry    = zip.CreateEntry(relative, CompressionLevel.Optimal);

                using (var entryStream = entry.Open())
                using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await entryStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        processedBytes += read;

                        if (progressCallback is not null)
                        {
                            double percent;
                            if (totalBytes > 0)
                            {
                                percent = processedBytes * 100d / totalBytes;
                                if (percent > 100d) percent = 100d;
                            }
                            else
                            {
                                percent = 0d;
                            }

                            var elapsed        = DateTime.UtcNow - startTime;
                            var elapsedSeconds = Math.Max(0.1, elapsed.TotalSeconds);
                            var speedBytesSec  = processedBytes / elapsedSeconds;
                            var speedMbSec     = speedBytesSec / (1024 * 1024);

                            string etaText;
                            if (percent > 0 && percent < 100)
                            {
                                var remainingFraction = (100d - percent) / percent;
                                var remainingSeconds  = elapsedSeconds * remainingFraction;
                                var eta               = TimeSpan.FromSeconds(remainingSeconds);
                                etaText               = $"{speedMbSec:0.0} MB/s · ETA {eta:mm\\:ss}";
                            }
                            else if (percent >= 100)
                            {
                                etaText = $"{speedMbSec:0.0} MB/s · Completed";
                            }
                            else
                            {
                                etaText = string.Empty;
                            }

                            progressCallback(percent, relative, etaText);
                        }
                    }
                }
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

        foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if (filter.ShouldExclude(sourceDir, filePath))
                continue;

            var fi = new FileInfo(filePath);
            total += fi.Length;
        }

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

            var fileInfo  = new FileInfo(filePath);
            var relative  = Path.GetRelativePath(sourceDir, filePath);
            var targetPath = Path.Combine(destDir, relative);
            var targetDir  = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            fileInfo.CopyTo(targetPath, overwrite: true);
            totalBytes += fileInfo.Length;
            processedFiles++;

            if (progressCallback is not null)
            {
                double percent = totalFiles == 0
                    ? 100d
                    : processedFiles * 100d / totalFiles;

                var elapsed = DateTime.UtcNow - startTime;
                string etaText = string.Empty;

                if (processedFiles > 0 && percent < 100d)
                {
                    var avgPerFileSeconds  = elapsed.TotalSeconds / processedFiles;
                    var remainingFiles     = totalFiles - processedFiles;
                    var remainingSeconds   = avgPerFileSeconds * remainingFiles;
                    var eta                = TimeSpan.FromSeconds(remainingSeconds);
                    etaText                = $"ETA {eta:mm\\:ss}";
                }

                progressCallback(percent, relative, etaText);
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
}