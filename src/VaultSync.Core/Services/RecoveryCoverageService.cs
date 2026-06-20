using System;
using System.Collections.Generic;
using System.Linq;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class RecoveryCoverageService
{
    public RecoveryCoverageSummary BuildSummary(
        IReadOnlyList<Project> projects,
        IReadOnlyList<Backup> backups,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(backups);

        DateTime now = nowUtc ?? DateTime.UtcNow;
        var trackedProjectIds = projects.Select(project => project.Id).ToHashSet();
        List<Backup> latestBackups = backups
            .Where(backup => trackedProjectIds.Contains(backup.ProjectId))
            .GroupBy(backup => backup.ProjectId)
            .Select(group => group
                .OrderByDescending(backup => backup.CreatedUtc)
                .ThenByDescending(backup => backup.Id)
                .First())
            .ToList();

        return new RecoveryCoverageSummary
        {
            ProjectCount = projects.Count,
            Within24Hours = CountWithin(latestBackups, now, TimeSpan.FromHours(24)),
            Within7Days = CountWithin(latestBackups, now, TimeSpan.FromDays(7)),
            Within30Days = CountWithin(latestBackups, now, TimeSpan.FromDays(30)),
            Within90Days = CountWithin(latestBackups, now, TimeSpan.FromDays(90))
        };
    }

    private static int CountWithin(IEnumerable<Backup> backups, DateTime now, TimeSpan window) =>
        backups.Count(backup =>
        {
            TimeSpan age = now - backup.CreatedUtc;
            return age >= TimeSpan.Zero && age <= window;
        });
}
