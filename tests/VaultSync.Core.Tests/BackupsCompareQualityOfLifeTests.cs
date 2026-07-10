using System;
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

        Assert.Same(nearest, viewModel.SelectedSnapshotB);
        Assert.True(viewModel.CanCompareSelectedSnapshots);
        Assert.Contains("Ready:", viewModel.CompareSelectionHint);
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

    private static BackupSnapshotItem Point(string id, int snapshotId, string projectId, DateTime timestamp) =>
        new()
        {
            Id = id,
            SnapshotId = snapshotId,
            ProjectId = projectId,
            Timestamp = timestamp
        };
}
