#nullable enable

using System.Collections.Generic;
using VaultSync.Core.Models;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryProjectListFilterTests
{
    private static readonly IReadOnlyList<RecoveryProjectViewModel> Projects =
    [
        Create("Ready Project", RestoreReadinessState.Ready, 90, "Recent verified backup."),
        Create("Review Project", RestoreReadinessState.Attention, 70, "Protect a known-good point."),
        Create("Risk Project", RestoreReadinessState.Risk, 40, "Backup is stale."),
        Create("Blocked Project", RestoreReadinessState.Unavailable, 0, "Destination unreachable.")
    ];

    [Fact]
    public void Apply_NeedsAttention_IncludesEveryNonReadyStateInScoreOrder()
    {
        IReadOnlyList<RecoveryProjectViewModel> result =
            RecoveryProjectListFilter.Apply(Projects, null, RecoveryProjectFilter.NeedsAttention);

        Assert.Collection(
            result,
            project => Assert.Equal("Blocked Project", project.ProjectName),
            project => Assert.Equal("Risk Project", project.ProjectName),
            project => Assert.Equal("Review Project", project.ProjectName));
    }

    [Fact]
    public void Apply_Ready_ReturnsOnlyReadyProjects()
    {
        IReadOnlyList<RecoveryProjectViewModel> result =
            RecoveryProjectListFilter.Apply(Projects, null, RecoveryProjectFilter.Ready);

        RecoveryProjectViewModel project = Assert.Single(result);
        Assert.Equal("Ready Project", project.ProjectName);
    }

    [Theory]
    [InlineData("risk", "Risk Project")]
    [InlineData("unreachable", "Blocked Project")]
    [InlineData("clean", "Ready Project")]
    public void Apply_SearchesProjectStatusAndReason(string search, string expectedProject)
    {
        IReadOnlyList<RecoveryProjectViewModel> result =
            RecoveryProjectListFilter.Apply(Projects, search, RecoveryProjectFilter.All);

        Assert.Equal(expectedProject, Assert.Single(result).ProjectName);
    }

    private static RecoveryProjectViewModel Create(
        string name,
        RestoreReadinessState state,
        int score,
        string reason) =>
        new(new ProjectRestoreReadiness
        {
            ProjectId = score,
            ProjectName = name,
            State = state,
            Score = score,
            Label = state.ToString(),
            Reason = reason
        });
}
