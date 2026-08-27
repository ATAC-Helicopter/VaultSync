using System.Collections.Concurrent;
using System.Security.Cryptography;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public sealed class SnapshotCreationOptions
{
    public bool HashNow { get; init; } = true;
    public int? MaxSnapshotsToKeep { get; init; }
    public Action<double, string, string>? ProgressCallback { get; init; }
    public bool UseScanCache { get; init; }
    public bool AggressiveScanCache { get; init; }
}

public class SnapshotService
{
    private readonly SqliteRepository _repo;
    private readonly HashService _hash;
    private readonly IVaultLogger _logger;

    private readonly record struct SnapshotFileMetadata(string Full, string Rel, FileEntry Entry);
    private sealed record SnapshotBaseline(Dictionary<string, FileEntry> PreviousFiles, long PreviousTotalBytes);
    private sealed record SnapshotChangeSet(
        List<string> Added,
        List<string> Modified,
        List<string> Unchanged,
        List<string> Deleted,
        List<SnapshotFileMetadata> CurrentMetadata,
        Dictionary<string, SnapshotFileMetadata> CurrentMetadataByRel,
        Dictionary<string, FileEntry> CurrentFilesByRel);
    private sealed record SnapshotScanRequest(
        Project Project,
        FilterService Filter,
        IEnumerable<FileEntry> PreviousEntries,
        ScanCacheState? Cache,
        bool ForceFullScan);

    private sealed class HashProgressReporter(
        int totalToHash,
        long totalHashBytes,
        Action<double, string, string>? progressCallback,
        TimeSpan reportInterval)
    {
        private readonly DateTime _hashStart = DateTime.UtcNow;
        private DateTime _lastReport = DateTime.UtcNow;
        private readonly object _lock = new();

        public void Report(string relPath, int hashedCount, long hashedBytes, bool force)
        {
            if (progressCallback is null)
                return;

            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!force && (now - _lastReport) < reportInterval)
                    return;

                _lastReport = now;
                progressCallback(
                    GetPercent(hashedCount),
                    relPath,
                    GetEtaText(hashedCount, hashedBytes, now));
            }
        }

        private double GetPercent(int hashedCount) =>
            totalToHash > 0 ? hashedCount * 100d / totalToHash : 100d;

        private string GetEtaText(int hashedCount, long hashedBytes, DateTime now)
        {
            if (hashedCount >= totalToHash)
                return $"Hashing {hashedCount}/{totalToHash}";

            double speedBytesSec = GetSpeedBytesPerSecond(hashedBytes, now);
            if (hashedCount <= 0 || totalHashBytes <= 0 || speedBytesSec <= 0)
                return $"Hashing {hashedCount}/{totalToHash}";

            double speedMbSec = speedBytesSec / (1024d * 1024d);
            long remainingBytes = Math.Max(0L, totalHashBytes - hashedBytes);
            var eta = TimeSpan.FromSeconds(remainingBytes / speedBytesSec);
            return $"Hashing {hashedCount}/{totalToHash} - {speedMbSec:0.0} MB/s - ETA {eta:mm\\:ss}";
        }

        private double GetSpeedBytesPerSecond(long hashedBytes, DateTime now)
        {
            double elapsedSeconds = Math.Max(0.1, (now - _hashStart).TotalSeconds);
            return hashedBytes / elapsedSeconds;
        }
    }

    private sealed class SnapshotDirectoryScanner(
        SnapshotScanRequest request,
        Dictionary<string, long> directoryMtimeCache,
        CancellationToken cancellationToken)
    {
        private readonly List<FileEntry> _results = [];
        private readonly Dictionary<string, List<FileEntry>> _previousByDirectory =
            BuildPrevByDir(request.PreviousEntries);

        public int SkippedDirectories { get; private set; }

        public List<FileEntry> Scan()
        {
            ScanDirectory(request.Project.RootPath, string.Empty);
            return _results;
        }

        private void ScanDirectory(string fullDirectory, string relativeDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadDirectoryMtime(fullDirectory, out long mtimeTicks))
                return;

            directoryMtimeCache[relativeDirectory] = mtimeTicks;
            if (TryReuseDirectory(relativeDirectory, mtimeTicks))
                return;

            foreach (string childDirectory in EnumerateDirectoriesSafely(fullDirectory))
                ScanChildDirectory(childDirectory);

            AddFiles(fullDirectory);
        }

        private bool TryReuseDirectory(string relativeDirectory, long mtimeTicks)
        {
            if (request.ForceFullScan || request.Cache is null ||
                !request.Cache.DirectoryMtimeUtcTicks.TryGetValue(relativeDirectory, out long cachedTicks) ||
                cachedTicks != mtimeTicks ||
                !_previousByDirectory.TryGetValue(relativeDirectory, out List<FileEntry>? cachedEntries))
            {
                return false;
            }

            _results.AddRange(cachedEntries);
            SkippedDirectories++;
            return true;
        }

        private void ScanChildDirectory(string childDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Filter.ShouldExclude(request.Project.RootPath, childDirectory))
                return;
            if (IsLinkedPath(childDirectory))
            {
                RuntimeLog.WriteVerbose($"[SnapshotService] Skipping linked directory '{childDirectory}'.");
                return;
            }

            string relative = Path.GetRelativePath(request.Project.RootPath, childDirectory).Replace('\\', '/');
            ScanDirectory(childDirectory, relative);
        }

        private void AddFiles(string fullDirectory)
        {
            foreach (string file in EnumerateFilesSafely(fullDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Filter.ShouldExclude(request.Project.RootPath, file))
                    continue;
                if (IsLinkedPath(file))
                {
                    RuntimeLog.WriteVerbose($"[SnapshotService] Skipping linked file '{file}'.");
                    continue;
                }

                AddFile(file);
            }
        }

        private void AddFile(string file)
        {
            try
            {
                var info = new FileInfo(file);
                string relative = Path.GetRelativePath(request.Project.RootPath, file).Replace('\\', '/');
                _results.Add(new FileEntry(relative, info.Length, info.LastWriteTimeUtc, string.Empty));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.WriteVerbose($"[SnapshotService] Skipping inaccessible file '{file}': {ex.Message}");
            }
        }

        private static bool TryReadDirectoryMtime(string fullDirectory, out long mtimeTicks)
        {
            try
            {
                var info = new DirectoryInfo(fullDirectory);
                mtimeTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
                return info.Exists;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.WriteVerbose(
                    $"[SnapshotService] Skipping inaccessible directory '{fullDirectory}': {ex.Message}");
                mtimeTicks = 0;
                return false;
            }
        }

        private static string[] EnumerateDirectoriesSafely(string fullDirectory)
        {
            try
            {
                return Directory.GetDirectories(fullDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.WriteVerbose(
                    $"[SnapshotService] Cannot enumerate directories in '{fullDirectory}': {ex.Message}");
                return [];
            }
        }

        private static string[] EnumerateFilesSafely(string fullDirectory)
        {
            try
            {
                return Directory.GetFiles(fullDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.WriteVerbose(
                    $"[SnapshotService] Cannot enumerate files in '{fullDirectory}': {ex.Message}");
                return [];
            }
        }

        private static bool IsLinkedPath(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                RuntimeLog.WriteVerbose(
                    $"[SnapshotService] Skipping path with unverifiable link status '{path}': {ex.Message}");
                return true;
            }
        }

        private static Dictionary<string, List<FileEntry>> BuildPrevByDir(IEnumerable<FileEntry> entries)
        {
            var map = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
            foreach (FileEntry entry in entries)
            {
                string rel = entry.RelPath.Replace('\\', '/');
                string current = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? string.Empty;
                while (true)
                {
                    if (!map.TryGetValue(current, out List<FileEntry>? list))
                    {
                        list = [];
                        map[current] = list;
                    }
                    list.Add(entry);

                    if (string.IsNullOrEmpty(current))
                        break;

                    int separatorIndex = current.LastIndexOf('/');
                    current = separatorIndex >= 0 ? current[..separatorIndex] : string.Empty;
                }
            }
            return map;
        }
    }

    public SnapshotOutcome? LastCreatedOutcome { get; private set; }

    public SnapshotService(SqliteRepository repo, HashService hash, IVaultLogger? logger = null)
    {
        _repo = repo;
        _hash = hash;
        _logger = logger ?? RuntimeVaultLogger.Instance;
    }

    public Task<int> CreateSnapshotAsync(Project project, bool fullHash, int? maxSnapshotsToKeep = null, CancellationToken ct = default)
        => CreateSnapshotAsync(
            project,
            fullHash,
            new SnapshotCreationOptions { MaxSnapshotsToKeep = maxSnapshotsToKeep },
            ct);

    public Task<int> CreateSnapshotAsync(Project project, bool fullHash, bool hashNow, int? maxSnapshotsToKeep = null, CancellationToken ct = default)
        => CreateSnapshotAsync(
            project,
            fullHash,
            new SnapshotCreationOptions
            {
                HashNow = hashNow,
                MaxSnapshotsToKeep = maxSnapshotsToKeep
            },
            ct);

    public async Task<int> CreateSnapshotAsync(
        Project project,
        bool fullHash,
        SnapshotCreationOptions options,
        CancellationToken ct = default)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        ArgumentNullException.ThrowIfNull(options);
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

            SnapshotBaseline baseline = await LoadSnapshotBaselineAsync(project, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Build filter from preset + local overrides
            var filter = FilterService.FromPresetAndLocal(project.RootPath, project.Preset, logger: _logger);

            // Build current file list (with optional scan cache)
            string filterHash = ComputeFilterHash(filter);
            ScanCacheState? cache = options.UseScanCache ? ScanCacheStore.TryLoad(project, filterHash) : null;
            bool forceFullScan = ShouldForceFullScan(cache, options.UseScanCache, options.AggressiveScanCache);

            var dirMtimeCache = new Dictionary<string, long>(StringComparer.Ordinal);
            var scanRequest = new SnapshotScanRequest(
                project,
                filter,
                baseline.PreviousFiles.Values,
                cache,
                forceFullScan);
            List<FileEntry> currentEntries = BuildCurrentEntries(
                scanRequest,
                dirMtimeCache,
                out int skippedDirs,
                ct);

            _logger.Info($"[SnapshotService] Scan cache used={options.UseScanCache && cache is not null}, skippedDirs={skippedDirs}, files={currentEntries.Count}.");

            ct.ThrowIfCancellationRequested();

            SnapshotChangeSet changes = BuildSnapshotChangeSet(project, currentEntries, baseline.PreviousFiles, ct);

            int snapshotId = options.HashNow
                ? await HashAndPersistSnapshotAsync(
                    project,
                    changes,
                    baseline,
                    fullHash,
                    options.MaxSnapshotsToKeep,
                    options.ProgressCallback,
                    ct).ConfigureAwait(false)
                : PersistSnapshotWithoutHashing(
                    project,
                    changes,
                    baseline,
                    fullHash,
                    options.MaxSnapshotsToKeep,
                    ct);

            // The cache describes the durable snapshot baseline. Publishing it
            // before hashing/persistence succeeds can make a cancelled run's
            // directory mtimes suppress changes during the next cached scan.
            UpdateScanCache(project, filterHash, cache, forceFullScan, dirMtimeCache);
            return snapshotId;
        }, ct);
    }

    private async Task<SnapshotBaseline> LoadSnapshotBaselineAsync(Project project, CancellationToken ct)
    {
        Snapshot? previousSnapshot = _repo.GetLatestLocalSnapshotForProject(project.Id);
        if (previousSnapshot is null)
        {
            return new SnapshotBaseline(
                new Dictionary<string, FileEntry>(StringComparer.Ordinal),
                0L);
        }

        List<FileEntry> previousFiles = await _repo.GetFilesForSnapshotAsync(previousSnapshot.Id, ct).ConfigureAwait(false);
        return new SnapshotBaseline(
            previousFiles.ToDictionary(f => f.RelPath, StringComparer.Ordinal),
            previousSnapshot.TotalBytes);
    }

    private static SnapshotChangeSet BuildSnapshotChangeSet(
        Project project,
        List<FileEntry> currentEntries,
        Dictionary<string, FileEntry> previousFiles,
        CancellationToken ct)
    {
        List<SnapshotFileMetadata> currentMetadata = [.. currentEntries
            .Select(entry => new SnapshotFileMetadata(
                Path.Combine(project.RootPath, entry.RelPath.Replace('/', Path.DirectorySeparatorChar)),
                entry.RelPath,
                entry))];

        Dictionary<string, SnapshotFileMetadata> currentMetadataByRel =
            currentMetadata.ToDictionary(m => m.Rel, StringComparer.Ordinal);
        Dictionary<string, FileEntry> currentFilesByRel = currentMetadataByRel.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Entry,
            StringComparer.Ordinal);

        var added = new List<string>();
        var modified = new List<string>();
        var unchanged = new List<string>();
        ClassifySnapshotChanges(currentMetadataByRel, previousFiles, added, modified, unchanged, ct);

        var deleted = previousFiles.Keys
            .Except(currentMetadataByRel.Keys, StringComparer.Ordinal)
            .ToList();

        return new SnapshotChangeSet(
            added,
            modified,
            unchanged,
            deleted,
            currentMetadata,
            currentMetadataByRel,
            currentFilesByRel);
    }

    private static void ClassifySnapshotChanges(
        Dictionary<string, SnapshotFileMetadata> currentMetadataByRel,
        Dictionary<string, FileEntry> previousFiles,
        List<string> added,
        List<string> modified,
        List<string> unchanged,
        CancellationToken ct)
    {
        foreach (KeyValuePair<string, SnapshotFileMetadata> kvp in currentMetadataByRel)
        {
            ct.ThrowIfCancellationRequested();

            if (!previousFiles.TryGetValue(kvp.Key, out FileEntry? old))
            {
                added.Add(kvp.Key);
                continue;
            }

            if (HasFileChanged(old, kvp.Value.Entry))
                modified.Add(kvp.Key);
            else
                unchanged.Add(kvp.Key);
        }
    }

    private static bool HasFileChanged(FileEntry old, FileEntry current)
    {
        bool sameSize = old.Size == current.Size;
        bool sameTime = Math.Abs((old.MTimeUtc - current.MTimeUtc).TotalSeconds) < 1.0;
        return !sameSize || !sameTime;
    }

    private int PersistSnapshotWithoutHashing(
        Project project,
        SnapshotChangeSet changes,
        SnapshotBaseline baseline,
        bool fullHash,
        int? maxSnapshotsToKeep,
        CancellationToken ct)
    {
        long snapshotTotalBytes = 0;
        List<FileEntry> snapshotEntries = new List<FileEntry>(changes.CurrentMetadataByRel.Count);

        foreach (SnapshotFileMetadata meta in changes.CurrentMetadataByRel.Values)
        {
            ct.ThrowIfCancellationRequested();

            string hash = ResolveDeferredSnapshotHash(meta.Rel, baseline.PreviousFiles, fullHash);
            snapshotEntries.Add(new FileEntry(meta.Rel, meta.Entry.Size, meta.Entry.MTimeUtc, hash));
            snapshotTotalBytes += meta.Entry.Size;
        }

        int snapshotId = CreateSnapshotRecord(project, changes, baseline, snapshotEntries, snapshotTotalBytes);
        FinishSnapshot(project, changes, snapshotEntries.Count, snapshotTotalBytes, maxSnapshotsToKeep);
        return snapshotId;
    }

    private static string ResolveDeferredSnapshotHash(
        string relativePath,
        Dictionary<string, FileEntry> previousFiles,
        bool fullHash)
    {
        return !fullHash &&
               previousFiles.TryGetValue(relativePath, out FileEntry? previousEntry) &&
               !string.IsNullOrWhiteSpace(previousEntry.HashSha256)
            ? previousEntry.HashSha256
            : string.Empty;
    }

    private async Task<int> HashAndPersistSnapshotAsync(
        Project project,
        SnapshotChangeSet changes,
        SnapshotBaseline baseline,
        bool fullHash,
        int? maxSnapshotsToKeep,
        Action<double, string, string>? progressCallback,
        CancellationToken ct)
    {
        var entries = new ConcurrentBag<FileEntry>();
        long totalBytes = AddReusableHashEntries(changes, baseline.PreviousFiles, fullHash, entries, ct);
        List<SnapshotFileMetadata> toHash = GetSnapshotFilesToHash(changes, fullHash);

        _logger.Info($"[SnapshotService] toHash = {toHash.Count}, fullHash={fullHash}, added={changes.Added.Count}, modified={changes.Modified.Count}, unchanged={changes.Unchanged.Count}, deleted={changes.Deleted.Count}");

        totalBytes += await HashChangedSnapshotFilesAsync(toHash, entries, progressCallback, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        int snapshotId = CreateSnapshotRecord(project, changes, baseline, entries, totalBytes);
        FinishSnapshot(project, changes, entries.Count, totalBytes, maxSnapshotsToKeep);
        return snapshotId;
    }

    private static long AddReusableHashEntries(
        SnapshotChangeSet changes,
        Dictionary<string, FileEntry> previousFiles,
        bool fullHash,
        ConcurrentBag<FileEntry> entries,
        CancellationToken ct)
    {
        if (fullHash)
            return 0L;

        long totalBytes = 0;
        foreach (string rel in changes.Unchanged)
        {
            ct.ThrowIfCancellationRequested();

            if (!changes.CurrentMetadataByRel.TryGetValue(rel, out SnapshotFileMetadata meta) ||
                !previousFiles.TryGetValue(rel, out FileEntry? previousEntry))
            {
                continue;
            }

            entries.Add(new FileEntry(rel, meta.Entry.Size, meta.Entry.MTimeUtc, previousEntry.HashSha256));
            totalBytes += meta.Entry.Size;
        }

        return totalBytes;
    }

    private static List<SnapshotFileMetadata> GetSnapshotFilesToHash(SnapshotChangeSet changes, bool fullHash)
    {
        if (fullHash)
            return changes.CurrentMetadata;

        HashSet<string> changedRel = new HashSet<string>(changes.Added, StringComparer.Ordinal);
        changedRel.UnionWith(changes.Modified);
        return [.. changes.CurrentMetadata.Where(m => changedRel.Contains(m.Rel))];
    }

    private async Task<long> HashChangedSnapshotFilesAsync(
        List<SnapshotFileMetadata> toHash,
        ConcurrentBag<FileEntry> entries,
        Action<double, string, string>? progressCallback,
        CancellationToken ct)
    {
        long hashedBytes = 0;
        int hashedCount = 0;
        long totalHashBytes = toHash.Sum(m => m.Entry.Size);
        var progress = CreateHashProgressReporter(toHash.Count, totalHashBytes, progressCallback);

        if (progressCallback is not null && toHash.Count == 0)
            progressCallback(0, string.Empty, "Hashing 0/0");

        await Parallel.ForEachAsync(
            toHash,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            },
            async (m, token) =>
            {
                string hash = await _hash.Sha256Async(m.Full, token).ConfigureAwait(false);
                entries.Add(new FileEntry(m.Rel, m.Entry.Size, m.Entry.MTimeUtc, hash));

                Interlocked.Add(ref hashedBytes, m.Entry.Size);
                int currentCount = Interlocked.Increment(ref hashedCount);
                progress.Report(m.Rel, currentCount, hashedBytes, currentCount >= toHash.Count);
            }).ConfigureAwait(false);

        return hashedBytes;
    }

    private static HashProgressReporter CreateHashProgressReporter(
        int totalToHash,
        long totalHashBytes,
        Action<double, string, string>? progressCallback)
    {
        return new HashProgressReporter(totalToHash, totalHashBytes, progressCallback, TimeSpan.FromMilliseconds(200));
    }

    private int CreateSnapshotRecord(
        Project project,
        SnapshotChangeSet changes,
        SnapshotBaseline baseline,
        IEnumerable<FileEntry> entries,
        long totalBytes)
    {
        var entryList = entries as IReadOnlyCollection<FileEntry> ?? entries.ToList();
        SnapshotDiffSummary diffSummary = BuildSnapshotDiffSummary(
            changes.Added,
            changes.Modified,
            changes.Deleted,
            changes.CurrentFilesByRel,
            baseline.PreviousFiles,
            totalBytes,
            baseline.PreviousTotalBytes);

        int snapshotId = _repo.CreateSnapshot(project.Id, entryList.Count, totalBytes, diffSummary);
        _repo.InsertFiles(snapshotId, entryList.OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase));
        return snapshotId;
    }

    private void FinishSnapshot(
        Project project,
        SnapshotChangeSet changes,
        int totalFiles,
        long totalBytes,
        int? maxSnapshotsToKeep)
    {
        ApplySnapshotRetentionIfNeeded(project, maxSnapshotsToKeep);
        LastCreatedOutcome = new SnapshotOutcome(
            Added: changes.Added.Count,
            Modified: changes.Modified.Count,
            Deleted: changes.Deleted.Count,
            Unchanged: changes.Unchanged.Count,
            TotalFiles: totalFiles,
            TotalBytes: totalBytes);
        LastOutcome = LastCreatedOutcome;

        _logger.Info($"[SnapshotService] Finished snapshot for '{project.Name}': " +
                     $"added={changes.Added.Count}, modified={changes.Modified.Count}, deleted={changes.Deleted.Count}, unchanged={changes.Unchanged.Count}, totalFiles={totalFiles}, totalBytes={totalBytes}");
    }

    private void ApplySnapshotRetentionIfNeeded(Project project, int? maxSnapshotsToKeep)
    {
        if (!maxSnapshotsToKeep.HasValue || maxSnapshotsToKeep.Value <= 0)
            return;

        try
        {
            ApplySnapshotRetention(project, Math.Max(1, maxSnapshotsToKeep.Value));
        }
        catch (Exception ex)
        {
            _logger.Error($"[SnapshotService] Retention step failed for project '{project.Name}': {ex}");
        }
    }

    private static string ComputeFilterHash(FilterService filter)
    {
        string joined = string.Join('\n', filter.RawPatterns ?? Array.Empty<string>());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(joined);
        byte[] hash = SHA256.HashData(bytes);
        return HashService.FormatHex(hash);
    }

    private static List<FileEntry> BuildCurrentEntries(
        SnapshotScanRequest request,
        Dictionary<string, long> dirMtimeCache,
        out int skippedDirs,
        CancellationToken ct)
    {
        var scanner = new SnapshotDirectoryScanner(request, dirMtimeCache, ct);
        List<FileEntry> results = scanner.Scan();
        skippedDirs = scanner.SkippedDirectories;
        return results;
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

        var pathStats = new Dictionary<string, (int Changes, long ChangedBytes)>(StringComparer.Ordinal);

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

        List<FileEntry> files = (await _repo
            .GetFilesForSnapshotAsync(snapshotId, ct)
            .ConfigureAwait(false))
            .Where(f => string.IsNullOrWhiteSpace(f.HashSha256))
            .ToList();

        if (files.Count == 0)
            return 0;

        int totalToHash = files.Count;
        long totalBytes = files.Sum(f => f.Size);
        int hashedCount = 0;
        long hashedBytes = 0;
        var updates = new ConcurrentBag<(string RelPath, string HashSha256)>();
        var progress = new HashProgressReporter(
            totalToHash,
            totalBytes,
            progressCallback,
            TimeSpan.FromMilliseconds(250));

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

                    long currentBytes = Interlocked.Add(ref hashedBytes, entry.Size);
                    int currentCount = Interlocked.Increment(ref hashedCount);
                    progress.Report(entry.RelPath, currentCount, currentBytes, currentCount >= totalToHash);
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
