using System;
using System.Collections.Generic;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DisasterRecoveryAdvisorServiceTests
{
    [Fact]
    public void BuildSummary_RequiresThreeCopiesTwoMediaAndExplicitOffsiteConfirmation()
    {
        var project = new Project { Id = 1, Name = "App", RootPath = "/Users/dev/App", Preset = "default" };
        var local = new Backup { Id = 10, ProjectId = 1, SnapshotId = 20, DestinationPath = "/Volumes/Archive", CreatedUtc = DateTime.UtcNow };
        var remote = new Backup { Id = 11, ProjectId = 1, SnapshotId = 21, DestinationPath = "smb://nas/backups", CreatedUtc = DateTime.UtcNow };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                UseAdvancedDestinations = true,
                Destinations =
                [
                    new BackupDestination { Path = "/Volumes/Archive", Alias = "Archive" },
                    new BackupDestination { Path = "smb://nas/backups", Alias = "Offsite NAS", IsOffsite = true }
                ]
            }
        };

        DisasterRecoverySummary result = new DisasterRecoveryAdvisorService().BuildSummary(
            [project],
            [local, remote],
            [
                new Snapshot { Id = 20, ProjectId = 1 },
                new Snapshot { Id = 21, ProjectId = 1 }
            ],
            new Dictionary<int, SnapshotHistoryMetadata>(),
            config);

        ProjectProtectionAssessment assessment = Assert.Single(result.Projects);
        Assert.Equal(3, assessment.CopyCount);
        Assert.True(assessment.MediaCount >= 2);
        Assert.True(assessment.HasOffsiteCopy);
        Assert.True(assessment.MeetsThreeTwoOne);
    }

    [Fact]
    public void BuildSummary_RecommendsUnprotectedReleaseMarkerBeforeGenericBaseline()
    {
        var project = new Project { Id = 1, Name = "App", RootPath = "/repo", Preset = "default" };
        var backup = new Backup { Id = 10, ProjectId = 1, SnapshotId = 20, DestinationPath = "/backup", CreatedUtc = DateTime.UtcNow };
        var metadata = new SnapshotHistoryMetadata { SnapshotId = 20, Label = "Release v1.8.4" };

        DisasterRecoverySummary result = new DisasterRecoveryAdvisorService().BuildSummary(
            [project],
            [backup],
            [new Snapshot { Id = 20, ProjectId = 1, FileCount = 5 }],
            new Dictionary<int, SnapshotHistoryMetadata> { [20] = metadata },
            new AppConfig());

        ProtectionRecommendation recommendation = Assert.Single(result.Projects).Recommendation!;
        Assert.Equal(ProtectionRecommendationKind.ReleaseMarker, recommendation.Kind);
        Assert.Equal(10, recommendation.BackupId);
    }
}
