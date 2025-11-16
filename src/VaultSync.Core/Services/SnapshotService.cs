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

    public async Task<int> CreateSnapshotAsync(Project project, bool fullHash, CancellationToken ct = default)
    {
        Console.WriteLine($"[SnapshotService] Starting snapshot for project '{project.Name}'");
        Console.WriteLine($"[SnapshotService]   RootPath = '{project.RootPath}'");
        Console.WriteLine($"[SnapshotService]   Preset   = '{project.Preset}'");

        // Load previous snapshot (if any) to enable incremental behavior
        var prev = _repo.GetLatestSnapshot(project.Id);
        var prevFiles = prev != null
            ? _repo.GetFilesForSnapshot(prev.Id).ToDictionary(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

        // Build filter from preset + local overrides
        var filter = FilterService.FromPresetAndLocal(project.RootPath, project.Preset);

        // Enumerate all current files first
        var allPaths = Directory
            .EnumerateFiles(project.RootPath, "*", SearchOption.AllDirectories)
            .ToList();

        Console.WriteLine($"[SnapshotService] Enumerated {allPaths.Count} files under root before filtering.");

        // Apply filter
        var currentPaths = allPaths
            .Where(p => !filter.ShouldExclude(project.RootPath, p))
            .ToList();

        Console.WriteLine($"[SnapshotService] {currentPaths.Count} files remain after filtering.");

        // Map rel path -> file system info
        var currMeta = currentPaths.Select(p => new
        {
            Full = p,
            Rel = Path.GetRelativePath(project.RootPath, p).Replace('\\', '/'),
            Info = new FileInfo(p)
        }).ToList();

        // Determine changes
        var added = new List<string>();
        var modified = new List<string>();
        var unchanged = new List<string>();

        foreach (var f in currMeta)
        {
            if (!prevFiles.TryGetValue(f.Rel, out var old))
            {
                added.Add(f.Rel);
                continue;
            }

            // consider unchanged if size and mtime are identical (UTC)
            var sameSize = old.Size == f.Info.Length;
            var sameTime = Math.Abs((old.MTimeUtc - f.Info.LastWriteTimeUtc).TotalSeconds) < 1.0; // tolerate FS granularity

            if (!sameSize || !sameTime)
                modified.Add(f.Rel);
            else
                unchanged.Add(f.Rel);
        }

        var deleted = prevFiles.Keys.Except(currMeta.Select(m => m.Rel), StringComparer.OrdinalIgnoreCase).ToList();

        // Hash strategy
        var entries = new ConcurrentBag<FileEntry>();
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
                var meta = currMeta.First(m => m.Rel == rel);
                var prevEntry = prevFiles[rel];
                AddEntry(rel, meta.Info.Length, meta.Info.LastWriteTimeUtc, prevEntry.HashSha256);
            }
        }

        // Added + Modified (and everything if fullHash)
        var toHash = fullHash ? currMeta : currMeta.Where(m => added.Contains(m.Rel) || modified.Contains(m.Rel)).ToList();

        await Parallel.ForEachAsync(toHash, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, async (m, token) =>
        {
            var h = await _hash.Sha256Async(m.Full, token);
            AddEntry(m.Rel, m.Info.Length, m.Info.LastWriteTimeUtc, h);
        });

        // Create snapshot record
        var snapId = _repo.CreateSnapshot(project.Id, entries.Count, totalBytes);
        _repo.InsertFiles(snapId, entries.OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase));

        // Attach summary for CLI
        LastOutcome = new SnapshotOutcome
        (
            Added: added.Count,
            Modified: modified.Count,
            Deleted: deleted.Count,
            Unchanged: unchanged.Count,
            TotalFiles: entries.Count,
            TotalBytes: totalBytes
        );

        return snapId;
    }

    // simple store for most recent outcome in-process
    public static SnapshotOutcome? LastOutcome { get; private set; }
}

public record SnapshotOutcome(int Added, int Modified, int Deleted, int Unchanged, int TotalFiles, long TotalBytes);