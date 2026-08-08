using System;
using VaultSync.Core.Config;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ScheduleViewModelTests
{
    [Fact]
    public void QuickPolicyEditsUseTheSettingsSourceOfTruth()
    {
        using var configScope = new TestAppConfigScope();
        AppConfigStore.Save(new AppConfig
        {
            Backups =
            {
                EnableAutoBackups = true,
                IntervalMinutes = 30,
                BackupRoot = configScope.ConfigDirectory,
                EnableQuietHours = false,
                QuietHoursStart = "23:00",
                QuietHoursEnd = "07:00"
            }
        });

        var localization = new LocalizationService();
        var settings = new SettingsViewModel(localization);
        var schedule = new ScheduleViewModel(
            settings,
            localization,
            new ScheduleViewModelDependencies(
                () => DateTimeOffset.Now.AddMinutes(30),
                () =>
                [
                    new ScheduleProjectSnapshot(
                        1,
                        "Client Portal",
                        AutomaticEnabled: true,
                        LastBackupUtc: DateTime.UtcNow.AddHours(-1),
                        LastAutomaticBackupUtc: DateTime.UtcNow.AddHours(-2))
                ],
                () => PowerState.PluggedIn,
                () => { },
                () => { },
                () => { }));

        schedule.UseManualModeCommand.Execute(null);
        Assert.False(settings.EnableAutoBackups);
        Assert.True(schedule.IsManualMode);

        schedule.UseAutomaticModeCommand.Execute(null);
        schedule.IntervalMinutes = 45;
        schedule.EnableQuietHours = true;
        schedule.QuietHoursStart = "22:30";
        schedule.QuietHoursEnd = "06:30";

        Assert.True(settings.EnableAutoBackups);
        Assert.Equal(45, settings.AutoBackupIntervalMinutes);
        Assert.True(settings.EnableQuietHours);
        Assert.Equal("22:30", settings.QuietHoursStart);
        Assert.Equal("06:30", settings.QuietHoursEnd);
    }
}
