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
    public string TargetName => _targetNameProvider();
    public string RequiredView { get; }
    public Func<bool> IsComplete { get; }
    public bool AutoAdvance { get; }
    public Func<bool> IsApplicable { get; }
    private readonly Func<string> _targetNameProvider;

    public OnboardingTourStep(string title, string body, string targetName, string requiredView, Func<bool> isComplete, bool autoAdvance = true, Func<bool>? isApplicable = null)
    {
        Title = title;
        Body = body;
        _targetNameProvider = () => targetName;
        RequiredView = requiredView;
        IsComplete = isComplete;
        AutoAdvance = autoAdvance;
        IsApplicable = isApplicable ?? (() => true);
    }

    public OnboardingTourStep(string title, string body, Func<string> targetNameProvider, string requiredView, Func<bool> isComplete, bool autoAdvance = true, Func<bool>? isApplicable = null)
    {
        Title = title;
        Body = body;
        _targetNameProvider = targetNameProvider;
        RequiredView = requiredView;
        IsComplete = isComplete;
        AutoAdvance = autoAdvance;
        IsApplicable = isApplicable ?? (() => true);
    }
}

public sealed class OnboardingTourViewModel : ViewModelBase
{
    private readonly AppViewModel _app;
    private readonly List<OnboardingTourStep> _steps = new();
    private readonly DispatcherTimer _pollTimer;
    private int _index;
    private bool _isActive;
    private bool _isStepComplete;
    private int _advanceQueued;
    private DateTime _lastNavigateAt;

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
            var applicable = _steps.Where(s => s.IsApplicable()).ToList();
            var total = applicable.Count;
            var current = CurrentStep;
            var index = current is null ? -1 : applicable.IndexOf(current);
            if (index < 0)
            {
                index = Math.Clamp(_index, 0, Math.Max(total - 1, 0));
            }
            return Lf("Onboarding.StepCounter", "Step {0} of {1}", index + 1, total);
        }
    }
    public string Title => CurrentStep?.Title ?? string.Empty;
    public string Body => CurrentStep?.Body ?? string.Empty;
    public string TargetName => CurrentStep?.TargetName ?? string.Empty;

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
                    "Settings" => L("Onboarding.GoSettings", "Open Settings"),
                    "Projects" => L("Onboarding.GoProjects", "Open Projects"),
                    "Backups" => L("Onboarding.GoBackups", "Open Backups"),
                    _ => L("Onboarding.Go", "Go")
                };
            }

            return IsLastStep
                ? L("Onboarding.Finish", "Finish")
                : L("Onboarding.Next", "Next");
        }
    }

    public string SkipLabel => L("Onboarding.Skip", "Skip");

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
        NavigateToRequiredView();
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
            case "Settings":
                _app.NavigateSettings.Execute(null);
                break;
            case "Projects":
                _app.NavigateProjects.Execute(null);
                break;
            case "Backups":
                _app.NavigateBackups.Execute(null);
                break;
        }
    }

    private void BuildSteps()
    {
        _steps.Clear();

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step1.Title", "Choose your language"),
            L("Onboarding.Tour.Step1.Body", "Pick the language for VaultSync. We default to your system language when available."),
            "LanguageSelectCombo",
            "Settings",
            () =>
            {
                var cfg = AppConfigStore.Load();
                return !string.IsNullOrWhiteSpace(cfg.Advanced.Language);
            }));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step2.Title", "Set your projects root"),
            L("Onboarding.Tour.Step2.Body", "Choose the folder where your projects live so VaultSync can discover them."),
            "ProjectsRootInput",
            "Settings",
            () =>
            {
                var cfg = AppConfigStore.Load();
                return !string.IsNullOrWhiteSpace(cfg.ProjectsRoot);
            }));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step3.Title", "Choose your destination mode"),
            L("Onboarding.Tour.Step3.Body", "Use simple mode for a single backup location, or enable advanced destinations to manage multiple paths and credentials."),
            "DestinationsModeToggle",
            "Settings",
            () => true,
            autoAdvance: false));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step4.Title", "Add a destination"),
            L("Onboarding.Tour.Step4.Body", "Add at least one destination where backups should be stored."),
            "AddDestinationButton",
            "Settings",
            () =>
            {
                var cfg = AppConfigStore.Load();
                if (!cfg.Backups.UseAdvancedDestinations)
                    return true;
                return cfg.Backups.Destinations.Any(d => !string.IsNullOrWhiteSpace(d.Path));
            },
            isApplicable: () => AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step5.Title", "Destination basics"),
            L("Onboarding.Tour.Step5.Body", "Set a label and enable/disable the destination so VaultSync knows where to write."),
            "DestinationBasicsRow",
            "Settings",
            () => true,
            autoAdvance: false,
            isApplicable: () => AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step6.Title", "Destination path"),
            L("Onboarding.Tour.Step6.Body", "Choose the path where backups are stored for this destination."),
            "DestinationPathRow",
            "Settings",
            () => true,
            autoAdvance: false,
            isApplicable: () => AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step7.Title", "Destination credentials"),
            L("Onboarding.Tour.Step7.Body", "Attach a credential profile for NAS/SMB destinations and test connectivity."),
            "DestinationCredentialRow",
            "Settings",
            () => true,
            autoAdvance: false,
            isApplicable: () => AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step8.Title", "Mount behavior"),
            L("Onboarding.Tour.Step8.Body", "Control whether VaultSync mounts the destination automatically and how it handles pre-mounted paths."),
            "DestinationMountOptionsRow",
            "Settings",
            () => true,
            autoAdvance: false,
            isApplicable: () => AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step9.Title", "History sync"),
            L("Onboarding.Tour.Step9.Body", "Enable history sync and import options to keep backups consistent across devices."),
            "DestinationHistoryOptionsRow",
            "Settings",
            () => true,
            autoAdvance: false,
            isApplicable: () => AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step10.Title", "Credential profiles"),
            L("Onboarding.Tour.Step10.Body", "Create credential profiles for network destinations so VaultSync can connect securely."),
            "CredentialProfilesSection",
            "Settings",
            () => true,
            autoAdvance: false,
            isApplicable: () => AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step11.Title", "Select a backup destination"),
            L("Onboarding.Tour.Step11.Body", "Choose the folder where your backups will be stored in simple mode."),
            () =>
            {
                var cfg = AppConfigStore.Load();
                return cfg.Backups.UseAdvancedDestinations ? string.Empty : "BackupLocationInput";
            },
            "Settings",
            () =>
            {
                var cfg = AppConfigStore.Load();
                if (cfg.Backups.UseAdvancedDestinations)
                    return true;
                return HasDestinationConfigured(cfg);
            },
            isApplicable: () => !AppConfigStore.Load().Backups.UseAdvancedDestinations));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step12.Title", "Backup settings"),
            L("Onboarding.Tour.Step12.Body", "Review auto backup settings, retention, and history sync for your projects."),
            "SettingsBackupsCard",
            "Settings",
            () => true,
            autoAdvance: false));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step13.Title", "Appearance settings"),
            L("Onboarding.Tour.Step13.Body", "Control theme, compact layout, and project avatars."),
            "SettingsAppearanceCard",
            "Settings",
            () => true,
            autoAdvance: false));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step14.Title", "Notification settings"),
            L("Onboarding.Tour.Step14.Body", "Choose when VaultSync notifies you about backups and warnings."),
            "SettingsNotificationsCard",
            "Settings",
            () => true,
            autoAdvance: false));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step15.Title", "Advanced settings"),
            L("Onboarding.Tour.Step15.Body", "Configure logging, update checks, and beta channel options."),
            "SettingsAdvancedCard",
            "Settings",
            () => true,
            autoAdvance: false));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step16.Title", "Danger zone"),
            L("Onboarding.Tour.Step16.Body", "Use these actions to clear cache or forget projects when troubleshooting."),
            "SettingsDangerCard",
            "Settings",
            () => true,
            autoAdvance: false));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step17.Title", "Add a project"),
            L("Onboarding.Tour.Step17.Body", "Select a project and add it to VaultSync to start tracking snapshots."),
            "ProjectSnapshotButton",
            "Projects",
            () => _app.ProjectsViewModel.Projects.Any(p => p.IsRegistered)));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step18.Title", "Enable auto backups"),
            L("Onboarding.Tour.Step18.Body", "Turn on auto backups for a project to keep snapshots up to date."),
            "AutoBackupToggle",
            "Backups",
            () => true,
            autoAdvance: false));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step19.Title", "Run your first backup"),
            L("Onboarding.Tour.Step19.Body", "Start a backup for a project to create the first snapshot."),
            "PerProjectBackupButton",
            "Backups",
            () => _app.BackupsViewModel.HasAnyBackups));
    }

    private static bool HasDestinationConfigured(AppConfig cfg)
    {
        if (cfg.Backups.UseAdvancedDestinations)
        {
            return cfg.Backups.Destinations.Any(d =>
                d.Active && !string.IsNullOrWhiteSpace(d.Path));
        }

        var root = cfg.Backups.BackupRoot ?? cfg.Backups.BackupLocation ?? string.Empty;
        return !string.IsNullOrWhiteSpace(root);
    }

    private void Poll()
    {
        if (!IsActive)
            return;

        UpdateState();

        if (!IsOnRequiredView && ShouldNavigate())
        {
            NavigateToRequiredView();
        }

        if (IsStepComplete && IsOnRequiredView)
        {
            if ((CurrentStep?.AutoAdvance ?? true) && Interlocked.Exchange(ref _advanceQueued, 1) == 0)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(650);
                    if (IsStepComplete && IsOnRequiredView && (CurrentStep?.AutoAdvance ?? true))
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
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(IsPrimaryEnabled));
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
        NavigateToRequiredView();
    }

    private void EnsureApplicableStep()
    {
        var safety = _steps.Count + 1;
        while (safety-- > 0 && CurrentStep is not null && !CurrentStep.IsApplicable())
        {
            if (IsLastStep)
                break;

            _index = Math.Min(_index + 1, _steps.Count - 1);
        }
    }

    private bool ShouldNavigate()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastNavigateAt).TotalMilliseconds < 900)
            return false;

        _lastNavigateAt = now;
        return true;
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }
}
