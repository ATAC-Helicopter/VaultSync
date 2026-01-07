using System.Collections.Concurrent;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public class SnapshotService
{
    private readonly SqliteRepository _repo;
    private readonly HashService _hash;

    public SnapshotService(SqliteRepository repo, HashService hash)
    {
        _repo = repo;
        _hash = hash;
    }

    public Task<int> CreateSnapshotAsync(Project project, bool fullHash, int? maxSnapshotsToKeep = null, CancellationToken ct = default)
        => CreateSnapshotAsync(project, fullHash, hashNow: true, maxSnapshotsToKeep, ct, null);

    public Task<int> CreateSnapshotAsync(Project project, bool fullHash, bool hashNow, int? maxSnapshotsToKeep = null, CancellationToken ct = default)
        => CreateSnapshotAsync(project, fullHash, hashNow, maxSnapshotsToKeep, ct, null);

    public async Task<int> CreateSnapshotAsync(
        Project project,
        bool fullHash,
        bool hashNow,
        int? maxSnapshotsToKeep,
        CancellationToken ct,
        Action<double, string, string>? progressCallback)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");

        Console.WriteLine($"[SnapshotService] Starting snapshot for project '{project.Name}'");
        Console.WriteLine($"[SnapshotService]   RootPath = '{project.RootPath}'");
        Console.WriteLine($"[SnapshotService]   Preset   = '{project.Preset}'");

        // IMPORTANT:
        // Run the entire snapshot pipeline on a background thread so that
        // callers from a UI (Avalonia) do not block the UI thread while we:
        //  - enumerate all files
        //  - build FileInfo metadata
        //  - diff against previous snapshot
        //  - hash files in parallel
        //
        // CLI callers won't care that this uses the thread pool.
        return await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            // Load previous snapshot (if any) to enable incremental behavior
            var prev = _repo.GetLatestSnapshot(project.Id);
            var prevFiles = prev != null
                ? _repo.GetFilesForSnapshot(prev.Id).ToDictionary(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

            ct.ThrowIfCancellationRequested();

            // Build filter from preset + local overrides
            var filter = FilterService.FromPresetAndLocal(project.RootPath, project.Preset);

            // Enumerate all current files first (can be expensive on large trees)
            var allPaths = Directory
                .EnumerateFiles(project.RootPath, "*", SearchOption.AllDirectories)
                .ToList();

            Console.WriteLine($"[SnapshotService] Enumerated {allPaths.Count} files under root before filtering.");

            ct.ThrowIfCancellationRequested();

            // Apply filter
            var currentPaths = allPaths
                .Where(p => !filter.ShouldExclude(project.RootPath, p))
                .ToList();

            Console.WriteLine($"[SnapshotService] {currentPaths.Count} files remain after filtering.");

            ct.ThrowIfCancellationRequested();

            // Map rel path -> file system info
            var currMeta = currentPaths.Select(p => new
            {
                Full = p,
                Rel  = Path.GetRelativePath(project.RootPath, p).Replace('\\', '/'),
                Info = new FileInfo(p)
            }).ToList();

            // Index by relative path for faster lookups
            var currMetaByRel = currMeta.ToDictionary(m => m.Rel, StringComparer.OrdinalIgnoreCase);

            // Determine changes
            var added     = new List<string>();
            var modified  = new List<string>();
            var unchanged = new List<string>();

            foreach (var kvp in currMetaByRel)
            {
                ct.ThrowIfCancellationRequested();

                var rel = kvp.Key;
                var f   = kvp.Value;

                if (!prevFiles.TryGetValue(rel, out var old))
                {
                    added.Add(rel);
                    continue;
                }

                // consider unchanged if size and mtime are identical (UTC)
                var sameSize = old.Size == f.Info.Length;
                var sameTime = Math.Abs((old.MTimeUtc - f.Info.LastWriteTimeUtc).TotalSeconds) < 1.0; // tolerate FS granularity

                if (!sameSize || !sameTime)
                    modified.Add(rel);
                else
                    unchanged.Add(rel);
            }

            var deleted = prevFiles.Keys
                .Except(currMetaByRel.Keys, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!hashNow)
            {
                long snapshotTotalBytes = 0;
                var snapshotEntries = new List<FileEntry>(currMetaByRel.Count);

                foreach (var meta in currMetaByRel.Values)
                {
                    ct.ThrowIfCancellationRequested();

                    var hash = string.Empty;
                    if (!fullHash &&
                        prevFiles.TryGetValue(meta.Rel, out var prevEntry) &&
                        !string.IsNullOrWhiteSpace(prevEntry.HashSha256))
                    {
                        hash = prevEntry.HashSha256;
                    }

                    snapshotEntries.Add(new FileEntry(meta.Rel, meta.Info.Length, meta.Info.LastWriteTimeUtc, hash));
                    snapshotTotalBytes += meta.Info.Length;
                }

                var snapshotId = _repo.CreateSnapshot(project.Id, snapshotEntries.Count, snapshotTotalBytes);
                _repo.InsertFiles(snapshotId, snapshotEntries.OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase));

                if (maxSnapshotsToKeep.HasValue && maxSnapshotsToKeep.Value > 0)
                {
                    try
                    {
                        ApplySnapshotRetention(project, Math.Max(1, maxSnapshotsToKeep.Value));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SnapshotService] Retention step failed for project '{project.Name}': {ex}");
                    }
                }

                LastOutcome = new SnapshotOutcome
                (
                    Added:      added.Count,
                    Modified:   modified.Count,
                    Deleted:    deleted.Count,
                    Unchanged:  unchanged.Count,
                    TotalFiles: snapshotEntries.Count,
                    TotalBytes: snapshotTotalBytes
                );

                Console.WriteLine($"[SnapshotService] Finished snapshot for '{project.Name}': " +
                                  $"added={added.Count}, modified={modified.Count}, deleted={deleted.Count}, unchanged={unchanged.Count}, totalFiles={snapshotEntries.Count}, totalBytes={snapshotTotalBytes}");

                return snapshotId;
            }

            // Hash strategy
            var entries    = new ConcurrentBag<FileEntry>();
            long totalBytes = 0;

            // helper to add entry
            void AddEntry(string rel, long size, DateTime mtime, string hash)
            {
                entries.Add(new FileEntry(rel, size, mtime, hash));
                Interlocked.Add(ref totalBytes, size);
            }

            // Unchanged: reuse previous hash unless fullHash forces recompute
            if (!fullHash && unchanged.Count > 0)
            {
                foreach (var rel in unchanged)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!currMetaByRel.TryGetValue(rel, out var meta))
                        continue;

                    if (!prevFiles.TryGetValue(rel, out var prevEntry))
                        continue;

                    AddEntry(rel, meta.Info.Length, meta.Info.LastWriteTimeUtc, prevEntry.HashSha256);
                }
            }

            // Added + Modified (and everything if fullHash)
            var toHash = fullHash
                ? currMeta
                : currMeta.Where(m => added.Contains(m.Rel) || modified.Contains(m.Rel)).ToList();

            Console.WriteLine($"[SnapshotService] toHash = {toHash.Count}, fullHash={fullHash}, added={added.Count}, modified={modified.Count}, unchanged={unchanged.Count}, deleted={deleted.Count}");

            var totalToHash = toHash.Count;
            var totalHashBytes = toHash.Sum(m => m.Info.Length);
            var hashedCount = 0;
            long hashedBytes = 0;
            var hashStart = DateTime.UtcNow;
            var lastReport = hashStart;
            var reportInterval = TimeSpan.FromMilliseconds(200);
            var progressLock = new object();

            void ReportHashProgress(string relPath, bool force)
            {
                if (progressCallback is null)
                    return;

                var now = DateTime.UtcNow;
                lock (progressLock)
                {
                    if (!force && (now - lastReport) < reportInterval)
                        return;

                    lastReport = now;
                    var count = hashedCount;
                    var bytes = hashedBytes;
                    var percent = totalToHash > 0 ? count * 100d / totalToHash : 100d;

                    var elapsedSeconds = Math.Max(0.1, (now - hashStart).TotalSeconds);
                    var speedBytesSec = bytes / elapsedSeconds;
                    var speedMbSec = speedBytesSec / (1024d * 1024d);

                    string etaText;
                    if (count >= totalToHash)
                    {
                        etaText = $"Hashing {count}/{totalToHash}";
                    }
                    else if (count > 0 && totalHashBytes > 0 && speedBytesSec > 0)
                    {
                        var remainingBytes = Math.Max(0L, totalHashBytes - bytes);
                        var remainingSeconds = remainingBytes / speedBytesSec;
                        var eta = TimeSpan.FromSeconds(remainingSeconds);
                        etaText = $"Hashing {count}/{totalToHash} - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
                    }
                    else
                    {
                        etaText = $"Hashing {count}/{totalToHash}";
                    }

                    progressCallback(percent, relPath, etaText);
                }
            }

            if (progressCallback is not null && totalToHash == 0)
            {
                progressCallback(0, string.Empty, "Hashing 0/0");
            }

            // Hash in parallel (CPU-heavy, but off the UI thread and cancellable)
            await Parallel.ForEachAsync(
                toHash,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken      = ct
                },
                async (m, token) =>
                {
                    var h = await _hash.Sha256Async(m.Full, token);
                    AddEntry(m.Rel, m.Info.Length, m.Info.LastWriteTimeUtc, h);

                    Interlocked.Add(ref hashedBytes, m.Info.Length);
                    var currentCount = Interlocked.Increment(ref hashedCount);
                    ReportHashProgress(m.Rel, currentCount >= totalToHash);
                });

            ct.ThrowIfCancellationRequested();

            // Create snapshot record
            var snapId = _repo.CreateSnapshot(project.Id, entries.Count, totalBytes);

            // Persist files in a stable order
            _repo.InsertFiles(snapId, entries.OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase));

            // Apply snapshot retention (keep only the most recent N snapshots that have no backups referencing them)
            if (maxSnapshotsToKeep.HasValue && maxSnapshotsToKeep.Value > 0)
            {
                try
                {
                    ApplySnapshotRetention(project, Math.Max(1, maxSnapshotsToKeep.Value));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SnapshotService] Retention step failed for project '{project.Name}': {ex}");
                }
            }

            // Attach summary for CLI
            LastOutcome = new SnapshotOutcome
            (
                Added:      added.Count,
                Modified:   modified.Count,
                Deleted:    deleted.Count,
                Unchanged:  unchanged.Count,
                TotalFiles: entries.Count,
                TotalBytes: totalBytes
            );

            Console.WriteLine($"[SnapshotService] Finished snapshot for '{project.Name}': " +
                              $"added={added.Count}, modified={modified.Count}, deleted={deleted.Count}, unchanged={unchanged.Count}, totalFiles={entries.Count}, totalBytes={totalBytes}");

            return snapId;
        }, ct);
    }

    // simple store for most recent outcome in-process
    public static SnapshotOutcome? LastOutcome { get; private set; }

    public async Task<int> HashMissingFilesAsync(
        Project project,
        int snapshotId,
        CancellationToken ct = default,
        Action<double, string, string>? progressCallback = null)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");

        var files = _repo.GetFilesForSnapshot(snapshotId)
            .Where(f => string.IsNullOrWhiteSpace(f.HashSha256))
            .ToList();

        if (files.Count == 0)
            return 0;

        var totalToHash = files.Count;
        var totalBytes = files.Sum(f => f.Size);
        var hashedCount = 0;
        long hashedBytes = 0;
        var hashStart = DateTime.UtcNow;
        var lastReport = hashStart;
        var reportInterval = TimeSpan.FromMilliseconds(250);
        var progressLock = new object();
        var updates = new ConcurrentBag<(string RelPath, string HashSha256)>();

        void ReportProgress(string relPath, bool force)
        {
            if (progressCallback is null)
                return;

            var now = DateTime.UtcNow;
            lock (progressLock)
            {
                if (!force && (now - lastReport) < reportInterval)
                    return;

                lastReport = now;
                var count = hashedCount;
                var bytes = hashedBytes;
                var percent = totalToHash > 0 ? count * 100d / totalToHash : 100d;

                var elapsedSeconds = Math.Max(0.1, (now - hashStart).TotalSeconds);
                var speedBytesSec = bytes / elapsedSeconds;
                var speedMbSec = speedBytesSec / (1024d * 1024d);

                string etaText;
                if (count >= totalToHash)
                {
                    etaText = $"Hashing {count}/{totalToHash}";
                }
                else if (count > 0 && totalBytes > 0 && speedBytesSec > 0)
                {
                    var remainingBytes = Math.Max(0L, totalBytes - bytes);
                    var remainingSeconds = remainingBytes / speedBytesSec;
                    var eta = TimeSpan.FromSeconds(remainingSeconds);
                    etaText = $"Hashing {count}/{totalToHash} - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
                }
                else
                {
                    etaText = $"Hashing {count}/{totalToHash}";
                }

                progressCallback(percent, relPath, etaText);
            }
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            },
            async (entry, token) =>
            {
                var relPath = entry.RelPath.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.Combine(project.RootPath, relPath);
                try
                {
                    var hash = await _hash.Sha256Async(fullPath, token);
                    updates.Add((entry.RelPath, hash));

                    Interlocked.Add(ref hashedBytes, entry.Size);
                    var currentCount = Interlocked.Increment(ref hashedCount);
                    ReportProgress(entry.RelPath, currentCount >= totalToHash);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Console.WriteLine($"[SnapshotService] Failed to hash '{fullPath}': {ex.Message}");
                }
            });

        ct.ThrowIfCancellationRequested();
        _repo.UpdateFileHashes(snapshotId, updates);
        return updates.Count;
    }

    private void ApplySnapshotRetention(Project project, int maxSnapshotsToKeep)
    {
        // Load all snapshots for this project (newest first)
        var snapshots = _repo
            .GetSnapshotsForProject(project.Name)
            .OrderByDescending(s => s.CreatedUtc)
            .ToList();

        // If snapshots are <= max, nothing to prune
        if (snapshots.Count <= maxSnapshotsToKeep)
            return;

        // Load all backups for this project to find protected snapshot IDs
        var backups = _repo.GetBackupsForProject(project.Id);
        var protectedIds = new HashSet<int>(backups.Select(b => b.SnapshotId));

        // Determine snapshots that are not referenced by any backup
        var freeSnapshots = snapshots.Where(s => !protectedIds.Contains(s.Id)).ToList();

        // If free snapshots <= max, nothing to delete
        if (freeSnapshots.Count <= maxSnapshotsToKeep)
            return;

        // Delete oldest free snapshots beyond the keep limit
        var toDelete = freeSnapshots
            .OrderByDescending(s => s.CreatedUtc)
            .Skip(maxSnapshotsToKeep)
            .Select(s => s.Id)
            .ToList();

        if (toDelete.Count > 0)
        {
            Console.WriteLine($"[SnapshotService] Retention deleting {toDelete.Count} old snapshots for project '{project.Name}'.");
            _repo.DeleteSnapshotsById(project.Name, toDelete);
        }
    }
}

public record SnapshotOutcome(int Added, int Modified, int Deleted, int Unchanged, int TotalFiles, long TotalBytes);
