using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

public sealed class HistoryViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromSeconds(20);
    private readonly IAppConfigStore _configStore;
    private readonly IRepositoryFactory _repositoryFactory;
    private int _refreshInFlight;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private string _summary = L("History.Summary.Empty", "Project history will appear here as backups and snapshots are created.");
    private string _emptyState = L("History.Empty", "No history yet. Create a backup to start building a project timeline.");
    private int _projectCount;
    private int _backupCount;
    private int _snapshotOnlyCount;
    private int _totalEventCount;
    private string _latestEventLabel = L("History.Event.NoRecent", "No recent history");
    private string _latestEventTitle = L("History.Event.NoRecent", "No recent history");
    private string _latestEventDetail = L("History.Summary.Empty", "Project history will appear here as backups and snapshots are created.");

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string LF(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);

    public HistoryViewModel()
        : this(StaticAppConfigStore.Instance, new SqliteRepositoryFactory(StaticAppConfigStore.Instance))
    {
    }

    internal HistoryViewModel(IAppConfigStore configStore, IRepositoryFactory? repositoryFactory = null)
    {
        _configStore = configStore;
        _repositoryFactory = repositoryFactory ?? new SqliteRepositoryFactory(_configStore);
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(force: true));
    }

    public ObservableCollection<HistoryTimelineItemViewModel> TimelineItems { get; } = [];

    public ICommand RefreshCommand { get; }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public string EmptyState
    {
        get => _emptyState;
        private set => SetField(ref _emptyState, value);
    }

    public int ProjectCount
    {
        get => _projectCount;
        private set => SetField(ref _projectCount, value);
    }

    public int BackupCount
    {
        get => _backupCount;
        private set => SetField(ref _backupCount, value);
    }

    public int SnapshotOnlyCount
    {
        get => _snapshotOnlyCount;
        private set => SetField(ref _snapshotOnlyCount, value);
    }

    public int TotalEventCount
    {
        get => _totalEventCount;
        private set => SetField(ref _totalEventCount, value);
    }

    public string LatestEventLabel
    {
        get => _latestEventLabel;
        private set => SetField(ref _latestEventLabel, value);
    }

    public string LatestEventTitle
    {
        get => _latestEventTitle;
        private set => SetField(ref _latestEventTitle, value);
    }

    public string LatestEventDetail
    {
        get => _latestEventDetail;
        private set => SetField(ref _latestEventDetail, value);
    }

    public string BackupSignalLabel => LF("History.Signal.Backup", "{0} backup event(s)", BackupCount);
    public string MetadataSignalLabel => LF("History.Signal.Metadata", "{0} metadata-only event(s)", SnapshotOnlyCount);
    public string ProjectSignalLabel => LF("History.Signal.Projects", "{0} tracked project(s)", ProjectCount);

    public bool HasTimelineItems => TimelineItems.Count > 0;

    public async Task RefreshAsync(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastRefreshUtc) < RefreshTtl)
            return;

        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
            return;

        try
        {
            HistoryTimelineData data = await Task.Run(LoadTimelineData).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyTimelineData(data));
            _lastRefreshUtc = DateTime.UtcNow;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private HistoryTimelineData LoadTimelineData()
    {
        AppConfig config = _configStore.GetSnapshot();
        SqliteRepository repo = _repositoryFactory.Create(config);
        repo.EnsureSchema();

        var projects = repo.GetAllProjects().ToList();
        var projectsById = projects.ToDictionary(project => project.Id);
        var backups = repo.GetRecentBackups(60);
        var snapshotOnly = repo.GetRecentSnapshotsWithoutBackup(20);

        var items = backups
            .Select(backup =>
            {
                string projectName = projectsById.TryGetValue(backup.projectId, out Project? project)
                    ? project.Name
                    : LF("History.Event.ProjectFallback", "Project #{0}", backup.projectId);
                string type = string.IsNullOrWhiteSpace(backup.type) ? "backup" : backup.type;
                return new HistoryTimelineItemViewModel(
                    "Backup",
                    projectName,
                    backup.createdUtc,
                    LF("History.Event.BackupTitle", "{0} {1} backup", projectName, type),
                    L("History.Event.BackupDetail", "Snapshot captured and available for restore."),
                    type.Equals("manual", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Auto");
            })
            .Concat(snapshotOnly.Select(snapshot =>
            {
                string projectName = projectsById.TryGetValue(snapshot.projectId, out Project? project)
                    ? project.Name
                    : LF("History.Event.ProjectFallback", "Project #{0}", snapshot.projectId);
                return new HistoryTimelineItemViewModel(
                    "Snapshot",
                    projectName,
                    snapshot.createdUtc,
                    LF("History.Event.SnapshotTitle", "{0} snapshot", projectName),
                    L("History.Event.MetadataDetail", "Snapshot metadata exists without a linked backup record."),
                    "Metadata");
            }))
            .OrderByDescending(item => item.CreatedUtc)
            .Take(80)
            .ToList();

        return new HistoryTimelineData(projects.Count, backups.Count, snapshotOnly.Count, items);
    }

    private void ApplyTimelineData(HistoryTimelineData data)
    {
        ProjectCount = data.ProjectCount;
        BackupCount = data.BackupCount;
        SnapshotOnlyCount = data.SnapshotOnlyCount;
        TotalEventCount = data.Items.Count;
        Summary = data.Items.Count == 0
            ? L("History.Summary.Empty", "Project history will appear here as backups and snapshots are created.")
            : LF("History.Summary.Events", "Showing {0} recent history events across {1} project(s).", data.Items.Count, data.ProjectCount);
        HistoryTimelineItemViewModel? latest = data.Items.FirstOrDefault();
        LatestEventLabel = latest?.TimeLabel ?? L("History.Event.NoRecent", "No recent history");
        LatestEventTitle = latest?.Title ?? L("History.Event.NoRecent", "No recent history");
        LatestEventDetail = latest?.Detail ?? L("History.Summary.Empty", "Project history will appear here as backups and snapshots are created.");

        TimelineItems.Clear();
        foreach (HistoryTimelineItemViewModel item in data.Items)
            TimelineItems.Add(item);

        OnPropertyChanged(nameof(HasTimelineItems));
        OnPropertyChanged(nameof(BackupSignalLabel));
        OnPropertyChanged(nameof(MetadataSignalLabel));
        OnPropertyChanged(nameof(ProjectSignalLabel));
    }

    private sealed record HistoryTimelineData(
        int ProjectCount,
        int BackupCount,
        int SnapshotOnlyCount,
        IReadOnlyList<HistoryTimelineItemViewModel> Items);
}

public sealed class HistoryTimelineItemViewModel
{
    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string LF(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);

    public HistoryTimelineItemViewModel(
        string kind,
        string projectName,
        DateTime createdUtc,
        string title,
        string detail,
        string lane)
    {
        Kind = kind;
        ProjectName = projectName;
        CreatedUtc = createdUtc;
        Title = title;
        Detail = detail;
        Lane = lane;
    }

    public string Kind { get; }
    public string ProjectName { get; }
    public DateTime CreatedUtc { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Lane { get; }
    public string TimeLabel => CreatedUtc.ToLocalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.CurrentCulture);
    public string RelativeLabel
    {
        get
        {
            TimeSpan age = DateTime.UtcNow - CreatedUtc.ToUniversalTime();
            if (age < TimeSpan.FromMinutes(1))
                return L("History.Relative.Now", "Just now");
            if (age < TimeSpan.FromHours(1))
                return LF("History.Relative.Minutes", "{0}m ago", Math.Max(1, (int)age.TotalMinutes));
            if (age < TimeSpan.FromDays(1))
                return LF("History.Relative.Hours", "{0}h ago", Math.Max(1, (int)age.TotalHours));
            return LF("History.Relative.Days", "{0}d ago", Math.Max(1, (int)age.TotalDays));
        }
    }

    public string Accent => Kind.Equals("Backup", StringComparison.OrdinalIgnoreCase) ? "#4F8DFF" : "#8B7CFF";
    public string GraphCode => Kind.Equals("Backup", StringComparison.OrdinalIgnoreCase) ? "BKP" : "SNP";
}
