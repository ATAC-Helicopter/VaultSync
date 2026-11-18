using System;
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
    private static readonly string[] ReorderedItems = ["c", "a"];

    [Theory]
    [InlineData("project", true)]
    [InlineData("NAS", true)]
    [InlineData("backups/subfolder", true)]
    [InlineData("archive.zip", true)]
    [InlineData("Laptop", true)]
    [InlineData("missing", false)]
    public void BackupInventorySearchMatchesNamesAndPaths(string search, bool expected)
    {
        var snapshot = new BackupSnapshotItem
        {
            DestinationDisplay = "nas",
            DestinationRootPath = "/data/backups/subfolder",
            BackupRelativePath = "archive.zip",
            OriginMachineName = "laptop"
        };

        Assert.Equal(expected, BackupsViewModel.MatchesSnapshotInventory(snapshot, "Project Alpha", search, null, false));
    }

    [Fact]
    public void BackupInventoryRecencyIncludesCutoffAndKeepsOlderBackupsAvailable()
    {
        var cutoff = new DateTime(2026, 11, 1);
        var snapshot = new BackupSnapshotItem { Timestamp = cutoff, BackupRelativePath = "archive.zip" };

        Assert.True(BackupsViewModel.MatchesSnapshotInventory(snapshot, "Project", "archive", cutoff, false));
        snapshot.Timestamp = cutoff.AddTicks(-1);
        Assert.False(BackupsViewModel.MatchesSnapshotInventory(snapshot, "Project", "archive", cutoff, false));
        Assert.True(BackupsViewModel.MatchesSnapshotInventory(snapshot, "Project", "archive", null, false));
    }

    [Fact]
    public void BackupInventoryLocalFilterHidesOnlyImportedRecords()
    {
        var snapshot = new BackupSnapshotItem();
        Assert.True(BackupsViewModel.MatchesSnapshotInventory(snapshot, "Project", "", null, true));
        snapshot.IsImported = true;
        Assert.False(BackupsViewModel.MatchesSnapshotInventory(snapshot, "Project", "", null, true));
        Assert.True(BackupsViewModel.MatchesSnapshotInventory(snapshot, "Project", "", null, false));
    }

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
        NotifyCollectionChangedEventArgs change = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
    }

    [Fact]
    public void ReorderAndRemovalKeepSurvivorsWithoutReset()
    {
        var items = new ObservableCollection<string> { "a", "b", "c" };
        items.CollectionChanged += (_, args) =>
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);

        items.SyncWith(ReorderedItems);
        Assert.Equal(ReorderedItems, items);
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
        original.InventoryRows.CollectionChanged += (_, args) =>
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);

        vm.ReplaceSnapshotGroups(new[] { incoming });

        Assert.Same(original, Assert.Single(vm.SnapshotGroups));
        Assert.Equal(expanded, original.IsExpanded);
        Assert.Equal(40, original.VisibleRowCount);
        Assert.Equal(64, original.TotalSnapshotCount);
        Assert.Equal("Renamed", original.ProjectName);
        Assert.Equal("64 backups", original.Summary);
        Assert.Same(points[1], original.InventoryRows[0].SelectedSnapshot);
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

    [Fact]
    public void BackupProjectRefreshKeepsSelectedRowInstance()
    {
        var vm = new BackupsViewModel();
        var first = new ProjectBackupItem { Id = "first", Name = "First", SnapshotCount = 3 };
        var selected = new ProjectBackupItem { Id = "selected", Name = "Selected", SnapshotCount = 2 };
        vm.ProjectBackups.Add(first);
        vm.ProjectBackups.Add(selected);
        vm.SelectedProject = selected;
        vm.ProjectBackups.CollectionChanged += (_, args) =>
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);

        vm.ReplaceProjectBackups(new[]
        {
            new ProjectBackupItem { Id = "first", Name = "First", SnapshotCount = 3 },
            new ProjectBackupItem { Id = "selected", Name = "Selected", SnapshotCount = 1 }
        });

        Assert.Same(selected, vm.SelectedProject);
        Assert.Same(selected, vm.ProjectBackups[1]);
        Assert.Equal(1, selected.SnapshotCount);
    }

    [Fact]
    public void BackupProjectRefreshDoesNotSelectFirstRowWhenNothingWasSelected()
    {
        var vm = new BackupsViewModel();
        var first = new ProjectBackupItem { Id = "first", Name = "First" };
        vm.ProjectBackups.Add(first);

        vm.ReplaceProjectBackups(new[]
        {
            new ProjectBackupItem { Id = "first", Name = "First renamed" },
            new ProjectBackupItem { Id = "second", Name = "Second" }
        });

        Assert.Null(vm.SelectedProject);
        Assert.Same(first, vm.ProjectBackups[0]);
        Assert.Equal("First renamed", first.Name);
    }
}
