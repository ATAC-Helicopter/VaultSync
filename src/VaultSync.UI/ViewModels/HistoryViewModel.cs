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
    private const string EmptySummaryKey = "History.Summary.Empty";
    private const string EmptySummaryFallback = "Project history will appear here as backups and snapshots are created.";
    private const string NoRecentHistoryKey = "History.Event.NoRecent";
    private const string NoRecentHistoryFallback = "No recent history";
    private const int PageSize = 30;
    private readonly IAppConfigStore _configStore;
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private readonly RelayCommand _resetFiltersCommand;
    private readonly RelayCommand _toggleSelectedProtectedCommand;
    private readonly RelayCommand _toggleSelectedKnownGoodCommand;
    private readonly RelayCommand _saveSelectedSnapshotMetadataCommand;
    private readonly RelayCommand _clearSelectedSnapshotMetadataCommand;
    private readonly RelayCommand _browseSelectedSnapshotCommand;
    private readonly RelayCommand _openRecoveryCommand;
    private readonly RelayCommand _compareSelectedSnapshotCommand;
    private readonly List<HistoryTimelineItemViewModel> _allTimelineItems = [];
    private int _filterRevision;
    private int _refreshInFlight;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private string _summary = L(EmptySummaryKey, EmptySummaryFallback);
    private int _projectCount;
    private int _backupCount;
    private int _snapshotOnlyCount;
    private int _totalEventCount;
    private string _latestEventLabel = L(NoRecentHistoryKey, NoRecentHistoryFallback);
    private string _latestEventTitle = L(NoRecentHistoryKey, NoRecentHistoryFallback);
    private string _latestEventDetail = L(EmptySummaryKey, EmptySummaryFallback);
    private string _insight = L("History.Insight.Empty", "Create a backup to start building a readable project history.");
    private HistoryActivityFilterOption? _selectedActivityFilter;
    private HistoryDateRangeOption? _selectedDateRange;
    private HistoryProjectFilterOption? _selectedProjectFilter;
    private HistoryLaneFilterOption? _selectedLaneFilter;
    private HistoryViewModeOption? _selectedViewMode;
    private string _searchText = string.Empty;
    private HistoryTimelineItemViewModel? _selectedTimelineItem;
    private string _selectedSnapshotLabelDraft = string.Empty;
    private string _selectedSnapshotNoteDraft = string.Empty;
    private string _selectedSnapshotTagsDraft = string.Empty;
    private string _selectedComparisonSummary = string.Empty;
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
        _saveSelectedSnapshotMetadataCommand = new RelayCommand(
            async _ => await SaveSelectedSnapshotMetadataAsync(clearTextMetadata: false),
            _ => CanEditSelectedSnapshotMetadata);
        _clearSelectedSnapshotMetadataCommand = new RelayCommand(
            async _ => await SaveSelectedSnapshotMetadataAsync(clearTextMetadata: true),
            _ => CanEditSelectedSnapshotMetadata && HasSelectedSnapshotTextMetadata);
        _browseSelectedSnapshotCommand = new RelayCommand(
            _ => BrowseSelectedSnapshot(),
            _ => CanBrowseSelectedSnapshot);
        _openRecoveryCommand = new RelayCommand(
            _ => OpenRecoveryRequested?.Invoke(),
            _ => HasSelectedTimelineItem);
        _compareSelectedSnapshotCommand = new RelayCommand(
            _ => CompareSelectedSnapshot(),
            _ => CanCompareSelectedSnapshot);
        PreviousPageCommand = _previousPageCommand;
        NextPageCommand = _nextPageCommand;
        ResetFiltersCommand = _resetFiltersCommand;
        ToggleSelectedProtectedCommand = _toggleSelectedProtectedCommand;
        ToggleSelectedKnownGoodCommand = _toggleSelectedKnownGoodCommand;
        SaveSelectedSnapshotMetadataCommand = _saveSelectedSnapshotMetadataCommand;
        ClearSelectedSnapshotMetadataCommand = _clearSelectedSnapshotMetadataCommand;
        BrowseSelectedSnapshotCommand = _browseSelectedSnapshotCommand;
        OpenRecoveryCommand = _openRecoveryCommand;
        CompareSelectedSnapshotCommand = _compareSelectedSnapshotCommand;

        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.All, L("History.Filter.All", "All events")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Backups, L("History.Filter.Backups", "Backups")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Restores, L("History.Filter.Restores", "Restores")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Imported, L("History.Filter.Imported", "Imported")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Metadata, L("History.Filter.Metadata", "Metadata")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.Protected, L("History.Filter.Protected", "Protected")));
        ActivityFilterOptions.Add(new HistoryActivityFilterOption(HistoryActivityFilter.KnownGood, L("History.Filter.KnownGood", "Known good")));
        _selectedActivityFilter = ActivityFilterOptions[0];

        ProjectFilterOptions.Add(new HistoryProjectFilterOption(null, L("History.Project.All", "All projects")));
        _selectedProjectFilter = ProjectFilterOptions[0];

        LaneFilterOptions.Add(new HistoryLaneFilterOption(null, L("History.LaneFilter.All", "All lanes")));
        LaneFilterOptions.Add(new HistoryLaneFilterOption(HistoryTimelineLane.Backup, L("History.LaneFilter.Backup", "Backup trunk")));
        LaneFilterOptions.Add(new HistoryLaneFilterOption(HistoryTimelineLane.Manual, L("History.LaneFilter.Manual", "Manual backups")));
        LaneFilterOptions.Add(new HistoryLaneFilterOption(HistoryTimelineLane.Restore, L("History.LaneFilter.Restore", "Restore branches")));
        LaneFilterOptions.Add(new HistoryLaneFilterOption(HistoryTimelineLane.Metadata, L("History.LaneFilter.Metadata", "Snapshot notes")));
        _selectedLaneFilter = LaneFilterOptions[0];

        DateRangeOptions.Add(new HistoryDateRangeOption(HistoryDateRange.All, L("History.Range.All", "All time")));
        DateRangeOptions.Add(new HistoryDateRangeOption(HistoryDateRange.Today, L("History.Range.Today", "Today")));
        DateRangeOptions.Add(new HistoryDateRangeOption(HistoryDateRange.Last7Days, L("History.Range.7Days", "Last 7 days")));
        DateRangeOptions.Add(new HistoryDateRangeOption(HistoryDateRange.Last30Days, L("History.Range.30Days", "Last 30 days")));
        _selectedDateRange = DateRangeOptions[0];

        ViewModeOptions.Add(new HistoryViewModeOption(HistoryViewMode.Timeline, L("History.ViewMode.Timeline", "Timeline")));
        ViewModeOptions.Add(new HistoryViewModeOption(HistoryViewMode.Compact, L("History.ViewMode.Compact", "Compact")));
        _selectedViewMode = ViewModeOptions[0];
    }

    public ObservableCollection<HistoryTimelineItemViewModel> TimelineItems { get; } = [];
    public ObservableCollection<HistoryActivityFilterOption> ActivityFilterOptions { get; } = [];
    public ObservableCollection<HistoryDateRangeOption> DateRangeOptions { get; } = [];
    public ObservableCollection<HistoryProjectFilterOption> ProjectFilterOptions { get; } = [];
    public ObservableCollection<HistoryLaneFilterOption> LaneFilterOptions { get; } = [];
    public ObservableCollection<HistoryViewModeOption> ViewModeOptions { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand ResetFiltersCommand { get; }
    public ICommand ToggleSelectedProtectedCommand { get; }
    public ICommand ToggleSelectedKnownGoodCommand { get; }
    public ICommand SaveSelectedSnapshotMetadataCommand { get; }
    public ICommand ClearSelectedSnapshotMetadataCommand { get; }
    public ICommand BrowseSelectedSnapshotCommand { get; }
    public ICommand OpenRecoveryCommand { get; }
    public ICommand CompareSelectedSnapshotCommand { get; }

    public event Action<int>? OpenBackupFolderRequested;
    public event Action? OpenRecoveryRequested;

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public static string EmptyState
    {
        get => L("History.Empty", "No history yet. Create a backup to start building a project timeline.");
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
            QueueApplyFiltersAndPaging();
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
            QueueApplyFiltersAndPaging();
        }
    }

    public HistoryProjectFilterOption? SelectedProjectFilter
    {
        get => _selectedProjectFilter;
        set
        {
            if (!SetField(ref _selectedProjectFilter, value))
                return;

            _pageIndex = 0;
            QueueApplyFiltersAndPaging();
        }
    }

    public HistoryLaneFilterOption? SelectedLaneFilter
    {
        get => _selectedLaneFilter;
        set
        {
            if (!SetField(ref _selectedLaneFilter, value))
                return;

            _pageIndex = 0;
            QueueApplyFiltersAndPaging();
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
            OnPropertyChanged(nameof(SelectedEventSize));
            OnPropertyChanged(nameof(SelectedEventFiles));
            OnPropertyChanged(nameof(SelectedEventChangeSummary));
            OnPropertyChanged(nameof(SelectedEventLabel));
            OnPropertyChanged(nameof(SelectedEventNote));
            OnPropertyChanged(nameof(SelectedEventTags));
            OnPropertyChanged(nameof(SelectedEventMarkerSummary));
            OnPropertyChanged(nameof(SelectedEventHasMarkerSummary));
            OnPropertyChanged(nameof(SelectedEventOriginSummary));
            OnPropertyChanged(nameof(SelectedEventHasOriginSummary));
            OnPropertyChanged(nameof(SelectedRecoveryStatus));
            OnPropertyChanged(nameof(SelectedRecoveryStatusDetail));
            OnPropertyChanged(nameof(CanEditSelectedSnapshotMetadata));
            OnPropertyChanged(nameof(CanBrowseSelectedSnapshot));
            OnPropertyChanged(nameof(CanCompareSelectedSnapshot));
            OnPropertyChanged(nameof(BrowseSelectedSnapshotTooltip));
            OnPropertyChanged(nameof(CompareSelectedSnapshotTooltip));
            OnPropertyChanged(nameof(OpenRecoveryTooltip));
            SelectedComparisonSummary = string.Empty;
            LoadSelectedMetadataDrafts();
            OnPropertyChanged(nameof(SelectedProtectedActionLabel));
            OnPropertyChanged(nameof(SelectedKnownGoodActionLabel));
            _toggleSelectedProtectedCommand.RaiseCanExecuteChanged();
            _toggleSelectedKnownGoodCommand.RaiseCanExecuteChanged();
            _saveSelectedSnapshotMetadataCommand.RaiseCanExecuteChanged();
            _clearSelectedSnapshotMetadataCommand.RaiseCanExecuteChanged();
            _browseSelectedSnapshotCommand.RaiseCanExecuteChanged();
            _openRecoveryCommand.RaiseCanExecuteChanged();
            _compareSelectedSnapshotCommand.RaiseCanExecuteChanged();
        }
    }

    public HistoryViewModeOption? SelectedViewMode
    {
        get => _selectedViewMode;
        set
        {
            if (!SetField(ref _selectedViewMode, value))
                return;

            OnPropertyChanged(nameof(IsCompactView));
            OnPropertyChanged(nameof(ShowTimelineCards));
            OnPropertyChanged(nameof(ShowCompactRows));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value ?? string.Empty))
                return;

            _pageIndex = 0;
            QueueApplyFiltersAndPaging();
        }
    }

    public string BackupSignalLabel => LF("History.Signal.Backup", "{0} backup event(s)", BackupCount);
    public string MetadataSignalLabel => LF("History.Signal.Metadata", "{0} metadata-only event(s)", SnapshotOnlyCount);
    public string ProjectSignalLabel => LF("History.Signal.Projects", "{0} tracked project(s)", ProjectCount);

    public bool HasTimelineItems => TimelineItems.Count > 0;
    public bool HasLoadedEvents => _allTimelineItems.Count > 0;
    public bool HasActiveFilters =>
        (SelectedActivityFilter?.Filter ?? HistoryActivityFilter.All) != HistoryActivityFilter.All ||
        (SelectedDateRange?.Range ?? HistoryDateRange.All) != HistoryDateRange.All ||
        SelectedProjectFilter?.ProjectId is not null ||
        SelectedLaneFilter?.Lane is not null ||
        !string.IsNullOrWhiteSpace(SearchText);
    public bool IsCompactView => SelectedViewMode?.Mode == HistoryViewMode.Compact;
    public bool ShowTimelineCards => HasTimelineItems && !IsCompactView;
    public bool ShowCompactRows => HasTimelineItems && IsCompactView;
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
    public string SelectedEventSize => SelectedTimelineItem?.SizeLabel ?? L("History.Detail.SizeUnknown", "Size unknown");
    public string SelectedEventFiles => SelectedTimelineItem?.FileCountLabel ?? L("History.Detail.FileCountUnknown", "File count unknown");
    public string SelectedEventChangeSummary => SelectedTimelineItem?.ChangeSummaryLabel ?? L("History.Detail.NetNoChange", "net 0 B");
    public string SelectedEventLabel => SelectedTimelineItem?.MetadataLabel ?? string.Empty;
    public string SelectedEventNote => SelectedTimelineItem?.MetadataNote ?? string.Empty;
    public string SelectedEventTags => SelectedTimelineItem?.MetadataTags ?? string.Empty;
    public string SelectedEventMarkerSummary => SelectedTimelineItem?.MarkerSummary ?? string.Empty;
    public bool SelectedEventHasMarkerSummary => SelectedTimelineItem?.HasMarkerSummary == true;
    public string SelectedEventOriginSummary => SelectedTimelineItem?.OriginSummary ?? string.Empty;
    public bool SelectedEventHasOriginSummary => SelectedTimelineItem?.HasOriginSummary == true;
    public string SelectedRecoveryStatus => SelectedTimelineItem?.RecoveryStatus ?? L("History.Status.NotSelected", "Select an event");
    public string SelectedRecoveryStatusDetail => SelectedTimelineItem?.RecoveryStatusDetail ?? L("History.Status.NotSelectedDetail", "Choose a history event to see recovery details.");
    public bool CanEditSelectedSnapshotMetadata => SelectedTimelineItem?.SnapshotId > 0;
    public bool CanBrowseSelectedSnapshot => SelectedTimelineItem?.BackupId > 0;
    public bool CanCompareSelectedSnapshot => SelectedTimelineItem?.SnapshotId > 0;
    public string BrowseSelectedSnapshotTooltip => CanBrowseSelectedSnapshot
        ? L("History.Action.OpenBackupTip", "Open this backup in the system file manager.")
        : L("History.Action.OpenBackupUnavailableTip", "This history event does not have a linked backup folder.");
    public string OpenRecoveryTooltip => HasSelectedTimelineItem
        ? L("History.Action.OpenRecoveryTip", "Open Recovery to start a restore workflow for the selected project.")
        : L("History.Action.OpenRecoveryUnavailableTip", "Select an event before opening Recovery.");
    public string CompareSelectedSnapshotTooltip => CanCompareSelectedSnapshot
        ? L("History.Action.CompareTip", "Show the selected snapshot's changed-file summary.")
        : L("History.Action.CompareUnavailableTip", "Select a snapshot-backed event to compare changes.");
    public string SelectedComparisonSummary
    {
        get => _selectedComparisonSummary;
        private set
        {
            if (!SetField(ref _selectedComparisonSummary, value))
                return;

            OnPropertyChanged(nameof(HasSelectedComparisonSummary));
        }
    }
    public bool HasSelectedComparisonSummary => !string.IsNullOrWhiteSpace(SelectedComparisonSummary);
    public bool HasSelectedSnapshotTextMetadata =>
        !string.IsNullOrWhiteSpace(SelectedSnapshotLabelDraft) ||
        !string.IsNullOrWhiteSpace(SelectedSnapshotNoteDraft) ||
        !string.IsNullOrWhiteSpace(SelectedSnapshotTagsDraft);
    public string SelectedProtectedActionLabel => SelectedTimelineItem?.IsProtectedMarker == true
        ? L("History.Action.Unprotect", "Unprotect")
        : L("History.Action.Protect", "Protect");
    public string SelectedKnownGoodActionLabel => SelectedTimelineItem?.IsKnownGoodMarker == true
        ? L("History.Action.ClearKnownGood", "Clear known good")
        : L("History.Action.MarkKnownGood", "Mark known good");

    public string SelectedSnapshotLabelDraft
    {
        get => _selectedSnapshotLabelDraft;
        set
        {
            if (!SetField(ref _selectedSnapshotLabelDraft, value))
                return;

            OnSelectedMetadataDraftChanged();
        }
    }

    public string SelectedSnapshotNoteDraft
    {
        get => _selectedSnapshotNoteDraft;
        set
        {
            if (!SetField(ref _selectedSnapshotNoteDraft, value))
                return;

            OnSelectedMetadataDraftChanged();
        }
    }

    public string SelectedSnapshotTagsDraft
    {
        get => _selectedSnapshotTagsDraft;
        set
        {
            if (!SetField(ref _selectedSnapshotTagsDraft, value))
                return;

            OnSelectedMetadataDraftChanged();
        }
    }

    private void LoadSelectedMetadataDrafts()
    {
        SelectedSnapshotLabelDraft = SelectedEventLabel;
        SelectedSnapshotNoteDraft = SelectedEventNote;
        SelectedSnapshotTagsDraft = SelectedEventTags;
    }

    private void OnSelectedMetadataDraftChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSnapshotTextMetadata));
        _clearSelectedSnapshotMetadataCommand.RaiseCanExecuteChanged();
    }

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
        var allSnapshots = repo.GetAllSnapshots().ToList();
        var snapshotById = allSnapshots.ToDictionary(snapshot => snapshot.Id);
        var snapshotOnly = allSnapshots
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
        AddBackupTimelineItems(items, backups, projectsById, metadataBySnapshotId, snapshotById);
        AddRestoreTimelineItems(items, restores, projectsById, metadataBySnapshotId, backupBySnapshotId, snapshotById);
        AddSnapshotTimelineItems(items, snapshotOnly, projectsById, metadataBySnapshotId);

        items = items
            .OrderByDescending(item => item.CreatedUtc)
            .Take(80)
            .ToList();

        return new HistoryTimelineData(projects.Count, backups.Count, snapshotOnly.Count, restores.Count, items);
    }

    private static void AddBackupTimelineItems(
        List<HistoryTimelineItemViewModel> items,
        IEnumerable<Backup> backups,
        IReadOnlyDictionary<int, Project> projectsById,
        IReadOnlyDictionary<int, SnapshotHistoryMetadata> metadataBySnapshotId,
        Dictionary<int, Snapshot> snapshotById)
    {
        foreach (Backup backup in backups)
        {
            string projectName = ResolveProjectName(projectsById, backup.ProjectId);
            string type = string.IsNullOrWhiteSpace(backup.Type) ? "backup" : backup.Type;
            metadataBySnapshotId.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata);
            snapshotById.TryGetValue(backup.SnapshotId, out Snapshot? backupSnapshot);
            bool isManual = type.Equals("manual", StringComparison.OrdinalIgnoreCase);
            string markerSummary = BuildMarkerSummary(metadata);

            items.Add(new HistoryTimelineItemViewModel(CreateBackupItemData(
                backup,
                backupSnapshot,
                metadata,
                projectName,
                type,
                isManual,
                markerSummary)));

            if (metadata is not null && !string.IsNullOrWhiteSpace(markerSummary))
                items.Add(new HistoryTimelineItemViewModel(CreateMetadataItemData(backup, backupSnapshot, metadata, projectName, markerSummary)));
        }
    }

    private static void AddRestoreTimelineItems(
        List<HistoryTimelineItemViewModel> items,
        IEnumerable<RestoreHistoryEvent> restores,
        IReadOnlyDictionary<int, Project> projectsById,
        IReadOnlyDictionary<int, SnapshotHistoryMetadata> metadataBySnapshotId,
        Dictionary<int, Backup> backupBySnapshotId,
        Dictionary<int, Snapshot> snapshotById)
    {
        foreach (RestoreHistoryEvent restore in restores)
        {
            string projectName = ResolveProjectName(projectsById, restore.ProjectId);
            metadataBySnapshotId.TryGetValue(restore.SnapshotId, out SnapshotHistoryMetadata? metadata);
            backupBySnapshotId.TryGetValue(restore.SnapshotId, out Backup? restoreBackup);
            snapshotById.TryGetValue(restore.SnapshotId, out Snapshot? restoreSnapshot);

            int originBackupId = restore.BackupId;
            if (originBackupId <= 0 && restoreBackup is not null)
                originBackupId = restoreBackup.Id;

            items.Add(new HistoryTimelineItemViewModel(CreateRestoreItemData(
                restore,
                restoreBackup,
                restoreSnapshot,
                metadata,
                projectName,
                originBackupId)));
        }
    }

    private static void AddSnapshotTimelineItems(
        List<HistoryTimelineItemViewModel> items,
        IEnumerable<Snapshot> snapshots,
        IReadOnlyDictionary<int, Project> projectsById,
        IReadOnlyDictionary<int, SnapshotHistoryMetadata> metadataBySnapshotId)
    {
        foreach (Snapshot snapshot in snapshots)
        {
            string projectName = ResolveProjectName(projectsById, snapshot.ProjectId);
            metadataBySnapshotId.TryGetValue(snapshot.Id, out SnapshotHistoryMetadata? metadata);

            items.Add(new HistoryTimelineItemViewModel(new HistoryTimelineItemData
            {
                Kind = L("History.Event.Kind.Snapshot", "Snapshot"),
                ProjectName = projectName,
                ProjectId = snapshot.ProjectId,
                CreatedUtc = snapshot.CreatedUtc,
                Title = LF("History.Event.SnapshotTitle", "{0} snapshot", projectName),
                Detail = BuildSnapshotDetail(metadata),
                Lane = L("History.Lane.Metadata", "Snapshot"),
                GraphLane = HistoryTimelineLane.Metadata,
                IsImported = false,
                SnapshotId = snapshot.Id,
                OriginSummary = LF("History.Event.OriginSnapshotOnly", "snapshot #{0} has no linked backup node", snapshot.Id),
                IsProtectedMarker = metadata?.IsProtected == true,
                IsKnownGoodMarker = metadata?.IsKnownGood == true,
                MetadataLabel = metadata?.Label ?? string.Empty,
                MetadataNote = metadata?.Note ?? string.Empty,
                MetadataTags = metadata?.Tags ?? string.Empty,
                MarkerSummary = BuildMarkerSummary(metadata),
                TotalBytes = snapshot.TotalBytes,
                FileCount = snapshot.FileCount,
                DiffAdded = snapshot.DiffAdded,
                DiffModified = snapshot.DiffModified,
                DiffDeleted = snapshot.DiffDeleted,
                DiffNetBytes = snapshot.DiffNetBytes
            }));
        }
    }

    private void ApplyTimelineData(HistoryTimelineData data)
    {
        ProjectCount = data.ProjectCount;
        BackupCount = data.BackupCount;
        SnapshotOnlyCount = data.SnapshotOnlyCount;
        TotalEventCount = data.Items.Count;
        Summary = data.Items.Count == 0
            ? L(EmptySummaryKey, EmptySummaryFallback)
            : LF("History.Summary.Events", "Showing {0} recent history events across {1} project(s).", data.Items.Count, data.ProjectCount);
        HistoryTimelineItemViewModel? latest = data.Items.Count > 0 ? data.Items[0] : null;
        LatestEventLabel = latest?.TimeLabel ?? L(NoRecentHistoryKey, NoRecentHistoryFallback);
        LatestEventTitle = latest?.Title ?? L(NoRecentHistoryKey, NoRecentHistoryFallback);
        LatestEventDetail = latest?.Detail ?? L(EmptySummaryKey, EmptySummaryFallback);
        Insight = BuildInsight(data);

        _allTimelineItems.Clear();
        _allTimelineItems.AddRange(data.Items);
        ReplaceProjectFilterOptions(data.Items);
        _pageIndex = 0;
        QueueApplyFiltersAndPaging();

        OnPropertyChanged(nameof(HasTimelineItems));
        OnPropertyChanged(nameof(HasLoadedEvents));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(SelectedProjectFilter));
        OnPropertyChanged(nameof(SelectedLaneFilter));
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
        OnPropertyChanged(nameof(SelectedEventSize));
        OnPropertyChanged(nameof(SelectedEventFiles));
        OnPropertyChanged(nameof(SelectedEventChangeSummary));
        OnPropertyChanged(nameof(SelectedEventLabel));
        OnPropertyChanged(nameof(SelectedEventNote));
        OnPropertyChanged(nameof(SelectedEventTags));
        OnPropertyChanged(nameof(SelectedEventMarkerSummary));
        OnPropertyChanged(nameof(SelectedEventHasMarkerSummary));
        OnPropertyChanged(nameof(SelectedEventOriginSummary));
        OnPropertyChanged(nameof(SelectedEventHasOriginSummary));
        OnPropertyChanged(nameof(SelectedRecoveryStatus));
        OnPropertyChanged(nameof(SelectedRecoveryStatusDetail));
        OnPropertyChanged(nameof(CanEditSelectedSnapshotMetadata));
        OnPropertyChanged(nameof(CanBrowseSelectedSnapshot));
        OnPropertyChanged(nameof(CanCompareSelectedSnapshot));
        OnPropertyChanged(nameof(BrowseSelectedSnapshotTooltip));
        OnPropertyChanged(nameof(CompareSelectedSnapshotTooltip));
        OnPropertyChanged(nameof(OpenRecoveryTooltip));
        SelectedComparisonSummary = string.Empty;
        LoadSelectedMetadataDrafts();
        OnPropertyChanged(nameof(SelectedProtectedActionLabel));
        OnPropertyChanged(nameof(SelectedKnownGoodActionLabel));
        OnPropertyChanged(nameof(FilterStateLabel));
        _toggleSelectedProtectedCommand.RaiseCanExecuteChanged();
        _toggleSelectedKnownGoodCommand.RaiseCanExecuteChanged();
        _saveSelectedSnapshotMetadataCommand.RaiseCanExecuteChanged();
        _clearSelectedSnapshotMetadataCommand.RaiseCanExecuteChanged();
        _browseSelectedSnapshotCommand.RaiseCanExecuteChanged();
        _openRecoveryCommand.RaiseCanExecuteChanged();
        _compareSelectedSnapshotCommand.RaiseCanExecuteChanged();
        _resetFiltersCommand.RaiseCanExecuteChanged();
    }

    private static string ResolveProjectName(IReadOnlyDictionary<int, Project> projectsById, int projectId) =>
        projectsById.TryGetValue(projectId, out Project? project)
            ? project.Name
            : LF("History.Event.ProjectFallback", "Project #{0}", projectId);

    private static HistoryTimelineItemData CreateBackupItemData(
        Backup backup,
        Snapshot? snapshot,
        SnapshotHistoryMetadata? metadata,
        string projectName,
        string type,
        bool isManual,
        string markerSummary)
    {
        return new HistoryTimelineItemData
        {
            Kind = backup.IsImported
                ? L("History.Event.Kind.Imported", "Imported")
                : L("History.Event.Kind.Backup", "Backup"),
            ProjectName = projectName,
            ProjectId = backup.ProjectId,
            CreatedUtc = backup.CreatedUtc,
            Title = LF("History.Event.BackupTitle", "{0} {1} backup", projectName, type),
            Detail = BuildBackupDetail(backup, metadata),
            Lane = BuildBackupLaneLabel(backup.IsImported, isManual),
            GraphLane = BuildBackupGraphLane(backup.IsImported, isManual),
            IsImported = backup.IsImported,
            BackupId = backup.Id,
            SnapshotId = backup.SnapshotId,
            IsProtectedMarker = metadata?.IsProtected == true,
            IsKnownGoodMarker = metadata?.IsKnownGood == true,
            MetadataLabel = metadata?.Label ?? string.Empty,
            MetadataNote = metadata?.Note ?? string.Empty,
            MetadataTags = metadata?.Tags ?? string.Empty,
            MarkerSummary = markerSummary,
            TotalBytes = backup.TotalBytes,
            FileCount = snapshot?.FileCount ?? 0,
            DiffAdded = snapshot?.DiffAdded ?? 0,
            DiffModified = snapshot?.DiffModified ?? 0,
            DiffDeleted = snapshot?.DiffDeleted ?? 0,
            DiffNetBytes = snapshot?.DiffNetBytes ?? 0
        };
    }

    private static HistoryTimelineItemData CreateMetadataItemData(
        Backup backup,
        Snapshot? snapshot,
        SnapshotHistoryMetadata metadata,
        string projectName,
        string markerSummary)
    {
        DateTime metadataUtc = metadata.UpdatedUtc > metadata.CreatedUtc
            ? metadata.UpdatedUtc
            : metadata.CreatedUtc;

        return new HistoryTimelineItemData
        {
            Kind = L("History.Event.Kind.Metadata", "Metadata"),
            ProjectName = projectName,
            ProjectId = backup.ProjectId,
            CreatedUtc = metadataUtc,
            Title = LF("History.Event.MetadataTitle", "{0} snapshot metadata", projectName),
            Detail = BuildMetadataBranchDetail(metadata),
            Lane = L("History.Lane.Metadata", "Snapshot notes"),
            GraphLane = HistoryTimelineLane.Metadata,
            IsImported = false,
            BackupId = backup.Id,
            SnapshotId = backup.SnapshotId,
            OriginSummary = BuildOriginSummary(backup.Id, backup.SnapshotId),
            IsProtectedMarker = metadata.IsProtected,
            IsKnownGoodMarker = metadata.IsKnownGood,
            MetadataLabel = metadata.Label,
            MetadataNote = metadata.Note,
            MetadataTags = metadata.Tags,
            MarkerSummary = markerSummary,
            TotalBytes = backup.TotalBytes,
            FileCount = snapshot?.FileCount ?? 0,
            DiffAdded = snapshot?.DiffAdded ?? 0,
            DiffModified = snapshot?.DiffModified ?? 0,
            DiffDeleted = snapshot?.DiffDeleted ?? 0,
            DiffNetBytes = snapshot?.DiffNetBytes ?? 0
        };
    }

    private static HistoryTimelineItemData CreateRestoreItemData(
        RestoreHistoryEvent restore,
        Backup? backup,
        Snapshot? snapshot,
        SnapshotHistoryMetadata? metadata,
        string projectName,
        int originBackupId)
    {
        return new HistoryTimelineItemData
        {
            Kind = L("History.Event.Kind.Restore", "Restore"),
            ProjectName = projectName,
            ProjectId = restore.ProjectId,
            CreatedUtc = restore.CreatedUtc,
            Title = LF("History.Event.RestoreTitle", "{0} restored from backup", projectName),
            Detail = BuildRestoreDetail(restore, metadata),
            Lane = BuildRestoreLaneLabel(restore.RestoreMode),
            GraphLane = HistoryTimelineLane.Restore,
            IsImported = false,
            BackupId = originBackupId,
            SnapshotId = restore.SnapshotId,
            OriginSummary = BuildOriginSummary(originBackupId, restore.SnapshotId),
            IsProtectedMarker = metadata?.IsProtected == true,
            IsKnownGoodMarker = metadata?.IsKnownGood == true,
            MetadataLabel = metadata?.Label ?? string.Empty,
            MetadataNote = metadata?.Note ?? string.Empty,
            MetadataTags = metadata?.Tags ?? string.Empty,
            MarkerSummary = BuildMarkerSummary(metadata),
            TotalBytes = backup?.TotalBytes ?? snapshot?.TotalBytes ?? 0,
            FileCount = snapshot?.FileCount ?? 0,
            DiffAdded = snapshot?.DiffAdded ?? 0,
            DiffModified = snapshot?.DiffModified ?? 0,
            DiffDeleted = snapshot?.DiffDeleted ?? 0,
            DiffNetBytes = snapshot?.DiffNetBytes ?? 0
        };
    }

    private static HistoryTimelineLane BuildBackupGraphLane(bool isImported, bool isManual)
    {
        if (isImported)
            return HistoryTimelineLane.Restore;

        return isManual ? HistoryTimelineLane.Manual : HistoryTimelineLane.Backup;
    }

    private static string BuildBackupLaneLabel(bool isImported, bool isManual)
    {
        if (isImported)
            return L("History.Lane.Imported", "Imported");

        return isManual
            ? L("History.Lane.Manual", "Manual")
            : L("History.Lane.Auto", "Auto");
    }

    private static string BuildRestoreLaneLabel(string restoreMode) =>
        string.Equals(restoreMode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase)
            ? L("History.Lane.RestoreSandbox", "Sandbox restore")
            : L("History.Lane.RestoreDirect", "Direct restore");

    private void ResetFilters()
    {
        _selectedActivityFilter = ActivityFilterOptions.FirstOrDefault(option => option.Filter == HistoryActivityFilter.All);
        _selectedDateRange = DateRangeOptions.FirstOrDefault(option => option.Range == HistoryDateRange.All);
        _selectedProjectFilter = ProjectFilterOptions.FirstOrDefault(option => option.ProjectId is null);
        _selectedLaneFilter = LaneFilterOptions.FirstOrDefault(option => option.Lane is null);
        SearchText = string.Empty;
        _pageIndex = 0;

        OnPropertyChanged(nameof(SelectedActivityFilter));
        OnPropertyChanged(nameof(SelectedDateRange));
        OnPropertyChanged(nameof(SelectedProjectFilter));
        OnPropertyChanged(nameof(SelectedLaneFilter));
        OnPropertyChanged(nameof(SearchText));
        QueueApplyFiltersAndPaging();
    }

    private void MovePage(int delta)
    {
        int next = Math.Clamp(_pageIndex + delta, 0, Math.Max(0, PageCount - 1));
        if (next == _pageIndex)
            return;

        _pageIndex = next;
        QueueApplyFiltersAndPaging();
    }

    private void QueueApplyFiltersAndPaging()
    {
        int revision = Interlocked.Increment(ref _filterRevision);
        var source = _allTimelineItems.ToList();
        var filterState = new HistoryFilterState(
            SelectedActivityFilter?.Filter ?? HistoryActivityFilter.All,
            SelectedDateRange?.Range ?? HistoryDateRange.All,
            SelectedProjectFilter?.ProjectId,
            SelectedLaneFilter?.Lane,
            SearchText,
            _pageIndex);

        _ = ApplyFiltersAndPagingAsync(revision, source, filterState);
    }

    private async Task ApplyFiltersAndPagingAsync(
        int revision,
        IReadOnlyList<HistoryTimelineItemViewModel> source,
        HistoryFilterState filterState)
    {
        HistoryPageResult result = await Task.Run(() => BuildHistoryPageResult(source, filterState)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyHistoryPageResult(revision, result));
    }

    private static HistoryPageResult BuildHistoryPageResult(
        IReadOnlyList<HistoryTimelineItemViewModel> source,
        HistoryFilterState filterState)
    {
        IEnumerable<HistoryTimelineItemViewModel> query = source;

        query = filterState.ActivityFilter switch
        {
            HistoryActivityFilter.Backups => query.Where(item =>
                item.GraphLane is HistoryTimelineLane.Backup or HistoryTimelineLane.Manual),
            HistoryActivityFilter.Restores => query.Where(item => item.GraphLane == HistoryTimelineLane.Restore),
            HistoryActivityFilter.Imported => query.Where(item => item.IsImported),
            HistoryActivityFilter.Metadata => query.Where(item => item.GraphLane == HistoryTimelineLane.Metadata),
            HistoryActivityFilter.Protected => query.Where(item => item.IsProtectedMarker),
            HistoryActivityFilter.KnownGood => query.Where(item => item.IsKnownGoodMarker),
            _ => query
        };

        if (filterState.ProjectId is int projectId)
            query = query.Where(item => item.ProjectId == projectId);

        if (filterState.Lane is HistoryTimelineLane lane)
            query = query.Where(item => item.GraphLane == lane || (lane == HistoryTimelineLane.Backup && item.GraphLane == HistoryTimelineLane.Manual));

        if (!string.IsNullOrWhiteSpace(filterState.SearchText))
        {
            string search = filterState.SearchText.Trim();
            query = query.Where(item => item.MatchesSearch(search));
        }

        DateTime cutoffUtc = filterState.DateRange switch
        {
            HistoryDateRange.Today => DateTime.UtcNow.Date,
            HistoryDateRange.Last7Days => DateTime.UtcNow.AddDays(-7),
            HistoryDateRange.Last30Days => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.MinValue
        };

        if (cutoffUtc > DateTime.MinValue)
            query = query.Where(item => item.CreatedUtc.ToUniversalTime() >= cutoffUtc);

        List<HistoryTimelineItemViewModel> filtered = query.ToList();
        int pageCount = filtered.Count == 0 ? 0 : (int)Math.Ceiling(filtered.Count / (double)PageSize);
        int pageIndex = Math.Clamp(filterState.PageIndex, 0, Math.Max(0, pageCount - 1));
        int pageStart = pageIndex * PageSize;
        List<HistoryTimelineItemViewModel> pageItems = filtered.Skip(pageStart).Take(PageSize).ToList();
        ApplyDateGroupLabels(pageItems);
        List<HistoryGraphPaths> graphPaths = BuildPageGraphPaths(filtered, pageStart, pageItems.Count);

        return new HistoryPageResult(filtered.Count, pageIndex, pageItems, graphPaths);
    }

    private void ApplyHistoryPageResult(int revision, HistoryPageResult result)
    {
        if (revision != _filterRevision)
            return;

        HistoryTimelineItemViewModel? previousSelection = SelectedTimelineItem;
        _filteredEventCount = result.FilteredEventCount;
        _pageIndex = result.PageIndex;

        TimelineItems.Clear();
        for (int i = 0; i < result.PageItems.Count; i++)
        {
            HistoryTimelineItemViewModel item = result.PageItems[i];
            HistoryGraphPaths paths = result.GraphPaths[i];
            item.SetPageGraphPaths(paths.BackupPath, paths.MetadataPath, paths.RestorePath);
            TimelineItems.Add(item);
        }

        if (TimelineItems.Count == 0)
        {
            SelectedTimelineItem = null;
        }
        else if (previousSelection is null)
        {
            SelectedTimelineItem = TimelineItems[0];
        }
        else if (!TimelineItems.Contains(previousSelection))
        {
            SelectedTimelineItem =
                TimelineItems.FirstOrDefault(item =>
                    item.SnapshotId > 0 &&
                    item.SnapshotId == previousSelection.SnapshotId &&
                    item.BackupId == previousSelection.BackupId &&
                    item.GraphLane == previousSelection.GraphLane) ??
                TimelineItems.FirstOrDefault(item =>
                    item.SnapshotId > 0 &&
                    item.SnapshotId == previousSelection.SnapshotId) ??
                TimelineItems[0];
        }

        OnPropertyChanged(nameof(HasTimelineItems));
        OnPropertyChanged(nameof(ShowTimelineCards));
        OnPropertyChanged(nameof(ShowCompactRows));
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
        _browseSelectedSnapshotCommand.RaiseCanExecuteChanged();
        _openRecoveryCommand.RaiseCanExecuteChanged();
        _compareSelectedSnapshotCommand.RaiseCanExecuteChanged();
    }

    private void BrowseSelectedSnapshot()
    {
        int backupId = SelectedTimelineItem?.BackupId ?? 0;
        if (backupId <= 0)
            return;

        OpenBackupFolderRequested?.Invoke(backupId);
    }

    private void CompareSelectedSnapshot()
    {
        HistoryTimelineItemViewModel? selected = SelectedTimelineItem;
        if (selected is null || selected.SnapshotId <= 0)
            return;

        SelectedComparisonSummary = LF(
            "History.Compare.SelectedSummary",
            "Snapshot #{0}: {1}. {2} in {3}.",
            selected.SnapshotId,
            selected.ChangeSummaryLabel,
            selected.FileCountLabel,
            selected.SizeLabel);
    }

    private void ReplaceProjectFilterOptions(IReadOnlyList<HistoryTimelineItemViewModel> items)
    {
        int? previousProjectId = SelectedProjectFilter?.ProjectId;
        var options = items
            .Where(item => item.ProjectId > 0)
            .GroupBy(item => item.ProjectId)
            .Select(group => new HistoryProjectFilterOption(
                group.Key,
                group.OrderByDescending(item => item.CreatedUtc).First().ProjectName))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ProjectFilterOptions.Clear();
        ProjectFilterOptions.Add(new HistoryProjectFilterOption(null, L("History.Project.All", "All projects")));
        foreach (HistoryProjectFilterOption option in options)
            ProjectFilterOptions.Add(option);

        _selectedProjectFilter = ProjectFilterOptions.FirstOrDefault(option => option.ProjectId == previousProjectId)
            ?? ProjectFilterOptions[0];
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

    private static void ApplyDateGroupLabels(IReadOnlyList<HistoryTimelineItemViewModel> items)
    {
        DateTime? previousDate = null;
        foreach (HistoryTimelineItemViewModel item in items)
        {
            DateTime localDate = item.CreatedUtc.ToLocalTime().Date;
            item.SetDateGroupLabel(previousDate == localDate ? string.Empty : FormatDateGroupLabel(localDate));
            previousDate = localDate;
        }
    }

    private static string FormatDateGroupLabel(DateTime localDate)
    {
        DateTime today = DateTime.Now.Date;
        if (localDate == today)
            return L("History.Group.Today", "Today");
        if (localDate == today.AddDays(-1))
            return L("History.Group.Yesterday", "Yesterday");

        return localDate.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private async Task SaveSelectedSnapshotMetadataAsync(bool clearTextMetadata)
    {
        HistoryTimelineItemViewModel? selected = SelectedTimelineItem;
        if (selected is null || selected.SnapshotId <= 0)
            return;

        int snapshotId = selected.SnapshotId;
        string label = clearTextMetadata ? string.Empty : NormalizeMetadataText(SelectedSnapshotLabelDraft);
        string note = clearTextMetadata ? string.Empty : NormalizeMetadataText(SelectedSnapshotNoteDraft);
        string tags = clearTextMetadata ? string.Empty : NormalizeMetadataTags(SelectedSnapshotTagsDraft);

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
                Label = label,
                Note = note,
                Tags = tags,
                IsProtected = existing?.IsProtected ?? selected.IsProtectedMarker,
                IsKnownGood = existing?.IsKnownGood ?? selected.IsKnownGoodMarker,
                CreatedUtc = existing is null || existing.CreatedUtc == default ? now : existing.CreatedUtc,
                UpdatedUtc = now
            });
        }).ConfigureAwait(false);

        await RefreshAsync(force: true).ConfigureAwait(false);
    }

    private static string NormalizeMetadataText(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Trim();

    private static string NormalizeMetadataTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(
            ", ",
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
    }

    private static List<HistoryGraphPaths> BuildPageGraphPaths(List<HistoryTimelineItemViewModel> filtered, int pageStart, int pageCount)
    {
        var paths = new List<HistoryGraphPaths>(pageCount);
        for (int pageOffset = 0; pageOffset < pageCount; pageOffset++)
        {
            int index = pageStart + pageOffset;
            HistoryTimelineItemViewModel current = filtered[index];
            HistoryTimelineLane? previous = index > 0 ? filtered[index - 1].GraphRailLane : null;
            HistoryTimelineLane? next = index + 1 < filtered.Count ? filtered[index + 1].GraphRailLane : null;

            paths.Add(new HistoryGraphPaths(
                BuildBackupRailPath(),
                BuildSideRailPath(HistoryTimelineLane.Metadata, current.GraphRailLane, previous, next),
                BuildSideRailPath(HistoryTimelineLane.Restore, current.GraphRailLane, previous, next)));
        }

        return paths;
    }

    private const double GraphRowMidpoint = 32;
    private const double GraphRowBottom = 64;

    private static string BuildBackupRailPath() => string.Create(CultureInfo.InvariantCulture, $"M 28,0 L 28,{GraphRowBottom}");

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
            ? string.Create(CultureInfo.InvariantCulture, $"M {x},0 L {x},{GraphRowMidpoint}")
            : string.Create(CultureInfo.InvariantCulture, $"M {trunkX},0 L {trunkX},18 C {trunkX},26 {x - 14},24 {x},{GraphRowMidpoint}");
        string bottomSegment = continuesToNext
            ? string.Create(CultureInfo.InvariantCulture, $" M {x},{GraphRowMidpoint} L {x},{GraphRowBottom}")
            : string.Create(CultureInfo.InvariantCulture, $" M {x},{GraphRowMidpoint} C {x - 14},40 {trunkX},42 {trunkX},48 L {trunkX},{GraphRowBottom}");

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
                "{0} snapshot-only event(s) are visible. Use labels, notes, tags, protection, and known-good markers to explain important restore points.",
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

    private sealed record HistoryFilterState(
        HistoryActivityFilter ActivityFilter,
        HistoryDateRange DateRange,
        int? ProjectId,
        HistoryTimelineLane? Lane,
        string SearchText,
        int PageIndex);

    private sealed record HistoryPageResult(
        int FilteredEventCount,
        int PageIndex,
        IReadOnlyList<HistoryTimelineItemViewModel> PageItems,
        IReadOnlyList<HistoryGraphPaths> GraphPaths);

    private sealed record HistoryGraphPaths(
        string BackupPath,
        string MetadataPath,
        string RestorePath);
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
    Imported,
    Metadata,
    Protected,
    KnownGood
}

public enum HistoryDateRange
{
    All,
    Today,
    Last7Days,
    Last30Days
}

public enum HistoryViewMode
{
    Timeline,
    Compact
}

public sealed record HistoryActivityFilterOption(HistoryActivityFilter Filter, string Label);

public sealed record HistoryDateRangeOption(HistoryDateRange Range, string Label);

public sealed record HistoryProjectFilterOption(int? ProjectId, string Label);

public sealed record HistoryLaneFilterOption(HistoryTimelineLane? Lane, string Label);

public sealed record HistoryViewModeOption(HistoryViewMode Mode, string Label);

internal sealed record HistoryTimelineItemData
{
    public string Kind { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public int ProjectId { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Lane { get; init; } = string.Empty;
    public HistoryTimelineLane GraphLane { get; init; }
    public bool IsImported { get; init; }
    public int BackupId { get; init; }
    public int SnapshotId { get; init; }
    public string OriginSummary { get; init; } = string.Empty;
    public bool IsProtectedMarker { get; init; }
    public bool IsKnownGoodMarker { get; init; }
    public string MetadataLabel { get; init; } = string.Empty;
    public string MetadataNote { get; init; } = string.Empty;
    public string MetadataTags { get; init; } = string.Empty;
    public string MarkerSummary { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FileCount { get; init; }
    public int DiffAdded { get; init; }
    public int DiffModified { get; init; }
    public int DiffDeleted { get; init; }
    public long DiffNetBytes { get; init; }
}

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

    internal HistoryTimelineItemViewModel(HistoryTimelineItemData data)
    {
        Kind = data.Kind;
        ProjectName = data.ProjectName;
        ProjectId = data.ProjectId;
        CreatedUtc = data.CreatedUtc;
        Title = data.Title;
        Detail = data.Detail;
        Lane = data.Lane;
        GraphLane = data.GraphLane;
        IsImported = data.IsImported;
        BackupId = data.BackupId;
        SnapshotId = data.SnapshotId;
        OriginSummary = data.OriginSummary ?? string.Empty;
        IsProtectedMarker = data.IsProtectedMarker;
        IsKnownGoodMarker = data.IsKnownGoodMarker;
        MetadataLabel = data.MetadataLabel ?? string.Empty;
        MetadataNote = data.MetadataNote ?? string.Empty;
        MetadataTags = data.MetadataTags ?? string.Empty;
        MarkerSummary = data.MarkerSummary ?? string.Empty;
        TotalBytes = Math.Max(0, data.TotalBytes);
        FileCount = Math.Max(0, data.FileCount);
        DiffAdded = Math.Max(0, data.DiffAdded);
        DiffModified = Math.Max(0, data.DiffModified);
        DiffDeleted = Math.Max(0, data.DiffDeleted);
        DiffNetBytes = data.DiffNetBytes;
    }

    public string Kind { get; }
    public string ProjectName { get; }
    public int ProjectId { get; }
    public DateTime CreatedUtc { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Lane { get; }
    public HistoryTimelineLane GraphLane { get; }
    public bool IsImported { get; }
    public int BackupId { get; }
    public int SnapshotId { get; }
    public string OriginSummary { get; }
    public bool HasOriginSummary => !string.IsNullOrWhiteSpace(OriginSummary);
    public bool IsProtectedMarker { get; }
    public bool IsKnownGoodMarker { get; }
    public string MetadataLabel { get; }
    public string MetadataNote { get; }
    public string MetadataTags { get; }
    public string MarkerSummary { get; }
    public bool HasMarkerSummary => !string.IsNullOrWhiteSpace(MarkerSummary);
    public long TotalBytes { get; }
    public long FileCount { get; }
    public int DiffAdded { get; }
    public int DiffModified { get; }
    public int DiffDeleted { get; }
    public long DiffNetBytes { get; }
    public string SizeLabel => TotalBytes > 0
        ? UiFormat.FormatBytes(TotalBytes, "0.#")
        : L("History.Detail.SizeUnknown", "Size unknown");
    public string FileCountLabel => FileCount > 0
        ? LF("History.Detail.FileCount", "{0:N0} files", FileCount)
        : L("History.Detail.FileCountUnknown", "File count unknown");
    public string DiffNetLabel => DiffNetBytes == 0
        ? L("History.Detail.NetNoChange", "net 0 B")
        : LF("History.Detail.NetChange", "net {0}", ByteSizeFormat.FormatSignedBytes(DiffNetBytes, "0.#"));
    public string ChangeSummaryLabel => LF(
        "History.Detail.ChangeSummary",
        "+{0} / ~{1} / -{2} | {3}",
        DiffAdded,
        DiffModified,
        DiffDeleted,
        DiffNetLabel);
    public string DateGroupLabel { get; private set; } = string.Empty;
    public bool HasDateGroupLabel => !string.IsNullOrWhiteSpace(DateGroupLabel);
    public bool IsRestorable => SnapshotId > 0 && GraphLane != HistoryTimelineLane.Metadata;
    public string RecoveryStatus
    {
        get
        {
            if (IsKnownGoodMarker)
                return L("History.Status.KnownGood", "Known good restore point");
            if (IsProtectedMarker)
                return L("History.Status.Protected", "Protected restore point");
            if (IsRestorable)
                return L("History.Status.Ready", "Ready to restore");
            return L("History.Status.MetadataOnly", "Metadata only");
        }
    }
    public string RecoveryStatusDetail
    {
        get
        {
            if (IsKnownGoodMarker)
                return L("History.Status.KnownGoodDetail", "This snapshot is marked as a reliable recovery point.");
            if (IsProtectedMarker)
                return L("History.Status.ProtectedDetail", "This snapshot is protected from automatic cleanup and retention pruning.");
            if (IsRestorable)
                return L("History.Status.ReadyDetail", "This snapshot is indexed and available for recovery workflows.");
            return L("History.Status.MetadataOnlyDetail", "This event helps complete project history, but may not contain restorable files by itself.");
        }
    }
    public string PrimaryBadge => GraphLane switch
    {
        HistoryTimelineLane.Restore => L("History.Badge.Restore", "Restore"),
        HistoryTimelineLane.Metadata => L("History.Badge.Metadata", "Metadata"),
        _ when IsImported => L("History.Badge.Imported", "Imported"),
        _ => L("History.Badge.Backup", "Backup")
    };
    public string SecondaryBadge => GraphLane switch
    {
        HistoryTimelineLane.Manual => L("History.Badge.Manual", "Manual"),
        HistoryTimelineLane.Backup => L("History.Badge.Auto", "Auto"),
        HistoryTimelineLane.Restore => L("History.Badge.Recovery", "Recovery"),
        _ => L("History.Badge.Snapshot", "Snapshot")
    };
    public string SafetyBadge
    {
        get
        {
            if (IsKnownGoodMarker)
                return L("History.Badge.KnownGood", "Known good");
            if (IsProtectedMarker)
                return L("History.Badge.Protected", "Protected");
            return IsRestorable
                ? L("History.Badge.Restorable", "Restorable")
                : L("History.Badge.Indexed", "Indexed");
        }
    }
    public string HoverDetail => string.Join(
        Environment.NewLine,
        new[]
        {
            Title,
            Detail,
            LF("History.Detail.HoverMeta", "{0} | {1} | {2}", SizeLabel, FileCountLabel, ChangeSummaryLabel),
            TimeLabel
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
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

    public void SetDateGroupLabel(string value) => DateGroupLabel = value ?? string.Empty;

    public bool MatchesSearch(string search)
    {
        return Contains(ProjectName, search) ||
            Contains(Title, search) ||
            Contains(Detail, search) ||
            Contains(Kind, search) ||
            Contains(Lane, search) ||
            Contains(MetadataLabel, search) ||
            Contains(MetadataNote, search) ||
            Contains(MetadataTags, search) ||
            Contains(OriginSummary, search);
    }

    private static bool Contains(string value, string search) =>
        value?.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;

    public double NodeLeft => GraphLane switch
    {
        HistoryTimelineLane.Backup => 18,
        HistoryTimelineLane.Manual => 18,
        HistoryTimelineLane.Metadata => 42,
        _ => 66
    };

    public double InnerNodeLeft => NodeLeft + 5;

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
