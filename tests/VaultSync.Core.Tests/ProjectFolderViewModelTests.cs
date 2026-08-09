using System;
using System.Collections.Specialized;
using VaultSync.Core.Models;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectFolderViewModelTests
{
    [Fact]
    public void FilteredFolderKeepsFullMembershipForBatchActions()
    {
        var visible = new ProjectItemViewModel
        {
            ProjectId = 1,
            Name = "Visible",
            IsRegistered = true,
            Health = ProjectHealthStatus.Healthy
        };
        var hidden = new ProjectItemViewModel
        {
            ProjectId = 2,
            Name = "Hidden by search",
            IsRegistered = true,
            Health = ProjectHealthStatus.Warning,
            IsAutoBackupEnabled = false
        };
        var folder = new ProjectFolderViewModel(
            new ProjectGroup { Id = "group-1", Name = "Client work" },
            "Ungrouped");

        folder.ReplaceProjects([visible], [visible, hidden]);

        Assert.Single(folder.Projects);
        Assert.Equal(2, folder.ProjectCount);
        Assert.Equal(2, folder.RegisteredProjectCount);
        Assert.Equal(1, folder.AttentionProjectCount);
        Assert.Equal(1, folder.PausedProjectCount);
        Assert.True(folder.CanRunBatchActions);
        Assert.True(folder.ShowBatchActions);

        folder.IsRenaming = true;

        Assert.False(folder.ShowBatchActions);
    }

    [Fact]
    public void UngroupedFolderCannotBeRenamedOrDeleted()
    {
        var folder = new ProjectFolderViewModel(null, "Ungrouped");

        Assert.True(folder.IsUngrouped);
        Assert.False(folder.CanManage);
        Assert.Equal(ProjectFolderViewModel.UngroupedId, folder.Id);
    }

    [Fact]
    public void FolderChoiceIsStagedUntilTheProjectMoveIsCommitted()
    {
        var ungrouped = new ProjectGroupOption(ProjectGroupOption.UngroupedId, "Ungrouped");
        var work = new ProjectGroupOption("work", "Work");
        var project = new ProjectItemViewModel { Name = "VaultSync" };
        project.SetGroupOption(ungrouped);

        project.SelectedGroupOption = work;

        Assert.True(project.HasPendingGroupChange);
        Assert.Equal(ProjectGroupOption.UngroupedId, project.GroupId);
        Assert.Contains("Work", project.FolderMovePreview, StringComparison.Ordinal);

        project.CommitGroupOption(work);

        Assert.False(project.HasPendingGroupChange);
        Assert.Equal("work", project.GroupId);
        Assert.Contains("Work", project.FolderLocationText, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderSummarySeparatesMembershipHealthAndPausedCounts()
    {
        var active = new ProjectItemViewModel
        {
            ProjectId = 1,
            IsRegistered = true,
            Health = ProjectHealthStatus.Healthy,
            IsAutoBackupEnabled = true
        };
        var paused = new ProjectItemViewModel
        {
            ProjectId = 2,
            IsRegistered = true,
            Health = ProjectHealthStatus.Warning,
            IsAutoBackupEnabled = false
        };
        var folder = new ProjectFolderViewModel(
            new ProjectGroup { Id = "work", Name = "Work" },
            "Ungrouped");

        folder.ReplaceProjects([active, paused]);

        Assert.Equal(2, folder.ProjectCount);
        Assert.Equal("2 projects", folder.ProjectCountLabel);
        Assert.Contains("1 healthy", folder.HealthSummary, StringComparison.Ordinal);
        Assert.True(folder.CanPauseAutoBackups);
        Assert.True(folder.CanResumeAutoBackups);
    }

    [Fact]
    public void ReplacingFolderProjectsReconcilesWithoutResettingTheBoundList()
    {
        var first = new ProjectItemViewModel { ProjectId = 1, Name = "First" };
        var second = new ProjectItemViewModel { ProjectId = 2, Name = "Second" };
        var third = new ProjectItemViewModel { ProjectId = 3, Name = "Third" };
        var folder = new ProjectFolderViewModel(
            new ProjectGroup { Id = "work", Name = "Work" },
            "Ungrouped");
        folder.ReplaceProjects([first, second]);

        bool resetRaised = false;
        folder.Projects.CollectionChanged += (_, args) =>
            resetRaised |= args.Action == NotifyCollectionChangedAction.Reset;

        folder.ReplaceProjects([second, third]);

        Assert.False(resetRaised);
        Assert.Equal([second, third], folder.Projects);
    }
}
