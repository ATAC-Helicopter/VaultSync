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
    private readonly List<OnboardingTourStep> _steps = [];
    private readonly DispatcherTimer _pollTimer;
    private int _index;
    private bool _isActive;
    private bool _isStepComplete;
    private int _advanceQueued;
    private DateTime _lastNavigateAt;
    private string? _lastRequiredView;
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

    public static string SkipLabel => L("Onboarding.Skip", "Skip");

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
        bool UseAdvanced()
        {
            SettingsViewModel? live = _app.SettingsViewModel;
            if (live is not null)
                return live.UseAdvancedDestinations;

            return GetConfig().Backups.UseAdvancedDestinations;
        }
        bool HasAdvancedDestination()
        {
            SettingsViewModel? live = _app.SettingsViewModel;
            if (live is not null)
            {
                // Step 4 is "Add a destination": it should complete when an entry exists.
                // Path configuration is guided by the following onboarding steps.
                return live.Destinations.Count > 0;
            }

            AppConfig cfg = GetConfig();
            return cfg.Backups.Destinations.Any();
        }
        bool HasLanguageSelected()
        {
            AppConfig cfg = GetConfig();
            return !string.IsNullOrWhiteSpace(cfg.Advanced.Language);
        }
        bool HasProjectsRoot()
        {
            SettingsViewModel? live = _app.SettingsViewModel;
            if (live is not null)
                return !string.IsNullOrWhiteSpace(live.ProjectsRootPath);

            AppConfig cfg = GetConfig();
            return !string.IsNullOrWhiteSpace(cfg.ProjectsRoot);
        }
        void AddStep(string title, string body, string targetName, string requiredView, Func<bool> isComplete, bool autoAdvance = true, Func<bool>? isApplicable = null)
            => _steps.Add(new OnboardingTourStep(title, body, targetName, requiredView, isComplete, autoAdvance, isApplicable));
        void AddStepDynamic(string title, string body, Func<string> targetNameProvider, string requiredView, Func<bool> isComplete, bool autoAdvance = true, Func<bool>? isApplicable = null)
            => _steps.Add(new OnboardingTourStep(title, body, targetNameProvider, requiredView, isComplete, autoAdvance, isApplicable));
        void AddSettingsStep(string title, string body, string targetName, Func<bool>? isComplete = null, bool autoAdvance = true, Func<bool>? isApplicable = null)
            => AddStep(title, body, targetName, "Settings", isComplete ?? (() => true), autoAdvance, isApplicable);

        AddSettingsStep(
            L("Onboarding.Tour.Step1.Title", "Choose your language"),
            L("Onboarding.Tour.Step1.Body", "Pick the language for VaultSync. We default to your system language when available."),
            "LanguageSelectCombo",
            HasLanguageSelected);

        AddSettingsStep(
            L("Onboarding.Tour.Step2.Title", "Set your projects root"),
            L("Onboarding.Tour.Step2.Body", "Choose the folder where your projects live so VaultSync can discover them."),
            "ProjectsRootRow",
            HasProjectsRoot);

        AddSettingsStep(
            L("Onboarding.Tour.Step3.Title", "Choose your destination mode"),
            L("Onboarding.Tour.Step3.Body", "Use simple mode for a single backup location, or enable advanced destinations to manage multiple paths and credentials."),
            "DestinationsModeToggle",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step4.Title", "Add a destination"),
            L("Onboarding.Tour.Step4.Body", "Add at least one destination where backups should be stored."),
            "AddDestinationButton",
            () =>
            {
                if (!UseAdvanced())
                    return true;
                return HasAdvancedDestination();
            },
            isApplicable: UseAdvanced);

        AddSettingsStep(
            L("Onboarding.Tour.Step5.Title", "Destination basics"),
            L("Onboarding.Tour.Step5.Body", "Set a label and enable/disable the destination so VaultSync knows where to write."),
            "DestinationBasicsRow",
            autoAdvance: false,
            isApplicable: UseAdvanced);

        AddSettingsStep(
            L("Onboarding.Tour.Step6.Title", "Destination path"),
            L("Onboarding.Tour.Step6.Body", "Choose the path where backups are stored for this destination."),
            "DestinationPathRow",
            autoAdvance: false,
            isApplicable: UseAdvanced);

        AddSettingsStep(
            L("Onboarding.Tour.Step7.Title", "Destination credentials"),
            L("Onboarding.Tour.Step7.Body", "Attach a credential profile for NAS/SMB destinations and test connectivity."),
            "DestinationCredentialRow",
            autoAdvance: false,
            isApplicable: UseAdvanced);

        AddSettingsStep(
            L("Onboarding.Tour.Step8.Title", "Mount behavior"),
            L("Onboarding.Tour.Step8.Body", "Control whether VaultSync mounts the destination automatically and how it handles pre-mounted paths."),
            "DestinationMountOptionsRow",
            autoAdvance: false,
            isApplicable: UseAdvanced);

        AddSettingsStep(
            L("Onboarding.Tour.Step9.Title", "History sync"),
            L("Onboarding.Tour.Step9.Body", "Enable history sync and import options to keep backups consistent across devices."),
            "DestinationHistoryOptionsRow",
            autoAdvance: false,
            isApplicable: UseAdvanced);

        AddSettingsStep(
            L("Onboarding.Tour.Step10.Title", "Credential profiles"),
            L("Onboarding.Tour.Step10.Body", "Create credential profiles for network destinations so VaultSync can connect securely."),
            "CredentialProfilesSection",
            autoAdvance: false,
            isApplicable: UseAdvanced);

        AddStepDynamic(
            L("Onboarding.Tour.Step11.Title", "Select a backup destination"),
            L("Onboarding.Tour.Step11.Body", "Choose the folder where your backups will be stored in simple mode."),
            () => UseAdvanced() ? string.Empty : "BackupLocationRow",
            "Settings",
            () =>
            {
                if (UseAdvanced())
                    return true;

                SettingsViewModel? live = _app.SettingsViewModel;
                if (live is not null)
                    return !string.IsNullOrWhiteSpace(live.BackupLocationPath);

                AppConfig cfg = GetConfig();
                return HasDestinationConfigured(cfg);
            },
            isApplicable: () => !UseAdvanced());

        AddSettingsStep(
            L("Onboarding.Tour.Step12.Title", "Backup settings"),
            L("Onboarding.Tour.Step12.Body", "Review auto backup settings, retention, and history sync for your projects."),
            "SettingsBackupsCard",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step26.Title", "Bandwidth and quiet hours"),
            L("Onboarding.Tour.Step26.Body", "Set transfer limits and quiet hours so automatic backups pause/defer at the right time without stopping active runs."),
            "SettingsQuietHoursWindowCard",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step20.Title", "Global encryption"),
            L("Onboarding.Tour.Step20.Body", "Encryption is off by default. Enable it here when you want new backups encrypted by default, then review secure password status."),
            "SettingsEncryptionCard",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step21.Title", "Set backup encryption password"),
            L("Onboarding.Tour.Step21.Body", "Set or clear the global encryption password used by projects that inherit global protection."),
            "SettingsEncryptionPasswordInput",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step13.Title", "Appearance settings"),
            L("Onboarding.Tour.Step13.Body", "Control theme, build custom palettes, and adjust compact layout or project avatars."),
            "SettingsAppearanceCard",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step14.Title", "Notification settings"),
            L("Onboarding.Tour.Step14.Body", "Choose when VaultSync notifies you about backups and warnings."),
            "SettingsNotificationsCard",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step15.Title", "Advanced settings"),
            L("Onboarding.Tour.Step15.Body", "Configure logging, update checks, and beta channel options."),
            "SettingsAdvancedCard",
            autoAdvance: false);

        AddSettingsStep(
            L("Onboarding.Tour.Step16.Title", "Danger zone"),
            L("Onboarding.Tour.Step16.Body", "Use these actions to clear cache or forget projects when troubleshooting."),
            "SettingsDangerCard",
            autoAdvance: false);

        AddStep(
            L("Onboarding.Tour.Step17.Title", "Add a project"),
            L("Onboarding.Tour.Step17.Body", "Select a project and add it to VaultSync to start tracking snapshots."),
            "ProjectSnapshotButton",
            "Projects",
            () => _app.ProjectsViewModel.Projects.Any(p => p.IsRegistered));

        AddStep(
            L("Onboarding.Tour.Step28.Title", "Project tag colors"),
            L("Onboarding.Tour.Step28.Body", "Type or pick a tag in Projects, then open the color editor here to style that tag app-wide with a live preview."),
            "ProjectTagsEditorSection",
            "Projects",
            () => true,
            autoAdvance: false);

        AddStep(
            L("Onboarding.Tour.Step22.Title", "Project encryption policy"),
            L("Onboarding.Tour.Step22.Body", "Set the per-project encryption policy from the Backups page so each project can inherit global protection, force encryption, or stay plain."),
            "BackupsProjectEncryptionPolicyCombo",
            "Backups",
            () => true,
            autoAdvance: false);

        AddStep(
            L("Onboarding.Tour.Step23.Title", "Project encryption password"),
            L("Onboarding.Tour.Step23.Body", "Set or clear the project-specific encryption password from the Backups page."),
            "BackupsProjectEncryptionPasswordButton",
            "Backups",
            () => true,
            autoAdvance: false);

        AddStep(
            L("Onboarding.Tour.Step27.Title", "Snapshot diff summaries"),
            L("Onboarding.Tour.Step27.Body", "Each backup now shows diff stats (added, changed, deleted, net size) with preview and export actions."),
            "BackupsHistorySection",
            "Backups",
            () => true,
            autoAdvance: false);

        AddStep(
            L("Onboarding.Tour.Step18.Title", "Enable auto backups"),
            L("Onboarding.Tour.Step18.Body", "Turn on auto backups for a project to keep snapshots up to date."),
            "AutoBackupToggle",
            "Backups",
            () => true,
            autoAdvance: false);

        AddStep(
            L("Onboarding.Tour.Step19.Title", "Run your first backup"),
            L("Onboarding.Tour.Step19.Body", "Start a backup for a project to create the first snapshot."),
            "PerProjectBackupButton",
            "Backups",
            () => _app.BackupsViewModel.HasAnyBackups);
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

        MaybeNavigate();

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
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(IsPrimaryEnabled));

        string? required = CurrentStep?.RequiredView;
        if (!string.Equals(_lastRequiredView, required, StringComparison.OrdinalIgnoreCase))
        {
            _lastRequiredView = required;
            _lastNavigateAt = DateTime.MinValue;
        }
    }

    private void MaybeNavigate()
    {
        if (!IsOnRequiredView && ShouldNavigate())
        {
            NavigateToRequiredView();
        }
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
        NavigateToRequiredView();
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

    private bool ShouldNavigate()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastNavigateAt).TotalMilliseconds < 900)
            return false;

        _lastNavigateAt = now;
        return true;
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
