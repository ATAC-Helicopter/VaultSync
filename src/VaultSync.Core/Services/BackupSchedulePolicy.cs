using System;

namespace VaultSync.Core.Services;

public enum BackupScheduleStatus
{
    ManualOnly,
    Scheduled,
    QuietHours
}

public readonly record struct BackupScheduleProjection(
    BackupScheduleStatus Status,
    DateTimeOffset? NextRunAtLocal,
    DateTimeOffset? DeferredUntilLocal);

/// <summary>
/// Projects the next automatic-backup opportunity from the timer cadence and quiet-hours policy.
/// </summary>
public static class BackupSchedulePolicy
{
    public static BackupScheduleProjection Project(
        bool automaticBackupsEnabled,
        int intervalMinutes,
        bool quietHoursEnabled,
        string? quietHoursStart,
        string? quietHoursEnd,
        DateTimeOffset nowLocal,
        DateTimeOffset? timerDueAtLocal = null)
    {
        if (!automaticBackupsEnabled || intervalMinutes <= 0)
        {
            return new BackupScheduleProjection(
                BackupScheduleStatus.ManualOnly,
                NextRunAtLocal: null,
                DeferredUntilLocal: null);
        }

        TimeSpan interval = TimeSpan.FromMinutes(intervalMinutes);
        DateTimeOffset candidate = timerDueAtLocal is { } due && due > nowLocal
            ? due
            : nowLocal.Add(interval);

        QuietHoursDecision quietAtCandidate = QuietHoursPolicy.Evaluate(
            quietHoursEnabled,
            quietHoursStart,
            quietHoursEnd,
            candidate);
        if (!quietAtCandidate.IsInQuietHours || quietAtCandidate.ResumeAtLocal is not { } resumeAt)
        {
            return new BackupScheduleProjection(
                BackupScheduleStatus.Scheduled,
                candidate,
                DeferredUntilLocal: null);
        }

        while (candidate < resumeAt)
            candidate = candidate.Add(interval);

        return new BackupScheduleProjection(
            BackupScheduleStatus.QuietHours,
            candidate,
            resumeAt);
    }
}
