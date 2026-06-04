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
    private const int PageSize = 30;
    private readonly IAppConfigStore _configStore;
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private readonly RelayCommand _resetFiltersCommand;
    private readonly RelayCommand _toggleSelectedProtectedCommand;
    private readonly RelayCommand _toggleSelectedKnownGoodCommand;
    private readonly List<HistoryTimelineItemViewModel> _allTimelineItems = [];
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
    private HistoryActivityFilterOption? _selectedActivityFilter;
    private HistoryDateRangeOption? _selectedDateRange;
    private HistoryTimelineItemViewModel? _selectedTimelineItem;
    private int _pageIndex;
    private int _filteredEventCount;

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

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
        _previousPageCommand = new RelayCommand(_ => MovePage(-1), _ => CanGoToPreviousPage);
        _nextPageCommand = new RelayCommand(_ => MovePage(1), _ => CanGoToNextPage);
        _resetFiltersCommand = new RelayCommand(_ => ResetFilters(), _ => HasActiveFilters);
        _toggleSelectedProtectedCommand = new RelayCommand(
            async _ => await ToggleSelectedSnapshotMarkerAsync(toggleProtected: true),
            _ => CanEditSelectedSnapshotMetadata);
        _toggleSelectedKnownGoodCommand = new RelayCommand(
            async _ => await ToggleSelectedSnapshotMarkerAsync(toggleProtected: false),
            _ => CanEditSelectedSnapshotMetadata);
        PreviousPageCommand = _previousPageCommand;
        NextPageCommand = _nextPageCommand;
        ResetFiltersCommand = _resetFiltersCommand;
        ToggleSelectedProtectedCommand = _toggleSelectedProtectedCommand;
        ToggleSelectedKnownGoodCommand = _toggleSelectedKnownGoodCommand;

        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.All, L("History.Filter.All", "All activity")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Backups, L("History.Filter.Backups", "Backups")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Restores, L("History.Filter.Restores", "Restores")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Metadata, L("History.Filter.Metadata", "Metadata")));
        _selectedActivityFilter = ActivityFilterOptions[0];

        DateRangeOptions.Add(new HistoryDateRangeOption(HistoryDateRange.All, L("History.Range.All", "All time")));
        DateRangeOptions.Add(new HistoryDateRangeOption(HistoryDateRange.Last7Days, L("History.Range.7Days", "Last 7 days")));
        DateRangeOptions.Add(new HistoryDateRangeOption(HistoryDateRange.Last30Days, L("History.Range.30Days", "Last 30 days")));
        _selectedDateRange = DateRangeOptions[0];
    }

    public ObservableCollection<HistoryTimelineItemViewModel> TimelineItems { get; } = [];
    public ObservableCollection<HistoryActivityFilterOption> ActivityFilterOptions { get; } = [];
    public ObservableCollection<HistoryDateRangeOption> DateRangeOptions { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand ResetFiltersCommand { get; }
    public ICommand ToggleSelectedProtectedCommand { get; }
    public ICommand ToggleSelectedKnownGoodCommand { get; }

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

    public HistoryActivityFilterOption? SelectedActivityFilter
    {
        get => _selectedActivityFilter;
        set
        {
            if (!SetField(ref _selectedActivityFilter, value))
                return;

            _pageIndex = 0;
            ApplyFiltersAndPaging();
        }
    }

    public HistoryDateRangeOption? SelectedDateRange
    {
        get => _selectedDateRange;
        set
        {
            if (!SetField(ref _selectedDateRange, value))
                return;

            _pageIndex = 0;
            ApplyFiltersAndPaging();
        }
    }

    public HistoryTimelineItemViewModel? SelectedTimelineItem
    {
        get => _selectedTimelineItem;
        set
        {
            if (!SetField(ref _selectedTimelineItem, value))
                return;

            OnPropertyChanged(nameof(HasSelectedTimelineItem));
            OnPropertyChanged(nameof(SelectedEventTitle));
            OnPropertyChanged(nameof(SelectedEventDetail));
            OnPropertyChanged(nameof(SelectedEventProject));
            OnPropertyChanged(nameof(SelectedEventWhen));
            OnPropertyChanged(nameof(SelectedEventLane));
            OnPropertyChanged(nameof(SelectedEventKind));
            OnPropertyChanged(nameof(SelectedEventAccent));
            OnPropertyChanged(nameof(SelectedEventMarkerSummary));
            OnPropertyChanged(nameof(SelectedEventHasMarkerSummary));
            OnPropertyChanged(nameof(SelectedEventOriginSummary));
            OnPropertyChanged(nameof(SelectedEventHasOriginSummary));
            OnPropertyChanged(nameof(CanEditSelectedSnapshotMetadata));
            OnPropertyChanged(nameof(SelectedProtectedActionLabel));
            OnPropertyChanged(nameof(SelectedKnownGoodActionLabel));
            _toggleSelectedProtectedCommand.RaiseCanExecuteChanged();
            _toggleSelectedKnownGoodCommand.RaiseCanExecuteChanged();
        }
    }

    public string BackupSignalLabel => LF("History.Signal.Backup", "{0} backup event(s)", BackupCount);
    public string MetadataSignalLabel => LF("History.Signal.Metadata", "{0} metadata-only event(s)", SnapshotOnlyCount);
    public string ProjectSignalLabel => LF("History.Signal.Projects", "{0} tracked project(s)", ProjectCount);

    public bool HasTimelineItems => TimelineItems.Count > 0;
    public bool HasLoadedEvents => _allTimelineItems.Count > 0;
    public bool HasActiveFilters =>
        (SelectedActivityFilter?.Filter ?? HistoryActivityFilter.All) != HistoryActivityFilter.All ||
        (SelectedDateRange?.Range ?? HistoryDateRange.All) != HistoryDateRange.All;
    public string EmptyTitle => HasLoadedEvents
        ? L("History.Filter.EmptyTitle", "No matching events")
        : L("History.Event.NoRecent", "No recent history");
    public string EmptyMessage => HasLoadedEvents
        ? L("History.Filter.EmptyMessage", "Try a different activity type or date range.")
        : EmptyState;
    public string EventCountLabel => string.Format(
        CultureInfo.CurrentCulture,
        "{0} {1}",
        _filteredEventCount,
        L("History.Panel.TotalEvents", "Visible events").ToLower(CultureInfo.CurrentCulture));
    public string PageLabel => _filteredEventCount == 0
        ? L("History.Page.Empty", "Page 0 of 0")
        : LF("History.Page.Label", "Page {0} of {1}", _pageIndex + 1, PageCount);
    public string WindowLabel => _filteredEventCount == 0
        ? L("History.Window.Empty", "No matching events")
        : LF(
            "History.Window.Label",
            "{0}-{1} of {2} events",
            (_pageIndex * PageSize) + 1,
            Math.Min((_pageIndex + 1) * PageSize, _filteredEventCount),
            _filteredEventCount);
    public int PageCount => _filteredEventCount == 0
        ? 0
        : (int)Math.Ceiling(_filteredEventCount / (double)PageSize);
    public bool CanGoToPreviousPage => _pageIndex > 0;
    public bool CanGoToNextPage => _pageIndex + 1 < PageCount;
    public string LoadedSummaryLabel => LF("History.Summary.Loaded", "{0} loaded", TotalEventCount);
    public string ProjectSummaryLabel => LF("History.Summary.Projects", "{0} projects", ProjectCount);
    public string RestoreSummaryLabel => LF("History.Summary.Restores", "{0} restores", _allTimelineItems.Count(item => item.GraphLane == HistoryTimelineLane.Restore));
    public string FilterStateLabel => HasActiveFilters
        ? LF("History.Filter.State", "{0} of {1} events shown", _filteredEventCount, TotalEventCount)
        : LF("History.Filter.State.All", "All {0} events shown", _filteredEventCount);
    public bool HasSelectedTimelineItem => SelectedTimelineItem is not null;
    public string SelectedEventTitle => SelectedTimelineItem?.Title ?? LatestEventTitle;
    public string SelectedEventDetail => SelectedTimelineItem?.Detail ?? LatestEventDetail;
    public string SelectedEventProject => SelectedTimelineItem?.ProjectName ?? L("History.Detail.ProjectFallback", "No project selected");
    public string SelectedEventWhen => SelectedTimelineItem is null
        ? LatestEventLabel
        : string.Concat(SelectedTimelineItem.RelativeLabel, " - ", SelectedTimelineItem.TimeLabel);
    public string SelectedEventLane => SelectedTimelineItem?.Lane ?? L("History.Detail.LaneFallback", "Timeline");
    public string SelectedEventKind => SelectedTimelineItem?.Kind ?? L("History.Detail.KindFallback", "Activity");
    public string SelectedEventAccent => SelectedTimelineItem?.Accent ?? "#4F8DFF";
    public string SelectedEventMarkerSummary => SelectedTimelineItem?.MarkerSummary ?? string.Empty;
    public bool SelectedEventHasMarkerSummary => SelectedTimelineItem?.HasMarkerSummary == true;
    public string SelectedEventOriginSummary => SelectedTimelineItem?.OriginSummary ?? string.Empty;
    public bool SelectedEventHasOriginSummary => SelectedTimelineItem?.HasOriginSummary == true;
    public bool CanEditSelectedSnapshotMetadata => SelectedTimelineItem?.SnapshotId > 0;
    public string SelectedProtectedActionLabel => SelectedTimelineItem?.IsProtectedMarker == true
        ? L("History.Action.Unprotect", "Unprotect")
        : L("History.Action.Protect", "Protect");
    public string SelectedKnownGoodActionLabel => SelectedTimelineItem?.IsKnownGoodMarker == true
        ? L("History.Action.ClearKnownGood", "Clear known good")
        : L("History.Action.MarkKnownGood", "Mark known good");

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
        var allBackups = repo.GetAllBackups()
            .OrderByDescending(backup => backup.CreatedUtc)
            .ThenByDescending(backup => backup.Id)
            .ToList();
        var backups = allBackups
            .Take(60)
            .ToList();
        var backupSnapshotIds = allBackups.Select(backup => backup.SnapshotId).ToHashSet();
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

        var backupBySnapshotId = allBackups
            .GroupBy(backup => backup.SnapshotId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(backup => backup.CreatedUtc).First());

        var items = new List<HistoryTimelineItemViewModel>();
        foreach (Backup backup in backups)
        {
            string projectName = projectsById.TryGetValue(backup.ProjectId, out Project? project)
                ? project.Name
                : LF("History.Event.ProjectFallback", "Project #{0}", backup.ProjectId);
            string type = string.IsNullOrWhiteSpace(backup.Type) ? "backup" : backup.Type;
            metadataBySnapshotId.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata);
            bool isManual = type.Equals("manual", StringComparison.OrdinalIgnoreCase);
            HistoryTimelineLane graphLane = backup.IsImported
                ? HistoryTimelineLane.Restore
                : isManual
                    ? HistoryTimelineLane.Manual
                    : HistoryTimelineLane.Backup;
            string markerSummary = BuildMarkerSummary(metadata);

            items.Add(new HistoryTimelineItemViewModel(
                backup.IsImported
                    ? L("History.Event.Kind.Imported", "Imported")
                    : L("History.Event.Kind.Backup", "Backup"),
                projectName,
                backup.CreatedUtc,
                LF("History.Event.BackupTitle", "{0} {1} backup", projectName, type),
                BuildBackupDetail(backup, metadata),
                backup.IsImported
                    ? L("History.Lane.Imported", "Imported")
                    : isManual
                    ? L("History.Lane.Manual", "Manual")
                    : L("History.Lane.Auto", "Auto"),
                graphLane,
                backup.Id,
                backup.SnapshotId,
                string.Empty,
                metadata?.IsProtected == true,
                metadata?.IsKnownGood == true,
                markerSummary));

            if (metadata is not null && !string.IsNullOrWhiteSpace(markerSummary))
            {
                DateTime metadataUtc = metadata.UpdatedUtc > metadata.CreatedUtc
                    ? metadata.UpdatedUtc
                    : metadata.CreatedUtc;
                items.Add(new HistoryTimelineItemViewModel(
                    L("History.Event.Kind.Metadata", "Metadata"),
                    projectName,
                    metadataUtc,
                    LF("History.Event.MetadataTitle", "{0} snapshot metadata", projectName),
                    BuildMetadataBranchDetail(metadata),
                    L("History.Lane.Metadata", "Snapshot notes"),
                    HistoryTimelineLane.Metadata,
                    backup.Id,
                    backup.SnapshotId,
                    BuildOriginSummary(backup.Id, backup.SnapshotId),
                    metadata.IsProtected,
                    metadata.IsKnownGood,
                    markerSummary));
            }
        }

        foreach (RestoreHistoryEvent restore in restores)
        {
            string projectName = projectsById.TryGetValue(restore.ProjectId, out Project? project)
                ? project.Name
                : LF("History.Event.ProjectFallback", "Project #{0}", restore.ProjectId);
            metadataBySnapshotId.TryGetValue(restore.SnapshotId, out SnapshotHistoryMetadata? metadata);
            string mode = string.Equals(restore.RestoreMode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase)
                ? L("History.Lane.RestoreSandbox", "Sandbox restore")
                : L("History.Lane.RestoreDirect", "Direct restore");
            int originBackupId = restore.BackupId;
            if (originBackupId <= 0 && backupBySnapshotId.TryGetValue(restore.SnapshotId, out Backup? originBackup))
                originBackupId = originBackup.Id;

            items.Add(new HistoryTimelineItemViewModel(
                L("History.Event.Kind.Restore", "Restore"),
                projectName,
                restore.CreatedUtc,
                LF("History.Event.RestoreTitle", "{0} restored from backup", projectName),
                BuildRestoreDetail(restore, metadata),
                mode,
                HistoryTimelineLane.Restore,
                originBackupId,
                restore.SnapshotId,
                BuildOriginSummary(originBackupId, restore.SnapshotId),
                metadata?.IsProtected == true,
                metadata?.IsKnownGood == true,
                BuildMarkerSummary(metadata)));
        }

        foreach (Snapshot snapshot in snapshotOnly)
        {
            string projectName = projectsById.TryGetValue(snapshot.ProjectId, out Project? project)
                ? project.Name
                : LF("History.Event.ProjectFallback", "Project #{0}", snapshot.ProjectId);
            metadataBySnapshotId.TryGetValue(snapshot.Id, out SnapshotHistoryMetadata? metadata);

            items.Add(new HistoryTimelineItemViewModel(
                L("History.Event.Kind.Snapshot", "Snapshot"),
                projectName,
                snapshot.CreatedUtc,
                LF("History.Event.SnapshotTitle", "{0} snapshot", projectName),
                BuildSnapshotDetail(metadata),
                L("History.Lane.Metadata", "Snapshot"),
                HistoryTimelineLane.Metadata,
                0,
                snapshot.Id,
                LF("History.Event.OriginSnapshotOnly", "snapshot #{0} has no linked backup node", snapshot.Id),
                metadata?.IsProtected == true,
                metadata?.IsKnownGood == true,
                BuildMarkerSummary(metadata)));
        }

        items = items
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

        _allTimelineItems.Clear();
        _allTimelineItems.AddRange(data.Items);
        _pageIndex = 0;
        ApplyFiltersAndPaging();

        OnPropertyChanged(nameof(HasTimelineItems));
        OnPropertyChanged(nameof(HasLoadedEvents));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(BackupSignalLabel));
        OnPropertyChanged(nameof(MetadataSignalLabel));
        OnPropertyChanged(nameof(ProjectSignalLabel));
        OnPropertyChanged(nameof(LoadedSummaryLabel));
        OnPropertyChanged(nameof(ProjectSummaryLabel));
        OnPropertyChanged(nameof(RestoreSummaryLabel));
        OnPropertyChanged(nameof(SelectedEventTitle));
        OnPropertyChanged(nameof(SelectedEventDetail));
        OnPropertyChanged(nameof(SelectedEventProject));
        OnPropertyChanged(nameof(SelectedEventWhen));
        OnPropertyChanged(nameof(SelectedEventLane));
        OnPropertyChanged(nameof(SelectedEventKind));
        OnPropertyChanged(nameof(SelectedEventAccent));
        OnPropertyChanged(nameof(SelectedEventOriginSummary));
        OnPropertyChanged(nameof(SelectedEventHasOriginSummary));
        OnPropertyChanged(nameof(CanEditSelectedSnapshotMetadata));
        OnPropertyChanged(nameof(SelectedProtectedActionLabel));
        OnPropertyChanged(nameof(SelectedKnownGoodActionLabel));
        OnPropertyChanged(nameof(FilterStateLabel));
        _toggleSelectedProtectedCommand.RaiseCanExecuteChanged();
        _toggleSelectedKnownGoodCommand.RaiseCanExecuteChanged();
        _resetFiltersCommand.RaiseCanExecuteChanged();
    }

    private void ResetFilters()
    {
        _selectedActivityFilter = ActivityFilterOptions.FirstOrDefault(option => option.Filter == HistoryActivityFilter.All);
        _selectedDateRange = DateRangeOptions.FirstOrDefault(option => option.Range == HistoryDateRange.All);
        _pageIndex = 0;

        OnPropertyChanged(nameof(SelectedActivityFilter));
        OnPropertyChanged(nameof(SelectedDateRange));
        ApplyFiltersAndPaging();
    }

    private void MovePage(int delta)
    {
        int next = Math.Clamp(_pageIndex + delta, 0, Math.Max(0, PageCount - 1));
        if (next == _pageIndex)
            return;

        _pageIndex = next;
        ApplyFiltersAndPaging();
    }

    private void ApplyFiltersAndPaging()
    {
        IEnumerable<HistoryTimelineItemViewModel> query = _allTimelineItems;

        query = (SelectedActivityFilter?.Filter ?? HistoryActivityFilter.All) switch
        {
            HistoryActivityFilter.Backups => query.Where(item =>
                item.GraphLane is HistoryTimelineLane.Backup or HistoryTimelineLane.Manual),
            HistoryActivityFilter.Restores => query.Where(item => item.GraphLane == HistoryTimelineLane.Restore),
            HistoryActivityFilter.Metadata => query.Where(item => item.GraphLane == HistoryTimelineLane.Metadata),
            _ => query
        };

        DateTime cutoffUtc = (SelectedDateRange?.Range ?? HistoryDateRange.All) switch
        {
            HistoryDateRange.Last7Days => DateTime.UtcNow.AddDays(-7),
            HistoryDateRange.Last30Days => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.MinValue
        };

        if (cutoffUtc > DateTime.MinValue)
            query = query.Where(item => item.CreatedUtc.ToUniversalTime() >= cutoffUtc);

        List<HistoryTimelineItemViewModel> filtered = query.ToList();
        _filteredEventCount = filtered.Count;
        int maxPageIndex = Math.Max(0, PageCount - 1);
        if (_pageIndex > maxPageIndex)
            _pageIndex = maxPageIndex;

        int pageStart = _pageIndex * PageSize;
        TimelineItems.Clear();
        List<HistoryTimelineItemViewModel> pageItems = filtered.Skip(pageStart).Take(PageSize).ToList();
        ApplyPageGraph(filtered, pageStart, pageItems.Count);
        foreach (HistoryTimelineItemViewModel item in pageItems)
            TimelineItems.Add(item);

        if (TimelineItems.Count == 0)
        {
            SelectedTimelineItem = null;
        }
        else if (SelectedTimelineItem is null || !TimelineItems.Contains(SelectedTimelineItem))
        {
            SelectedTimelineItem = TimelineItems[0];
        }

        OnPropertyChanged(nameof(HasTimelineItems));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(EventCountLabel));
        OnPropertyChanged(nameof(FilterStateLabel));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(WindowLabel));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        _previousPageCommand.RaiseCanExecuteChanged();
        _nextPageCommand.RaiseCanExecuteChanged();
        _resetFiltersCommand.RaiseCanExecuteChanged();
        _toggleSelectedProtectedCommand.RaiseCanExecuteChanged();
        _toggleSelectedKnownGoodCommand.RaiseCanExecuteChanged();
    }

    private async Task ToggleSelectedSnapshotMarkerAsync(bool toggleProtected)
    {
        HistoryTimelineItemViewModel? selected = SelectedTimelineItem;
        if (selected is null || selected.SnapshotId <= 0)
            return;

        int snapshotId = selected.SnapshotId;
        bool nextProtected = toggleProtected ? !selected.IsProtectedMarker : selected.IsProtectedMarker;
        bool nextKnownGood = toggleProtected ? selected.IsKnownGoodMarker : !selected.IsKnownGoodMarker;

        await Task.Run(() =>
        {
            AppConfig config = _configStore.GetSnapshot();
            SqliteRepository repo = _repositoryFactory.Create(config);
            repo.EnsureSchema();
            SnapshotHistoryMetadata? existing = repo.GetSnapshotHistoryMetadata(snapshotId);
            DateTime now = DateTime.UtcNow;
            repo.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
            {
                SnapshotId = snapshotId,
                Label = existing?.Label ?? string.Empty,
                Note = existing?.Note ?? string.Empty,
                Tags = existing?.Tags ?? string.Empty,
                IsProtected = nextProtected,
                IsKnownGood = nextKnownGood,
                CreatedUtc = existing is null || existing.CreatedUtc == default ? now : existing.CreatedUtc,
                UpdatedUtc = now
            });
        }).ConfigureAwait(false);

        await RefreshAsync(force: true).ConfigureAwait(false);
    }

    private static void ApplyPageGraph(IReadOnlyList<HistoryTimelineItemViewModel> filtered, int pageStart, int pageCount)
    {
        for (int pageOffset = 0; pageOffset < pageCount; pageOffset++)
        {
            int index = pageStart + pageOffset;
            HistoryTimelineItemViewModel current = filtered[index];
            HistoryTimelineLane? previous = index > 0 ? filtered[index - 1].GraphRailLane : null;
            HistoryTimelineLane? next = index + 1 < filtered.Count ? filtered[index + 1].GraphRailLane : null;

            current.SetPageGraphPaths(
                BuildBackupRailPath(),
                BuildSideRailPath(HistoryTimelineLane.Metadata, current.GraphRailLane, previous, next),
                BuildSideRailPath(HistoryTimelineLane.Restore, current.GraphRailLane, previous, next));
        }
    }

    private static string BuildBackupRailPath() => "M 28,0 L 28,58";

    private static string BuildSideRailPath(
        HistoryTimelineLane lane,
        HistoryTimelineLane current,
        HistoryTimelineLane? previous,
        HistoryTimelineLane? next)
    {
        if (current != lane)
            return string.Empty;

        double x = LaneCenter(lane);
        double trunkX = LaneCenter(HistoryTimelineLane.Backup);
        bool continuesFromPrevious = previous == lane;
        bool continuesToNext = next == lane;
        string topSegment = continuesFromPrevious
            ? FormattableString.Invariant($"M {x},0 L {x},29")
            : FormattableString.Invariant($"M {trunkX},0 L {trunkX},18 C {trunkX},27 {x - 14},18 {x},29");
        string bottomSegment = continuesToNext
            ? FormattableString.Invariant($" M {x},29 L {x},58")
            : FormattableString.Invariant($" M {x},29 C {x - 14},40 {trunkX},31 {trunkX},40 L {trunkX},58");

        return string.Concat(topSegment, bottomSegment);
    }

    private static double LaneCenter(HistoryTimelineLane lane) => lane switch
    {
        HistoryTimelineLane.Backup => 28,
        HistoryTimelineLane.Manual => 28,
        HistoryTimelineLane.Metadata => 52,
        _ => 76
    };

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

    private static string BuildMetadataBranchDetail(SnapshotHistoryMetadata metadata)
    {
        string detail = L("History.Event.MetadataBranchDetail", "Snapshot markers were added to the restore point.");
        return AppendMetadataDetail(detail, metadata);
    }

    private static string BuildOriginSummary(int backupId, int snapshotId)
    {
        if (backupId > 0 && snapshotId > 0)
            return LF("History.Event.OriginBackupSnapshot", "branches from backup #{0} / snapshot #{1}", backupId, snapshotId);

        if (backupId > 0)
            return LF("History.Event.OriginBackup", "branches from backup #{0}", backupId);

        return snapshotId > 0
            ? LF("History.Event.OriginSnapshot", "branches from snapshot #{0}", snapshotId)
            : string.Empty;
    }

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
    Manual,
    Restore
}

public enum HistoryActivityFilter
{
    All,
    Backups,
    Restores,
    Metadata
}

public enum HistoryDateRange
{
    All,
    Last7Days,
    Last30Days
}

public sealed record HistoryActivityFilterOption(HistoryActivityFilter Filter, string Label);

public sealed record HistoryDateRangeOption(HistoryDateRange Range, string Label);

public sealed class HistoryTimelineItemViewModel
{
    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

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
        int backupId,
        int snapshotId,
        string originSummary,
        bool isProtectedMarker,
        bool isKnownGoodMarker,
        string markerSummary = "")
    {
        Kind = kind;
        ProjectName = projectName;
        CreatedUtc = createdUtc;
        Title = title;
        Detail = detail;
        Lane = lane;
        GraphLane = graphLane;
        BackupId = backupId;
        SnapshotId = snapshotId;
        OriginSummary = originSummary ?? string.Empty;
        IsProtectedMarker = isProtectedMarker;
        IsKnownGoodMarker = isKnownGoodMarker;
        MarkerSummary = markerSummary ?? string.Empty;
    }

    public string Kind { get; }
    public string ProjectName { get; }
    public DateTime CreatedUtc { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Lane { get; }
    public HistoryTimelineLane GraphLane { get; }
    public int BackupId { get; }
    public int SnapshotId { get; }
    public string OriginSummary { get; }
    public bool HasOriginSummary => !string.IsNullOrWhiteSpace(OriginSummary);
    public bool IsProtectedMarker { get; }
    public bool IsKnownGoodMarker { get; }
    public string MarkerSummary { get; }
    public bool HasMarkerSummary => !string.IsNullOrWhiteSpace(MarkerSummary);
    public string BackupGraphPathData { get; private set; } = "M 28,0 L 28,58";
    public string MetadataGraphPathData { get; private set; } = string.Empty;
    public string RestoreGraphPathData { get; private set; } = string.Empty;
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
        HistoryTimelineLane.Manual => "#FFB454",
        HistoryTimelineLane.Backup => "#4F8DFF",
        _ => "#8B7CFF"
    };

    public HistoryTimelineLane GraphRailLane => GraphLane switch
    {
        HistoryTimelineLane.Manual => HistoryTimelineLane.Backup,
        _ => GraphLane
    };

    public void SetPageGraphPaths(string backupPath, string metadataPath, string restorePath)
    {
        BackupGraphPathData = backupPath;
        MetadataGraphPathData = metadataPath;
        RestoreGraphPathData = restorePath;
    }

    public double NodeLeft => GraphLane switch
    {
        HistoryTimelineLane.Backup => 19,
        HistoryTimelineLane.Manual => 19,
        HistoryTimelineLane.Metadata => 43,
        _ => 67
    };

    public double InnerNodeLeft => NodeLeft + 5;

    private static double LaneCenter(HistoryTimelineLane lane) => lane switch
    {
        HistoryTimelineLane.Backup => 28,
        HistoryTimelineLane.Manual => 28,
        HistoryTimelineLane.Metadata => 52,
        _ => 76
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
