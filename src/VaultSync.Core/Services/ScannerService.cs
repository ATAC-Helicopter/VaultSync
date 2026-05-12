using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public class ScannerService
{
    private readonly FilterService _filter;

    public ScannerService(FilterService filter) => _filter = filter;

    /// <summary>
    /// Synchronous scan. Use this only from background threads (e.g. CLI, services).
    /// Calling this directly from the UI thread on large trees will freeze the UI.
    /// </summary>
    public IEnumerable<FileEntry> Scan(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (_filter.ShouldExclude(root, path))
                continue;

            var fi  = new FileInfo(path);
            string rel = Path.GetRelativePath(root, path).Replace('\\', '/');

            yield return new FileEntry(rel, fi.Length, fi.LastWriteTimeUtc, "");
        }
    }

    /// <summary>
    /// Asynchronous scan that runs on a background thread and supports cancellation.
    /// This is safe to call from UI code without blocking the UI thread.
    /// </summary>
    public Task<List<FileEntry>> ScanAsync(string root, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root path must be provided.", nameof(root));

        return Task.Run(() =>
        {
            var results = new List<FileEntry>();

            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                if (_filter.ShouldExclude(root, path))
                    continue;

                try
                {
                    var fi  = new FileInfo(path);
                    string rel = Path.GetRelativePath(root, path).Replace('\\', '/');

                    results.Add(new FileEntry(rel, fi.Length, fi.LastWriteTimeUtc, ""));
                }
                catch (IOException ex)
                {
                    // Skip unreadable files but log for diagnostics.
                    Console.WriteLine($"[ScannerService] IO error while scanning '{path}': {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Skip restricted files but log for diagnostics.
                    Console.WriteLine($"[ScannerService] Access denied while scanning '{path}': {ex.Message}");
                }
            }

            return results;
        }, ct);
    }
}