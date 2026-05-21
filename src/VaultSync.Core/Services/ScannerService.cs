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
    private readonly IVaultLogger _logger;

    public ScannerService(FilterService filter, IVaultLogger? logger = null)
    {
        _filter = filter;
        _logger = logger ?? RuntimeVaultLogger.Instance;
    }

    /// <summary>
    /// Synchronous scan. Use this only from background threads (e.g. CLI, services).
    /// Calling this directly from the UI thread on large trees will freeze the UI.
    /// </summary>
    public IEnumerable<FileEntry> Scan(string root)
    {
        foreach (var path in EnumerateFilesSafely(root, CancellationToken.None))
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

            foreach (var path in EnumerateFilesSafely(root, ct))
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
                    _logger.Warning($"[ScannerService] IO error while scanning '{path}': {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Skip restricted files but log for diagnostics.
                    _logger.Warning($"[ScannerService] Access denied while scanning '{path}': {ex.Message}");
                }
            }

            return results;
        }, ct);
    }

    private IEnumerable<string> EnumerateFilesSafely(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();

            if (!string.Equals(Path.GetFullPath(root), Path.GetFullPath(current), GetPathComparison()) &&
                (BackupSafetyService.IsReservedPath(root, current) || _filter.ShouldExclude(root, current)))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
            {
                _logger.Warning($"[ScannerService] Skipping directory '{current}': {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return file;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
            {
                _logger.Warning($"[ScannerService] Skipping subdirectory scan for '{current}': {ex.Message}");
                continue;
            }

            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                if (BackupSafetyService.IsReservedPath(root, directory) || _filter.ShouldExclude(root, directory))
                    continue;

                stack.Push(directory);
            }
        }
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
