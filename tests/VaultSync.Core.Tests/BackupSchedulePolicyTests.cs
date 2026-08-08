using System;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupSchedulePolicyTests
{
    [Fact]
    public void Project_Disabled_ReturnsManualOnlyWithoutNextRun()
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.FromHours(2));

        BackupScheduleProjection result = BackupSchedulePolicy.Project(
            false, 30, false, "23:00", "07:00", now);

        Assert.Equal(BackupScheduleStatus.ManualOnly, result.Status);
        Assert.Null(result.NextRunAtLocal);
        Assert.Null(result.DeferredUntilLocal);
    }

    [Fact]
    public void Project_UsesKnownTimerDeadlineWhenItIsStillFuture()
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.FromHours(2));
        DateTimeOffset due = now.AddMinutes(12);

        BackupScheduleProjection result = BackupSchedulePolicy.Project(
            true, 30, false, "23:00", "07:00", now, due);

        Assert.Equal(BackupScheduleStatus.Scheduled, result.Status);
        Assert.Equal(due, result.NextRunAtLocal);
    }

    [Fact]
    public void Project_FallsBackToOneIntervalWhenDeadlineIsStale()
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.FromHours(2));

        BackupScheduleProjection result = BackupSchedulePolicy.Project(
            true, 45, false, "23:00", "07:00", now, now.AddMinutes(-1));

        Assert.Equal(now.AddMinutes(45), result.NextRunAtLocal);
    }

    [Fact]
    public void Project_AdvancesTimerTicksPastQuietHours()
    {
        var now = new DateTimeOffset(2026, 8, 5, 22, 50, 0, TimeSpan.FromHours(2));

        BackupScheduleProjection result = BackupSchedulePolicy.Project(
            true, 30, true, "23:00", "07:00", now, now.AddMinutes(30));

        Assert.Equal(BackupScheduleStatus.QuietHours, result.Status);
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 7, 0, 0, TimeSpan.FromHours(2)), result.DeferredUntilLocal);
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 7, 20, 0, TimeSpan.FromHours(2)), result.NextRunAtLocal);
    }

    [Fact]
    public void Project_DoesNotDelayCandidateOutsideQuietHours()
    {
        var now = new DateTimeOffset(2026, 8, 5, 8, 0, 0, TimeSpan.FromHours(2));
        DateTimeOffset due = now.AddMinutes(30);

        BackupScheduleProjection result = BackupSchedulePolicy.Project(
            true, 30, true, "23:00", "07:00", now, due);

        Assert.Equal(BackupScheduleStatus.Scheduled, result.Status);
        Assert.Equal(due, result.NextRunAtLocal);
        Assert.Null(result.DeferredUntilLocal);
    }

    [Fact]
    public void ProjectUpcoming_ReturnsNoItemsForManualMode()
    {
        var now = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.FromHours(2));

        var result = BackupSchedulePolicy.ProjectUpcoming(
            false, 30, false, "23:00", "07:00", now, count: 4);

        Assert.Empty(result);
    }

    [Fact]
    public void ProjectUpcoming_PreviewsCadenceAndQuietHoursDeferral()
    {
        var now = new DateTimeOffset(2026, 8, 8, 22, 40, 0, TimeSpan.FromHours(2));

        var result = BackupSchedulePolicy.ProjectUpcoming(
            true,
            30,
            true,
            "23:00",
            "07:00",
            now,
            now.AddMinutes(10),
            count: 4);

        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal(new DateTimeOffset(2026, 8, 8, 22, 50, 0, TimeSpan.FromHours(2)), item.OccursAtLocal);
                Assert.False(item.WasDeferredByQuietHours);
            },
            item =>
            {
                Assert.Equal(new DateTimeOffset(2026, 8, 9, 7, 20, 0, TimeSpan.FromHours(2)), item.OccursAtLocal);
                Assert.True(item.WasDeferredByQuietHours);
            },
            item => Assert.Equal(new DateTimeOffset(2026, 8, 9, 7, 50, 0, TimeSpan.FromHours(2)), item.OccursAtLocal),
            item => Assert.Equal(new DateTimeOffset(2026, 8, 9, 8, 20, 0, TimeSpan.FromHours(2)), item.OccursAtLocal));
    }
}
