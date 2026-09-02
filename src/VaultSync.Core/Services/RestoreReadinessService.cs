using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class RestoreReadinessService
{
    public RestoreReadinessSummary BuildSummary(
        IReadOnlyList<Project> projects,
        IReadOnlyList<Backup> backups,
        AppConfig config,
        BackupIndexScanSummary? scanSummary = null,
        IReadOnlyDictionary<string, bool>? destinationReachability = null,
        IReadOnlyDictionary<int, SnapshotHistoryMetadata>? snapshotMetadataById = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyDictionary<string, bool> reachability = destinationReachability ?? BuildDestinationReachabilityLookup(config);
        var backupsByProject = backups
            .GroupBy(backup => backup.ProjectId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(backup => backup.CreatedUtc).ThenByDescending(backup => backup.Id).ToList());

        var results = projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => EvaluateProject(
                project,
                backupsByProject.GetValueOrDefault(project.Id),
                config,
                scanSummary,
                reachability,
                snapshotMetadataById))
            .ToList();

        int ready = results.Count(result => result.State == RestoreReadinessState.Ready);
        int attention = results.Count(result => result.State == RestoreReadinessState.Attention);
        int risk = results.Count(result => result.State == RestoreReadinessState.Risk);
        int unavailable = results.Count(result => result.State == RestoreReadinessState.Unavailable);

        string headline = BuildHeadline(results.Count, ready, attention, risk, unavailable);

        string detail = string.Format(
            CultureInfo.InvariantCulture,
            "Ready {0} | Attention {1} | Risk {2} | Unavailable {3}",
            ready,
            attention,
            risk,
            unavailable);

        return new RestoreReadinessSummary
        {
            ReadyCount = ready,
            AttentionCount = attention,
            RiskCount = risk,
            UnavailableCount = unavailable,
            ProjectCount = results.Count,
            Headline = headline,
            Detail = detail,
            Projects = results
        };
    }

    private static string BuildHeadline(int projectCount, int ready, int attention, int risk, int unavailable)
    {
        if (projectCount > 0 && ready == projectCount)
            return "Restore ready across all tracked projects";
        if (unavailable > 0)
            return $"{unavailable} project(s) are not currently restore-ready";
        if (risk > 0)
            return $"{risk} project(s) need restore-readiness attention";
        if (attention > 0)
            return $"{attention} project(s) should be reviewed";

        return "No tracked projects yet";
    }

    public static IReadOnlyDictionary<string, bool> BuildDestinationReachabilityLookup(AppConfig config)
    {
        var lookup = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (BackupDestination destination in GetAllDestinations(config))
        {
            string id = DestinationIdentityService.GetId(destination);
            if (lookup.ContainsKey(id))
                continue;

            lookup[id] = IsReachable(destination.Path);
        }

        return lookup;
    }

    [SuppressMessage(
        "Major Code Smell",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "The readiness scorecard keeps its small independent deductions together so the final score and user-facing reasons remain auditable in one place.")]
    private static ProjectRestoreReadiness EvaluateProject(
        Project project,
        IReadOnlyList<Backup>? backups,
        AppConfig config,
        BackupIndexScanSummary? scanSummary,
        IReadOnlyDictionary<string, bool> destinationReachability,
        IReadOnlyDictionary<int, SnapshotHistoryMetadata>? snapshotMetadataById)
    {
        if (backups is null || backups.Count == 0)
        {
            return new ProjectRestoreReadiness
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                State = RestoreReadinessState.Unavailable,
                Score = 0,
                Label = "Unavailable",
                Reason = "No backup history is currently available for this project."
            };
        }

        Backup latest = backups[0];

        int score = 100;
        var reasons = new List<string>();
        TimeSpan latestAge = DateTime.UtcNow - latest.CreatedUtc;
        if (latestAge > TimeSpan.FromHours(72))
        {
            score -= 45;
            reasons.Add("latest backup is older than 72 hours");
        }
        else if (latestAge > TimeSpan.FromHours(24))
        {
            score -= 20;
            reasons.Add("latest backup is older than 24 hours");
        }

        string verificationPolicy = ProjectVerificationPolicy.Normalize(project.VerificationPolicy);
        if (string.Equals(verificationPolicy, ProjectVerificationPolicy.Manual, StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
            reasons.Add("verification is manual-only");
        }
        else if (string.Equals(verificationPolicy, ProjectVerificationPolicy.Scheduled, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(latest.Type, "auto", StringComparison.OrdinalIgnoreCase))
        {
            score -= 10;
            reasons.Add("latest backup has not been covered by scheduled verification");
        }

        List<BackupDestination> selectedDestinations = ResolveSelectedDestinations(project, config);
        if (selectedDestinations.Count == 0)
        {
            score = Math.Min(score, 25);
            reasons.Add("no backup destination is currently configured for this project");
        }
        else
        {
            int reachableCount = selectedDestinations.Count(destination =>
                destinationReachability.TryGetValue(DestinationIdentityService.GetId(destination), out bool reachable) && reachable);

            if (reachableCount == 0)
            {
                score = Math.Min(score, 20);
                reasons.Add("selected destination(s) are currently unreachable");
            }
            else if (reachableCount < selectedDestinations.Count)
            {
                score -= 15;
                reasons.Add("some selected destination(s) are unreachable");
            }
        }

        if ((scanSummary?.ErrorCount ?? 0) > 0)
        {
            score -= 15;
            reasons.Add("backup index consistency issues are present");
        }
        else if ((scanSummary?.WarningCount ?? 0) > 0)
        {
            score -= 8;
            reasons.Add("backup index warnings should be reviewed");
        }

        if (snapshotMetadataById is not null)
        {
            bool hasProtectedRestorePoint = backups.Any(backup =>
                snapshotMetadataById.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata) &&
                metadata.IsProtected);
            bool hasKnownGoodRestorePoint = backups.Any(backup =>
                snapshotMetadataById.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata) &&
                metadata.IsKnownGood);

            if (!hasProtectedRestorePoint)
            {
                score -= 8;
                reasons.Add("no protected recovery point is marked");
            }

            if (!hasKnownGoodRestorePoint)
            {
                score -= 8;
                reasons.Add("no known-good recovery point is marked");
            }
        }

        score = Math.Clamp(score, 0, 100);
        RestoreReadinessState state = score switch
        {
            >= 85 => RestoreReadinessState.Ready,
            >= 60 => RestoreReadinessState.Attention,
            >= 35 => RestoreReadinessState.Risk,
            _ => RestoreReadinessState.Unavailable
        };

        return new ProjectRestoreReadiness
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            State = state,
            Score = score,
            Label = state.ToString(),
            Reason = reasons.Count == 0
                ? "Latest backup is recent, destinations are reachable, and no integrity warnings are active."
                : string.Join("; ", reasons)
        };
    }

    private static List<BackupDestination> ResolveSelectedDestinations(Project project, AppConfig config)
    {
        List<BackupDestination> allDestinations = GetAllDestinations(config);
        var activeDestinations = allDestinations.Where(destination => destination.Active).ToList();
        string preferredId = DestinationIdentityService.NormalizePreferredDestinationId(project.PreferredDestinationId, allDestinations);

        if (string.IsNullOrWhiteSpace(preferredId))
            return activeDestinations;

        if (string.Equals(preferredId, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
            return allDestinations;

        BackupDestination? match = DestinationIdentityService.FindByPreferredDestinationId(allDestinations, preferredId);
        if (match?.Active != true)
            return activeDestinations;

        return new List<BackupDestination> { match };
    }

    private static List<BackupDestination> GetAllDestinations(AppConfig config)
    {
        if (config.Backups.UseAdvancedDestinations && config.Backups.Destinations is { Count: > 0 })
            return [.. config.Backups.Destinations];

        if (!string.IsNullOrWhiteSpace(config.Backups.BackupLocation))
        {
            return new List<BackupDestination>
            {
                new()
                {
                    Alias = "Primary",
                    Path = config.Backups.BackupLocation,
                    Active = true,
                    PreMounted = true
                }
            };
        }

        return new List<BackupDestination>();
    }

    private static bool IsReachable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
