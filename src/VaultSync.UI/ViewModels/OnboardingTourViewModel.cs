using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class OnboardingTourStep
{
    public string Title { get; }
    public string Body { get; }
    public string ActionText { get; }
    public string CompleteText { get; }
    public string RequiredView { get; }
    public bool TracksProgress { get; }
    public Func<OnboardingSetupState, bool> IsComplete { get; }

    public OnboardingTourStep(
        string title,
        string body,
        string actionText,
        string completeText,
        string requiredView,
        bool tracksProgress,
        Func<OnboardingSetupState, bool> isComplete)
    {
        Title = title;
        Body = body;
        ActionText = actionText;
        CompleteText = completeText;
        RequiredView = requiredView;
        TracksProgress = tracksProgress;
        IsComplete = isComplete;
    }
}

public sealed class OnboardingChecklistItem : ViewModelBase
{
    private bool _isCurrent;
    private bool _isComplete;

    public OnboardingChecklistItem(int number, string title)
    {
        Number = number;
        Title = title;
    }

    public int Number { get; }
    public string Title { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetField(ref _isCurrent, value))
                OnPropertiesChanged(nameof(StatusText), nameof(StatusBrush), nameof(NumberBrush), nameof(NumberText));
        }
    }

    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (SetField(ref _isComplete, value))
                OnPropertiesChanged(nameof(StatusText), nameof(StatusBrush), nameof(NumberBrush), nameof(NumberText));
        }
    }

    public string NumberText => IsComplete ? "✓" : Number.ToString(CultureInfo.CurrentCulture);

    public string StatusText
    {
        get
        {
            if (IsComplete)
                return L("Onboarding.Status.Done", "Done");
            return IsCurrent
                ? L("Onboarding.Status.Current", "Now")
                : L("Onboarding.Status.Upcoming", "Next");
        }
    }

    public IBrush StatusBrush
    {
        get
        {
            if (IsComplete)
                return CompleteBrush;
            return IsCurrent
                ? CurrentBrush
                : MutedBrush;
        }
    }

    public IBrush NumberBrush
    {
        get
        {
            if (IsComplete)
                return CompleteBrush;
            return IsCurrent
                ? CurrentBrush
                : MutedBrush;
        }
    }

    private static readonly IBrush CompleteBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
    private static readonly IBrush CurrentBrush = new ImmutableSolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse("#8A94A7"));

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}

public sealed record OnboardingSetupState(
    bool HasProjectsRoot,
    bool HasBackupDestination,
    int RegisteredProjectCount,
    bool HasValidSchedule,
    int BackupCount,
    int PassedRecoveryDrillCount)
{
    public static OnboardingSetupState Empty { get; } = new(false, false, 0, false, 0, 0);
}

public sealed class OnboardingTourViewModel : ViewModelBase
{
    private const string BackupsViewName = "Backups";
    private const string ProjectsViewName = "Projects";
    private const string SettingsViewName = "Settings";
    private const string RecoveryViewName = "Recovery";
    private const string ScheduleViewName = "Schedule";

    private readonly AppViewModel _app;
    private readonly List<OnboardingTourStep> _steps = [];
    private readonly DispatcherTimer _pollTimer;
    private readonly IRepositoryFactory _repositoryFactory = new SqliteRepositoryFactory(StaticAppConfigStore.Instance);
    private int _index;
    private bool _isActive;
    private bool _isStepComplete;
    private OnboardingSetupState _setupState = OnboardingSetupState.Empty;
    private DateTime _lastStateRefreshUtc = DateTime.MinValue;
    private string _lastObservedViewKey = string.Empty;
    private int _stateRefreshInFlight;

    public event Action? TourCompleted;

    public ObservableCollection<OnboardingChecklistItem> ChecklistItems { get; } = [];

    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }

    public string StepCounter => Lf(
        "Onboarding.StepCounter",
        "Step {0} of {1}",
        Math.Clamp(_index + 1, 1, Math.Max(_steps.Count, 1)),
        _steps.Count);

    public string ProgressText => Lf(
        "Onboarding.Progress",
        "{0} of {1} complete",
        CompletedStepCount(),
        TrackedStepCount());

    public string Title => CurrentStep?.Title ?? string.Empty;
    public string Body => CurrentStep?.Body ?? string.Empty;
    public string ActionText => IsStepComplete
        ? CurrentStep?.CompleteText ?? string.Empty
        : CurrentStep?.ActionText ?? string.Empty;

    public string ActionHeadingText => IsStepComplete
        ? L("Onboarding.CompleteHeading", "Done")
        : L("Onboarding.ActionHeading", "What to do");

    public bool HasActionText => !string.IsNullOrWhiteSpace(ActionText);
    public bool CanGoBack => _index > 0;
    public double ProgressValue => _steps.Count == 0 ? 0d : Math.Clamp(CompletedStepCount() / (double)_steps.Count, 0d, 1d);

    public string StatusText
    {
        get
        {
            if (!IsOnRequiredView)
                return L("Onboarding.Status.OpenPage", "Open the right page to continue");

            return IsStepComplete
                ? L("Onboarding.Status.Done", "Done")
                : L("Onboarding.Status.Waiting", "Waiting for you");
        }
    }

    public bool IsStepComplete
    {
        get => _isStepComplete;
        private set
        {
            if (SetField(ref _isStepComplete, value))
            {
                OnPropertiesChanged(
                    nameof(PrimaryLabel),
                    nameof(IsPrimaryEnabled),
                    nameof(ActionText),
                    nameof(ActionHeadingText),
                    nameof(HasActionText),
                    nameof(StatusText));
            }
        }
    }

    public bool IsPrimaryEnabled => true;

    public string PrimaryLabel
    {
        get
        {
            if (!IsOnRequiredView)
            {
                return CurrentStep?.RequiredView switch
                {
                    SettingsViewName => L("Onboarding.GoSettings", "Open Settings"),
                    ProjectsViewName => L("Onboarding.GoProjects", "Open Projects"),
                    BackupsViewName => L("Onboarding.GoBackups", "Open Backups"),
                    ScheduleViewName => L("Nav.Schedule", "Schedule"),
                    RecoveryViewName => L("Onboarding.GoRecovery", "Open Recovery"),
                    _ => L("Onboarding.Go", "Go")
                };
            }

            if (!IsStepComplete)
                return L("Common.Refresh", "Check again");

            return IsLastStep
                ? L("Onboarding.Finish", "Finish")
                : L("Onboarding.Next", "Next");
        }
    }

    public static string SkipLabel => L("Onboarding.Skip", "Continue later");
    public static string BackLabel => L("Onboarding.Back", "Back");

    public RelayCommand PreviousCommand { get; }
    public RelayCommand PrimaryCommand { get; }
    public RelayCommand SkipCommand { get; }

    private OnboardingTourStep? CurrentStep =>
        _index >= 0 && _index < _steps.Count ? _steps[_index] : null;

    private bool IsLastStep => _index >= _steps.Count - 1;

    private bool IsOnRequiredView =>
        CurrentStep is null ||
        string.IsNullOrWhiteSpace(CurrentStep.RequiredView) ||
        string.Equals(_app.CurrentViewKey, CurrentStep.RequiredView, StringComparison.OrdinalIgnoreCase);

    public OnboardingTourViewModel(AppViewModel app)
    {
        _app = app;

        PreviousCommand = new RelayCommand(_ => GoBack());
        PrimaryCommand = new RelayCommand(_ => HandlePrimary());
        SkipCommand = new RelayCommand(_ => Stop());

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += (_, _) => Poll();

        BuildSteps();
        RebuildChecklist();
    }

    public void Start()
    {
        _index = 0;
        _lastObservedViewKey = _app.CurrentViewKey;
        IsActive = true;
        RefreshSetupState(force: true);
        UpdateState();
        _pollTimer.Start();
    }

    public void Stop()
    {
        IsActive = false;
        _pollTimer.Stop();
        TourCompleted?.Invoke();
    }

    private void HandlePrimary()
    {
        if (!IsOnRequiredView)
        {
            NavigateToRequiredView();
            return;
        }

        if (!IsStepComplete)
        {
            RefreshSetupState(force: true);
            return;
        }

        Advance();
    }

    private void NavigateToRequiredView()
    {
        switch (CurrentStep?.RequiredView)
        {
            case SettingsViewName:
                _app.NavigateSettings.Execute(null);
                break;
            case ProjectsViewName:
                _app.NavigateProjects.Execute(null);
                break;
            case BackupsViewName:
                _app.NavigateBackups.Execute(null);
                break;
            case ScheduleViewName:
                _app.NavigateSchedule.Execute(null);
                break;
            case RecoveryViewName:
                _app.NavigateRecovery.Execute(null);
                break;
        }

        _lastObservedViewKey = _app.CurrentViewKey;
        UpdateState();
    }

    private void GoBack()
    {
        if (_index <= 0)
            return;

        _index--;
        UpdateState();
    }

    private void BuildSteps()
    {
        _steps.Clear();

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Setup.Intro.Title", "Set up your first backup"),
            L("Onboarding.Setup.Intro.Body", "VaultSync only needs three things to become useful: where your projects live, where backups should go, and one project to protect. Advanced options can wait."),
            L("Onboarding.Setup.Intro.Action", "Start with the basics. This guide will track real setup progress and keep the next step visible."),
            L("Onboarding.Setup.Intro.Done", "Ready."),
            string.Empty,
            tracksProgress: false,
            _ => true));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Setup.ProjectsRoot.Title", "Tell VaultSync where your projects live"),
            L("Onboarding.Setup.ProjectsRoot.Body", "Choose the folder that contains your project folders. VaultSync uses this to find candidates and keep future setup fast."),
            L("Onboarding.Setup.ProjectsRoot.Action", "Open Settings and choose your projects root folder."),
            L("Onboarding.Setup.ProjectsRoot.Done", "Projects root selected."),
            SettingsViewName,
            tracksProgress: true,
            state => state.HasProjectsRoot));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Setup.Destination.Title", "Choose where backups should be stored"),
            L("Onboarding.Setup.Destination.Body", "Pick a local, external, or network location that is not inside the project you are backing up. Simple mode is enough for a first run."),
            L("Onboarding.Setup.Destination.Action", "Choose a backup folder. If you use advanced destinations, add one active destination with a path."),
            L("Onboarding.Setup.Destination.Done", "Backup destination ready."),
            SettingsViewName,
            tracksProgress: true,
            state => state.HasBackupDestination));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Setup.Project.Title", "Add your first project"),
            L("Onboarding.Setup.Project.Body", "Register one project first. You can add more later after you have confirmed the backup flow works."),
            L("Onboarding.Setup.Project.Action", "Open Projects, select one project candidate, and add it to VaultSync."),
            L("Onboarding.Setup.Project.Done", "First project registered."),
            ProjectsViewName,
            tracksProgress: true,
            state => state.RegisteredProjectCount > 0));

        _steps.Add(new OnboardingTourStep(
            L("Schedule.Overview.Title", "Your protection schedule"),
            L("Schedule.Mode.Description", "Choose whether VaultSync runs automatically or only when you start a backup."),
            L("Schedule.SaveHint", "Review the mode, interval, and quiet hours. Changes are saved automatically."),
            L("Onboarding.Status.Done", "Done"),
            ScheduleViewName,
            tracksProgress: true,
            state => state.HasValidSchedule));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Setup.Backup.Title", "Run the first backup"),
            L("Onboarding.Setup.Backup.Body", "Start a backup for the project. Once it completes, VaultSync can show history, diff summaries, and restore points."),
            L("Onboarding.Setup.Backup.Action", "Open Backups and run a backup for the registered project."),
            L("Onboarding.Setup.Backup.Done", "First backup completed."),
            BackupsViewName,
            tracksProgress: true,
            state => state.BackupCount > 0));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Setup.Done.Title", "You have a restore point"),
            L("Onboarding.Setup.Done.Body", "This Backups section is where you verify snapshots, browse backup contents, restore files, and review future backup history."),
            L("Onboarding.Setup.Done.Action", "Review this page when you want to restore files or inspect backup history."),
            L("Onboarding.Setup.Done.Done", "Onboarding complete."),
            BackupsViewName,
            tracksProgress: false,
            state => state.BackupCount > 0));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Setup.Proof.Title", "Prove that recovery works"),
            L("Onboarding.Setup.Proof.Body", "A completed backup is only the start. Recovery checks the destination, inventory, file hashes, and restore plan without touching your original files."),
            L("Onboarding.Setup.Proof.Action", "Open Recovery and run the drill for your first project. Review any limited or failed evidence before relying on the backup."),
            L("Onboarding.Setup.Proof.Passed", "Recovery has been proved with a passed drill."),
            RecoveryViewName,
            tracksProgress: true,
            state => state.PassedRecoveryDrillCount > 0));
    }

    private void RebuildChecklist()
    {
        ChecklistItems.Clear();
        int number = 1;
        foreach (OnboardingTourStep step in _steps.Where(step => step.TracksProgress))
        {
            ChecklistItems.Add(new OnboardingChecklistItem(number++, step.Title));
        }
    }

    private static bool HasDestinationConfigured(AppConfig cfg)
    {
        if (cfg.Backups.UseAdvancedDestinations)
        {
            return cfg.Backups.Destinations.Any(destination =>
                destination.Active && !string.IsNullOrWhiteSpace(destination.Path));
        }

        string root = cfg.Backups.BackupRoot ?? cfg.Backups.BackupLocation ?? string.Empty;
        return !string.IsNullOrWhiteSpace(root);
    }

    private void Poll()
    {
        if (!IsActive)
            return;

        if (!string.Equals(_lastObservedViewKey, _app.CurrentViewKey, StringComparison.Ordinal))
        {
            _lastObservedViewKey = _app.CurrentViewKey;
            UpdateState();
        }
        RefreshSetupState();
    }

    private void RefreshSetupState(bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && (now - _lastStateRefreshUtc) < TimeSpan.FromSeconds(2))
            return;

        if (Interlocked.Exchange(ref _stateRefreshInFlight, 1) == 1)
            return;

        _lastStateRefreshUtc = now;
        DetachedTask.Run(() =>
        {
            try
            {
                OnboardingSetupState state = BuildSetupState();
                Dispatcher.UIThread.Post(() =>
                {
                    if (state == _setupState)
                        return;
                    _setupState = state;
                    UpdateState();
                });
            }
            finally
            {
                Interlocked.Exchange(ref _stateRefreshInFlight, 0);
            }
        }, nameof(RefreshSetupState));
    }

    private OnboardingSetupState BuildSetupState()
    {
        AppConfig cfg = _app.GetConfigSnapshot();
        int projectCount = 0;
        int backupCount = 0;
        int passedDrillCount = 0;

        try
        {
            SqliteRepository repo = _repositoryFactory.Create(cfg);
            projectCount = repo.GetAllProjects().Count();
            backupCount = repo.GetBackupCount();
            var latestDrills = repo.GetRecoveryDrills()
                .GroupBy(drill => drill.ProjectId)
                .Select(group => group.OrderByDescending(drill => drill.RunUtc).First())
                .ToList();
            passedDrillCount = latestDrills.Count(drill =>
                drill.Status == VaultSync.Core.Models.RecoveryDrillStatus.Passed);
        }
        catch
        {
            projectCount = _app.ProjectsViewModel.Projects.Count(project => project.IsRegistered);
            backupCount = _app.BackupsViewModel.HasAnyBackups ? 1 : 0;
        }

        bool hasProjectsRoot =
            !string.IsNullOrWhiteSpace(_app.SettingsViewModel.ProjectsRootPath) ||
            !string.IsNullOrWhiteSpace(cfg.ProjectsRoot);

        bool hasDestination =
            HasDestinationConfigured(cfg) ||
            (_app.SettingsViewModel.UseAdvancedDestinations
                ? _app.SettingsViewModel.Destinations.Any(destination =>
                    destination.Active && !string.IsNullOrWhiteSpace(destination.Path))
                : !string.IsNullOrWhiteSpace(_app.SettingsViewModel.BackupLocationPath));

        return new OnboardingSetupState(
            hasProjectsRoot,
            hasDestination,
            projectCount,
            !cfg.Backups.EnableAutoBackups || cfg.Backups.IntervalMinutes > 0,
            backupCount,
            passedDrillCount);
    }

    private void UpdateState()
    {
        IsStepComplete = CurrentStep?.IsComplete(_setupState) ?? false;
        int checklistIndex = 0;
        foreach (OnboardingTourStep step in _steps.Where(step => step.TracksProgress))
        {
            OnboardingChecklistItem item = ChecklistItems[checklistIndex++];
            item.IsCurrent = ReferenceEquals(step, CurrentStep);
            item.IsComplete = step.IsComplete(_setupState);
        }

        OnPropertiesChanged(
            nameof(StepCounter),
            nameof(ProgressText),
            nameof(Title),
            nameof(Body),
            nameof(ActionText),
            nameof(ActionHeadingText),
            nameof(HasActionText),
            nameof(CanGoBack),
            nameof(ProgressValue),
            nameof(StatusText),
            nameof(PrimaryLabel),
            nameof(IsPrimaryEnabled));
    }

    private int CompletedStepCount() =>
        _steps.Count(step => step.TracksProgress && step.IsComplete(_setupState));

    private int TrackedStepCount() =>
        _steps.Count(step => step.TracksProgress);

    private void Advance()
    {
        if (IsLastStep)
        {
            Stop();
            return;
        }

        _index = Math.Min(_index + 1, _steps.Count - 1);
        UpdateState();
    }

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal))
            return fallback;
        return value;
    }

    private static string Lf(string key, string fallback, params object[] args)
    {
        string fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }
}
