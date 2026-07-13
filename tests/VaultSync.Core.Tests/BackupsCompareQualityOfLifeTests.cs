using System;
using VaultSync.Core.Models;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupsCompareQualityOfLifeTests
{
    [Fact]
    public void SelectingFirstRestorePointSuggestsNearestPointFromSameProject()
    {
        var viewModel = new BackupsViewModel();
        var selected = Point("1", 11, "7", new DateTime(2026, 7, 10, 10, 0, 0));
        var nearest = Point("2", 12, "7", new DateTime(2026, 7, 10, 9, 0, 0));
        var otherProject = Point("3", 13, "8", new DateTime(2026, 7, 10, 9, 30, 0));
        viewModel.Snapshots.Add(selected);
        viewModel.Snapshots.Add(nearest);
        viewModel.Snapshots.Add(otherProject);

        viewModel.SelectedSnapshotA = selected;

        Assert.Same(nearest, viewModel.SelectedSnapshotA);
        Assert.Same(selected, viewModel.SelectedSnapshotB);
        Assert.True(viewModel.CanCompareSelectedSnapshots);
        Assert.Contains("Ready to compare:", viewModel.CompareSelectionHint);
    }

    [Fact]
    public void CompareHintExplainsCrossProjectSelection()
    {
        var viewModel = new BackupsViewModel
        {
            SelectedSnapshotA = Point("1", 11, "7", new DateTime(2026, 7, 10, 10, 0, 0)),
            SelectedSnapshotB = Point("2", 12, "8", new DateTime(2026, 7, 10, 11, 0, 0))
        };

        Assert.False(viewModel.CanCompareSelectedSnapshots);
        Assert.Contains("same project", viewModel.CompareSelectionHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompareRejectsTwoBackupEntriesForSameSnapshot()
    {
        var viewModel = new BackupsViewModel
        {
            SelectedSnapshotA = Point("backup-1", 11, "7", new DateTime(2026, 7, 10, 10, 0, 0)),
            SelectedSnapshotB = Point("backup-2", 11, "7", new DateTime(2026, 7, 10, 11, 0, 0))
        };

        Assert.False(viewModel.CanCompareSelectedSnapshots);
        Assert.Contains("different restore points", viewModel.CompareSelectionHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangedFileNavigationMovesBetweenVisibleResults()
    {
        var viewModel = new BackupsViewModel();
        var first = new DiffPreviewFileItem(new SnapshotFileChange("first.txt", SnapshotFileChangeKind.Modified, 1, 2));
        var second = new DiffPreviewFileItem(new SnapshotFileChange("second.txt", SnapshotFileChangeKind.Modified, 2, 3));
        viewModel.DiffPreviewFiles.Add(first);
        viewModel.DiffPreviewFiles.Add(second);
        viewModel.SelectedDiffPreviewFile = first;

        Assert.False(viewModel.SelectPreviousDiffFileCommand.CanExecute(null));
        Assert.True(viewModel.SelectNextDiffFileCommand.CanExecute(null));

        viewModel.SelectNextDiffFileCommand.Execute(null);

        Assert.Same(second, viewModel.SelectedDiffPreviewFile);
        Assert.True(viewModel.SelectPreviousDiffFileCommand.CanExecute(null));
        Assert.False(viewModel.SelectNextDiffFileCommand.CanExecute(null));
    }

    [Fact]
    public void ClearChangedFileFiltersRestoresDefaultFilter()
    {
        var viewModel = new BackupsViewModel
        {
            DiffFileSearchText = "config"
        };
        viewModel.SelectedDiffFileKindFilter = viewModel.DiffFileKindFilters[1];

        viewModel.ClearDiffFileFiltersCommand.Execute(null);

        Assert.Equal(string.Empty, viewModel.DiffFileSearchText);
        Assert.Same(viewModel.DiffFileKindFilters[0], viewModel.SelectedDiffFileKindFilter);
    }

    private static BackupSnapshotItem Point(string id, int snapshotId, string projectId, DateTime timestamp) =>
        new()
        {
            Id = id,
            SnapshotId = snapshotId,
            ProjectId = projectId,
            Timestamp = timestamp
        };
}
