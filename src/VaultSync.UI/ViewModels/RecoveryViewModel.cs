using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class RecoveryViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromSeconds(20);
    private readonly IAppConfigStore _configStore;
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly RestoreReadinessService _readinessService = new();
    private int _refreshInFlight;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private string _headline = "Loading recovery readiness...";
    private string _detail = "Checking projects, backup recency, verification policy, and destination availability.";
    private int _readyCount;
    private int _attentionCount;
    private int _riskCount;
    private int _unavailableCount;
    private int _projectCount;
    private int _coverage24Hours;
    private int _coverage7Days;
    private int _coverage30Days;
    private int _coverage90Days;
    private string _coverageSummary = L("Recovery.Coverage.Empty", "Recovery coverage will appear after backups are available.");
    private string _topRecommendation = L("Recovery.Recommendation.Start", "Create backups for tracked projects to start measuring recovery coverage.");
    private int _readinessPercent;
    private string _readinessBand = L("Recovery.Band.NotMeasured", "Not measured");
    private string _insight = L("Recovery.Insight.Empty", "Add a project and create a backup to measure recovery readiness.");

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string LF(string key, string fallback, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L(key, fallback), args);

    public RecoveryViewModel()
        : this(StaticAppConfigStore.Instance, new SqliteRepositoryFactory(StaticAppConfigStore.Instance))
    {
    }

    internal RecoveryViewModel(IAppConfigStore configStore, IRepositoryFactory? repositoryFactory = null)
    {
        _configStore = configStore;
        _repositoryFactory = repositoryFactory ?? new SqliteRepositoryFactory(_configStore);
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(force: true));
    }

    public ObservableCollection<RecoveryProjectViewModel> Projects { get; } = [];

    public ICommand RefreshCommand { get; }

    public string Headline
    {
        get => _headline;
        private set => SetField(ref _headline, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    public int ReadyCount
    {
        get => _readyCount;
        private set => SetField(ref _readyCount, value);
    }

    public int AttentionCount
    {
        get => _attentionCount;
        private set => SetField(ref _attentionCount, value);
    }

    public int RiskCount
    {
        get => _riskCount;
        private set => SetField(ref _riskCount, value);
    }

    public int UnavailableCount
    {
        get => _unavailableCount;
        private set => SetField(ref _unavailableCount, value);
    }

    public int ProjectCount
    {
        get => _projectCount;
        private set => SetField(ref _projectCount, value);
    }

    public int Coverage24Hours
    {
        get => _coverage24Hours;
        private set => SetField(ref _coverage24Hours, value);
    }

    public int Coverage7Days
    {
        get => _coverage7Days;
        private set => SetField(ref _coverage7Days, value);
    }

    public int Coverage30Days
    {
        get => _coverage30Days;
        private set => SetField(ref _coverage30Days, value);
    }

    public int Coverage90Days
    {
        get => _coverage90Days;
        private set => SetField(ref _coverage90Days, value);
    }

    public string CoverageSummary
    {
        get => _coverageSummary;
        private set => SetField(ref _coverageSummary, value);
    }

    public string TopRecommendation
    {
        get => _topRecommendation;
        private set => SetField(ref _topRecommendation, value);
    }

    public int ReadinessPercent
    {
        get => _readinessPercent;
        private set => SetField(ref _readinessPercent, value);
    }

    public string ReadinessScoreLabel => $"{ReadinessPercent}%";

    public string ReadinessBand
    {
        get => _readinessBand;
        private set => SetField(ref _readinessBand, value);
    }

    public string Insight
    {
        get => _insight;
        private set => SetField(ref _insight, value);
    }

    public int Coverage24Percent => Percent(Coverage24Hours, ProjectCount);
    public int Coverage7Percent => Percent(Coverage7Days, ProjectCount);
    public int Coverage30Percent => Percent(Coverage30Days, ProjectCount);
    public int Coverage90Percent => Percent(Coverage90Days, ProjectCount);
    public string ProjectSummaryLabel => LF("Recovery.ProjectSummary", "{0} project(s) measured", ProjectCount);

    public bool HasProjects => Projects.Count > 0;

    public async Task RefreshAsync(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastRefreshUtc) < RefreshTtl)
            return;

        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
            return;

        try
        {
            RecoveryData data = await Task.Run(LoadData).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyData(data));
            _lastRefreshUtc = DateTime.UtcNow;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private RecoveryData LoadData()
    {
        AppConfig config = _configStore.GetSnapshot();
        SqliteRepository repo = _repositoryFactory.Create(config);
        repo.EnsureSchema();
        var projects = repo.GetAllProjects().ToList();
        var backups = repo.GetAllBackups();
        IReadOnlyDictionary<int, SnapshotHistoryMetadata> metadataBySnapshotId =
            repo.GetSnapshotHistoryMetadataBySnapshotIds(backups.Select(backup => backup.SnapshotId));
        RestoreReadinessSummary summary = _readinessService.BuildSummary(
            projects,
            backups,
            config,
            snapshotMetadataById: metadataBySnapshotId);

        var latestBackups = backups
            .GroupBy(backup => backup.ProjectId)
            .Select(group => group.OrderByDescending(backup => backup.CreatedUtc).ThenByDescending(backup => backup.Id).First())
            .ToList();

        DateTime now = DateTime.UtcNow;
        int within24Hours = latestBackups.Count(backup => now - backup.CreatedUtc <= TimeSpan.FromHours(24));
        int within7Days = latestBackups.Count(backup => now - backup.CreatedUtc <= TimeSpan.FromDays(7));
        int within30Days = latestBackups.Count(backup => now - backup.CreatedUtc <= TimeSpan.FromDays(30));
        int within90Days = latestBackups.Count(backup => now - backup.CreatedUtc <= TimeSpan.FromDays(90));

        string coverageSummary = projects.Count == 0
            ? L("Recovery.NoProjects", "No tracked projects yet.")
            : LF(
                "Recovery.Coverage.Summary",
                "{0}/{1} project(s) have a backup from the last 24 hours; {2}/{1} are covered within 7 days.",
                within24Hours,
                projects.Count,
                within7Days);

        ProjectRestoreReadiness? firstIssue = summary.Projects
            .Where(project => project.State != RestoreReadinessState.Ready)
            .OrderBy(project => project.Score)
            .FirstOrDefault();

        string topRecommendation = firstIssue is null
            ? L("Recovery.Recommendation.AllReady", "All tracked projects with backups are currently restore-ready.")
            : LF("Recovery.Recommendation.ProjectReason", "{0}: {1}", firstIssue.ProjectName, firstIssue.Reason);

        return new RecoveryData(
            summary,
            within24Hours,
            within7Days,
            within30Days,
            within90Days,
            coverageSummary,
            topRecommendation);
    }

    private void ApplyData(RecoveryData data)
    {
        RestoreReadinessSummary summary = data.Summary;
        ReadyCount = summary.ReadyCount;
        AttentionCount = summary.AttentionCount;
        RiskCount = summary.RiskCount;
        UnavailableCount = summary.UnavailableCount;
        ProjectCount = summary.ProjectCount;
        ReadinessPercent = summary.ProjectCount == 0
            ? 0
            : (int)Math.Round(summary.Projects.Average(project => project.Score));
        ReadinessBand = ReadinessPercent switch
        {
            >= 85 => L("Recovery.Band.Ready", "Ready"),
            >= 60 => L("Recovery.Band.Review", "Review"),
            >= 35 => L("Recovery.Band.AtRisk", "At risk"),
            _ => L("Recovery.Band.Unavailable", "Unavailable")
        };
        Headline = summary.Headline;
        Detail = summary.Detail;
        Coverage24Hours = data.Coverage24Hours;
        Coverage7Days = data.Coverage7Days;
        Coverage30Days = data.Coverage30Days;
        Coverage90Days = data.Coverage90Days;
        CoverageSummary = data.CoverageSummary;
        TopRecommendation = data.TopRecommendation;
        Insight = BuildInsight(summary);

        Projects.Clear();
        foreach (ProjectRestoreReadiness project in summary.Projects
                     .OrderBy(project => project.Score)
                     .ThenBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase))
            Projects.Add(new RecoveryProjectViewModel(project));

        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(ReadinessScoreLabel));
        OnPropertyChanged(nameof(Coverage24Percent));
        OnPropertyChanged(nameof(Coverage7Percent));
        OnPropertyChanged(nameof(Coverage30Percent));
        OnPropertyChanged(nameof(Coverage90Percent));
        OnPropertyChanged(nameof(ProjectSummaryLabel));
    }

    private static int Percent(int value, int total) =>
        total <= 0 ? 0 : (int)Math.Round(value * 100.0 / total);

    private static string BuildInsight(RestoreReadinessSummary summary)
    {
        if (summary.ProjectCount == 0)
            return L("Recovery.Insight.Empty", "Add a project and create a backup to measure recovery readiness.");

        if (summary.UnavailableCount > 0)
        {
            return LF(
                "Recovery.Insight.Unavailable",
                "{0} project(s) cannot be considered restore-ready yet. Start with the first project in the list.",
                summary.UnavailableCount);
        }

        if (summary.RiskCount > 0)
        {
            return LF(
                "Recovery.Insight.Risk",
                "{0} project(s) are recoverable but need attention before this is release-ready.",
                summary.RiskCount);
        }

        if (summary.AttentionCount > 0)
        {
            return LF(
                "Recovery.Insight.Attention",
                "{0} project(s) should be reviewed, but the main recovery baseline exists.",
                summary.AttentionCount);
        }

        return L("Recovery.Insight.Ready", "All measured projects have a healthy recovery baseline.");
    }

    private sealed record RecoveryData(
        RestoreReadinessSummary Summary,
        int Coverage24Hours,
        int Coverage7Days,
        int Coverage30Days,
        int Coverage90Days,
        string CoverageSummary,
        string TopRecommendation);
}

public sealed class RecoveryProjectViewModel
{
    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    public RecoveryProjectViewModel(ProjectRestoreReadiness project)
    {
        ProjectName = project.ProjectName;
        Label = project.Label;
        Score = project.Score;
        Reason = project.Reason;
        Accent = project.State switch
        {
            RestoreReadinessState.Ready => "#22CC88",
            RestoreReadinessState.Attention => "#F2B84B",
            RestoreReadinessState.Risk => "#FF7A45",
            _ => "#D9534F"
        };
    }

    public string ProjectName { get; }
    public string Label { get; }
    public int Score { get; }
    public string Reason { get; }
    public string Accent { get; }
    public string ScoreLabel => $"{Score}%";
    public int ScoreValue => Score;
    public string TrackLabel => Score switch
    {
        >= 85 => L("Recovery.Track.Clean", "Clean"),
        >= 60 => L("Recovery.Track.Review", "Review"),
        >= 35 => L("Recovery.Track.Risk", "Risk"),
        _ => L("Recovery.Track.Blocked", "Blocked")
    };
}
