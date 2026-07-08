using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Centralized guardrails for backup source/destination separation and VaultSync-owned backup artifacts.
/// These checks intentionally fail closed: a backup destination must never be inside the project tree.
/// </summary>
public static class BackupSafetyService
{
    public const string LegacyProjectTempBackupDirectoryName = ".vaultsync-temp-backups";
    public const string OfflineStagingDirectoryName = "pending-backups";

    private static readonly string[] ReservedDirectoryNames =
    {
        ".vaultsync",
        LegacyProjectTempBackupDirectoryName,
        "Backup",
        "Backups"
    };

    private static readonly string[] ReservedFileNames =
    {
        ".vaultsync_inprogress",
        ".vaultsync_complete",
        ".vaultsync_resume.json"
    };

    public static IReadOnlyList<string> ReservedIgnorePatterns { get; } =
    [
        ".vaultsync/**",
        $"{LegacyProjectTempBackupDirectoryName}/**",
        "Backup/**",
        "Backups/**",
        ".vaultsync_inprogress",
        ".vaultsync_complete",
        ".vaultsync_resume.json"
    ];

    public static IEnumerable<string> AddReservedIgnorePatterns(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
            yield return pattern;

        foreach (var pattern in ReservedIgnorePatterns)
            yield return pattern;
    }

    public static string GetOfflineStagingRoot(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var projectKey = project.Id > 0 ? project.Id.ToString() : Slugify(project.Name);
        return Path.Combine(GetVaultSyncHomeDirectory(), OfflineStagingDirectoryName, projectKey);
    }

    public static string GetLegacyProjectTempRoot(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.RootPath))
            throw new InvalidOperationException("Project.RootPath is not set.");

        return Path.Combine(project.RootPath, LegacyProjectTempBackupDirectoryName);
    }

    public static void EnsureSafeBackupRoot(Project project, string backupRoot)
    {
        ArgumentNullException.ThrowIfNull(project);
        EnsureSafeBackupRoot(project.RootPath, backupRoot);
    }

    public static void EnsureSafeBackupRoot(string projectRoot, string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new InvalidOperationException("Project.RootPath is not set.");
        if (string.IsNullOrWhiteSpace(backupRoot))
            throw new InvalidOperationException("Backup root is empty. Configure a backup location in Settings.");

        var source = NormalizeDirectoryPath(projectRoot);
        var target = NormalizeDirectoryPath(backupRoot);

        if (IsSameOrChildPath(source, target))
        {
            throw new InvalidOperationException(
                "Unsafe backup destination: the backup location is inside the project root. " +
                "VaultSync blocked this backup to prevent recursively backing up backups into backups.");
        }

        if (IsSameOrChildPath(target, source))
        {
            throw new InvalidOperationException(
                "Unsafe backup destination: the project root is inside the backup location. " +
                "Choose a backup location outside the project tree.");
        }
    }

    public static bool IsReservedPath(string projectRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(fullPath))
            return false;

        string relative;
        try
        {
            relative = Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
        }
        catch
        {
            return false;
        }

        if (relative.Length == 0 || relative == "." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        if (ReservedDirectoryNames.Any(name => segments.Any(segment => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase))))
            return true;

        var fileName = segments[^1];
        return ReservedFileNames.Any(name => string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryCombinePathUnderRoot(string root, string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            if (!string.IsNullOrWhiteSpace(relativePath) && Path.IsPathFullyQualified(relativePath))
                return false;

            string normalizedRoot = NormalizeDirectoryPath(root);
            string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath ?? string.Empty));
            if (!IsSameOrChildPath(normalizedRoot, candidate))
                return false;

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return full + Path.DirectorySeparatorChar;
    }

    private static bool IsSameOrChildPath(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return child.StartsWith(parent, comparison);
    }

    private static string GetVaultSyncHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("VaultSync user storage directory could not be resolved.");

        return Path.Combine(home, ".vaultsync");
    }

    private static string Slugify(string? value)
    {
        var input = string.IsNullOrWhiteSpace(value) ? "project" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : char.ToLowerInvariant(ch)).ToArray();
        var slug = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }
}
