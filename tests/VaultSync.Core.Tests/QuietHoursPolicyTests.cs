using System;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class QuietHoursPolicyTests
{
    [Fact]
    public void Evaluate_Disabled_ReturnsNotInQuietHours()
    {
        var now = new DateTimeOffset(2026, 2, 14, 23, 30, 0, TimeSpan.Zero);
        QuietHoursDecision result = QuietHoursPolicy.Evaluate(false, "23:00", "07:00", now);
        Assert.False(result.IsEnabled);
        Assert.False(result.IsInQuietHours);
        Assert.Null(result.ResumeAtLocal);
    }

    [Fact]
    public void Evaluate_OvernightWindow_ReportsQuietHoursAndNextDayResume()
    {
        var now = new DateTimeOffset(2026, 2, 14, 23, 30, 0, TimeSpan.Zero);
        QuietHoursDecision result = QuietHoursPolicy.Evaluate(true, "23:00", "07:00", now);
        Assert.True(result.IsEnabled);
        Assert.True(result.IsInQuietHours);
        Assert.Equal(new TimeSpan(23, 0, 0), result.StartTime);
        Assert.Equal(new TimeSpan(7, 0, 0), result.EndTime);
        Assert.Equal(new DateTimeOffset(2026, 2, 15, 7, 0, 0, TimeSpan.Zero), result.ResumeAtLocal);
    }

    [Fact]
    public void Evaluate_OvernightWindow_BeforeEnd_ResumesSameDay()
    {
        var now = new DateTimeOffset(2026, 2, 15, 6, 30, 0, TimeSpan.Zero);
        QuietHoursDecision result = QuietHoursPolicy.Evaluate(true, "23:00", "07:00", now);
        Assert.True(result.IsInQuietHours);
        Assert.Equal(new DateTimeOffset(2026, 2, 15, 7, 0, 0, TimeSpan.Zero), result.ResumeAtLocal);
    }

    [Fact]
    public void Evaluate_DaytimeWindow_OutsideRange_NotQuiet()
    {
        var now = new DateTimeOffset(2026, 2, 14, 13, 0, 0, TimeSpan.Zero);
        QuietHoursDecision result = QuietHoursPolicy.Evaluate(true, "09:00", "12:00", now);
        Assert.True(result.IsEnabled);
        Assert.False(result.IsInQuietHours);
        Assert.Null(result.ResumeAtLocal);
    }

    [Fact]
    public void Evaluate_InvalidTimes_DisablesPolicySafely()
    {
        var now = new DateTimeOffset(2026, 2, 14, 13, 0, 0, TimeSpan.Zero);
        QuietHoursDecision result = QuietHoursPolicy.Evaluate(true, "invalid", "07:00", now);
        Assert.False(result.IsEnabled);
        Assert.False(result.IsInQuietHours);
    }
}
