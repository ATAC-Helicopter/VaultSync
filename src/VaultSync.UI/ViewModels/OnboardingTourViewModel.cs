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
    public string TargetName { get; }
    public string RequiredView { get; }
    public Func<bool> IsComplete { get; }

    public OnboardingTourStep(string title, string body, string targetName, string requiredView, Func<bool> isComplete)
    {
        Title = title;
        Body = body;
        TargetName = targetName;
        RequiredView = requiredView;
        IsComplete = isComplete;
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

    public event Action? TourCompleted;

    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }

    public string StepCounter => Lf("Onboarding.StepCounter", "Step {0} of {1}", _index + 1, _steps.Count);
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
            L("Onboarding.Tour.Step1.Title", "Set your projects root"),
            L("Onboarding.Tour.Step1.Body", "Choose the folder where your projects live so VaultSync can find them."),
            "ProjectsRootInput",
            "Settings",
            () =>
            {
                var cfg = AppConfigStore.Load();
                return !string.IsNullOrWhiteSpace(cfg.ProjectsRoot);
            }));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step2.Title", "Add a project"),
            L("Onboarding.Tour.Step2.Body", "Select a project and add it to VaultSync to start tracking snapshots."),
            "ProjectSnapshotButton",
            "Projects",
            () => _app.ProjectsViewModel.Projects.Any(p => p.IsRegistered)));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step3.Title", "Pick a preset"),
            L("Onboarding.Tour.Step3.Body", "Use a preset to filter temp files. You can keep the auto-selected one."),
            "ProjectPresetCombo",
            "Projects",
            () =>
            {
                var selected = _app.ProjectsViewModel.SelectedProject;
                return selected is not null && !string.IsNullOrWhiteSpace(selected.Preset);
            }));

        _steps.Add(new OnboardingTourStep(
            L("Onboarding.Tour.Step4.Title", "Run your first backup"),
            L("Onboarding.Tour.Step4.Body", "Start a backup to lock in your first snapshot on your chosen destination."),
            "BackupAllButton",
            "Backups",
            () => _app.BackupsViewModel.HasAnyBackups));
    }

    private void Poll()
    {
        if (!IsActive)
            return;

        UpdateState();

        if (IsStepComplete && IsOnRequiredView)
        {
            if (Interlocked.Exchange(ref _advanceQueued, 1) == 0)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(650);
                    if (IsStepComplete && IsOnRequiredView)
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
        UpdateState();
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }
}
