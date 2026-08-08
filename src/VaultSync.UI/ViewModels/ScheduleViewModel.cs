using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public readonly record struct ScheduleProjectSnapshot(
    int ProjectId,
    string Name,
    bool AutomaticEnabled,
    DateTime? LastBackupUtc,
    DateTime? LastAutomaticBackupUtc,
    string GroupName = "");

public sealed class ScheduleOpportunityViewModel
{
    public required string Sequence
    {
        get; init;
    }
    public required string Day
    {
        get; init;
    }
    public required string Time
    {
        get; init;
    }
    public required string Status
    {
        get; init;
    }
}

public sealed class ScheduleProjectRowViewModel
{
    public required string GroupName
    {
        get; init;
    }
    public required bool ShowGroupHeader
    {
        get; init;
    }
    public required string Name
    {
        get; init;
    }
    public required string AutomaticStatus
    {
        get; init;
    }
    public required string LastBackup
    {
        get; init;
    }
}

public sealed class ScheduleViewModel : ViewModelBase
{
    private readonly SettingsViewModel _settings;
    private readonly Func<DateTimeOffset?> _timerDueProvider;
    private readonly Func<IReadOnlyList<ScheduleProjectSnapshot>> _projectSnapshotProvider;
    private readonly Func<PowerState> _powerStateProvider;
    private BackupScheduleProjection _projection;

    public ScheduleViewModel(
        SettingsViewModel settings,
        LocalizationService localizationService,
        Func<DateTimeOffset?> timerDueProvider,
        Func<IReadOnlyList<ScheduleProjectSnapshot>> projectSnapshotProvider,
        Func<PowerState> powerStateProvider,
        Action openProjects,
        Action openBackups,
        Action openSettings)
    {
        _settings = settings;
        _timerDueProvider = timerDueProvider;
        _projectSnapshotProvider = projectSnapshotProvider;
        _powerStateProvider = powerStateProvider;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        localizationService.LanguageChanged += Refresh;
        UseManualModeCommand = new RelayCommand(_ => IsManualMode = true);
        UseAutomaticModeCommand = new RelayCommand(_ => IsAutomaticMode = true);
        OpenProjectsCommand = new RelayCommand(_ => openProjects());
        OpenBackupsCommand = new RelayCommand(_ => openBackups());
        OpenSettingsCommand = new RelayCommand(_ => openSettings());
        RefreshCommand = new RelayCommand(_ => Refresh());
        Refresh();
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string Lf(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);

    public ICommand UseManualModeCommand
    {
        get;
    }
    public ICommand UseAutomaticModeCommand
    {
        get;
    }
    public ICommand OpenProjectsCommand
    {
        get;
    }
    public ICommand OpenBackupsCommand
    {
        get;
    }
    public ICommand OpenSettingsCommand
    {
        get;
    }
    public ICommand RefreshCommand
    {
        get;
    }

    public ObservableCollection<ScheduleOpportunityViewModel> UpcomingRuns { get; } = [];
    public ObservableCollection<ScheduleProjectRowViewModel> ProjectCoverage { get; } = [];

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

    public bool HasUpcomingRuns => UpcomingRuns.Count > 0;
    public bool HasProjects => RegisteredProjectCount > 0;
    private bool IsDelayed => _projection.Status == BackupScheduleStatus.QuietHours;
    public bool CanEditQuietHours => IsAutomaticMode && EnableQuietHours;
    public int RegisteredProjectCount
    {
        get; private set;
    }
    public int IncludedProjectCount
    {
        get; private set;
    }
    public int PausedProjectCount
    {
        get; private set;
    }

    public string NextRunText => _projection.NextRunAtLocal is { } nextRun
        ? nextRun.ToString("ddd, d MMM · HH:mm", CultureInfo.CurrentCulture)
        : L("Schedule.NextRun.None", "No automatic run scheduled");

    public string DelayExplanation => _projection.Status switch
    {
        BackupScheduleStatus.ManualOnly => L(
            "Schedule.Delay.Manual",
            "Automatic backups are off. You can still start a backup at any time."),
        BackupScheduleStatus.QuietHours when _projection.DeferredUntilLocal is { } resumeAt => Lf(
            "Schedule.Delay.QuietHours",
            "The next timer falls inside quiet hours. Backup work resumes after {0}.",
            resumeAt.ToString("ddd, d MMM · HH:mm", CultureInfo.CurrentCulture)),
        _ => Lf(
            "Schedule.Delay.Interval",
            "VaultSync checks for work every {0} minutes.",
            IntervalMinutes)
    };

    public string QuietHoursSummary => EnableQuietHours
        ? Lf(
            "Schedule.QuietHours.Active",
            "Automatic backups pause from {0} to {1}.",
            QuietHoursStart,
            QuietHoursEnd)
        : L("Schedule.QuietHours.Off", "Quiet hours are off, so scheduled work can run at any time.");

    public string CoverageValue => $"{IncludedProjectCount}/{RegisteredProjectCount}";

    public string CoverageSummary => RegisteredProjectCount == 0
        ? L("Schedule.Coverage.Empty", "Register a project to build an automatic protection plan.")
        : Lf(
            "Schedule.Coverage.Summary",
            "{0} included · {1} paused",
            IncludedProjectCount,
            PausedProjectCount);

    public string DestinationSummary
    {
        get
        {
            int destinationCount = GetConfiguredDestinationCount();
            return destinationCount == 0
                ? L("Schedule.Destination.None", "No active destination is configured")
                : Lf(
                    "Schedule.Destination.Ready",
                    "Active destinations: {0}",
                    destinationCount);
        }
    }

    public string PowerSummary
    {
        get
        {
            PowerState power = GetPowerState();
            if (!_settings.PauseBackupsOnBattery)
                return L("Schedule.Power.Allowed", "Backups may run on battery power");

            return power switch
            {
                PowerState.OnBattery => L("Schedule.Power.Waiting", "On battery · automatic work waits"),
                PowerState.PluggedIn => L("Schedule.Power.PluggedIn", "Plugged in · battery rule is clear"),
                _ => L("Schedule.Power.Unknown", "Battery pause is enabled · power state unknown")
            };
        }
    }

    public string LastAutomaticBackupText { get; private set; } = string.Empty;
    public string ReadinessTitle { get; private set; } = string.Empty;
    public string ReadinessDetail { get; private set; } = string.Empty;

    public string RunBehaviorSummary => Lf(
        "Schedule.Behavior.Summary",
        "At each opportunity, VaultSync checks the included project set ({0}) and writes a backup only when files changed.",
        IncludedProjectCount);

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

        RebuildProjectCoverage();
        RebuildUpcomingRuns(now, timerDue);
        UpdateReadiness(now);

        OnPropertiesChanged(
            nameof(IsManualMode),
            nameof(IsAutomaticMode),
            nameof(IntervalMinutes),
            nameof(EnableQuietHours),
            nameof(QuietHoursStart),
            nameof(QuietHoursEnd),
            nameof(HasUpcomingRuns),
            nameof(HasProjects),
            nameof(CanEditQuietHours),
            nameof(NextRunText),
            nameof(DelayExplanation),
            nameof(QuietHoursSummary),
            nameof(RegisteredProjectCount),
            nameof(IncludedProjectCount),
            nameof(PausedProjectCount),
            nameof(CoverageValue),
            nameof(CoverageSummary),
            nameof(DestinationSummary),
            nameof(PowerSummary),
            nameof(LastAutomaticBackupText),
            nameof(ReadinessTitle),
            nameof(ReadinessDetail),
            nameof(RunBehaviorSummary));
    }

    private void RebuildProjectCoverage()
    {
        IReadOnlyList<ScheduleProjectSnapshot> projects;
        try
        {
            projects = _projectSnapshotProvider() ?? [];
        }
        catch
        {
            projects = [];
        }

        ProjectCoverage.Clear();
        string previousGroup = string.Empty;
        foreach (ScheduleProjectSnapshot project in projects
                     .OrderBy(item => ResolveGroupName(item.GroupName), StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string groupName = ResolveGroupName(project.GroupName);
            ProjectCoverage.Add(new ScheduleProjectRowViewModel
            {
                GroupName = groupName,
                ShowGroupHeader = !string.Equals(previousGroup, groupName, StringComparison.CurrentCultureIgnoreCase),
                Name = project.Name,
                AutomaticStatus = project.AutomaticEnabled
                    ? L("Schedule.Project.Included", "Included")
                    : L("Schedule.Project.Paused", "Paused"),
                LastBackup = project.LastBackupUtc is { } lastBackup
                    ? lastBackup.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : L("Schedule.Project.Never", "No backup yet")
            });
            previousGroup = groupName;
        }

        RegisteredProjectCount = projects.Count;
        IncludedProjectCount = projects.Count(project => project.AutomaticEnabled);
        PausedProjectCount = RegisteredProjectCount - IncludedProjectCount;

        DateTime? lastAutomaticUtc = projects
            .Select(project => project.LastAutomaticBackupUtc)
            .Where(value => value.HasValue)
            .Max();
        LastAutomaticBackupText = lastAutomaticUtc is { } last
            ? Lf(
                "Schedule.LastRun.Value",
                "Last automatic backup: {0}",
                last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : L("Schedule.LastRun.None", "No automatic backup recorded");
    }

    private static string ResolveGroupName(string? groupName) =>
        string.IsNullOrWhiteSpace(groupName)
            ? L("Projects.Folder.Ungrouped", "Ungrouped")
            : groupName.Trim();

    private void RebuildUpcomingRuns(DateTimeOffset now, DateTimeOffset? timerDue)
    {
        IReadOnlyList<BackupScheduleOpportunity> opportunities = BackupSchedulePolicy.ProjectUpcoming(
            IsAutomaticMode,
            IntervalMinutes,
            EnableQuietHours,
            QuietHoursStart,
            QuietHoursEnd,
            now,
            timerDue,
            count: 4);

        UpcomingRuns.Clear();
        for (int index = 0; index < opportunities.Count; index++)
        {
            BackupScheduleOpportunity opportunity = opportunities[index];
            UpcomingRuns.Add(new ScheduleOpportunityViewModel
            {
                Sequence = (index + 1).ToString(CultureInfo.CurrentCulture),
                Day = opportunity.OccursAtLocal.ToString("ddd, d MMM", CultureInfo.CurrentCulture),
                Time = opportunity.OccursAtLocal.ToString("HH:mm", CultureInfo.CurrentCulture),
                Status = opportunity.WasDeferredByQuietHours
                    ? L("Schedule.Opportunity.Deferred", "After quiet hours")
                    : L("Schedule.Opportunity.Ready", "Timer opportunity")
            });
        }
    }

    private void UpdateReadiness(DateTimeOffset now)
    {
        if (RegisteredProjectCount == 0)
        {
            ReadinessTitle = L("Schedule.Readiness.NoProjects.Title", "No projects to schedule");
            ReadinessDetail = L("Schedule.Readiness.NoProjects.Detail", "Register a project, then return here to review its protection plan.");
            return;
        }

        if (GetConfiguredDestinationCount() == 0)
        {
            ReadinessTitle = L("Schedule.Readiness.NoDestination.Title", "A destination is required");
            ReadinessDetail = L("Schedule.Readiness.NoDestination.Detail", "Automatic timing is configured, but there is nowhere to write a backup.");
            return;
        }

        if (!IsAutomaticMode)
        {
            ReadinessTitle = L("Schedule.Readiness.Manual.Title", "Manual protection");
            ReadinessDetail = L("Schedule.Readiness.Manual.Detail", "Nothing runs on a timer. Open Backups whenever you want to create a restore point.");
            return;
        }

        if (IncludedProjectCount == 0)
        {
            ReadinessTitle = L("Schedule.Readiness.Paused.Title", "Every project is paused");
            ReadinessDetail = L("Schedule.Readiness.Paused.Detail", "The timer will wake up, but no project is currently included.");
            return;
        }

        if (_settings.PauseBackupsOnBattery && GetPowerState() == PowerState.OnBattery)
        {
            ReadinessTitle = L("Schedule.Readiness.Battery.Title", "Waiting for external power");
            ReadinessDetail = L("Schedule.Readiness.Battery.Detail", "The next opportunity waits until this device is plugged in.");
            return;
        }

        QuietHoursDecision quietHours = QuietHoursPolicy.Evaluate(
            EnableQuietHours,
            QuietHoursStart,
            QuietHoursEnd,
            now);
        if (quietHours.IsInQuietHours)
        {
            ReadinessTitle = L("Schedule.Readiness.Quiet.Title", "Quiet hours are active");
            ReadinessDetail = quietHours.ResumeAtLocal is { } resume
                ? Lf(
                    "Schedule.Readiness.Quiet.Detail",
                    "Automatic work resumes after {0}.",
                    resume.ToString("g", CultureInfo.CurrentCulture))
                : L("Schedule.Readiness.Quiet.DetailFallback", "Automatic work resumes when quiet hours end.");
            return;
        }

        ReadinessTitle = IsDelayed
            ? L("Schedule.Readiness.Deferred.Title", "Scheduled after quiet hours")
            : L("Schedule.Readiness.Ready.Title", "Ready for the next opportunity");
        ReadinessDetail = Lf(
            "Schedule.Readiness.Ready.Detail",
            "{0} project(s) are included. Unchanged projects are skipped without writing another backup.",
            IncludedProjectCount);
    }

    private int GetConfiguredDestinationCount()
    {
        if (_settings.UseAdvancedDestinations)
        {
            return _settings.Destinations.Count(destination =>
                destination.Active && !string.IsNullOrWhiteSpace(destination.Path));
        }

        return string.IsNullOrWhiteSpace(_settings.BackupLocationPath) ? 0 : 1;
    }

    private PowerState GetPowerState()
    {
        try
        {
            return _powerStateProvider();
        }
        catch
        {
            return PowerState.Unknown;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.EnableAutoBackups)
            or nameof(SettingsViewModel.AutoBackupIntervalMinutes)
            or nameof(SettingsViewModel.EnableQuietHours)
            or nameof(SettingsViewModel.QuietHoursStart)
            or nameof(SettingsViewModel.QuietHoursEnd)
            or nameof(SettingsViewModel.PauseBackupsOnBattery)
            or nameof(SettingsViewModel.UseAdvancedDestinations)
            or nameof(SettingsViewModel.BackupLocationPath))
        {
            Refresh();
        }
    }
}
