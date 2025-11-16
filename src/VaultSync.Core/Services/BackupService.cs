using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories; // adjust namespace if needed

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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ID of the created backup row in the database.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the project has no snapshots yet, or backupRoot is not configured.
    /// </exception>
    public Task<int> RunBackupAsync(
        Project project,
        string backupRoot,
        bool isAuto,
        Action<double, string, string>? progressCallback = null,
        CancellationToken ct = default)
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

        // Normalise / create root
        backupRoot = Path.GetFullPath(backupRoot);
        Directory.CreateDirectory(backupRoot);

        // Project-specific backup root: <backupRoot>/project-slug/
        var projectSlug = Slugify(project.Name);
        var projectBackupRoot = Path.Combine(backupRoot, projectSlug);
        Directory.CreateDirectory(projectBackupRoot);

        // Timestamped folder name: 2025-11-16_15-47-30
        var timestamp = DateTime.UtcNow;
        var folderName = timestamp.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupFolder = Path.Combine(projectBackupRoot, folderName);
        Directory.CreateDirectory(backupFolder);

        long totalBytes = 0;

        // Copy entire project folder into backup folder, reporting progress if requested and
        // respecting the project's snapshot/backup preset (vaultsyncignore-style filters).
        CopyDirectoryRecursive(project.RootPath, backupFolder, project.Preset, ref totalBytes, progressCallback, ct);

        // Store relative path so if backupRoot moves, paths are still valid.
        var relativePath = Path.GetRelativePath(backupRoot, backupFolder);

        var backupType = isAuto ? "auto" : "manual";

        // Persist metadata in the backups table
        var backupId = _repo.CreateBackup(
            projectId:  project.Id,
            snapshotId: latestSnapshot.Id,
            type:       backupType,
            totalBytes: totalBytes,
            relativePath: relativePath);

        return Task.FromResult(backupId);
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
        var totalFiles = allFiles.Length;
        var processedFiles = 0;
        var startTime = DateTime.UtcNow;

        foreach (var filePath in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(filePath);
            var relative = Path.GetRelativePath(sourceDir, filePath);
            var targetPath = Path.Combine(destDir, relative);
            var targetDir = Path.GetDirectoryName(targetPath);
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
                    var avgPerFileSeconds = elapsed.TotalSeconds / processedFiles;
                    var remainingFiles = totalFiles - processedFiles;
                    var remainingSeconds = avgPerFileSeconds * remainingFiles;
                    var eta = TimeSpan.FromSeconds(remainingSeconds);
                    etaText = $"ETA {eta:mm\\:ss}";
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