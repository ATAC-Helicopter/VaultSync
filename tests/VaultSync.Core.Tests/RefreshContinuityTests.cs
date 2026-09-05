using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RefreshContinuityTests
{
    [Fact]
    public void InsertionDoesNotReplaceSurvivingItems()
    {
        var first = new object();
        var second = new object();
        var added = new object();
        var items = new ObservableCollection<object> { first, second };
        var events = new List<NotifyCollectionChangedEventArgs>();
        items.CollectionChanged += (_, args) => events.Add(args);

        items.SyncWith(new[] { added, first, second });

        Assert.Equal(new[] { added, first, second }, items);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, events[0].Action);
    }

    [Fact]
    public void ReorderAndRemovalKeepSurvivorsWithoutReset()
    {
        var items = new ObservableCollection<string> { "a", "b", "c" };
        items.CollectionChanged += (_, args) =>
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);

        items.SyncWith(new[] { "c", "a" });
        Assert.Equal(new[] { "c", "a" }, items);
        items.SyncWith(System.Array.Empty<string>());
        Assert.Empty(items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackupRefreshPreservesExpansionAndLoadedPage(bool expanded)
    {
        var vm = new BackupsViewModel();
        var points = Enumerable.Range(1, 65)
            .Select(id => new BackupSnapshotItem { Id = id.ToString(), ProjectId = "project" }).ToList();
        var original = new SnapshotProjectGroup { ProjectId = "project", IsExpanded = expanded };
        original.SetSnapshots(points);
        original.LoadMoreSnapshots();
        vm.SnapshotGroups.Add(original);
        var incoming = new SnapshotProjectGroup
        {
            ProjectId = "project", IsExpanded = !expanded, ProjectName = "Renamed", Summary = "64 backups"
        };
        incoming.SetSnapshots(points.Skip(1));
        original.Snapshots.CollectionChanged += (_, args) =>
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);

        vm.ReplaceSnapshotGroups(new[] { incoming });

        Assert.Same(original, Assert.Single(vm.SnapshotGroups));
        Assert.Equal(expanded, original.IsExpanded);
        Assert.Equal(40, original.VisibleSnapshotCount);
        Assert.Equal(64, original.TotalSnapshotCount);
        Assert.Equal("Renamed", original.ProjectName);
        Assert.Equal("64 backups", original.Summary);
        Assert.Same(points[1], original.Snapshots[0]);
    }

    [Fact]
    public void RemovingLastBackupRemovesOnlyItsProjectGroup()
    {
        var vm = new BackupsViewModel();
        var removed = new SnapshotProjectGroup { ProjectId = "removed" };
        var survivor = new SnapshotProjectGroup { ProjectId = "survivor", IsExpanded = true };
        vm.SnapshotGroups.Add(removed);
        vm.SnapshotGroups.Add(survivor);

        vm.ReplaceSnapshotGroups(new[] { new SnapshotProjectGroup { ProjectId = "survivor" } });

        Assert.Same(survivor, Assert.Single(vm.SnapshotGroups));
        Assert.True(survivor.IsExpanded);
    }
}
