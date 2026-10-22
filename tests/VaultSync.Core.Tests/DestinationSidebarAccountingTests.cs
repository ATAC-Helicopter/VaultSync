#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DestinationSidebarAccountingTests
{
    [Fact]
    public void CompleteAccountingRetainsOlderProtectedBackupsAndSeparateDestinations()
    {
        using var temp = new TempDirectory();
        var repo = TestRepository.Create(Path.Combine(temp.Path, "accounting.db"));
        int projectId = TestRepository.AddProject(repo, "Project", Path.Combine(temp.Path, "source"));
        var destination = new BackupDestination { Path = Path.Combine(temp.Path, "destination"), SoftQuotaBytes = 700, QuotaWarningPercent = 80 };
        for (int index = 0; index < 8; index++)
        {
            int snapshotId = repo.CreateSnapshot(projectId, 1, 100);
            repo.CreateBackup(projectId, snapshotId, "manual", 100, $"backup-{index}", destination.Path, "NAS", isProtected: index < 3);
        }
        int otherSnapshotId = repo.CreateSnapshot(projectId, 1, 9_000);
        repo.CreateBackup(projectId, otherSnapshotId, "manual", 9_000, "other", Path.Combine(temp.Path, "other"), "Other");

        var complete = DestinationQuotaPlanner.BuildPlans([destination], repo.GetBackupsForQuotaAccounting()).Single();
        var displayed = DestinationQuotaPlanner.BuildPlans([destination], repo.GetRecentBackupsByProject(5)).Single();
        Assert.Equal(800, complete.StoredBytes);
        Assert.True(displayed.StoredBytes < complete.StoredBytes);
        Assert.True(complete.ExceedsQuota);
        Assert.Equal(3, complete.SuggestedCandidateCount);
        Assert.True(complete.CanReachWarningThreshold);
        Assert.Equal(DestinationQuotaPlanner.BuildPlans([destination], repo.GetAllBackups()).Single(), complete);
    }

    [Fact]
    public void PartialPageAndEmptyPageCannotReplaceCompleteSidebarTotal()
    {
        using var temp = new TempDirectory();
        string dbPath = Path.Combine(temp.Path, "sidebar.db");
        var repo = TestRepository.Create(dbPath);
        var destination = new BackupDestination { Path = Path.Combine(temp.Path, "destination") };
        var config = new AppConfig { DbPath = dbPath };
        config.Backups.Destinations = [destination];
        var vm = new BackupsViewModel(new TestConfigStore(config));
        var item = new DestinationStatusItem { Id = DestinationIdentityService.GetId(destination), Path = destination.Path };
        vm.DestinationStatuses.Add(item);
        var plans = DestinationQuotaPlanner.BuildPlans([destination], [new Backup { TotalBytes = 800, DestinationPath = destination.Path }]);
        vm.UpdateDestinationQuotaPlans(plans);
        string expected = item.StoredBytesText;
        Assert.NotEmpty(expected);
        string expectedValue = item.StoredBytesValueText;
        Assert.NotEmpty(expectedValue);

        vm.LoadFromBackups([], [new Backup { Id = 1, TotalBytes = 100, DestinationPath = destination.Path }]);
        Assert.Equal(expected, item.StoredBytesText);
        Assert.Equal(expectedValue, item.StoredBytesValueText);
        vm.LoadFromBackups([], []);
        Assert.Equal(expected, item.StoredBytesText);
        Assert.Equal(expectedValue, item.StoredBytesValueText);
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
