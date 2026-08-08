using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class GuideViewModel : ViewModelBase
{
    private readonly AppViewModel _app;
    private readonly IAppConfigStore _configStore;
    private readonly IRepositoryFactory _repositoryFactory;
    private string _progressSummary = "Checking your setup…";
    private string _pageTitle = string.Empty;
    private string _pageDescription = string.Empty;
    private string _restartSetupLabel = string.Empty;
    private string _documentationLabel = string.Empty;
    private string _recoveryRuleTitle = string.Empty;
    private string _recoveryRuleBody = string.Empty;
    private string _terminologyTitle = string.Empty;
    private string _terminologyDescription = string.Empty;

    public GuideViewModel(
        AppViewModel app,
        IAppConfigStore configStore,
        IRepositoryFactory repositoryFactory)
    {
        _app = app;
        _configStore = configStore;
        _repositoryFactory = repositoryFactory;
        RestartSetupCommand = new RelayCommand(_ => _app.OnboardingTour.Start());
        OpenDocumentationCommand = new RelayCommand(_ =>
            SystemFileLauncher.OpenUri("https://github.com/flaviorame/VaultSync/wiki"));
        RefreshHeaderText();
        RebuildContent();
    }

    public ObservableCollection<GuideTopicViewModel> Topics { get; } = [];
    public ObservableCollection<GuideTermViewModel> Terms { get; } = [];
    public ICommand RestartSetupCommand
    {
        get;
    }
    public ICommand OpenDocumentationCommand
    {
        get;
    }

    public string PageTitle => _pageTitle;
    public string PageDescription => _pageDescription;
    public string RestartSetupLabel => _restartSetupLabel;
    public string DocumentationLabel => _documentationLabel;
    public string RecoveryRuleTitle => _recoveryRuleTitle;
    public string RecoveryRuleBody => _recoveryRuleBody;
    public string TerminologyTitle => _terminologyTitle;
    public string TerminologyDescription => _terminologyDescription;

    private void RefreshHeaderText()
    {
        _pageTitle = L("Guide.Title", "VaultSync guide");
        _pageDescription = L("Guide.Description", "A practical path from first setup to a recovery you have actually proved.");
        _restartSetupLabel = L("Guide.RestartSetup", "Restart guided setup");
        _documentationLabel = L("Guide.Documentation", "Full documentation");
        _recoveryRuleTitle = L("Guide.RecoveryRule.Title", "The recovery rule");
        _recoveryRuleBody = L("Guide.RecoveryRule.Body", "A backup is not recovery proof. VaultSync treats the destination, credentials, integrity check, restore plan, and drill as separate evidence. Recovery tells you exactly which one is missing.");
        _terminologyTitle = L("Guide.Terms.Title", "What VaultSync terms mean");
        _terminologyDescription = L("Guide.Terms.Description", "These labels describe different artifacts or evidence. They are not interchangeable.");
    }

    private void RebuildContent()
    {
        Topics.Clear();
        foreach (GuideTopicViewModel topic in new GuideTopicViewModel[]
        {
            new GuideTopicViewModel(
                "1",
                L("Guide.Topic.Setup.Title", "Set up VaultSync"),
                L("Guide.Topic.Setup.Body", "Choose a projects folder and one or more backup destinations. Start simple; destinations and encryption can be refined later."),
                L("Onboarding.GoSettings", "Open Settings"),
                _app.NavigateSettings),
            new GuideTopicViewModel(
                "2",
                L("Guide.Topic.Project.Title", "Protect a project"),
                L("Guide.Topic.Project.Body", "Register a project, assign its repository and exclusions, then create the first backup."),
                L("Onboarding.GoProjects", "Open Projects"),
                _app.NavigateProjects),
            new GuideTopicViewModel(
                "3",
                L("Guide.Topic.Schedule.Title", "Choose when protection runs"),
                L("Guide.Topic.Schedule.Body", "Choose manual or automatic protection, an interval, and optional quiet hours. The next run is always explained."),
                L("Nav.Schedule", "Schedule"),
                _app.NavigateSchedule),
            new GuideTopicViewModel(
                "4",
                L("Guide.Topic.Backups.Title", "Browse and restore"),
                L("Guide.Topic.Backups.Body", "Use Backups to review restore points, compare snapshots, verify content, and restore only what you need."),
                L("Onboarding.GoBackups", "Open Backups"),
                _app.NavigateBackups),
            new GuideTopicViewModel(
                "5",
                L("Guide.Topic.Recovery.Title", "Prove recovery"),
                L("Guide.Topic.Recovery.Body", "Recovery explains blockers, shows the evidence behind each result, and guides you through a safe recovery drill."),
                L("Onboarding.GoRecovery", "Open Recovery"),
                _app.NavigateRecovery)
        })
        {
            Topics.Add(topic);
        }

        Terms.Clear();
        foreach (GuideTermViewModel term in new GuideTermViewModel[]
        {
            new GuideTermViewModel(L("Guide.Term.Backup.Title", "Backup"), L("Guide.Term.Backup.Body", "A stored copy of project data at a destination. Backups are the payload used for restore.")),
            new GuideTermViewModel(L("Guide.Term.Snapshot.Title", "Snapshot"), L("Guide.Term.Snapshot.Body", "The indexed file inventory captured at one moment. It records what changed and what a backup represents.")),
            new GuideTermViewModel(L("Guide.Term.RestorePoint.Title", "Restore point"), L("Guide.Term.RestorePoint.Body", "A backup and snapshot combination that VaultSync can present as a recovery choice.")),
            new GuideTermViewModel(L("Guide.Term.Verification.Title", "Verification"), L("Guide.Term.Verification.Body", "An integrity check of stored backup content. Passing verification does not replace a restore drill.")),
            new GuideTermViewModel(L("Guide.Term.KnownGood.Title", "Known good"), L("Guide.Term.KnownGood.Body", "A restore point you explicitly mark as reliable after reviewing its evidence.")),
            new GuideTermViewModel(L("Guide.Term.Protected.Title", "Protected"), L("Guide.Term.Protected.Body", "A restore point excluded from automatic retention cleanup. Protection does not prove integrity.")),
            new GuideTermViewModel(L("Guide.Term.Drill.Title", "Recovery drill"), L("Guide.Term.Drill.Body", "A safe test that exercises recovery steps and records evidence without overwriting the project."))
        })
        {
            Terms.Add(term);
        }
    }

    public string ProgressSummary
    {
        get => _progressSummary;
        private set => SetField(ref _progressSummary, value);
    }

    public void Refresh()
    {
        RefreshHeaderText();
        RebuildContent();
        OnPropertiesChanged(
            nameof(PageTitle),
            nameof(PageDescription),
            nameof(RestartSetupLabel),
            nameof(DocumentationLabel),
            nameof(RecoveryRuleTitle),
            nameof(RecoveryRuleBody),
            nameof(TerminologyTitle),
            nameof(TerminologyDescription));
        try
        {
            AppConfig config = _configStore.GetSnapshot();
            SqliteRepository repo = _repositoryFactory.Create(config);
            repo.EnsureSchema();
            int projects = repo.GetAllProjects().Count();
            int backups = repo.GetBackupCount();
            int passedDrills = repo.GetRecoveryDrills()
                .GroupBy(drill => drill.ProjectId)
                .Count(group => group.OrderByDescending(drill => drill.RunUtc).First().Status ==
                                Core.Models.RecoveryDrillStatus.Passed);
            ProgressSummary = string.Format(
                L("Guide.Progress", "{0} project(s) · {1} backup(s) · {2} recovery proof(s) passed"),
                projects,
                backups,
                passedDrills);
        }
        catch
        {
            ProgressSummary = L("Guide.ProgressUnavailable", "Setup progress is temporarily unavailable.");
        }
    }

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}

public sealed record GuideTopicViewModel(
    string Number,
    string Title,
    string Body,
    string ActionLabel,
    ICommand ActionCommand);

public sealed record GuideTermViewModel(string Title, string Body);
