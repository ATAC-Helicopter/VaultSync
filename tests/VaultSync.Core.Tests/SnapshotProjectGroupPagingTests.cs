using System;
using System.Collections.Generic;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SnapshotProjectGroupPagingTests
{
    [Fact]
    public void SetSnapshotsBoundsInitialVisibleHistory()
    {
        var group = new SnapshotProjectGroup();

        group.SetSnapshots(CreateSnapshots(45));

        Assert.Equal(45, group.TotalSnapshotCount);
        Assert.Equal(20, group.VisibleRowCount);
        Assert.Equal(25, group.RemainingRowCount);
        Assert.True(group.HasMoreSnapshots);
        Assert.True(group.LoadMoreSnapshotsCommand.CanExecute(null));
    }

    [Fact]
    public void LoadMoreSnapshotsRevealsPagesUntilHistoryIsComplete()
    {
        var group = new SnapshotProjectGroup();
        group.SetSnapshots(CreateSnapshots(45));

        group.LoadMoreSnapshotsCommand.Execute(null);

        Assert.Equal(40, group.VisibleRowCount);
        Assert.Equal(5, group.RemainingRowCount);
        Assert.True(group.HasMoreSnapshots);

        group.LoadMoreSnapshotsCommand.Execute(null);

        Assert.Equal(45, group.VisibleRowCount);
        Assert.Equal(0, group.RemainingRowCount);
        Assert.False(group.HasMoreSnapshots);
        Assert.False(group.LoadMoreSnapshotsCommand.CanExecute(null));
    }

    [Fact]
    public void SetSnapshotsReplacesPreviouslyPagedHistory()
    {
        var group = new SnapshotProjectGroup();
        group.SetSnapshots(CreateSnapshots(45));
        group.LoadMoreSnapshotsCommand.Execute(null);

        group.SetSnapshots(CreateSnapshots(3));

        Assert.Equal(3, group.TotalSnapshotCount);
        Assert.Equal(3, group.VisibleRowCount);
        Assert.False(group.HasMoreSnapshots);
    }

    [Fact]
    public void RepeatedAndCaseOnlyLocationsShareOneRowAndKeepEveryVersion()
    {
        var newest = new BackupSnapshotItem { Id = "3", ProjectId = "p", DestinationRootPath = "/NAS", BackupRelativePath = "Set/Copy.zip" };
        var older = new BackupSnapshotItem { Id = "2", ProjectId = "p", DestinationRootPath = "/nas", BackupRelativePath = "set/copy.ZIP" };
        var oldest = new BackupSnapshotItem { Id = "1", ProjectId = "p", DestinationRootPath = "/NAS", BackupRelativePath = "Set/Copy.zip" };
        var group = new SnapshotProjectGroup();

        group.SetSnapshots([newest, older, oldest]);

        var row = Assert.Single(group.InventoryRows);
        Assert.Equal(3, group.TotalSnapshotCount);
        Assert.Equal(0, group.RemainingRowCount);
        Assert.True(row.HasVersions);
        Assert.Equal(new[] { newest, older, oldest }, row.Versions);
        Assert.Same(newest, row.SelectedSnapshot);
        row.SelectedSnapshot = older;

        var updated = new BackupSnapshotItem { Id = "2", ProjectId = "p", DestinationRootPath = "/NAS", BackupRelativePath = "Set/Copy.zip" };
        group.SetSnapshots([newest, updated]);

        Assert.Same(row, Assert.Single(group.InventoryRows));
        Assert.Same(updated, row.SelectedSnapshot);
    }

    [Fact]
    public void DifferentLocationsAndIncompletePathsStaySeparate()
    {
        var group = new SnapshotProjectGroup();
        group.SetSnapshots([
            new() { Id = "1", ProjectId = "p", DestinationRootPath = "/nas", BackupRelativePath = "Copy.zip" },
            new() { Id = "2", ProjectId = "p", DestinationRootPath = "/other", BackupRelativePath = "Copy.zip" },
            new() { Id = "3", ProjectId = "p", DestinationRootPath = "/nas", BackupRelativePath = "sub/Copy.zip" },
            new() { Id = "4", ProjectId = "p", BackupRelativePath = "Copy.zip" },
            new() { Id = "5", ProjectId = "p", BackupRelativePath = "Copy.zip" },
            new() { Id = "6", ProjectId = "other", DestinationRootPath = "/nas", BackupRelativePath = "Copy.zip" }
        ]);

        Assert.Equal(6, group.VisibleRowCount);
        Assert.All(group.InventoryRows, row => Assert.False(row.HasVersions));
    }

    private static IReadOnlyList<BackupSnapshotItem> CreateSnapshots(int count)
    {
        var snapshots = new List<BackupSnapshotItem>(count);
        for (int index = 0; index < count; index++)
        {
            snapshots.Add(new BackupSnapshotItem
            {
                Id = index.ToString(),
                Timestamp = new DateTime(2026, 7, 12).AddMinutes(-index)
            });
        }

        return snapshots;
    }
}
