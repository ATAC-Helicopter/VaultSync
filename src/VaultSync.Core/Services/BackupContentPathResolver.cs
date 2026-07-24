using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public static class BackupContentPathResolver
{
    public static string? Resolve(Backup backup, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<BackupDestination> destinations = config.Backups.Destinations ?? [];
        var roots = new List<string>();
        AddRoot(roots, backup.DestinationPath);

        foreach (BackupDestination destination in destinations.Where(destination =>
                     (!string.IsNullOrWhiteSpace(backup.DestinationPath) &&
                      PathsEqual(destination.Path, backup.DestinationPath)) ||
                     (!string.IsNullOrWhiteSpace(backup.DestinationAlias) &&
                      string.Equals(destination.Alias, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase))))
        {
            AddRoot(roots, destination.Path);
        }

        bool hasNoDestinationIdentity = string.IsNullOrWhiteSpace(backup.DestinationPath) &&
                                        string.IsNullOrWhiteSpace(backup.DestinationAlias);
        bool isRecordedLegacyRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath) &&
                                    PathsEqual(config.Backups.BackupRoot ?? string.Empty, backup.DestinationPath);
        if (hasNoDestinationIdentity || isRecordedLegacyRoot)
        {
            AddRoot(roots, config.Backups.BackupRoot);
        }

        // Legacy/imported records may have no destination identity. Only those records may
        // probe every configured root; otherwise a missing backup on one destination could
        // be mistaken for a same-named backup that belongs to another destination.
        if (string.IsNullOrWhiteSpace(backup.DestinationPath) &&
            string.IsNullOrWhiteSpace(backup.DestinationAlias))
        {
            foreach (BackupDestination destination in destinations)
                AddRoot(roots, destination.Path);
        }

        if (Path.IsPathFullyQualified(backup.Path))
        {
            try
            {
                string candidate = Path.GetFullPath(backup.Path);
                return roots.Any(root => BackupSafetyService.IsExistingPathSafeUnderRoot(root, candidate))
                    ? candidate
                    : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        foreach (string root in roots)
        {
            if (BackupSafetyService.TryCombinePathUnderRoot(root, backup.Path, out string fullPath) &&
                BackupSafetyService.IsExistingPathSafeUnderRoot(root, fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || roots.Any(existing => PathsEqual(existing, root)))
            return;

        roots.Add(root);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            DestinationIdentityService.NormalizeDestinationPath(left),
            DestinationIdentityService.NormalizeDestinationPath(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
