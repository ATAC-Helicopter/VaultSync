using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public sealed class SnapshotCompareService(SqliteRepository repository)
{
    public async Task<SnapshotCompareResult> CompareAsync(
        int olderSnapshotId,
        int newerSnapshotId,
        CancellationToken cancellationToken = default)
    {
        if (olderSnapshotId <= 0)
            throw new ArgumentOutOfRangeException(nameof(olderSnapshotId));
        if (newerSnapshotId <= 0)
            throw new ArgumentOutOfRangeException(nameof(newerSnapshotId));
        if (olderSnapshotId == newerSnapshotId)
            throw new ArgumentException("Snapshot comparison requires two different snapshots.");

        Task<List<FileEntry>> olderTask = repository.GetFilesForSnapshotAsync(olderSnapshotId, cancellationToken);
        Task<List<FileEntry>> newerTask = repository.GetFilesForSnapshotAsync(newerSnapshotId, cancellationToken);
        await Task.WhenAll(olderTask, newerTask).ConfigureAwait(false);

        return Compare(olderTask.Result, newerTask.Result, SnapshotCompareOptions.Default, cancellationToken);
    }

    public static SnapshotCompareResult Compare(
        IReadOnlyCollection<FileEntry> olderFiles,
        IReadOnlyCollection<FileEntry> newerFiles,
        SnapshotCompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(olderFiles);
        ArgumentNullException.ThrowIfNull(newerFiles);
        options ??= SnapshotCompareOptions.Default;

        Dictionary<string, FileEntry> olderByPath = IndexFiles(olderFiles);
        Dictionary<string, FileEntry> newerByPath = IndexFiles(newerFiles);
        var changes = new List<SnapshotFileChange>();
        int added = 0;
        int modified = 0;
        int unchanged = 0;

        foreach ((string path, FileEntry current) in newerByPath.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!olderByPath.TryGetValue(path, out FileEntry? previous))
            {
                added++;
                changes.Add(new SnapshotFileChange(path, SnapshotFileChangeKind.Added, 0, Math.Max(0L, current.Size)));
                continue;
            }

            if (HasChanged(previous, current))
            {
                modified++;
                changes.Add(new SnapshotFileChange(
                    path,
                    SnapshotFileChangeKind.Modified,
                    Math.Max(0L, previous.Size),
                    Math.Max(0L, current.Size)));
                continue;
            }

            unchanged++;
        }

        foreach ((string path, FileEntry previous) in olderByPath
                     .Where(entry => !newerByPath.ContainsKey(entry.Key))
                     .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            changes.Add(new SnapshotFileChange(path, SnapshotFileChangeKind.Deleted, Math.Max(0L, previous.Size), 0));
        }

        int deleted = changes.Count(change => change.Kind == SnapshotFileChangeKind.Deleted);
        long previousTotal = olderByPath.Values.Sum(file => Math.Max(0L, file.Size));
        long currentTotal = newerByPath.Values.Sum(file => Math.Max(0L, file.Size));
        long changedBytes = changes.Sum(change => Math.Max(change.PreviousSizeBytes, change.CurrentSizeBytes));
        IReadOnlyList<SnapshotDiffPathStat> topPaths = BuildTopChangedPaths(changes);
        IReadOnlyList<SnapshotChangeSignal> signals = BuildSignals(
            added,
            modified,
            deleted,
            unchanged,
            previousTotal,
            currentTotal,
            options);

        return new SnapshotCompareResult(
            added,
            modified,
            deleted,
            unchanged,
            previousTotal,
            currentTotal,
            changedBytes,
            changes,
            topPaths,
            signals);
    }

    private static Dictionary<string, FileEntry> IndexFiles(IEnumerable<FileEntry> files)
    {
        var result = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        foreach (FileEntry file in files)
        {
            string path = NormalizePath(file.RelPath);
            if (path.Length > 0)
                result[path] = file with { RelPath = path };
        }

        return result;
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim('/');

    private static bool HasChanged(FileEntry previous, FileEntry current)
    {
        if (previous.Size != current.Size)
            return true;

        if (!string.IsNullOrWhiteSpace(previous.HashSha256) && !string.IsNullOrWhiteSpace(current.HashSha256))
        {
            return !string.Equals(previous.HashSha256, current.HashSha256, StringComparison.OrdinalIgnoreCase);
        }

        return Math.Abs((previous.MTimeUtc - current.MTimeUtc).TotalSeconds) >= 1.0;
    }

    private static IReadOnlyList<SnapshotDiffPathStat> BuildTopChangedPaths(
        IEnumerable<SnapshotFileChange> changes)
    {
        return [.. changes
            .GroupBy(change => ToPathBucket(change.Path), StringComparer.Ordinal)
            .Select(group => new SnapshotDiffPathStat(
                group.Key,
                group.Count(),
                group.Sum(change => Math.Max(change.PreviousSizeBytes, change.CurrentSizeBytes))))
            .OrderByDescending(stat => stat.Changes)
            .ThenByDescending(stat => stat.ChangedBytes)
            .ThenBy(stat => stat.Path, StringComparer.OrdinalIgnoreCase)
            .Take(8)];
    }

    private static string ToPathBucket(string path)
    {
        int firstSlash = path.IndexOf('/');
        if (firstSlash < 0)
            return "(root)";

        int secondSlash = path.IndexOf('/', firstSlash + 1);
        return secondSlash < 0 ? path[..firstSlash] : path[..secondSlash];
    }

    private static IReadOnlyList<SnapshotChangeSignal> BuildSignals(
        int added,
        int modified,
        int deleted,
        int unchanged,
        long previousTotal,
        long currentTotal,
        SnapshotCompareOptions options)
    {
        var signals = new List<SnapshotChangeSignal>();
        int previousFileCount = deleted + modified + unchanged;
        int currentFileCount = added + modified + unchanged;
        int changedCount = added + modified + deleted;
        int comparisonBase = Math.Max(previousFileCount, currentFileCount);
        double deletionRatio = previousFileCount == 0 ? 0 : (double)deleted / previousFileCount;
        double churnRatio = comparisonBase == 0 ? 0 : (double)changedCount / comparisonBase;
        long growth = currentTotal - previousTotal;
        double growthRatio = previousTotal == 0 ? (growth > 0 ? 1 : 0) : (double)growth / previousTotal;

        if (deleted >= options.MassDeletionMinimumFiles && deletionRatio >= options.MassDeletionRatio)
        {
            signals.Add(new SnapshotChangeSignal(
                SnapshotChangeSignalKind.MassDeletion,
                deleted,
                deletionRatio,
                growth));
        }

        if (growth >= options.SignificantGrowthMinimumBytes && growthRatio >= options.SignificantGrowthRatio)
        {
            signals.Add(new SnapshotChangeSignal(
                SnapshotChangeSignalKind.SignificantGrowth,
                added + modified,
                growthRatio,
                growth));
        }

        if (changedCount >= options.HighChurnMinimumFiles && churnRatio >= options.HighChurnRatio)
        {
            signals.Add(new SnapshotChangeSignal(
                SnapshotChangeSignalKind.HighChurn,
                changedCount,
                churnRatio,
                growth));
        }

        return signals;
    }
}
