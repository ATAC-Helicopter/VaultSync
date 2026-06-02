using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
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
    private string _insight = L("History.Insight.Empty", "Create a backup to start building a readable project history.");

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

    public string Insight
    {
        get => _insight;
        private set => SetField(ref _insight, value);
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
        var backups = repo.GetAllBackups()
            .OrderByDescending(backup => backup.CreatedUtc)
            .ThenByDescending(backup => backup.Id)
            .Take(60)
            .ToList();
        var backupSnapshotIds = backups.Select(backup => backup.SnapshotId).ToHashSet();
        var snapshotOnly = repo.GetAllSnapshots()
            .Where(snapshot => !backupSnapshotIds.Contains(snapshot.Id))
            .OrderByDescending(snapshot => snapshot.CreatedUtc)
            .ThenByDescending(snapshot => snapshot.Id)
            .Take(20)
            .ToList();
        var restores = repo.GetRecentRestoreHistoryEvents(30);
        var metadataBySnapshotId = repo.GetSnapshotHistoryMetadataBySnapshotIds(
            backups.Select(backup => backup.SnapshotId)
                .Concat(snapshotOnly.Select(snapshot => snapshot.Id))
                .Concat(restores.Select(restore => restore.SnapshotId)));

        var items = backups
            .Select(backup =>
            {
                string projectName = projectsById.TryGetValue(backup.ProjectId, out Project? project)
                    ? project.Name
                    : LF("History.Event.ProjectFallback", "Project #{0}", backup.ProjectId);
                string type = string.IsNullOrWhiteSpace(backup.Type) ? "backup" : backup.Type;
                metadataBySnapshotId.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata);
                return new HistoryTimelineItemViewModel(
                    L("History.Event.Kind.Backup", "Backup"),
                    projectName,
                    backup.CreatedUtc,
                    LF("History.Event.BackupTitle", "{0} {1} backup", projectName, type),
                    BuildBackupDetail(backup, metadata),
                    type.Equals("manual", StringComparison.OrdinalIgnoreCase)
                        ? L("History.Lane.Manual", "Manual")
                        : L("History.Lane.Auto", "Auto"),
                    HistoryTimelineLane.Backup,
                    BuildMarkerSummary(metadata));
            })
            .Concat(restores.Select(restore =>
            {
                string projectName = projectsById.TryGetValue(restore.ProjectId, out Project? project)
                    ? project.Name
                    : LF("History.Event.ProjectFallback", "Project #{0}", restore.ProjectId);
                metadataBySnapshotId.TryGetValue(restore.SnapshotId, out SnapshotHistoryMetadata? metadata);
                string mode = string.Equals(restore.RestoreMode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase)
                    ? L("History.Lane.RestoreSandbox", "Sandbox restore")
                    : L("History.Lane.RestoreDirect", "Direct restore");
                return new HistoryTimelineItemViewModel(
                    L("History.Event.Kind.Restore", "Restore"),
                    projectName,
                    restore.CreatedUtc,
                    LF("History.Event.RestoreTitle", "{0} restored from backup", projectName),
                    BuildRestoreDetail(restore, metadata),
                    mode,
                    HistoryTimelineLane.Restore,
                    BuildMarkerSummary(metadata));
            }))
            .Concat(snapshotOnly.Select(snapshot =>
            {
                string projectName = projectsById.TryGetValue(snapshot.ProjectId, out Project? project)
                    ? project.Name
                    : LF("History.Event.ProjectFallback", "Project #{0}", snapshot.ProjectId);
                metadataBySnapshotId.TryGetValue(snapshot.Id, out SnapshotHistoryMetadata? metadata);
                return new HistoryTimelineItemViewModel(
                    L("History.Event.Kind.Snapshot", "Snapshot"),
                    projectName,
                    snapshot.CreatedUtc,
                    LF("History.Event.SnapshotTitle", "{0} snapshot", projectName),
                    BuildSnapshotDetail(metadata),
                    L("History.Lane.Metadata", "Metadata"),
                    HistoryTimelineLane.Metadata,
                    BuildMarkerSummary(metadata));
            }))
            .OrderByDescending(item => item.CreatedUtc)
            .Take(80)
            .ToList();

        return new HistoryTimelineData(projects.Count, backups.Count, snapshotOnly.Count, restores.Count, items);
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
        Insight = BuildInsight(data);

        TimelineItems.Clear();
        foreach (HistoryTimelineItemViewModel item in data.Items)
            TimelineItems.Add(item);

        OnPropertyChanged(nameof(HasTimelineItems));
        OnPropertyChanged(nameof(BackupSignalLabel));
        OnPropertyChanged(nameof(MetadataSignalLabel));
        OnPropertyChanged(nameof(ProjectSignalLabel));
    }

    private static string BuildBackupDetail(Backup backup, SnapshotHistoryMetadata? metadata)
    {
        string detail = backup.IsImported
            ? L("History.Event.ImportedBackupDetail", "Imported restore point is available for review.")
            : L("History.Event.BackupDetail", "Snapshot captured and available for restore.");
        return AppendMetadataDetail(detail, metadata);
    }

    private static string BuildRestoreDetail(RestoreHistoryEvent restore, SnapshotHistoryMetadata? metadata)
    {
        string detail = string.IsNullOrWhiteSpace(restore.Note)
            ? L("History.Event.RestoreDetail", "Restore operation recorded as a project-history event.")
            : restore.Note;
        return AppendMetadataDetail(detail, metadata);
    }

    private static string BuildSnapshotDetail(SnapshotHistoryMetadata? metadata) =>
        AppendMetadataDetail(
            L("History.Event.MetadataDetail", "Snapshot metadata exists without a linked backup record."),
            metadata);

    private static string AppendMetadataDetail(string detail, SnapshotHistoryMetadata? metadata)
    {
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Note))
            return detail;

        return string.Concat(detail, " ", metadata.Note.Trim());
    }

    private static string BuildMarkerSummary(SnapshotHistoryMetadata? metadata)
    {
        if (metadata is null)
            return string.Empty;

        var markers = new List<string>();
        if (metadata.IsKnownGood)
            markers.Add(L("History.Marker.KnownGood", "Known good"));
        if (metadata.IsProtected)
            markers.Add(L("History.Marker.Protected", "Protected"));
        if (!string.IsNullOrWhiteSpace(metadata.Tags))
            markers.Add(metadata.Tags);
        if (!string.IsNullOrWhiteSpace(metadata.Label))
            markers.Insert(0, metadata.Label);

        return string.Join(" - ", markers.Where(marker => !string.IsNullOrWhiteSpace(marker)));
    }

    private static string BuildInsight(HistoryTimelineData data)
    {
        if (data.Items.Count == 0)
            return L("History.Insight.Empty", "Create a backup to start building a readable project history.");

        if (data.SnapshotOnlyCount > data.BackupCount)
        {
            return LF(
                "History.Insight.MetadataHeavy",
                "{0} snapshot-only event(s) are visible. The next 1.8 foundation will make these easier to tag, explain, and protect.",
                data.SnapshotOnlyCount);
        }

        if (data.BackupCount > 0)
        {
            return LF(
                "History.Insight.BackupsReady",
                "{0} recent backup event(s) are available for review across {1} project(s).",
                data.BackupCount,
                data.ProjectCount);
        }

        return L("History.Insight.Readable", "Your recent project activity is ready to review.");
    }

    private sealed record HistoryTimelineData(
        int ProjectCount,
        int BackupCount,
        int SnapshotOnlyCount,
        int RestoreCount,
        IReadOnlyList<HistoryTimelineItemViewModel> Items);
}

public enum HistoryTimelineLane
{
    Metadata,
    Backup,
    Restore
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
        string lane,
        HistoryTimelineLane graphLane,
        string markerSummary = "")
    {
        Kind = kind;
        ProjectName = projectName;
        CreatedUtc = createdUtc;
        Title = title;
        Detail = detail;
        Lane = lane;
        GraphLane = graphLane;
        MarkerSummary = markerSummary ?? string.Empty;
    }

    public string Kind { get; }
    public string ProjectName { get; }
    public DateTime CreatedUtc { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Lane { get; }
    public HistoryTimelineLane GraphLane { get; }
    public string MarkerSummary { get; }
    public bool HasMarkerSummary => !string.IsNullOrWhiteSpace(MarkerSummary);
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

    public string Accent => GraphLane switch
    {
        HistoryTimelineLane.Restore => "#22CC88",
        HistoryTimelineLane.Backup => "#4F8DFF",
        _ => "#8B7CFF"
    };

    public Thickness NodeMargin => GraphLane switch
    {
        HistoryTimelineLane.Metadata => new Thickness(10, 0, 0, 0),
        HistoryTimelineLane.Backup => new Thickness(34, 0, 0, 0),
        _ => new Thickness(58, 0, 0, 0)
    };

    public Thickness ConnectorMargin => GraphLane switch
    {
        HistoryTimelineLane.Metadata => new Thickness(16, 0, 0, 0),
        HistoryTimelineLane.Backup => new Thickness(40, 0, 0, 0),
        _ => new Thickness(64, 0, 0, 0)
    };
}
