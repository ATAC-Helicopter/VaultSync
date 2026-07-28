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
        Topics =
        [
            new GuideTopicViewModel(
                "1",
                "Set up VaultSync",
                "Choose a projects folder and one or more backup destinations. Start simple; destinations and encryption can be refined later.",
                "Open Settings",
                _app.NavigateSettings),
            new GuideTopicViewModel(
                "2",
                "Protect a project",
                "Register a project, create its first backup, and inspect the resulting snapshot before relying on it.",
                "Open Projects",
                _app.NavigateProjects),
            new GuideTopicViewModel(
                "3",
                "Browse and restore",
                "Use Backups to compare points, browse files, verify content, and restore only what you need.",
                "Open Backups",
                _app.NavigateBackups),
            new GuideTopicViewModel(
                "4",
                "Prove recovery",
                "Recovery explains blockers, shows the evidence behind each result, and guides you through a safe recovery drill.",
                "Open Recovery",
                _app.NavigateRecovery)
        ];
    }

    public ObservableCollection<GuideTopicViewModel> Topics { get; }
    public ICommand RestartSetupCommand { get; }
    public ICommand OpenDocumentationCommand { get; }

    public string ProgressSummary
    {
        get => _progressSummary;
        private set => SetField(ref _progressSummary, value);
    }

    public void Refresh()
    {
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
            ProgressSummary = $"{projects} project(s) · {backups} backup(s) · {passedDrills} recovery proof(s) passed";
        }
        catch
        {
            ProgressSummary = "Setup progress is temporarily unavailable.";
        }
    }
}

public sealed record GuideTopicViewModel(
    string Number,
    string Title,
    string Body,
    string ActionLabel,
    ICommand ActionCommand);
