#nullable enable

using System.Collections.Generic;
using VaultSync.Core.Models;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class TrayProjectSnapshotTests
{
    [Fact]
    public void BuildTrayProjectItems_UsesRegisteredRepositoryProjectsBeforeViewsLoad()
    {
        Project[] projects =
        [
            Project(2, "  Zebra  "),
            Project(1, "Alpha"),
            Project(2, "Duplicate id"),
            Project(0, "Discovered only"),
            Project(3, "   ")
        ];

        IReadOnlyList<AppViewModel.TrayProjectItem> items =
            AppViewModel.BuildTrayProjectItems(projects);

        Assert.Collection(
            items,
            item => Assert.Equal((1, "Alpha"), (item.Id, item.Name)),
            item => Assert.Equal((2, "Zebra"), (item.Id, item.Name)));
    }

    private static Project Project(int id, string name) => new()
    {
        Id = id,
        Name = name,
        RootPath = "/tmp/project",
        Preset = "development"
    };
}
