using System;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryCoverageServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildSummary_UsesLatestBackupPerTrackedProject()
    {
        Project first = new ProjectBuilder().WithId(1).Build();
        Project second = new ProjectBuilder().WithId(2).Build();
        Backup[] backups =
        [
            new BackupBuilder().WithId(1).ForProject(first.Id).CreatedUtc(NowUtc.AddDays(-20)).Build(),
            new BackupBuilder().WithId(2).ForProject(first.Id).CreatedUtc(NowUtc.AddHours(-3)).Build(),
            new BackupBuilder().WithId(3).ForProject(second.Id).CreatedUtc(NowUtc.AddDays(-5)).Build()
        ];

        RecoveryCoverageSummary summary = new RecoveryCoverageService().BuildSummary(
            [first, second],
            backups,
            NowUtc);

        Assert.Equal(2, summary.ProjectCount);
        Assert.Equal(1, summary.Within24Hours);
        Assert.Equal(2, summary.Within7Days);
        Assert.Equal(2, summary.Within30Days);
        Assert.Equal(2, summary.Within90Days);
    }

    [Fact]
    public void BuildSummary_IgnoresUntrackedAndFutureBackups()
    {
        Project tracked = new ProjectBuilder().WithId(1).Build();
        Backup[] backups =
        [
            new BackupBuilder().WithId(1).ForProject(99).CreatedUtc(NowUtc.AddHours(-1)).Build(),
            new BackupBuilder().WithId(2).ForProject(tracked.Id).CreatedUtc(NowUtc.AddMinutes(5)).Build()
        ];

        RecoveryCoverageSummary summary = new RecoveryCoverageService().BuildSummary(
            [tracked],
            backups,
            NowUtc);

        Assert.Equal(1, summary.ProjectCount);
        Assert.Equal(0, summary.Within24Hours);
        Assert.Equal(0, summary.Within90Days);
    }

    [Fact]
    public void BuildSummary_CountsProjectsWithoutBackupsInDenominator()
    {
        Project first = new ProjectBuilder().WithId(1).Build();
        Project second = new ProjectBuilder().WithId(2).Build();
        Backup backup = new BackupBuilder()
            .ForProject(first.Id)
            .CreatedUtc(NowUtc.AddDays(-40))
            .Build();

        RecoveryCoverageSummary summary = new RecoveryCoverageService().BuildSummary(
            [first, second],
            [backup],
            NowUtc);

        Assert.Equal(2, summary.ProjectCount);
        Assert.Equal(0, summary.Within30Days);
        Assert.Equal(1, summary.Within90Days);
    }
}
