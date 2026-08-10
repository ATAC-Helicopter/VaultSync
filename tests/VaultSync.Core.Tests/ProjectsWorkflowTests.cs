using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectsWorkflowTests
{
    [Fact]
    public void RemoveConfirmation_BecomesAvailableWhenProjectIsSelected()
    {
        var viewModel = new ProjectsViewModel();

        Assert.False(viewModel.ConfirmRemoveProjectCommand.CanExecute(null));

        viewModel.SelectedProject = new ProjectItemViewModel
        {
            Name = "Example",
            Path = "/tmp/example",
            IsRegistered = true
        };

        Assert.True(viewModel.ConfirmRemoveProjectCommand.CanExecute(null));
    }
}
