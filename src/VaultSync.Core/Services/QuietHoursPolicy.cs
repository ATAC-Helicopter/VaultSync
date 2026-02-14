using System;
using System.Globalization;

namespace VaultSync.Core.Services;

/// <summary>
/// Shared quiet-hours scheduling policy for automatic backup execution.
/// </summary>
public static class QuietHoursPolicy
{
    private static readonly string[] SupportedFormats = ["HH:mm", @"hh\:mm"];

    public static QuietHoursDecision Evaluate(
        bool enabled,
        string? start,
        string? end,
        DateTimeOffset nowLocal)
    {
        if (!enabled)
            return QuietHoursDecision.Disabled;

        if (!TryParseTimeOfDay(start, out var startTime) || !TryParseTimeOfDay(end, out var endTime))
            return QuietHoursDecision.Disabled;

        if (startTime == endTime)
            return QuietHoursDecision.Disabled;

        var wrapsMidnight = endTime <= startTime;
        var nowTime = nowLocal.TimeOfDay;

        var inQuietHours = wrapsMidnight
            ? nowTime >= startTime || nowTime < endTime
            : nowTime >= startTime && nowTime < endTime;

        DateTimeOffset? resumeAtLocal = null;
        if (inQuietHours)
        {
            var todayStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
            resumeAtLocal = wrapsMidnight && nowTime >= startTime
                ? todayStart.AddDays(1).Add(endTime)
                : todayStart.Add(endTime);
        }

        return new QuietHoursDecision(
            IsEnabled: true,
            IsInQuietHours: inQuietHours,
            StartTime: startTime,
            EndTime: endTime,
            ResumeAtLocal: resumeAtLocal);
    }

    public static bool TryParseTimeOfDay(string? value, out TimeSpan timeOfDay)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            timeOfDay = default;
            return false;
        }

        foreach (var format in SupportedFormats)
        {
            if (format.Contains(@"\:"))
            {
                if (TimeSpan.TryParseExact(value.Trim(), format, CultureInfo.InvariantCulture, out timeOfDay))
                    return true;
            }
            else if (DateTime.TryParseExact(
                value.Trim(),
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt))
            {
                timeOfDay = dt.TimeOfDay;
                return true;
            }
        }

        timeOfDay = default;
        return false;
    }
}

public readonly record struct QuietHoursDecision(
    bool IsEnabled,
    bool IsInQuietHours,
    TimeSpan StartTime,
    TimeSpan EndTime,
    DateTimeOffset? ResumeAtLocal)
{
    public static QuietHoursDecision Disabled => new(
        IsEnabled: false,
        IsInQuietHours: false,
        StartTime: default,
        EndTime: default,
        ResumeAtLocal: null);
}
