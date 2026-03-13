using System;
using System.Linq;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DestinationQuotaPlannerTests
{
    [Fact]
    public void BuildPlans_ComputesStoredBytesAndSuggestions_WhenOverWarningThreshold()
    {
        var destination = new BackupDestination
        {
            Alias = "Home",
            Path = @"\\nas\share\vaultsync",
            SoftQuotaBytes = 1_000,
            QuotaWarningPercent = 80
        };

        var planner = new DestinationQuotaPlanner();
        var backups = new[]
        {
            CreateBackup(1, destination.Path, 250, DateTime.UtcNow.AddDays(-3)),
            CreateBackup(2, destination.Path, 400, DateTime.UtcNow.AddDays(-2)),
            CreateBackup(3, destination.Path, 300, DateTime.UtcNow.AddDays(-1))
        };

        var plan = planner.BuildPlans(new[] { destination }, backups).Single();

        Assert.Equal(950, plan.StoredBytes);
        Assert.Equal(1_000, plan.SoftQuotaBytes);
        Assert.Equal(800, plan.WarningBytes);
        Assert.True(plan.ExceedsWarningThreshold);
        Assert.False(plan.ExceedsQuota);
        Assert.Equal(150, plan.SuggestedReclaimBytes);
        Assert.Equal(1, plan.SuggestedCandidateCount);
        Assert.True(plan.CanReachWarningThreshold);
    }

    [Fact]
    public void BuildPlans_TracksUnreachableCleanupTarget_WhenOnlyProtectedBackupsRemain()
    {
        var destination = new BackupDestination
        {
            Path = @"D:\Backups",
            SoftQuotaBytes = 1_000,
            QuotaWarningPercent = 85
        };

        var planner = new DestinationQuotaPlanner();
        var backups = new[]
        {
            CreateBackup(1, destination.Path, 600, DateTime.UtcNow.AddDays(-2), isProtected: true),
            CreateBackup(2, destination.Path, 500, DateTime.UtcNow.AddDays(-1), isProtected: true)
        };

        var plan = planner.BuildPlans(new[] { destination }, backups).Single();

        Assert.True(plan.ExceedsWarningThreshold);
        Assert.True(plan.ExceedsQuota);
        Assert.Equal(250, plan.SuggestedReclaimBytes);
        Assert.Equal(0, plan.SuggestedCandidateCount);
        Assert.False(plan.CanReachWarningThreshold);
    }

    [Fact]
    public void BuildPlans_LeavesQuotaFieldsEmpty_WhenSoftQuotaMissing()
    {
        var destination = new BackupDestination
        {
            Path = @"E:\Backups"
        };

        var planner = new DestinationQuotaPlanner();
        var plan = planner.BuildPlans(new[] { destination }, new[] { CreateBackup(1, destination.Path, 123, DateTime.UtcNow) }).Single();

        Assert.Equal(123, plan.StoredBytes);
        Assert.Null(plan.SoftQuotaBytes);
        Assert.Null(plan.WarningBytes);
        Assert.False(plan.ExceedsWarningThreshold);
        Assert.Equal(0, plan.SuggestedReclaimBytes);
    }

    private static Backup CreateBackup(int id, string destinationPath, long totalBytes, DateTime createdUtc, bool isProtected = false) =>
        new()
        {
            Id = id,
            ExternalId = $"backup-{id}",
            ProjectId = 1,
            SnapshotId = id,
            CreatedUtc = createdUtc,
            TotalBytes = totalBytes,
            DestinationPath = destinationPath,
            DestinationAlias = "Dest",
            IsProtected = isProtected
        };
}
