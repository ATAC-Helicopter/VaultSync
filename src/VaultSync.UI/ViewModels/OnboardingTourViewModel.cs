using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class OnboardingTourStep
{
    public string Title { get; }
    public string Body { get; }
    public string ActionText { get; }
    public string CompleteText { get; }
    public string TargetName => _targetNameProvider();
    public string RequiredView { get; }
    public Func<bool> IsComplete { get; }
    public bool AutoAdvance { get; }
    public Func<bool> IsApplicable { get; }
    private readonly Func<string> _targetNameProvider;

    public OnboardingTourStep(
        string title,
        string body,
        string actionText,
        string completeText,
        string targetName,
        string requiredView,
        Func<bool> isComplete,
        bool autoAdvance = true,
        Func<bool>? isApplicable = null)
    {
        Title = title;
        Body = body;
        ActionText = actionText;
        CompleteText = completeText;
        _targetNameProvider = () => targetName;
        RequiredView = requiredView;
        IsComplete = isComplete;
        AutoAdvance = autoAdvance;
        IsApplicable = isApplicable ?? (() => true);
    }

    public OnboardingTourStep(
        string title,
        string body,
        string actionText,
        string completeText,
        Func<string> targetNameProvider,
        string requiredView,
        Func<bool> isComplete,
        bool autoAdvance = true,
        Func<bool>? isApplicable = null)
    {
        Title = title;
        Body = body;
        ActionText = actionText;
        CompleteText = completeText;
        _targetNameProvider = targetNameProvider;
        RequiredView = requiredView;
        IsComplete = isComplete;
        AutoAdvance = autoAdvance;
        IsApplicable = isApplicable ?? (() => true);
    }
}

public sealed class OnboardingTourViewModel : ViewModelBase
{
    private const string BackupsViewName = "Backups";
    private const string ProjectsViewName = "Projects";
    private const string SettingsViewName = "Settings";

    private readonly AppViewModel _app;
    private readonly List<OnboardingTourStep> _steps = [];
    private readonly DispatcherTimer _pollTimer;
    private int _index;
    private bool _isActive;
    private bool _isStepComplete;
    private int _advanceQueued;
    private AppConfig? _cachedConfig;
    private DateTime _lastConfigAt;
    private int _configRefreshInFlight;

    public event Action? TourCompleted;

    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }

    public string StepCounter
    {
        get
        {
            int total = 0;
            int index = -1;
            OnboardingTourStep? current = CurrentStep;
            for (int i = 0; i < _steps.Count; i++)
            {
                OnboardingTourStep step = _steps[i];
                if (!step.IsApplicable())
                    continue;

                if (ReferenceEquals(step, current) && index < 0)
                {
                    index = total;
                }
                total++;
            }

            if (index < 0)
                index = Math.Clamp(_index, 0, Math.Max(total - 1, 0));

            return Lf("Onboarding.StepCounter", "Step {0} of {1}", index + 1, total);
        }
    }
    public string Title => CurrentStep?.Title ?? string.Empty;
    public string Body => CurrentStep?.Body ?? string.Empty;
    public string ActionText => CurrentStep?.ActionText ?? string.Empty;
    public string CompleteText => CurrentStep?.CompleteText ?? string.Empty;
    public static string ActionHeadingText => L("Onboarding.ActionHeading", "Next");
    public static string CompleteHeadingText => L("Onboarding.CompleteHeading", "Done");
    public string TargetName => CurrentStep?.TargetName ?? string.Empty;
    public bool HasActionText => !string.IsNullOrWhiteSpace(ActionText) && !IsStepComplete;
    public bool HasCompleteText => !string.IsNullOrWhiteSpace(CompleteText) && IsStepComplete;
    public bool CanGoBack => PreviousApplicableIndex() >= 0;
    public double ProgressValue
    {
        get
        {
            int total = ApplicableStepCount();
            if (total <= 0)
                return 0d;

            int index = CurrentApplicableIndex();
            return Math.Clamp((index + 1d) / total, 0d, 1d);
        }
    }

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
                OnPropertyChanged(nameof(CanAdvance));
                OnPropertyChanged(nameof(PrimaryLabel));
                OnPropertyChanged(nameof(IsPrimaryEnabled));
                OnPropertyChanged(nameof(HasActionText));
                OnPropertyChanged(nameof(HasCompleteText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool CanAdvance => IsStepComplete && IsOnRequiredView;

    public bool IsPrimaryEnabled => !IsOnRequiredView || IsStepComplete;

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
                    _ => L("Onboarding.Go", "Go")
                };
            }

            return IsLastStep
                ? L("Onboarding.Finish", "Finish")
                : L("Onboarding.Next", "Next");
        }
    }

    public static string SkipLabel => L("Onboarding.Skip", "Skip");
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

    private int ApplicableStepCount() =>
        _steps.Count(step => step.IsApplicable());

    private int CurrentApplicableIndex()
    {
        int index = 0;
        OnboardingTourStep? current = CurrentStep;
        foreach (OnboardingTourStep step in _steps)
        {
            if (!step.IsApplicable())
                continue;

            if (ReferenceEquals(step, current))
                return index;

            index++;
        }

        return Math.Clamp(_index, 0, Math.Max(index - 1, 0));
    }

    private int PreviousApplicableIndex()
    {
        for (int i = _index - 1; i >= 0; i--)
        {
            if (_steps[i].IsApplicable())
                return i;
        }

        return -1;
    }

    public OnboardingTourViewModel(AppViewModel app)
    {
        _app = app;

        PreviousCommand = new RelayCommand(_ => GoBack());
        PrimaryCommand = new RelayCommand(_ => HandlePrimary());
        SkipCommand = new RelayCommand(_ => Stop());

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _pollTimer.Tick += (_, _) => Poll();

        BuildSteps();
    }

    public void Start()
    {
        _index = 0;
        Interlocked.Exchange(ref _advanceQueued, 0);
        IsActive = true;
        EnsureApplicableStep();
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
            return;

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
        }
    }

    private void GoBack()
    {
        int previous = PreviousApplicableIndex();
        if (previous < 0)
            return;

        _index = previous;
        Interlocked.Exchange(ref _advanceQueued, 0);
        UpdateState();
    }

    private void BuildSteps()
    {
        _steps.Clear();

        bool UseAdvancedDestinations()
        {
            SettingsViewModel? live = _app.SettingsViewModel;
            if (live is not null)
                return live.UseAdvancedDestinations;

            return GetConfig().Backups.UseAdvancedDestinations;
        }

        bool HasProjectsRoot()
        {
            SettingsViewModel? live = _app.SettingsViewModel;
            if (live is not null)
                return !string.IsNullOrWhiteSpace(live.ProjectsRootPath);

            return !string.IsNullOrWhiteSpace(GetConfig().ProjectsRoot);
        }

        bool HasBackupDestination()
        {
            SettingsViewModel? live = _app.SettingsViewModel;
            if (live is not null)
            {
                if (live.UseAdvancedDestinations)
                {
                    return live.Destinations.Any(d =>
                        d.Active && !string.IsNullOrWhiteSpace(d.Path));
                }

                return !string.IsNullOrWhiteSpace(live.BackupLocationPath);
            }

            return HasDestinationConfigured(GetConfig());
        }

        bool HasRegisteredProject() =>
            _app.ProjectsViewModel.Projects.Any(p => p.IsRegistered);

        string DestinationTarget() =>
            UseAdvancedDestinations() ? "AddDestinationButton" : "BackupLocationRow";

        void AddStep(
            string title,
            string body,
            string actionText,
            string completeText,
            string targetName,
            string requiredView,
            Func<bool> isComplete,
            bool autoAdvance = false,
            Func<bool>? isApplicable = null)
        {
            _steps.Add(new OnboardingTourStep(
                title,
                body,
                actionText,
                completeText,
                targetName,
                requiredView,
                isComplete,
                autoAdvance,
                isApplicable));
        }

        void AddStepDynamic(
            string title,
            string body,
            string actionText,
            string completeText,
            Func<string> targetNameProvider,
            string requiredView,
            Func<bool> isComplete,
            bool autoAdvance = false,
            Func<bool>? isApplicable = null)
        {
            _steps.Add(new OnboardingTourStep(
                title,
                body,
                actionText,
                completeText,
                targetNameProvider,
                requiredView,
                isComplete,
                autoAdvance,
                isApplicable));
        }

        AddStep(
            L("Onboarding.Setup.Intro.Title", "Set up your first backup"),
            L("Onboarding.Setup.Intro.Body", "VaultSync only needs three things to become useful: where your projects live, where backups should go, and one project to protect. Advanced options can wait."),
            L("Onboarding.Setup.Intro.Action", "Start with the basics. This guide will stay out of your way and only point at the next control you need."),
            L("Onboarding.Setup.Intro.Done", "Ready."),
            string.Empty,
            string.Empty,
            () => true);

        AddStep(
            L("Onboarding.Setup.ProjectsRoot.Title", "Tell VaultSync where your projects live"),
            L("Onboarding.Setup.ProjectsRoot.Body", "Choose the folder that contains your project folders. VaultSync uses this to find candidates and keep future setup fast."),
            L("Onboarding.Setup.ProjectsRoot.Action", "Open Settings and choose your projects root folder."),
            L("Onboarding.Setup.ProjectsRoot.Done", "Projects root selected."),
            "ProjectsRootRow",
            SettingsViewName,
            HasProjectsRoot);

        AddStepDynamic(
            L("Onboarding.Setup.Destination.Title", "Choose where backups should be stored"),
            L("Onboarding.Setup.Destination.Body", "Pick a local, external, or network location that is not inside the project you are backing up. Simple mode is enough for a first run."),
            L("Onboarding.Setup.Destination.Action", "Choose a backup folder. If you use advanced destinations, add one active destination with a path."),
            L("Onboarding.Setup.Destination.Done", "Backup destination ready."),
            DestinationTarget,
            SettingsViewName,
            HasBackupDestination);

        AddStep(
            L("Onboarding.Setup.Project.Title", "Add your first project"),
            L("Onboarding.Setup.Project.Body", "Register one project first. You can add more later after you have confirmed the backup flow works."),
            L("Onboarding.Setup.Project.Action", "Open Projects, select one project candidate, and add it to VaultSync."),
            L("Onboarding.Setup.Project.Done", "First project registered."),
            "ProjectSnapshotButton",
            ProjectsViewName,
            HasRegisteredProject);

        AddStep(
            L("Onboarding.Setup.Backup.Title", "Run the first backup"),
            L("Onboarding.Setup.Backup.Body", "Start a backup for the project. Once it completes, VaultSync can show history, diff summaries, and restore points."),
            L("Onboarding.Setup.Backup.Action", "Open Backups and run a backup for the registered project."),
            L("Onboarding.Setup.Backup.Done", "First backup completed."),
            "PerProjectBackupButton",
            BackupsViewName,
            () => _app.BackupsViewModel.HasAnyBackups);

        AddStep(
            L("Onboarding.Setup.Done.Title", "You have a restore point"),
            L("Onboarding.Setup.Done.Body", "This Backups section is where you verify snapshots, browse backup contents, restore files, and review future backup history."),
            L("Onboarding.Setup.Done.Action", "Review this page when you want to restore files or inspect backup history."),
            L("Onboarding.Setup.Done.Done", "Onboarding complete."),
            "BackupsHistorySection",
            BackupsViewName,
            () => true);
    }

    private static bool HasDestinationConfigured(AppConfig cfg)
    {
        if (cfg.Backups.UseAdvancedDestinations)
            return cfg.Backups.Destinations.Any();

        string root = cfg.Backups.BackupRoot ?? cfg.Backups.BackupLocation ?? string.Empty;
        return !string.IsNullOrWhiteSpace(root);
    }

    private void Poll()
    {
        if (!IsActive)
            return;

        UpdateState();

        if (IsStepComplete && IsOnRequiredView)
        {
            OnboardingTourStep? step = CurrentStep;
            bool autoAdvance = step?.AutoAdvance ?? true;
            if (step is not null && autoAdvance && Interlocked.Exchange(ref _advanceQueued, 1) == 0)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(650);
                    if (ReferenceEquals(CurrentStep, step) && IsStepComplete && IsOnRequiredView && step.AutoAdvance)
                    {
                        Advance();
                    }
                    Interlocked.Exchange(ref _advanceQueued, 0);
                }, DispatcherPriority.Background);
            }
        }
    }

    private void UpdateState()
    {
        EnsureApplicableStep();
        IsStepComplete = CurrentStep?.IsComplete() ?? false;
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CompleteText));
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(HasActionText));
        OnPropertyChanged(nameof(HasCompleteText));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(IsPrimaryEnabled));
    }

    private AppConfig GetConfig()
    {
        DateTime now = DateTime.UtcNow;
        if (_cachedConfig is not null && (now - _lastConfigAt).TotalMilliseconds < 250)
        {
            return _cachedConfig;
        }

        if (_cachedConfig is not null)
        {
            if (Interlocked.Exchange(ref _configRefreshInFlight, 1) == 0)
            {
                DetachedTask.Run(() =>
                {
                    try
                    {
                        AppConfig cfg = _app.GetConfigSnapshot();
                        Dispatcher.UIThread.Post(() =>
                        {
                            _cachedConfig = cfg;
                            _lastConfigAt = DateTime.UtcNow;
                        });
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _configRefreshInFlight, 0);
                    }
                }, nameof(GetConfig));
            }

            return _cachedConfig;
        }

        AppConfig fresh = _app.GetConfigSnapshot();
        _cachedConfig = fresh;
        _lastConfigAt = now;
        return fresh;
    }

    private void Advance()
    {
        if (IsLastStep)
        {
            Stop();
            return;
        }

        _index = Math.Min(_index + 1, _steps.Count - 1);
        Interlocked.Exchange(ref _advanceQueued, 0);
        EnsureApplicableStep();
        UpdateState();
    }

    private void EnsureApplicableStep()
    {
        int safety = _steps.Count + 1;
        while (safety-- > 0 && CurrentStep is not null && !CurrentStep.IsApplicable())
        {
            if (IsLastStep)
                break;

            _index = Math.Min(_index + 1, _steps.Count - 1);
        }
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
