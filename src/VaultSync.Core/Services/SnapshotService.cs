using System.Collections.Concurrent;
using System.Security.Cryptography;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public class SnapshotService
{
    private readonly SqliteRepository _repo;
    private readonly HashService _hash;
    private readonly IVaultLogger _logger;

    private readonly record struct SnapshotFileMetadata(string Full, string Rel, FileEntry Entry);
    public SnapshotOutcome? LastCreatedOutcome { get; private set; }

    public SnapshotService(SqliteRepository repo, HashService hash, IVaultLogger? logger = null)
    {
        _repo = repo;
        _hash = hash;
        _logger = logger ?? RuntimeVaultLogger.Instance;
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
        Action<double, string, string>? progressCallback,
        bool useScanCache = false,
        bool aggressiveScanCache = false)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");

        _logger.Info($"[SnapshotService] Starting snapshot for project '{project.Name}'");
        _logger.Info($"[SnapshotService]   RootPath = '{project.RootPath}'");
        _logger.Info($"[SnapshotService]   Preset   = '{project.Preset}'");

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

            // Load the latest local snapshot as the baseline. Imported snapshots can come
            // from another machine/OS and should not drive local diff math.
            Snapshot? prev = _repo.GetLatestLocalSnapshotForProject(project.Id);
            long previousTotalBytes = prev?.TotalBytes ?? 0L;
            Dictionary<string, FileEntry> prevFiles = prev != null
                ? _repo.GetFilesForSnapshot(prev.Id).ToDictionary(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

            ct.ThrowIfCancellationRequested();

            // Build filter from preset + local overrides
            var filter = FilterService.FromPresetAndLocal(project.RootPath, project.Preset, logger: _logger);

            // Build current file list (with optional scan cache)
            string filterHash = ComputeFilterHash(filter);
            ScanCacheState? cache = useScanCache ? ScanCacheStore.TryLoad(project, filterHash) : null;
            bool forceFullScan = ShouldForceFullScan(cache, useScanCache, aggressiveScanCache);

            var dirMtimeCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            List<FileEntry> currentEntries = BuildCurrentEntries(
                project,
                filter,
                prevFiles.Values,
                cache,
                forceFullScan,
                dirMtimeCache,
                out int skippedDirs,
                ct);

            _logger.Info($"[SnapshotService] Scan cache used={useScanCache && cache is not null}, skippedDirs={skippedDirs}, files={currentEntries.Count}.");

            ct.ThrowIfCancellationRequested();

            // Map rel path -> file system info
            List<SnapshotFileMetadata> currMeta = [.. currentEntries
                .Select(entry => new SnapshotFileMetadata(
                    Path.Combine(project.RootPath, entry.RelPath.Replace('/', Path.DirectorySeparatorChar)),
                    entry.RelPath,
                    entry))];

            // Index by relative path for faster lookups
            Dictionary<string, SnapshotFileMetadata> currMetaByRel = currMeta.ToDictionary(m => m.Rel, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, FileEntry> currentFilesByRel = currMetaByRel.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Entry,
                StringComparer.OrdinalIgnoreCase);

            // Determine changes
            List<string> added     = new List<string>();
            List<string> modified  = new List<string>();
            List<string> unchanged = new List<string>();

            foreach (KeyValuePair<string, SnapshotFileMetadata> kvp in currMetaByRel)
            {
                ct.ThrowIfCancellationRequested();

                string rel = kvp.Key;
                SnapshotFileMetadata f = kvp.Value;

                if (!prevFiles.TryGetValue(rel, out FileEntry? old))
                {
                    added.Add(rel);
                    continue;
                }

                // consider unchanged if size and mtime are identical (UTC)
                bool sameSize = old.Size == f.Entry.Size;
                bool sameTime = Math.Abs((old.MTimeUtc - f.Entry.MTimeUtc).TotalSeconds) < 1.0; // tolerate FS granularity

                if (!sameSize || !sameTime)
                    modified.Add(rel);
                else
                    unchanged.Add(rel);
            }

            var deleted = prevFiles.Keys
                .Except(currMetaByRel.Keys, StringComparer.OrdinalIgnoreCase)
                .ToList();

            UpdateScanCache(project, filterHash, cache, forceFullScan, dirMtimeCache);

            if (!hashNow)
            {
                long snapshotTotalBytes = 0;
                List<FileEntry> snapshotEntries = new List<FileEntry>(currMetaByRel.Count);

                foreach (SnapshotFileMetadata meta in currMetaByRel.Values)
                {
                    ct.ThrowIfCancellationRequested();

                    string hash = string.Empty;
                    if (!fullHash &&
                        prevFiles.TryGetValue(meta.Rel, out FileEntry? prevEntry) &&
                        !string.IsNullOrWhiteSpace(prevEntry.HashSha256))
                    {
                        hash = prevEntry.HashSha256;
                    }

                    snapshotEntries.Add(new FileEntry(meta.Rel, meta.Entry.Size, meta.Entry.MTimeUtc, hash));
                    snapshotTotalBytes += meta.Entry.Size;
                }

                SnapshotDiffSummary diffSummary = BuildSnapshotDiffSummary(
                    added,
                    modified,
                    deleted,
                    currentFilesByRel,
                    prevFiles,
                    snapshotTotalBytes,
                    previousTotalBytes);

                int snapshotId = _repo.CreateSnapshot(
                    project.Id,
                    snapshotEntries.Count,
                    snapshotTotalBytes,
                    diffSummary);
                _repo.InsertFiles(snapshotId, snapshotEntries.OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase));

                if (maxSnapshotsToKeep.HasValue && maxSnapshotsToKeep.Value > 0)
                {
                    try
                    {
                        ApplySnapshotRetention(project, Math.Max(1, maxSnapshotsToKeep.Value));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"[SnapshotService] Retention step failed for project '{project.Name}': {ex}");
                    }
                }

                LastCreatedOutcome = new SnapshotOutcome
                (
                    Added:      added.Count,
                    Modified:   modified.Count,
                    Deleted:    deleted.Count,
                    Unchanged:  unchanged.Count,
                    TotalFiles: snapshotEntries.Count,
                    TotalBytes: snapshotTotalBytes
                );
                LastOutcome = LastCreatedOutcome;

                _logger.Info($"[SnapshotService] Finished snapshot for '{project.Name}': " +
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
                foreach (string rel in unchanged)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!currMetaByRel.TryGetValue(rel, out SnapshotFileMetadata meta))
                        continue;

                    if (!prevFiles.TryGetValue(rel, out FileEntry? prevEntry))
                        continue;

                    AddEntry(rel, meta.Entry.Size, meta.Entry.MTimeUtc, prevEntry.HashSha256);
                }
            }

            // Added + Modified (and everything if fullHash)
            HashSet<string> changedRel = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
            changedRel.UnionWith(modified);
            List<SnapshotFileMetadata> toHash = fullHash
                ? currMeta
                : [.. currMeta.Where(m => changedRel.Contains(m.Rel))];

            _logger.Info($"[SnapshotService] toHash = {toHash.Count}, fullHash={fullHash}, added={added.Count}, modified={modified.Count}, unchanged={unchanged.Count}, deleted={deleted.Count}");

            int totalToHash = toHash.Count;
            long totalHashBytes = toHash.Sum(m => m.Entry.Size);
            int hashedCount = 0;
            long hashedBytes = 0;
            DateTime hashStart = DateTime.UtcNow;
            DateTime lastReport = hashStart;
            var reportInterval = TimeSpan.FromMilliseconds(200);
            object progressLock = new object();

            void ReportHashProgress(string relPath, bool force)
            {
                if (progressCallback is null)
                    return;

                DateTime now = DateTime.UtcNow;
                lock (progressLock)
                {
                    if (!force && (now - lastReport) < reportInterval)
                        return;

                    lastReport = now;
                    int count = hashedCount;
                    long bytes = hashedBytes;
                    double percent = totalToHash > 0 ? count * 100d / totalToHash : 100d;

                    double elapsedSeconds = Math.Max(0.1, (now - hashStart).TotalSeconds);
                    double speedBytesSec = bytes / elapsedSeconds;
                    double speedMbSec = speedBytesSec / (1024d * 1024d);

                    string etaText;
                    if (count >= totalToHash)
                    {
                        etaText = $"Hashing {count}/{totalToHash}";
                    }
                    else if (count > 0 && totalHashBytes > 0 && speedBytesSec > 0)
                    {
                        long remainingBytes = Math.Max(0L, totalHashBytes - bytes);
                        double remainingSeconds = remainingBytes / speedBytesSec;
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
                    string h = await _hash.Sha256Async(m.Full, token);
                    AddEntry(m.Rel, m.Entry.Size, m.Entry.MTimeUtc, h);

                    Interlocked.Add(ref hashedBytes, m.Entry.Size);
                    int currentCount = Interlocked.Increment(ref hashedCount);
                    ReportHashProgress(m.Rel, currentCount >= totalToHash);
                });

            ct.ThrowIfCancellationRequested();

            SnapshotDiffSummary diffSummaryWithHashes = BuildSnapshotDiffSummary(
                added,
                modified,
                deleted,
                currentFilesByRel,
                prevFiles,
                totalBytes,
                previousTotalBytes);

            // Create snapshot record
            int snapId = _repo.CreateSnapshot(
                project.Id,
                entries.Count,
                totalBytes,
                diffSummaryWithHashes);

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
                    _logger.Error($"[SnapshotService] Retention step failed for project '{project.Name}': {ex}");
                }
            }

            // Attach summary for CLI
            LastCreatedOutcome = new SnapshotOutcome
            (
                Added:      added.Count,
                Modified:   modified.Count,
                Deleted:    deleted.Count,
                Unchanged:  unchanged.Count,
                TotalFiles: entries.Count,
                TotalBytes: totalBytes
            );
            LastOutcome = LastCreatedOutcome;

            _logger.Info($"[SnapshotService] Finished snapshot for '{project.Name}': " +
                         $"added={added.Count}, modified={modified.Count}, deleted={deleted.Count}, unchanged={unchanged.Count}, totalFiles={entries.Count}, totalBytes={totalBytes}");

            return snapId;
        }, ct);
    }

    private static string ComputeFilterHash(FilterService filter)
    {
        string joined = string.Join('\n', filter.RawPatterns ?? Array.Empty<string>());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(joined);
        byte[] hash = SHA256.HashData(bytes);
        return HashService.FormatHex(hash);
    }

    private static List<FileEntry> BuildCurrentEntries(
        Project project,
        FilterService filter,
        IEnumerable<FileEntry> prevEntries,
        ScanCacheState? cache,
        bool forceFullScan,
        Dictionary<string, long> dirMtimeCache,
        out int skippedDirs,
        CancellationToken ct)
    {
        var results = new List<FileEntry>();
        Dictionary<string, List<FileEntry>> prevByDir = BuildPrevByDir(prevEntries);
        string root = project.RootPath;
        int skippedDirsLocal = 0;

        void ScanDir(string fullDir, string relDir)
        {
            ct.ThrowIfCancellationRequested();

            DirectoryInfo? info = null;
            try
            {
                info = new DirectoryInfo(fullDir);
                if (!info.Exists)
                    return;
            }
            catch
            {
                return;
            }

            string dirKey = relDir;
            long mtimeTicks = info.LastWriteTimeUtc.Ticks;
            dirMtimeCache[dirKey] = mtimeTicks;

            if (!forceFullScan &&
                cache is not null &&
                cache.DirectoryMtimeUtcTicks.TryGetValue(dirKey, out long cachedTicks) &&
                cachedTicks == mtimeTicks &&
                prevByDir.TryGetValue(dirKey, out List<FileEntry>? cachedEntries))
            {
                results.AddRange(cachedEntries);
                skippedDirsLocal++;
                return;
            }

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(fullDir);
            }
            catch
            {
                return;
            }

            foreach (string sub in dirs)
            {
                ct.ThrowIfCancellationRequested();
                if (filter.ShouldExclude(root, sub))
                    continue;

                string rel = Path.GetRelativePath(root, sub).Replace('\\', '/');
                ScanDir(sub, rel);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(fullDir);
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (filter.ShouldExclude(root, file))
                    continue;

                try
                {
                    var fi = new FileInfo(file);
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    results.Add(new FileEntry(rel, fi.Length, fi.LastWriteTimeUtc, string.Empty));
                }
                catch
                {
                }
            }
        }

        ScanDir(root, string.Empty);
        skippedDirs = skippedDirsLocal;
        return results;
    }

    private static Dictionary<string, List<FileEntry>> BuildPrevByDir(IEnumerable<FileEntry> entries)
    {
        var map = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (FileEntry entry in entries)
        {
            string rel = entry.RelPath.Replace('\\', '/');
            string dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? string.Empty;
            string current = dir;
            while (true)
            {
                if (!map.TryGetValue(current, out List<FileEntry>? list))
                {
                    list = new List<FileEntry>();
                    map[current] = list;
                }
                list.Add(entry);

                if (string.IsNullOrEmpty(current))
                    break;

                int idx = current.LastIndexOf('/');
                current = idx >= 0 ? current[..idx] : string.Empty;
            }
        }
        return map;
    }

    private static void UpdateScanCache(
        Project project,
        string filterHash,
        ScanCacheState? cache,
        bool forceFullScan,
        Dictionary<string, long> dirMtimeCache)
    {
        if (cache is null)
        {
            cache = new ScanCacheState
            {
                RootPath = project.RootPath,
                FilterHash = filterHash
            };
        }

        cache.DirectoryMtimeUtcTicks = dirMtimeCache;

        if (forceFullScan)
        {
            cache.RunsSinceFullScan = 0;
            cache.LastFullScanUtc = DateTime.UtcNow;
        }
        else
        {
            cache.RunsSinceFullScan = Math.Min(cache.RunsSinceFullScan + 1, 1000);
        }

        ScanCacheStore.Save(project, cache);
    }

    private static bool ShouldForceFullScan(
        ScanCacheState? cache,
        bool useScanCache,
        bool aggressiveScanCache)
    {
        if (!useScanCache || cache is null)
            return true;

        int fullScanInterval = aggressiveScanCache ? 10 : 5;
        TimeSpan fullScanMaxAge = aggressiveScanCache ? TimeSpan.FromDays(2) : TimeSpan.FromDays(7);

        if (cache.RunsSinceFullScan >= fullScanInterval)
            return true;

        if (cache.LastFullScanUtc != DateTime.MinValue &&
            DateTime.UtcNow - cache.LastFullScanUtc > fullScanMaxAge)
            return true;

        return false;
    }

    private static SnapshotDiffSummary BuildSnapshotDiffSummary(
        IReadOnlyCollection<string> added,
        IReadOnlyCollection<string> modified,
        IReadOnlyCollection<string> deleted,
        IReadOnlyDictionary<string, FileEntry> currentByRel,
        IReadOnlyDictionary<string, FileEntry> previousByRel,
        long currentTotalBytes,
        long previousTotalBytes)
    {
        if (added.Count == 0 && modified.Count == 0 && deleted.Count == 0)
            return SnapshotDiffSummary.Empty;

        var pathStats = new Dictionary<string, (int Changes, long ChangedBytes)>(StringComparer.OrdinalIgnoreCase);

        static void AddPathStat(
            IDictionary<string, (int Changes, long ChangedBytes)> stats,
            string bucket,
            long changedBytes)
        {
            if (string.IsNullOrWhiteSpace(bucket))
                return;

            if (stats.TryGetValue(bucket, out (int Changes, long ChangedBytes) current))
            {
                stats[bucket] = (current.Changes + 1, current.ChangedBytes + changedBytes);
                return;
            }

            stats[bucket] = (1, changedBytes);
        }

        foreach (string rel in added)
        {
            if (!currentByRel.TryGetValue(rel, out FileEntry? current))
                continue;

            AddPathStat(pathStats, ToChangedPathBucket(rel), Math.Max(0L, current.Size));
        }

        foreach (string rel in modified)
        {
            if (!currentByRel.TryGetValue(rel, out FileEntry? current))
                continue;

            previousByRel.TryGetValue(rel, out FileEntry? old);
            long oldSize = old?.Size ?? 0L;
            long newSize = current.Size;
            long changedBytes = Math.Max(oldSize, newSize);
            AddPathStat(pathStats, ToChangedPathBucket(rel), Math.Max(0L, changedBytes));
        }

        foreach (string rel in deleted)
        {
            if (!previousByRel.TryGetValue(rel, out FileEntry? old))
                continue;

            AddPathStat(pathStats, ToChangedPathBucket(rel), Math.Max(0L, old.Size));
        }

        SnapshotDiffPathStat[] topPaths = [.. pathStats
            .OrderByDescending(kvp => kvp.Value.Changes)
            .ThenByDescending(kvp => kvp.Value.ChangedBytes)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(kvp => new SnapshotDiffPathStat(kvp.Key, kvp.Value.Changes, kvp.Value.ChangedBytes))];

        return new SnapshotDiffSummary(
            Added: added.Count,
            Modified: modified.Count,
            Deleted: deleted.Count,
            NetSizeBytes: currentTotalBytes - previousTotalBytes,
            TopChangedPaths: topPaths);
    }

    private static string ToChangedPathBucket(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            return "(root)";

        string normalized = relPath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
            return "(root)";

        int slash = normalized.IndexOf('/');
        if (slash < 0)
            return normalized;

        int secondSlash = normalized.IndexOf('/', slash + 1);
        return secondSlash < 0 ? normalized[..slash] : normalized[..secondSlash];
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

        int totalToHash = files.Count;
        long totalBytes = files.Sum(f => f.Size);
        int hashedCount = 0;
        long hashedBytes = 0;
        DateTime hashStart = DateTime.UtcNow;
        DateTime lastReport = hashStart;
        var reportInterval = TimeSpan.FromMilliseconds(250);
        object progressLock = new object();
        var updates = new ConcurrentBag<(string RelPath, string HashSha256)>();

        void ReportProgress(string relPath, bool force)
        {
            if (progressCallback is null)
                return;

            DateTime now = DateTime.UtcNow;
            lock (progressLock)
            {
                if (!force && (now - lastReport) < reportInterval)
                    return;

                lastReport = now;
                int count = hashedCount;
                long bytes = hashedBytes;
                double percent = totalToHash > 0 ? count * 100d / totalToHash : 100d;

                double elapsedSeconds = Math.Max(0.1, (now - hashStart).TotalSeconds);
                double speedBytesSec = bytes / elapsedSeconds;
                double speedMbSec = speedBytesSec / (1024d * 1024d);

                string etaText;
                if (count >= totalToHash)
                {
                    etaText = $"Hashing {count}/{totalToHash}";
                }
                else if (count > 0 && totalBytes > 0 && speedBytesSec > 0)
                {
                    long remainingBytes = Math.Max(0L, totalBytes - bytes);
                    double remainingSeconds = remainingBytes / speedBytesSec;
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
                string relPath = entry.RelPath.Replace('/', Path.DirectorySeparatorChar);
                string fullPath = Path.Combine(project.RootPath, relPath);
                try
                {
                    string hash = await _hash.Sha256Async(fullPath, token);
                    updates.Add((entry.RelPath, hash));

                    Interlocked.Add(ref hashedBytes, entry.Size);
                    int currentCount = Interlocked.Increment(ref hashedCount);
                    ReportProgress(entry.RelPath, currentCount >= totalToHash);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _logger.Warning($"[SnapshotService] Failed to hash '{fullPath}': {ex.Message}");
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

        // Load all backups and metadata markers for this project to find protected snapshot IDs.
        IEnumerable<Backup> backups = _repo.GetBackupsForProject(project.Id);
        var protectedIds = new HashSet<int>(backups.Select(b => b.SnapshotId));
        var metadataBySnapshotId = _repo.GetSnapshotHistoryMetadataBySnapshotIds(snapshots.Select(snapshot => snapshot.Id));
        foreach (int snapshotId in metadataBySnapshotId
                     .Where(static entry => entry.Value.IsProtected)
                     .Select(static entry => entry.Key))
        {
            protectedIds.Add(snapshotId);
        }

        // Determine snapshots that are not referenced by any backup or protected by metadata.
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
            _logger.Info($"[SnapshotService] Retention deleting {toDelete.Count} old snapshots for project '{project.Name}'.");
            _repo.DeleteSnapshotsById(project.Name, toDelete);
        }
    }
}

public record SnapshotOutcome(int Added, int Modified, int Deleted, int Unchanged, int TotalFiles, long TotalBytes);
