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

    public async Task<int> CreateSnapshotAsync(Project project, bool fullHash, int? maxSnapshotsToKeep = null, CancellationToken ct = default)
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
