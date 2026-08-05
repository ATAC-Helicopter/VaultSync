using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class ScheduleViewModel : ViewModelBase
{
    private readonly SettingsViewModel _settings;
    private readonly Func<DateTimeOffset?> _timerDueProvider;
    private BackupScheduleProjection _projection;

    public ScheduleViewModel(
        SettingsViewModel settings,
        LocalizationService localizationService,
        Func<DateTimeOffset?> timerDueProvider)
    {
        _settings = settings;
        _timerDueProvider = timerDueProvider;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        localizationService.LanguageChanged += Refresh;
        UseManualModeCommand = new RelayCommand(_ => IsManualMode = true);
        UseAutomaticModeCommand = new RelayCommand(_ => IsAutomaticMode = true);
        Refresh();
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    public ICommand UseManualModeCommand
    {
        get;
    }
    public ICommand UseAutomaticModeCommand
    {
        get;
    }

    public bool IsManualMode
    {
        get => !_settings.EnableAutoBackups;
        set
        {
            if (value)
                _settings.EnableAutoBackups = false;
        }
    }

    public bool IsAutomaticMode
    {
        get => _settings.EnableAutoBackups;
        set
        {
            if (value)
                _settings.EnableAutoBackups = true;
        }
    }

    public int IntervalMinutes
    {
        get => _settings.AutoBackupIntervalMinutes;
        set => _settings.AutoBackupIntervalMinutes = value;
    }

    public bool EnableQuietHours
    {
        get => _settings.EnableQuietHours;
        set => _settings.EnableQuietHours = value;
    }

    public string QuietHoursStart
    {
        get => _settings.QuietHoursStart;
        set => _settings.QuietHoursStart = value;
    }

    public string QuietHoursEnd
    {
        get => _settings.QuietHoursEnd;
        set => _settings.QuietHoursEnd = value;
    }

    public bool HasNextRun => _projection.NextRunAtLocal.HasValue;
    public bool IsDelayed => _projection.Status == BackupScheduleStatus.QuietHours;
    public bool CanEditQuietHours => IsAutomaticMode && EnableQuietHours;

    public string ModeTitle => IsAutomaticMode
        ? L("Schedule.Mode.Automatic", "Automatic")
        : L("Schedule.Mode.Manual", "Manual only");

    public string NextRunText => _projection.NextRunAtLocal is { } nextRun
        ? nextRun.ToString("ddd, d MMM · HH:mm", CultureInfo.CurrentCulture)
        : L("Schedule.NextRun.None", "No automatic run scheduled");

    public string DelayExplanation => _projection.Status switch
    {
        BackupScheduleStatus.ManualOnly => L(
            "Schedule.Delay.Manual",
            "Automatic backups are off. You can still start a backup at any time."),
        BackupScheduleStatus.QuietHours when _projection.DeferredUntilLocal is { } resumeAt => string.Format(
            CultureInfo.CurrentCulture,
            L("Schedule.Delay.QuietHours", "The next timer falls inside quiet hours. Backup work resumes after {0}."),
            resumeAt.ToString("ddd, d MMM · HH:mm", CultureInfo.CurrentCulture)),
        _ => string.Format(
            CultureInfo.CurrentCulture,
            L("Schedule.Delay.Interval", "VaultSync checks for work every {0} minutes."),
            IntervalMinutes)
    };

    public string QuietHoursSummary => EnableQuietHours
        ? string.Format(
            CultureInfo.CurrentCulture,
            L("Schedule.QuietHours.Active", "Automatic backups pause from {0} to {1}."),
            QuietHoursStart,
            QuietHoursEnd)
        : L("Schedule.QuietHours.Off", "Quiet hours are off, so scheduled work can run at any time.");

    public void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset? timerDue = _timerDueProvider()?.ToLocalTime();
        _projection = BackupSchedulePolicy.Project(
            IsAutomaticMode,
            IntervalMinutes,
            EnableQuietHours,
            QuietHoursStart,
            QuietHoursEnd,
            now,
            timerDue);

        OnPropertiesChanged(
            nameof(IsManualMode),
            nameof(IsAutomaticMode),
            nameof(IntervalMinutes),
            nameof(EnableQuietHours),
            nameof(QuietHoursStart),
            nameof(QuietHoursEnd),
            nameof(ModeTitle),
            nameof(HasNextRun),
            nameof(IsDelayed),
            nameof(CanEditQuietHours),
            nameof(NextRunText),
            nameof(DelayExplanation),
            nameof(QuietHoursSummary));
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.EnableAutoBackups)
            or nameof(SettingsViewModel.AutoBackupIntervalMinutes)
            or nameof(SettingsViewModel.EnableQuietHours)
            or nameof(SettingsViewModel.QuietHoursStart)
            or nameof(SettingsViewModel.QuietHoursEnd))
        {
            Refresh();
        }
    }
}
