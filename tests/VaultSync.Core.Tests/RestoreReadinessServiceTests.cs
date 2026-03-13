using System;
using System.Collections.Generic;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RestoreReadinessServiceTests
{
    [Fact]
    public void BuildSummary_MarksProjectUnavailable_WhenNoBackupsExist()
    {
        var service = new RestoreReadinessService();
        var config = new AppConfig();
        var projects = new[]
        {
            new Project { Id = 1, Name = "VaultSync", RootPath = "C:\\Repo", Preset = "default" }
        };

        var summary = service.BuildSummary(
            projects,
            Array.Empty<Backup>(),
            config,
            new BackupIndexScanSummary(),
            new Dictionary<string, bool>());

        Assert.Equal(1, summary.UnavailableCount);
        Assert.Equal(RestoreReadinessState.Unavailable, summary.Projects[0].State);
    }

    [Fact]
    public void BuildSummary_MarksProjectReady_WhenRecentBackupAndReachableDestination()
    {
        var destination = new BackupDestination { Path = "C:\\Backups", Alias = "Primary", Active = true };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                UseAdvancedDestinations = true,
                Destinations = new List<BackupDestination> { destination }
            }
        };

        var project = new Project
        {
            Id = 1,
            Name = "VaultSync",
            RootPath = "C:\\Repo",
            Preset = "default",
            PreferredDestinationId = DestinationIdentityService.GetId(destination),
            VerificationPolicy = ProjectVerificationPolicy.Always
        };

        var backup = new Backup
        {
            Id = 10,
            ProjectId = 1,
            SnapshotId = 11,
            ExternalId = "b1",
            CreatedUtc = DateTime.UtcNow.AddHours(-2),
            Type = "auto",
            Path = "vaultsync\\2026-03-13_12-00-00",
            DestinationPath = destination.Path
        };

        var summary = new RestoreReadinessService().BuildSummary(
            new[] { project },
            new[] { backup },
            config,
            new BackupIndexScanSummary(),
            new Dictionary<string, bool> { [DestinationIdentityService.GetId(destination)] = true });

        Assert.Equal(1, summary.ReadyCount);
        Assert.Equal(RestoreReadinessState.Ready, summary.Projects[0].State);
    }

    [Fact]
    public void BuildSummary_MarksProjectRisk_WhenBackupIsStale_AndDestinationUnreachable()
    {
        var destination = new BackupDestination { Path = "\\\\nas\\backups", Alias = "NAS", Active = true };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                UseAdvancedDestinations = true,
                Destinations = new List<BackupDestination> { destination }
            }
        };

        var project = new Project
        {
            Id = 1,
            Name = "VaultSync",
            RootPath = "C:\\Repo",
            Preset = "default",
            PreferredDestinationId = DestinationIdentityService.GetId(destination),
            VerificationPolicy = ProjectVerificationPolicy.Manual
        };

        var backup = new Backup
        {
            Id = 10,
            ProjectId = 1,
            SnapshotId = 11,
            ExternalId = "b1",
            CreatedUtc = DateTime.UtcNow.AddDays(-5),
            Type = "manual",
            Path = "vaultsync\\2026-03-08_12-00-00",
            DestinationPath = destination.Path
        };

        var summary = new RestoreReadinessService().BuildSummary(
            new[] { project },
            new[] { backup },
            config,
            new BackupIndexScanSummary { ErrorCount = 1 },
            new Dictionary<string, bool> { [DestinationIdentityService.GetId(destination)] = false });

        Assert.Equal(1, summary.UnavailableCount);
        Assert.Equal(RestoreReadinessState.Unavailable, summary.Projects[0].State);
        Assert.Contains("unreachable", summary.Projects[0].Reason, StringComparison.OrdinalIgnoreCase);
    }
}
