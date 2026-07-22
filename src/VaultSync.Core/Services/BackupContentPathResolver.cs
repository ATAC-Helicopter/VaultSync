using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public static class BackupContentPathResolver
{
    public static string? Resolve(Backup backup, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(config);

        if (Path.IsPathFullyQualified(backup.Path) && (Directory.Exists(backup.Path) || File.Exists(backup.Path)))
            return backup.Path;

        IEnumerable<string> roots = new[] { backup.DestinationPath }
            .Concat((config.Backups.Destinations ?? []).Select(destination => destination.Path))
            .Append(config.Backups.BackupRoot)
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            if (BackupSafetyService.TryCombinePathUnderRoot(root, backup.Path, out string fullPath) &&
                (Directory.Exists(fullPath) || File.Exists(fullPath)))
            {
                return fullPath;
            }
        }

        return null;
    }
}
