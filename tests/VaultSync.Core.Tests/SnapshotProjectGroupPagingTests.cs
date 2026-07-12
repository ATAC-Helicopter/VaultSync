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
        Assert.Equal(20, group.VisibleSnapshotCount);
        Assert.Equal(25, group.RemainingSnapshotCount);
        Assert.True(group.HasMoreSnapshots);
        Assert.True(group.LoadMoreSnapshotsCommand.CanExecute(null));
    }

    [Fact]
    public void LoadMoreSnapshotsRevealsPagesUntilHistoryIsComplete()
    {
        var group = new SnapshotProjectGroup();
        group.SetSnapshots(CreateSnapshots(45));

        group.LoadMoreSnapshotsCommand.Execute(null);

        Assert.Equal(40, group.VisibleSnapshotCount);
        Assert.Equal(5, group.RemainingSnapshotCount);
        Assert.True(group.HasMoreSnapshots);

        group.LoadMoreSnapshotsCommand.Execute(null);

        Assert.Equal(45, group.VisibleSnapshotCount);
        Assert.Equal(0, group.RemainingSnapshotCount);
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
        Assert.Equal(3, group.VisibleSnapshotCount);
        Assert.False(group.HasMoreSnapshots);
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
