using System;
using System.Collections.Generic;
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
        IReadOnlyDictionary<string, bool>? destinationReachability = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(config);

        var reachability = destinationReachability ?? BuildDestinationReachabilityLookup(config);
        var backupsByProject = backups
            .GroupBy(backup => backup.ProjectId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(backup => backup.CreatedUtc).ThenByDescending(backup => backup.Id).ToList());

        var results = projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => EvaluateProject(project, backupsByProject.GetValueOrDefault(project.Id), config, scanSummary, reachability))
            .ToList();

        var ready = results.Count(result => result.State == RestoreReadinessState.Ready);
        var attention = results.Count(result => result.State == RestoreReadinessState.Attention);
        var risk = results.Count(result => result.State == RestoreReadinessState.Risk);
        var unavailable = results.Count(result => result.State == RestoreReadinessState.Unavailable);

        var headline = ready == results.Count && results.Count > 0
            ? "Restore ready across all tracked projects"
            : unavailable > 0
                ? $"{unavailable} project(s) are not currently restore-ready"
                : risk > 0
                    ? $"{risk} project(s) need restore-readiness attention"
                    : attention > 0
                        ? $"{attention} project(s) should be reviewed"
                        : "No tracked projects yet";

        var detail = string.Format(
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

    public static IReadOnlyDictionary<string, bool> BuildDestinationReachabilityLookup(AppConfig config)
    {
        var lookup = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in GetAllDestinations(config))
        {
            var id = DestinationIdentityService.GetId(destination);
            if (lookup.ContainsKey(id))
                continue;

            lookup[id] = IsReachable(destination.Path);
        }

        return lookup;
    }

    private static ProjectRestoreReadiness EvaluateProject(
        Project project,
        IReadOnlyList<Backup>? backups,
        AppConfig config,
        BackupIndexScanSummary? scanSummary,
        IReadOnlyDictionary<string, bool> destinationReachability)
    {
        var latest = backups?.FirstOrDefault();
        if (latest is null)
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

        var score = 100;
        var reasons = new List<string>();
        var latestAge = DateTime.UtcNow - latest.CreatedUtc;
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

        var verificationPolicy = ProjectVerificationPolicy.Normalize(project.VerificationPolicy);
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

        var selectedDestinations = ResolveSelectedDestinations(project, config);
        if (selectedDestinations.Count == 0)
        {
            score = Math.Min(score, 25);
            reasons.Add("no backup destination is currently configured for this project");
        }
        else
        {
            var reachableCount = selectedDestinations.Count(destination =>
                destinationReachability.TryGetValue(DestinationIdentityService.GetId(destination), out var reachable) && reachable);

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

        score = Math.Clamp(score, 0, 100);
        var state = score switch
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
        var allDestinations = GetAllDestinations(config);
        var activeDestinations = allDestinations.Where(destination => destination.Active).ToList();
        var preferredId = DestinationIdentityService.NormalizePreferredDestinationId(project.PreferredDestinationId, allDestinations);

        if (string.IsNullOrWhiteSpace(preferredId))
            return activeDestinations;

        if (string.Equals(preferredId, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
            return allDestinations;

        var match = DestinationIdentityService.FindByPreferredDestinationId(allDestinations, preferredId);
        if (match is null || !match.Active)
            return activeDestinations;

        return new List<BackupDestination> { match };
    }

    private static List<BackupDestination> GetAllDestinations(AppConfig config)
    {
        if (config.Backups.UseAdvancedDestinations && config.Backups.Destinations is { Count: > 0 })
            return config.Backups.Destinations.ToList();

        if (!string.IsNullOrWhiteSpace(config.Backups.BackupLocation))
        {
            return new List<BackupDestination>
            {
                new()
                {
                    Alias = "Primary",
                    Path = config.Backups.BackupLocation!,
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
