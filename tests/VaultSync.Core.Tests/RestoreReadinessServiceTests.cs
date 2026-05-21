using System;
using System.Collections.Generic;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RestoreReadinessServiceTests
{
    [Fact]
    public void BuildSummary_MarksProjectUnavailable_WhenNoBackupsExist()
    {
        var service = new RestoreReadinessService();
        var config = new AppConfig();
        Project[] projects =
        [
            new ProjectBuilder().Build()
        ];

        RestoreReadinessSummary summary = service.BuildSummary(
            projects,
            [],
            config,
            new BackupIndexScanSummary(),
            new Dictionary<string, bool>());

        Assert.Equal(1, summary.UnavailableCount);
        Assert.Equal(RestoreReadinessState.Unavailable, summary.Projects[0].State);
    }

    [Fact]
    public void BuildSummary_MarksProjectReady_WhenRecentBackupAndReachableDestination()
    {
        var destination = new BackupDestinationBuilder().Build();
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                UseAdvancedDestinations = true,
                Destinations = [destination]
            }
        };

        var project = new ProjectBuilder()
            .WithPreferredDestinationId(DestinationIdentityService.GetId(destination))
            .WithVerificationPolicy(ProjectVerificationPolicy.Always)
            .Build();

        var backup = new BackupBuilder()
            .CreatedUtc(DateTime.UtcNow.AddHours(-2))
            .AtDestination(destination.Path)
            .Build();

        RestoreReadinessSummary summary = new RestoreReadinessService().BuildSummary(
            [project],
            [backup],
            config,
            new BackupIndexScanSummary(),
            new Dictionary<string, bool> { [DestinationIdentityService.GetId(destination)] = true });

        Assert.Equal(1, summary.ReadyCount);
        Assert.Equal(RestoreReadinessState.Ready, summary.Projects[0].State);
    }

    [Fact]
    public void BuildSummary_MarksProjectRisk_WhenBackupIsStale_AndDestinationUnreachable()
    {
        var destination = new BackupDestinationBuilder()
            .WithPath("\\\\nas\\backups")
            .WithAlias("NAS")
            .Build();
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                UseAdvancedDestinations = true,
                Destinations = [destination]
            }
        };

        var project = new ProjectBuilder()
            .WithPreferredDestinationId(DestinationIdentityService.GetId(destination))
            .WithVerificationPolicy(ProjectVerificationPolicy.Manual)
            .Build();

        var backup = new BackupBuilder()
            .CreatedUtc(DateTime.UtcNow.AddDays(-5))
            .Manual()
            .AtDestination(destination.Path)
            .Build();

        RestoreReadinessSummary summary = new RestoreReadinessService().BuildSummary(
            [project],
            [backup],
            config,
            new BackupIndexScanSummary { ErrorCount = 1 },
            new Dictionary<string, bool> { [DestinationIdentityService.GetId(destination)] = false });

        Assert.Equal(1, summary.UnavailableCount);
        Assert.Equal(RestoreReadinessState.Unavailable, summary.Projects[0].State);
        Assert.Contains("unreachable", summary.Projects[0].Reason, StringComparison.OrdinalIgnoreCase);
    }
}
