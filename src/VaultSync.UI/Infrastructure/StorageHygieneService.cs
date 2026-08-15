using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VaultSync.UI.Infrastructure;

internal readonly record struct StorageCleanupSummary(int FilesRemoved, int DirectoriesRemoved, long BytesReclaimed)
{
    public StorageCleanupSummary Add(StorageCleanupSummary other) => new(
        FilesRemoved + other.FilesRemoved,
        DirectoriesRemoved + other.DirectoriesRemoved,
        BytesReclaimed + other.BytesReclaimed);
}

/// <summary>
/// Removes disposable VaultSync artifacts that are safe to recreate. Backup
/// destinations, databases, configuration, credentials, and mount contents are
/// deliberately outside this service's scope.
/// </summary>
internal static class StorageHygieneService
{
    private static readonly TimeSpan PatchRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan PatchWorkingRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan ScanCacheRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan ReleaseMetadataRetention = TimeSpan.FromDays(180);
    private static readonly TimeSpan TemporaryRetention = TimeSpan.FromDays(1);
    private const long MaximumLegacyLogBytes = 10L * 1024L * 1024L;
    private static readonly string[] TemporaryDirectoryPatterns =
    [
        "vaultsync-open-*",
        "vaultsync-rotate-*",
        "vaultsync-restore-*",
        "vaultsync_archive_*"
    ];

    internal static StorageCleanupSummary RunStartupCleanup(DateTime? utcNow = null)
    {
        DateTime now = utcNow ?? DateTime.UtcNow;
        StorageCleanupSummary summary = default;

        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localData))
        {
            summary = summary.Add(PruneApplicationData(
                Path.Combine(localData, "VaultSync"),
                now));
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            summary = summary.Add(PruneLegacyData(
                Path.Combine(userProfile, ".vaultsync"),
                now));
        }

        summary = summary.Add(PruneTemporaryData(Path.GetTempPath(), now));
        return summary;
    }

    internal static StorageCleanupSummary PruneApplicationData(string root, DateTime utcNow)
    {
        StorageCleanupSummary summary = default;
        summary = summary.Add(PruneFiles(
            Path.Combine(root, "patches"),
            static _ => true,
            utcNow - PatchRetention));

        string patchRuntime = Path.Combine(root, "patch-runtime");
        summary = summary.Add(PruneDirectories(
            patchRuntime,
            "patch-helper-*",
            utcNow - PatchRetention));
        summary = summary.Add(PruneDirectories(
            patchRuntime,
            "patch-*",
            utcNow - PatchWorkingRetention,
            directory => Guid.TryParseExact(
                directory.Name["patch-".Length..],
                "N",
                out _)));
        summary = summary.Add(PruneFiles(
            patchRuntime,
            file => string.Equals(file.Name, "patch-helper.log", StringComparison.OrdinalIgnoreCase),
            utcNow - LogRetention,
            maximumRetainedBytes: 1024L * 1024L));
        summary = summary.Add(PruneFiles(
            Path.Combine(root, "cache", "scan"),
            file => file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase),
            utcNow - ScanCacheRetention,
            maximumRetainedBytes: 20L * 1024L * 1024L));
        summary = summary.Add(PruneFiles(
            Path.Combine(root, "cache", "release-assets"),
            file => file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase),
            utcNow - ReleaseMetadataRetention,
            maximumRetainedBytes: 10L * 1024L * 1024L));
        return summary;
    }

    internal static StorageCleanupSummary PruneLegacyData(string root, DateTime utcNow)
    {
        StorageCleanupSummary summary = PruneFiles(
            Path.Combine(root, "logs"),
            file => file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase),
            utcNow - LogRetention,
            MaximumLegacyLogBytes);
        return summary.Add(PruneFiles(
            root,
            file => file.Name.StartsWith("appsettings.tmp.", StringComparison.OrdinalIgnoreCase) &&
                    file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase),
            utcNow - PatchWorkingRetention));
    }

    internal static StorageCleanupSummary PruneTemporaryData(string tempRoot, DateTime utcNow)
    {
        StorageCleanupSummary summary = default;
        foreach (string pattern in TemporaryDirectoryPatterns)
        {
            summary = summary.Add(PruneDirectories(tempRoot, pattern, utcNow - TemporaryRetention));
        }

        summary = summary.Add(PruneFiles(
            tempRoot,
            file => file.Name.StartsWith("vaultsync_exclude_", StringComparison.OrdinalIgnoreCase) &&
                    file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase),
            utcNow - TemporaryRetention));
        summary = summary.Add(PruneDirectories(
            Path.Combine(tempRoot, "VaultSync"),
            "updates",
            utcNow - TemporaryRetention));
        summary = summary.Add(PruneDirectories(
            Path.Combine(tempRoot, "VaultSync"),
            "recovery-tests",
            utcNow - TemporaryRetention));
        return summary;
    }

    private static StorageCleanupSummary PruneFiles(
        string directory,
        Func<FileInfo, bool> include,
        DateTime cutoffUtc,
        long maximumRetainedBytes = long.MaxValue)
    {
        try
        {
            if (!Directory.Exists(directory))
                return default;

            FileInfo[] files = new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(include)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            long retainedBytes = 0;
            StorageCleanupSummary summary = default;
            foreach (FileInfo file in files)
            {
                bool expired = file.LastWriteTimeUtc < cutoffUtc;
                bool exceedsCap = file.Length > maximumRetainedBytes - retainedBytes;
                if (!expired && !exceedsCap)
                {
                    retainedBytes += file.Length;
                    continue;
                }

                long length = file.Length;
                try
                {
                    file.Delete();
                    summary = summary.Add(new StorageCleanupSummary(1, 0, length));
                }
                catch
                {
                    // Cleanup is best effort and must never block application startup.
                }
            }
            return summary;
        }
        catch
        {
            return default;
        }
    }

    private static StorageCleanupSummary PruneDirectories(
        string root,
        string pattern,
        DateTime cutoffUtc,
        Func<DirectoryInfo, bool>? include = null)
    {
        try
        {
            if (!Directory.Exists(root))
                return default;

            StorageCleanupSummary summary = default;
            foreach (DirectoryInfo directory in new DirectoryInfo(root)
                         .EnumerateDirectories(pattern, SearchOption.TopDirectoryOnly)
                         .Where(candidate => include?.Invoke(candidate) ?? true))
            {
                if (directory.LastWriteTimeUtc >= cutoffUtc)
                    continue;

                long bytes = TryGetDirectorySize(directory);
                try
                {
                    bool isLink = (directory.Attributes & FileAttributes.ReparsePoint) != 0;
                    directory.Delete(recursive: !isLink);
                    summary = summary.Add(new StorageCleanupSummary(0, 1, bytes));
                }
                catch
                {
                    // Cleanup is best effort and must never block application startup.
                }
            }
            return summary;
        }
        catch
        {
            return default;
        }
    }

    private static long TryGetDirectorySize(DirectoryInfo directory)
    {
        try
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return 0;
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }
}
