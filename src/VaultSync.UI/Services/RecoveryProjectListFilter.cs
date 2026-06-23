using System;
using System.Collections.Generic;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Services;

public enum RecoveryProjectFilter
{
    All,
    NeedsAttention,
    Ready
}

internal static class RecoveryProjectListFilter
{
    public static IReadOnlyList<RecoveryProjectViewModel> Apply(
        IEnumerable<RecoveryProjectViewModel> projects,
        string? searchText,
        RecoveryProjectFilter filter)
    {
        string search = (searchText ?? string.Empty).Trim();
        return projects
            .Where(project => MatchesFilter(project, filter))
            .Where(project => MatchesSearch(project, search))
            .OrderBy(project => project.Score)
            .ThenBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesFilter(RecoveryProjectViewModel project, RecoveryProjectFilter filter) =>
        filter switch
        {
            RecoveryProjectFilter.Ready => project.State == RestoreReadinessState.Ready,
            RecoveryProjectFilter.NeedsAttention => project.State != RestoreReadinessState.Ready,
            _ => true
        };

    private static bool MatchesSearch(RecoveryProjectViewModel project, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return project.ProjectName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               project.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               project.TrackLabel.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               project.Reason.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }
}
