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
    }

    [Fact]
    public void UngroupedFolderCannotBeRenamedOrDeleted()
    {
        var folder = new ProjectFolderViewModel(null, "Ungrouped");

        Assert.True(folder.IsUngrouped);
        Assert.False(folder.CanManage);
        Assert.Equal(ProjectFolderViewModel.UngroupedId, folder.Id);
    }
}
