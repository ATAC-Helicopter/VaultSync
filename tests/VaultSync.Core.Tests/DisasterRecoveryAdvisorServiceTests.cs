using System;
using System.Collections.Generic;
using System.IO;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DisasterRecoveryAdvisorServiceTests
{
    [Fact]
    public void BuildSummary_DoesNotTreatTwoFoldersOnOneLocalFilesystemAsTwoMedia()
    {
        using var temp = new TempDirectory();
        string projectRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        string localContent = Directory.CreateDirectory(Path.Combine(temp.Path, "local-copy")).FullName;
        string remoteContent = Directory.CreateDirectory(Path.Combine(temp.Path, "remote-copy")).FullName;
        var project = new Project { Id = 1, Name = "App", RootPath = projectRoot, Preset = "default" };
        string localRoot = localContent;
        string remoteRoot = remoteContent;
        Directory.CreateDirectory(Path.Combine(localRoot, "point"));
        Directory.CreateDirectory(Path.Combine(remoteRoot, "point"));
        var local = new Backup { Id = 10, ProjectId = 1, SnapshotId = 20, Path = "point", DestinationPath = localRoot, DestinationAlias = "Archive", CreatedUtc = DateTime.UtcNow };
        var remote = new Backup { Id = 11, ProjectId = 1, SnapshotId = 21, Path = "point", DestinationPath = remoteRoot, DestinationAlias = "Offsite NAS", CreatedUtc = DateTime.UtcNow };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                UseAdvancedDestinations = true,
                Destinations =
                [
                    new BackupDestination { Path = localRoot, Alias = "Archive" },
                    new BackupDestination { Path = remoteRoot, Alias = "Offsite NAS", IsOffsite = true }
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
        Assert.Equal(1, assessment.MediaCount);
        Assert.True(assessment.HasOffsiteCopy);
        Assert.False(assessment.MeetsThreeTwoOne);
    }

    [Fact]
    public void BuildSummary_DoesNotCountMissingRecordedCopiesAsThreeTwoOneProtection()
    {
        using var temp = new TempDirectory();
        string projectRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var project = new Project { Id = 1, Name = "App", RootPath = projectRoot, Preset = "default" };
        var missingLocal = new Backup
        {
            Id = 10,
            ProjectId = 1,
            SnapshotId = 20,
            Path = "App/missing-local",
            DestinationPath = Path.Combine(temp.Path, "offline-local"),
            CreatedUtc = DateTime.UtcNow
        };
        var missingOffsite = new Backup
        {
            Id = 11,
            ProjectId = 1,
            SnapshotId = 21,
            Path = "App/missing-offsite",
            DestinationPath = Path.Combine(temp.Path, "offline-offsite"),
            DestinationAlias = "Offsite",
            CreatedUtc = DateTime.UtcNow
        };
        var config = new AppConfig
        {
            Backups = new BackupsConfig
            {
                Destinations =
                [
                    new BackupDestination
                    {
                        Path = missingOffsite.DestinationPath,
                        Alias = "Offsite",
                        IsOffsite = true
                    }
                ]
            }
        };

        ProjectProtectionAssessment assessment = Assert.Single(
            new DisasterRecoveryAdvisorService().BuildSummary(
                [project],
                [missingLocal, missingOffsite],
                [
                    new Snapshot { Id = 20, ProjectId = 1 },
                    new Snapshot { Id = 21, ProjectId = 1 }
                ],
                new Dictionary<int, SnapshotHistoryMetadata>(),
                config).Projects);

        Assert.Equal(1, assessment.CopyCount);
        Assert.Equal(1, assessment.MediaCount);
        Assert.False(assessment.HasOffsiteCopy);
        Assert.False(assessment.MeetsThreeTwoOne);
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
