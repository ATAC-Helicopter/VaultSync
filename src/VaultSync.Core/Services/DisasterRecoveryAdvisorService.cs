using System.Text.RegularExpressions;
using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed partial class DisasterRecoveryAdvisorService
{
    public DisasterRecoverySummary BuildSummary(
        IReadOnlyList<Project> projects,
        IReadOnlyList<Backup> backups,
        IReadOnlyList<Snapshot> snapshots,
        IReadOnlyDictionary<int, SnapshotHistoryMetadata> metadataBySnapshotId,
        AppConfig config,
        IReadOnlyList<RecoveryDrillResult>? drills = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(metadataBySnapshotId);
        ArgumentNullException.ThrowIfNull(config);

        drills ??= [];
        Dictionary<int, Snapshot> snapshotsById = snapshots.ToDictionary(snapshot => snapshot.Id);
        var assessments = projects.Select(project =>
        {
            List<Backup> projectBackups = [.. backups
                .Where(backup => backup.ProjectId == project.Id)
                .OrderByDescending(backup => backup.CreatedUtc)
                .ThenByDescending(backup => backup.Id)];
            int destinationCopies = projectBackups
                .Select(GetDestinationIdentity)
                .Where(identity => identity.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int copyCount = projectBackups.Count == 0 ? 1 : 1 + destinationCopies;
            int mediaCount = CountMedia(project, projectBackups);
            bool offsite = projectBackups.Any(backup => IsOffsite(backup, config));
            int protectedCount = projectBackups
                .Select(backup => backup.SnapshotId)
                .Distinct()
                .Count(snapshotId => metadataBySnapshotId.TryGetValue(snapshotId, out SnapshotHistoryMetadata? metadata) && metadata.IsProtected);
            RecoveryDrillResult? lastDrill = drills
                .Where(drill => drill.ProjectId == project.Id)
                .OrderByDescending(drill => drill.RunUtc)
                .FirstOrDefault();

            return new ProjectProtectionAssessment
            {
                ProjectId = project.Id,
                CopyCount = copyCount,
                MediaCount = mediaCount,
                HasOffsiteCopy = offsite,
                MeetsThreeTwoOne = copyCount >= 3 && mediaCount >= 2 && offsite,
                ProtectedPointCount = protectedCount,
                LastDrill = lastDrill,
                Recommendation = BuildRecommendation(project, projectBackups, snapshotsById, metadataBySnapshotId)
            };
        }).ToList();

        return new DisasterRecoverySummary
        {
            ProjectCount = projects.Count,
            ThreeTwoOneReadyCount = assessments.Count(item => item.MeetsThreeTwoOne),
            DrilledProjectCount = assessments.Count(item => item.LastDrill is not null),
            PassedDrillCount = assessments.Count(item => item.LastDrill?.Status == RecoveryDrillStatus.Passed),
            ProtectedPointCount = assessments.Sum(item => item.ProtectedPointCount),
            Projects = assessments
        };
    }

    private static ProtectionRecommendation? BuildRecommendation(
        Project project,
        IReadOnlyList<Backup> backups,
        IReadOnlyDictionary<int, Snapshot> snapshotsById,
        IReadOnlyDictionary<int, SnapshotHistoryMetadata> metadataBySnapshotId)
    {
        foreach (Backup backup in backups)
        {
            bool isProtected = metadataBySnapshotId.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata) && metadata.IsProtected;
            if (isProtected || !snapshotsById.TryGetValue(backup.SnapshotId, out Snapshot? snapshot))
                continue;

            string marker = $"{project.Tags} {metadata?.Label} {metadata?.Tags}";
            if (ReleaseMarkerRegex().IsMatch(marker))
                return new(project.Id, snapshot.Id, backup.Id, ProtectionRecommendationKind.ReleaseMarker, "This recovery point is labeled like a release or delivery milestone.");

            double deletionRatio = snapshot.FileCount <= 0 ? 0 : snapshot.DiffDeleted / (double)snapshot.FileCount;
            if (snapshot.DiffDeleted >= 10 && deletionRatio >= 0.10)
                return new(project.Id, snapshot.Id, backup.Id, ProtectionRecommendationKind.LargeDeletion, $"This point follows a large deletion ({snapshot.DiffDeleted:N0} files).");

            int changed = snapshot.DiffAdded + snapshot.DiffModified + snapshot.DiffDeleted;
            double churnRatio = snapshot.FileCount <= 0 ? 0 : changed / (double)snapshot.FileCount;
            if (changed >= 100 || churnRatio >= 0.25)
                return new(project.Id, snapshot.Id, backup.Id, ProtectionRecommendationKind.SignificantChange, $"This point captures a significant change ({changed:N0} files).");
        }

        Backup? latest = backups.FirstOrDefault();
        if (latest is null || metadataBySnapshotId.Values.Any(metadata => metadata.IsProtected && backups.Any(backup => backup.SnapshotId == metadata.SnapshotId)))
            return null;

        return new(project.Id, latest.SnapshotId, latest.Id, ProtectionRecommendationKind.Baseline, "Protect a recent baseline before cleanup or other risky work.");
    }

    private static string GetDestinationIdentity(Backup backup) =>
        DestinationIdentityService.NormalizeDestinationPath(
            string.IsNullOrWhiteSpace(backup.DestinationPath) ? backup.DestinationAlias : backup.DestinationPath);

    private static int CountMedia(Project project, IReadOnlyList<Backup> backups)
    {
        var media = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GetMediaIdentity(project.RootPath) };
        foreach (Backup backup in backups)
        {
            string destination = string.IsNullOrWhiteSpace(backup.DestinationPath) ? backup.DestinationAlias : backup.DestinationPath;
            if (!string.IsNullOrWhiteSpace(destination))
                media.Add(GetMediaIdentity(destination));
        }

        return media.Count;
    }

    private static string GetMediaIdentity(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();
        if (IsNetworkPath(normalized))
            return "network:" + GetNetworkAuthority(normalized);
        if (normalized.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            return "volume:" + normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        if (normalized.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/run/media/", StringComparison.OrdinalIgnoreCase))
        {
            return "mounted:" + string.Join('/', normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3));
        }

        return "local:" + (Path.GetPathRoot(path) ?? "root");
    }

    private static bool IsOffsite(Backup backup, AppConfig config)
    {
        BackupDestination? destination = config.Backups.Destinations?.FirstOrDefault(item =>
            string.Equals(item.Path, backup.DestinationPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Alias, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase));
        return destination?.IsOffsite == true;
    }

    private static bool IsNetworkPath(string path) =>
        path.StartsWith("//", StringComparison.Ordinal) ||
        path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase);

    private static string GetNetworkAuthority(string path)
    {
        string normalized = path.StartsWith("//", StringComparison.Ordinal) ? "smb:" + path : path;
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) ? uri.Host : normalized;
    }

    [GeneratedRegex(@"(^|[\s._-])(v?\d+\.\d+(?:\.\d+)?|release|final|delivery|submission)([\s._-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseMarkerRegex();
}
