using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using VaultSync.Core.Services;

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
    private static readonly TimeSpan TelemetryExportRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan TemporaryRetention = TimeSpan.FromDays(1);
    private const long MaximumLegacyLogBytes = 10L * 1024L * 1024L;
    private static readonly string[] TemporaryDirectoryPatterns =
    [
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

        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            summary = summary.Add(PruneConfigurationData(
                Path.Combine(applicationData, "VaultSync"),
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
        summary = summary.Add(PruneFiles(
            Path.Combine(root, "cache", "release-assets"),
            IsReleaseAssetCacheTemporaryFile,
            utcNow - TemporaryRetention));
        summary = summary.Add(PruneDirectories(
            Path.Combine(root, "exports"),
            "support-*",
            utcNow - TemporaryRetention,
            IsSupportBundleStagingDirectory));
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

    internal static StorageCleanupSummary PruneConfigurationData(string root, DateTime utcNow) =>
        PruneFiles(
            root,
            file => IsAtomicTemporaryFile(file, InstallationIdentityService.IdentityFileName) ||
                    IsAtomicTemporaryFile(file, "credentials.json"),
            utcNow - PatchWorkingRetention);

    internal static StorageCleanupSummary PruneTemporaryData(string tempRoot, DateTime utcNow)
    {
        int staleOpenWorkspaces = EncryptedOpenWorkspaceManager.CleanupStaleWorkspaces(
            tempRoot,
            utcNow,
            TemporaryRetention);
        var summary = new StorageCleanupSummary(0, staleOpenWorkspaces, 0);
        foreach (string pattern in TemporaryDirectoryPatterns)
        {
            summary = summary.Add(PruneDirectories(tempRoot, pattern, utcNow - TemporaryRetention));
        }

        summary = summary.Add(PruneFiles(
            tempRoot,
            file => file.Name.StartsWith("vaultsync_exclude_", StringComparison.OrdinalIgnoreCase) &&
                    file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase),
            utcNow - TemporaryRetention));
        summary = summary.Add(PruneFiles(
            Path.Combine(tempRoot, "VaultSync", "updates"),
            static _ => true,
            utcNow - TemporaryRetention));
        summary = summary.Add(PruneDirectories(
            Path.Combine(tempRoot, "VaultSync", "recovery-tests"),
            "*",
            utcNow - TemporaryRetention));
        summary = summary.Add(PruneFiles(
            Path.Combine(tempRoot, "vaultsync-telemetry-export"),
            IsTelemetryExport,
            utcNow - TelemetryExportRetention,
            maximumRetainedBytes: 100L * 1024L * 1024L));
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
                    DeleteDirectoryWithoutFollowingLinks(directory);
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

            long totalBytes = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(directory);
            while (pending.TryPop(out DirectoryInfo? current))
            {
                foreach (FileInfo file in current.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    totalBytes = file.Length > long.MaxValue - totalBytes
                        ? long.MaxValue
                        : totalBytes + file.Length;
                }

                foreach (DirectoryInfo child in current.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
            }

            return totalBytes;
        }
        catch
        {
            return 0;
        }
    }

    private static void DeleteDirectoryWithoutFollowingLinks(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete(recursive: false);
            return;
        }

        foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            file.Delete();

        foreach (DirectoryInfo child in directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            DeleteDirectoryWithoutFollowingLinks(child);

        directory.Delete(recursive: false);
    }

    private static bool IsReleaseAssetCacheTemporaryFile(FileInfo file)
    {
        string[] segments = file.Name.Split('.', StringSplitOptions.None);
        return segments is ["", var identity, "json", var writeId, "tmp"] &&
               identity.Length == 64 &&
               identity.All(Uri.IsHexDigit) &&
               Guid.TryParseExact(writeId, "N", out _);
    }

    private static bool IsAtomicTemporaryFile(FileInfo file, string durableFileName)
    {
        string prefix = $".{durableFileName}.";
        const string suffix = ".tmp";
        if (!file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int writeIdLength = file.Name.Length - prefix.Length - suffix.Length;
        return writeIdLength == 32 &&
               Guid.TryParseExact(file.Name.AsSpan(prefix.Length, writeIdLength), "N", out _);
    }

    private static bool IsSupportBundleStagingDirectory(DirectoryInfo directory)
    {
        const string prefix = "support-";
        const int timestampLength = 15;
        if (!directory.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        ReadOnlySpan<char> remainder = directory.Name.AsSpan(prefix.Length);
        return remainder.Length == timestampLength + 1 + 32 &&
               remainder[timestampLength] == '-' &&
               DateTime.TryParseExact(
                   remainder[..timestampLength],
                   "yyyyMMdd-HHmmss",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out _) &&
               Guid.TryParseExact(remainder[(timestampLength + 1)..], "N", out _);
    }

    private static bool IsTelemetryExport(FileInfo file)
    {
        const string prefix = "telemetry_";
        const string suffix = ".zip";
        if (!file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> timestamp = file.Name.AsSpan(
            prefix.Length,
            file.Name.Length - prefix.Length - suffix.Length);
        return DateTime.TryParseExact(
            timestamp,
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out _);
    }
}
