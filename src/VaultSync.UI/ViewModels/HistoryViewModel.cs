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
    private string _summary = "Loading project history...";
    private string _emptyState = "No history yet. Create a backup to start building a project timeline.";
    private int _projectCount;
    private int _backupCount;
    private int _snapshotOnlyCount;
    private string _latestEventLabel = "No recent history";

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

    public string LatestEventLabel
    {
        get => _latestEventLabel;
        private set => SetField(ref _latestEventLabel, value);
    }

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
                    : $"Project #{backup.projectId}";
                string type = string.IsNullOrWhiteSpace(backup.type) ? "backup" : backup.type;
                return new HistoryTimelineItemViewModel(
                    "Backup",
                    projectName,
                    backup.createdUtc,
                    $"{projectName} {type} backup",
                    "Snapshot captured and available for restore.",
                    type.Equals("manual", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Auto");
            })
            .Concat(snapshotOnly.Select(snapshot =>
            {
                string projectName = projectsById.TryGetValue(snapshot.projectId, out Project? project)
                    ? project.Name
                    : $"Project #{snapshot.projectId}";
                return new HistoryTimelineItemViewModel(
                    "Snapshot",
                    projectName,
                    snapshot.createdUtc,
                    $"{projectName} snapshot",
                    "Snapshot metadata exists without a linked backup record.",
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
        Summary = data.Items.Count == 0
            ? "Project history will appear here as backups and snapshots are created."
            : $"Showing {data.Items.Count.ToString(CultureInfo.InvariantCulture)} recent history events across {data.ProjectCount.ToString(CultureInfo.InvariantCulture)} project(s).";
        LatestEventLabel = data.Items.FirstOrDefault()?.TimeLabel ?? "No recent history";

        TimelineItems.Clear();
        foreach (HistoryTimelineItemViewModel item in data.Items)
            TimelineItems.Add(item);

        OnPropertyChanged(nameof(HasTimelineItems));
    }

    private sealed record HistoryTimelineData(
        int ProjectCount,
        int BackupCount,
        int SnapshotOnlyCount,
        IReadOnlyList<HistoryTimelineItemViewModel> Items);
}

public sealed class HistoryTimelineItemViewModel
{
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
}
