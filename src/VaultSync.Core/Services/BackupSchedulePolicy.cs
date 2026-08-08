using System;
using System.Collections.Generic;

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

public readonly record struct BackupScheduleOpportunity(
    DateTimeOffset OccursAtLocal,
    bool WasDeferredByQuietHours);

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

    /// <summary>
    /// Builds a short, deterministic preview of timer opportunities. The preview
    /// follows the same cadence and quiet-hours rules as the live timer; it does
    /// not promise that a backup will be written when no project changed.
    /// </summary>
    public static IReadOnlyList<BackupScheduleOpportunity> ProjectUpcoming(
        bool automaticBackupsEnabled,
        int intervalMinutes,
        bool quietHoursEnabled,
        string? quietHoursStart,
        string? quietHoursEnd,
        DateTimeOffset nowLocal,
        DateTimeOffset? timerDueAtLocal = null,
        int count = 4)
    {
        if (!automaticBackupsEnabled || intervalMinutes <= 0 || count <= 0)
            return [];

        int opportunityCount = Math.Clamp(count, 1, 24);
        var opportunities = new List<BackupScheduleOpportunity>(opportunityCount);
        DateTimeOffset? candidate = timerDueAtLocal;

        for (int index = 0; index < opportunityCount; index++)
        {
            BackupScheduleProjection projection = Project(
                true,
                intervalMinutes,
                quietHoursEnabled,
                quietHoursStart,
                quietHoursEnd,
                nowLocal,
                candidate);

            if (projection.NextRunAtLocal is not { } nextRun)
                break;

            opportunities.Add(new BackupScheduleOpportunity(
                nextRun,
                projection.Status == BackupScheduleStatus.QuietHours));
            candidate = nextRun.AddMinutes(intervalMinutes);
        }

        return opportunities;
    }
}
