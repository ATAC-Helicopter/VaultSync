using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.UI.Infrastructure;
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

    [Fact]
    public void UnifiedDiffRowsExposeChangeKindsAndLineNumberGutters()
    {
        IReadOnlyList<DiffPreviewLineItem> lines = DiffPreviewLineItem.ParseUnified(
            "--- a/file.txt\n+++ b/file.txt\n@@ -4,2 +4,2 @@\n-old\n+new\n same");

        Assert.True(lines[0].IsHunk);
        Assert.Equal("4", lines[1].OldLineNumber);
        Assert.Equal(string.Empty, lines[1].NewLineNumber);
        Assert.True(lines[1].IsDeleted);
        Assert.Equal("4", lines[2].NewLineNumber);
        Assert.True(lines[2].IsAdded);
        Assert.Equal("5", lines[3].OldLineNumber);
        Assert.Equal("5", lines[3].NewLineNumber);
    }

    [Fact]
    public void PreviousSnapshotResolverUsesNearestEarlierPointFromSameProject()
    {
        var selected = Point("5", 15, "7", new DateTime(2026, 7, 10, 12, 0, 0));
        var expected = Point("4", 14, "7", new DateTime(2026, 7, 10, 11, 0, 0));
        var result = BackupsViewModel.FindPreviousSnapshotForDiff(
            selected,
            [
                Point("later", 16, "7", new DateTime(2026, 7, 10, 13, 0, 0)),
                Point("other-project", 99, "8", new DateTime(2026, 7, 10, 11, 30, 0, 0)),
                Point("duplicate", 14, "7", new DateTime(2026, 7, 10, 10, 59, 0, 0)),
                expected,
                Point("old", 13, "7", new DateTime(2026, 7, 10, 9, 0, 0))
            ],
            candidate => ReferenceEquals(candidate, expected));

        Assert.Same(expected, result);
    }

    [Fact]
    public void BackupDiffResultPopulatesChangedFileWorkspace()
    {
        var viewModel = new BackupsViewModel();
        var older = Point("1", 11, "7", new DateTime(2026, 7, 10, 11, 0, 0));
        var selected = Point("2", 12, "7", new DateTime(2026, 7, 10, 11, 0, 0));
        selected.DiffAdded = 1;
        var result = new SnapshotCompareResult(
            Added: 1,
            Modified: 0,
            Deleted: 0,
            Unchanged: 2,
            PreviousTotalBytes: 20,
            CurrentTotalBytes: 30,
            ChangedBytes: 10,
            Changes: [new SnapshotFileChange("new.txt", SnapshotFileChangeKind.Added, 0, 10)],
            TopChangedPaths: [new SnapshotDiffPathStat("(root)", 1, 10)],
            Signals: []);

        viewModel.ApplySnapshotComparisonResult(older, selected, result, preserveStoredSummaryWhenInventoryMissing: true);

        DiffPreviewFileItem file = Assert.Single(viewModel.DiffPreviewFiles);
        Assert.Equal("new.txt", file.Path);
        Assert.Equal(1, viewModel.DiffPreviewAdded);
        Assert.True(viewModel.IsDiffPreviewOpen);
    }

    [Fact]
    public async Task BackupDiffCommandComparesEqualTimestampSnapshotsInIdOrder()
    {
        int comparedOlder = 0;
        int comparedNewer = 0;
        var result = new SnapshotCompareResult(
            1, 0, 0, 0, 0, 4, 4,
            [new SnapshotFileChange("new.txt", SnapshotFileChangeKind.Added, 0, 4)],
            [],
            []);
        var viewModel = new BackupsViewModel(
            new TestConfigStore(),
            compareSnapshotsAsync: (older, newer, _) =>
            {
                comparedOlder = older;
                comparedNewer = newer;
                return Task.FromResult(result);
            },
            invokeOnUiAsync: action =>
            {
                action();
                return Task.CompletedTask;
            });
        DateTime timestamp = new(2026, 7, 10, 11, 0, 0);
        var older = Point("backup-11", 11, "7", timestamp);
        var newer = Point("backup-12", 12, "7", timestamp);
        viewModel.Snapshots.Add(older);
        viewModel.Snapshots.Add(newer);

        var command = Assert.IsType<AsyncRelayCommand>(viewModel.ShowSnapshotDiffPreviewCommand);
        await command.ExecuteAsync(newer);

        Assert.Equal(11, comparedOlder);
        Assert.Equal(12, comparedNewer);
        Assert.Single(viewModel.DiffPreviewFiles);
    }

    [Fact]
    public void ImportedBackupWithoutInventoryKeepsPersistedChangeSummary()
    {
        var viewModel = new BackupsViewModel();
        var older = Point("1", 11, "7", new DateTime(2026, 7, 10, 10, 0, 0));
        var imported = Point("2", 12, "7", new DateTime(2026, 7, 10, 11, 0, 0));
        imported.IsImported = true;
        imported.DiffAdded = 13;
        imported.DiffModified = 65;
        imported.DiffDeleted = 3;
        imported.DiffNetBytes = 295_410;
        var emptyInventory = new SnapshotCompareResult(
            Added: 0,
            Modified: 0,
            Deleted: 0,
            Unchanged: 0,
            PreviousTotalBytes: 0,
            CurrentTotalBytes: 0,
            ChangedBytes: 0,
            Changes: [],
            TopChangedPaths: [],
            Signals: []);

        viewModel.ApplySnapshotComparisonResult(
            older,
            imported,
            emptyInventory,
            preserveStoredSummaryWhenInventoryMissing: true);

        Assert.Equal(13, viewModel.DiffPreviewAdded);
        Assert.Equal(65, viewModel.DiffPreviewModified);
        Assert.Equal(3, viewModel.DiffPreviewDeleted);
        Assert.Empty(viewModel.DiffPreviewFiles);
        Assert.Contains("imported without file details", viewModel.DiffPreviewEmptyMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialDatabaseInventoryDoesNotReplacePersistedSummaryWithFalseAdds()
    {
        var viewModel = new BackupsViewModel();
        var older = Point("1", 11, "7", new DateTime(2026, 7, 10, 10, 0, 0));
        var imported = Point("2", 12, "7", new DateTime(2026, 7, 10, 11, 0, 0));
        imported.IsImported = true;
        imported.DiffAdded = 13;
        imported.DiffModified = 65;
        imported.DiffDeleted = 3;
        var oneSidedInventory = new SnapshotCompareResult(
            Added: 497,
            Modified: 0,
            Deleted: 0,
            Unchanged: 0,
            PreviousTotalBytes: 0,
            CurrentTotalBytes: 1_000,
            ChangedBytes: 1_000,
            Changes: [new SnapshotFileChange("misleading.txt", SnapshotFileChangeKind.Added, 0, 1_000)],
            TopChangedPaths: [],
            Signals: []);

        viewModel.ApplySnapshotComparisonResult(
            older,
            imported,
            oneSidedInventory,
            preserveStoredSummaryWhenInventoryMissing: true);

        Assert.Equal(13, viewModel.DiffPreviewAdded);
        Assert.Equal(65, viewModel.DiffPreviewModified);
        Assert.Equal(3, viewModel.DiffPreviewDeleted);
        Assert.Empty(viewModel.DiffPreviewFiles);
    }

    [Fact]
    public void ReachableBackupContentsRecoverMissingDatabaseInventory()
    {
        string root = Path.Combine(Path.GetTempPath(), "vaultsync-diff-fallback-" + Guid.NewGuid().ToString("N"));
        string olderRoot = Path.Combine(root, "older");
        string newerRoot = Path.Combine(root, "newer");
        Directory.CreateDirectory(olderRoot);
        Directory.CreateDirectory(newerRoot);
        try
        {
            File.WriteAllText(Path.Combine(olderRoot, "same.txt"), "same");
            File.WriteAllText(Path.Combine(newerRoot, "same.txt"), "same");
            DateTime stableTimestamp = new(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(olderRoot, "same.txt"), stableTimestamp);
            File.SetLastWriteTimeUtc(Path.Combine(newerRoot, "same.txt"), stableTimestamp);
            File.WriteAllText(Path.Combine(newerRoot, "added.txt"), "new");
            var emptyDatabaseResult = new SnapshotCompareResult(
                0, 0, 0, 0, 0, 0, 0, [], [], []);

            SnapshotCompareResult result = BackupsViewModel.CompareBackupContentInventories(
                olderRoot,
                newerRoot,
                emptyDatabaseResult);

            Assert.Equal(1, result.Added);
            Assert.Contains(result.Changes, change =>
                change.Path == "added.txt" && change.Kind == SnapshotFileChangeKind.Added);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BackupSnapshotItem Point(string id, int snapshotId, string projectId, DateTime timestamp) =>
        new()
        {
            Id = id,
            SnapshotId = snapshotId,
            ProjectId = projectId,
            Timestamp = timestamp
        };

    private sealed class TestConfigStore : IAppConfigStore
    {
        private readonly AppConfig _config = new();

        public bool WasConfigMissingOnFirstLoad => false;
        public AppConfig GetSnapshot() => _config;
        public AppConfig Load() => _config;
        public void Save(AppConfig config) { }
        public Task SaveAsync(AppConfig config, CancellationToken ct = default) => Task.CompletedTask;
        public string GetDefaultDbPath() => string.Empty;
        public string ResolveDbPath(AppConfig config = null) => string.Empty;
    }

}
