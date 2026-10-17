#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task PersistSelectedSnapshotMetadata_NormalizesAndClearsTextFields()
    {
        using var temp = new TempDirectory();
        string dbPath = Path.Combine(temp.Path, "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "History Project", temp.Path);
        int snapshotId = repo.CreateSnapshot(projectId, 3, 128);
        var viewModel = CreateViewModel(dbPath);
        viewModel.SelectedTimelineItem = CreateTimelineItem(snapshotId);
        viewModel.SelectedSnapshotLabelDraft = "  Release candidate  ";
        viewModel.SelectedSnapshotNoteDraft = "  Validated restore  ";
        viewModel.SelectedSnapshotTagsDraft = " release, client, RELEASE, client ";

        await viewModel.PersistSelectedSnapshotMetadataAsync(clearTextMetadata: false);

        SnapshotHistoryMetadata saved = repo.GetSnapshotHistoryMetadata(snapshotId)!;
        Assert.Equal("Release candidate", saved.Label);
        Assert.Equal("Validated restore", saved.Note);
        Assert.Equal("release, client", saved.Tags);

        await viewModel.PersistSelectedSnapshotMetadataAsync(clearTextMetadata: true);

        SnapshotHistoryMetadata cleared = repo.GetSnapshotHistoryMetadata(snapshotId)!;
        Assert.Empty(cleared.Label);
        Assert.Empty(cleared.Note);
        Assert.Empty(cleared.Tags);
    }

    [Fact]
    public async Task PersistSelectedSnapshotMarker_TogglesProtectionAndKnownGood()
    {
        using var temp = new TempDirectory();
        string dbPath = Path.Combine(temp.Path, "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(repo, "Marker Project", temp.Path);
        int snapshotId = repo.CreateSnapshot(projectId, 3, 128);
        int backupId = repo.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            128,
            "Marker Project/backup.zip",
            temp.Path,
            "Primary");
        var viewModel = CreateViewModel(dbPath);
        viewModel.SelectedTimelineItem = CreateTimelineItem(snapshotId);

        await viewModel.PersistSelectedSnapshotMarkerAsync(toggleProtected: true);

        Assert.True(repo.GetSnapshotHistoryMetadata(snapshotId)!.IsProtected);
        Assert.True(repo.GetBackupById(backupId)!.IsProtected);

        viewModel.SelectedTimelineItem = CreateTimelineItem(snapshotId, isProtected: true);
        await viewModel.PersistSelectedSnapshotMarkerAsync(toggleProtected: false);

        SnapshotHistoryMetadata metadata = repo.GetSnapshotHistoryMetadata(snapshotId)!;
        Assert.True(metadata.IsProtected);
        Assert.True(metadata.IsKnownGood);
    }

    [Fact]
    public void RebuiltSelectedSnapshotRetainsUnfinishedMetadataDrafts()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(Path.Combine(temp.Path, "vaultsync.db"));
        viewModel.SelectedTimelineItem = CreateTimelineItem(7);
        viewModel.SelectedSnapshotLabelDraft = "Unfinished label";
        viewModel.SelectedSnapshotNoteDraft = "Unfinished note";
        viewModel.SelectedSnapshotTagsDraft = "draft, review";
        viewModel.SelectedTimelineItem = CreateTimelineItem(7);
        Assert.Equal("Unfinished label", viewModel.SelectedSnapshotLabelDraft);
        Assert.Equal("Unfinished note", viewModel.SelectedSnapshotNoteDraft);
        Assert.Equal("draft, review", viewModel.SelectedSnapshotTagsDraft);
    }

    [Fact]
    public void DifferentSnapshotDoesNotInheritThePreviousDraft()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(Path.Combine(temp.Path, "vaultsync.db"));
        viewModel.SelectedTimelineItem = CreateTimelineItem(7);
        viewModel.SelectedSnapshotLabelDraft = "Unfinished label";
        viewModel.SelectedTimelineItem = CreateTimelineItem(8);
        Assert.Empty(viewModel.SelectedSnapshotLabelDraft);
        viewModel.SelectedSnapshotLabelDraft = "Another unfinished label";
        viewModel.SelectedTimelineItem = null;
        Assert.Empty(viewModel.SelectedSnapshotLabelDraft);
    }

    [Fact]
    public void CleanDraftsFollowRefreshedMetadata()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(Path.Combine(temp.Path, "vaultsync.db"));
        viewModel.SelectedTimelineItem = CreateTimelineItem(7);
        viewModel.SelectedTimelineItem = new HistoryTimelineItemViewModel(new HistoryTimelineItemData
        {
            ProjectId = 1, SnapshotId = 7, MetadataLabel = "Updated elsewhere",
            MetadataNote = "New note", MetadataTags = "new"
        });
        Assert.Equal("Updated elsewhere", viewModel.SelectedSnapshotLabelDraft);
        Assert.Equal("New note", viewModel.SelectedSnapshotNoteDraft);
        Assert.Equal("new", viewModel.SelectedSnapshotTagsDraft);
    }

    [Fact]
    public void AnotherProjectCannotInheritDraftsFromAnIdenticalSnapshotNumber()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(Path.Combine(temp.Path, "vaultsync.db"));
        viewModel.SelectedTimelineItem = CreateTimelineItem(7);
        viewModel.SelectedSnapshotLabelDraft = "Unfinished label";
        viewModel.SelectedTimelineItem = new HistoryTimelineItemViewModel(new HistoryTimelineItemData
        {
            ProjectId = 2, SnapshotId = 7, MetadataLabel = "Different project"
        });
        Assert.Equal("Different project", viewModel.SelectedSnapshotLabelDraft);
    }

    [Fact]
    public void TransientListSelectionClearingDuringRefreshDoesNotDiscardDrafts()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(Path.Combine(temp.Path, "vaultsync.db"));
        HistoryTimelineItemViewModel previous = CreateTimelineItem(7);
        viewModel.TimelineItems.Add(previous);
        viewModel.SelectedTimelineItem = previous;
        viewModel.SelectedSnapshotLabelDraft = "Keep this unfinished label";
        viewModel.TimelineItems.CollectionChanged += (_, _) => viewModel.SelectedTimelineItem = null;
        HistoryTimelineItemViewModel refreshed = CreateTimelineItem(7);

        viewModel.ReplaceTimelinePageItems(new[] { refreshed });

        Assert.Same(refreshed, viewModel.SelectedTimelineItem);
        Assert.Equal("Keep this unfinished label", viewModel.SelectedSnapshotLabelDraft);
        viewModel.ReplaceTimelinePageItems(Array.Empty<HistoryTimelineItemViewModel>());
        Assert.Null(viewModel.SelectedTimelineItem);
        Assert.Empty(viewModel.SelectedSnapshotLabelDraft);
    }

    private static HistoryViewModel CreateViewModel(string dbPath)
    {
        var config = new AppConfig { DbPath = dbPath };
        var configStore = new TestConfigStore(config);
        return new HistoryViewModel(configStore, new TestRepositoryFactory(dbPath));
    }

    private static HistoryTimelineItemViewModel CreateTimelineItem(int snapshotId, bool isProtected = false) =>
        new(new HistoryTimelineItemData
        {
            ProjectId = 1,
            ProjectName = "History Project",
            SnapshotId = snapshotId,
            Title = "Snapshot",
            IsProtectedMarker = isProtected
        });

    private sealed class TestRepositoryFactory(string dbPath) : IRepositoryFactory
    {
        public SqliteRepository Create(AppConfig? config = null) => new(dbPath);

        public string ResolveDbPath(AppConfig? config = null) => dbPath;
    }

    private sealed class TestConfigStore(AppConfig config) : IAppConfigStore
    {
        public bool WasConfigMissingOnFirstLoad => false;

        public AppConfig GetSnapshot() => config;

        public AppConfig Load() => config;

        public void Save(AppConfig next) { }

        public Task SaveAsync(AppConfig next, CancellationToken ct = default) => Task.CompletedTask;

        public string GetDefaultDbPath() => config.DbPath!;

        public string ResolveDbPath(AppConfig? next = null) => next?.DbPath ?? config.DbPath!;
    }
}
