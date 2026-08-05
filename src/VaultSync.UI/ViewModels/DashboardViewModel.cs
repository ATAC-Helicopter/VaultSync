using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Avalonia.Media; // for Brush in legend + activity
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure; // for RelayCommand
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    public class DashboardViewModel : ViewModelBase
    {
        public enum StorageLegendSortMode
        {
            LargestFirst,
            Alphabetical
        }

        // KPIs (bindable, backed by fields)
        private int _projectCount;
        private string _projectsHint = string.Empty;
        private int _snapshotCount;
        private string _snapshotsHint = string.Empty;
        private string _storageUsed = "0 B";
        private string _storageUsedLocal = "0 B";
        private string _storageHint = string.Empty;
        // Backup disk usage card fields
        private double _backupDiskUsedPercent;
        private string _backupDiskFreeText = string.Empty;
        private string _backupDiskThresholdText = string.Empty;
        private string _backupDiskRiskReason = string.Empty;
        private bool _backupDiskIsBelowThreshold;
        private string _snapshotActivitySummary = string.Empty;
        private string _snapshotsSummaryLine = string.Empty;
        private string _restoreReadinessHeadline = string.Empty;
        private string _restoreReadinessDetail = string.Empty;
        private int _restoreReadinessReadyCount;
        private int _restoreReadinessAttentionCount;
        private int _restoreReadinessRiskCount;
        private int _restoreReadinessUnavailableCount;
        private string _restoreReadinessReadyLabel = string.Empty;
        private string _restoreReadinessAttentionLabel = string.Empty;
        private string _restoreReadinessRiskLabel = string.Empty;
        private string _restoreReadinessUnavailableLabel = string.Empty;
        private bool _showRestoreReadinessIssues;
        private string _recoveryCoverageDetail = string.Empty;
        private string _recoveryCoverage24Label = string.Empty;
        private string _recoveryCoverage7Label = string.Empty;
        private string _recoveryCoverage30Label = string.Empty;
        private string _recoveryCoverage90Label = string.Empty;
        private string _requiredActionTitle = string.Empty;
        private string _requiredActionDetail = string.Empty;
        private string _nextRunText = string.Empty;
        private string _nextRunDetail = string.Empty;
        private string _latestKnownGoodTitle = string.Empty;
        private string _latestKnownGoodDetail = string.Empty;

        // Backup storage segmented usage bar (Other + per-project)
        public IReadOnlyList<BackupUsageSegment> BackupUsageSegments { get; private set; } =
            [];
        public IReadOnlyList<BackupUsageSegment> BackupTopConsumers { get; private set; } =
            [];

        public ISeries[] BackupUsageSeries { get; private set; } = [];
        public Axis[] BackupUsageXAxes { get; private set; } = [];
        public Axis[] BackupUsageYAxes { get; private set; } = [];
        public bool HasBackupUsageSegments => BackupUsageSegments.Count > 0;
        public bool HasBackupTopConsumers => BackupTopConsumers.Count > 0;

        public int ProjectCount
        {
            get => _projectCount;
            private set
            {
                if (_projectCount == value) return;
                _projectCount = value;
                OnPropertyChanged();
            }
        }

        public string ProjectsHint
        {
            get => _projectsHint;
            private set
            {
                if (_projectsHint == value) return;
                _projectsHint = value;
                OnPropertyChanged();
            }
        }

        public int SnapshotCount
        {
            get => _snapshotCount;
            private set
            {
                if (_snapshotCount == value) return;
                _snapshotCount = value;
                OnPropertyChanged();
            }
        }

        public string SnapshotsHint
        {
            get => _snapshotsHint;
            private set
            {
                if (_snapshotsHint == value) return;
                _snapshotsHint = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalSnapshotsWeekLabel));
            }
        }

        public string StorageUsed
        {
            get => _storageUsed;
            private set
            {
                if (_storageUsed == value) return;
                _storageUsed = value;
                OnPropertyChanged();
            }
        }

        public string StorageUsedLocal
        {
            get => _storageUsedLocal;
            private set
            {
                if (_storageUsedLocal == value) return;
                _storageUsedLocal = value;
                OnPropertyChanged();
            }
        }

        public string StorageHint
        {
            get => _storageHint;
            private set
            {
                if (_storageHint == value) return;
                _storageHint = value;
                OnPropertyChanged();
            }
        }

        public double BackupDiskUsedPercent
        {
            get => _backupDiskUsedPercent;
            private set
            {
                if (Math.Abs(_backupDiskUsedPercent - value) < 0.001) return;
                _backupDiskUsedPercent = value;
                OnPropertyChanged();
            }
        }

        public string BackupDiskFreeText
        {
            get => _backupDiskFreeText;
            private set
            {
                if (_backupDiskFreeText == value) return;
                _backupDiskFreeText = value;
                OnPropertyChanged();
            }
        }

        public string BackupDiskThresholdText
        {
            get => _backupDiskThresholdText;
            private set
            {
                if (_backupDiskThresholdText == value) return;
                _backupDiskThresholdText = value;
                OnPropertyChanged();
            }
        }

        public bool BackupDiskIsBelowThreshold
        {
            get => _backupDiskIsBelowThreshold;
            private set
            {
                if (_backupDiskIsBelowThreshold == value) return;
                _backupDiskIsBelowThreshold = value;
                OnPropertyChanged();
            }
        }

        public string BackupDiskRiskReason
        {
            get => _backupDiskRiskReason;
            private set
            {
                if (_backupDiskRiskReason == value) return;
                _backupDiskRiskReason = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasBackupDiskRiskReason));
            }
        }

        public bool HasBackupDiskRiskReason => !string.IsNullOrWhiteSpace(BackupDiskRiskReason);

        // Summary pills shown in the dashboard backups section.
        public string SnapshotActivitySummary
        {
            get => _snapshotActivitySummary;
            private set
            {
                if (_snapshotActivitySummary == value) return;
                _snapshotActivitySummary = value;
                OnPropertyChanged();
            }
        }

        public string SnapshotsSummaryLine
        {
            get => _snapshotsSummaryLine;
            private set
            {
                if (_snapshotsSummaryLine == value) return;
                _snapshotsSummaryLine = value;
                OnPropertyChanged();
            }
        }

        public string RestoreReadinessHeadline
        {
            get => _restoreReadinessHeadline;
            private set
            {
                if (_restoreReadinessHeadline == value) return;
                _restoreReadinessHeadline = value;
                OnPropertyChanged();
            }
        }

        public string RestoreReadinessDetail
        {
            get => _restoreReadinessDetail;
            private set
            {
                if (_restoreReadinessDetail == value) return;
                _restoreReadinessDetail = value;
                OnPropertyChanged();
            }
        }

        public int RestoreReadinessReadyCount
        {
            get => _restoreReadinessReadyCount;
            private set
            {
                if (_restoreReadinessReadyCount == value) return;
                _restoreReadinessReadyCount = value;
                OnPropertyChanged();
            }
        }

        public int RestoreReadinessAttentionCount
        {
            get => _restoreReadinessAttentionCount;
            private set
            {
                if (_restoreReadinessAttentionCount == value) return;
                _restoreReadinessAttentionCount = value;
                OnPropertyChanged();
            }
        }

        public int RestoreReadinessRiskCount
        {
            get => _restoreReadinessRiskCount;
            private set
            {
                if (_restoreReadinessRiskCount == value) return;
                _restoreReadinessRiskCount = value;
                OnPropertyChanged();
            }
        }

        public int RestoreReadinessUnavailableCount
        {
            get => _restoreReadinessUnavailableCount;
            private set
            {
                if (_restoreReadinessUnavailableCount == value) return;
                _restoreReadinessUnavailableCount = value;
                OnPropertyChanged();
            }
        }

        public string RestoreReadinessReadyLabel
        {
            get => _restoreReadinessReadyLabel;
            private set
            {
                if (_restoreReadinessReadyLabel == value) return;
                _restoreReadinessReadyLabel = value;
                OnPropertyChanged();
            }
        }

        public string RestoreReadinessAttentionLabel
        {
            get => _restoreReadinessAttentionLabel;
            private set
            {
                if (_restoreReadinessAttentionLabel == value) return;
                _restoreReadinessAttentionLabel = value;
                OnPropertyChanged();
            }
        }

        public string RestoreReadinessRiskLabel
        {
            get => _restoreReadinessRiskLabel;
            private set
            {
                if (_restoreReadinessRiskLabel == value) return;
                _restoreReadinessRiskLabel = value;
                OnPropertyChanged();
            }
        }

        public string RestoreReadinessUnavailableLabel
        {
            get => _restoreReadinessUnavailableLabel;
            private set
            {
                if (_restoreReadinessUnavailableLabel == value) return;
                _restoreReadinessUnavailableLabel = value;
                OnPropertyChanged();
            }
        }

        public bool ShowRestoreReadinessIssues
        {
            get => _showRestoreReadinessIssues;
            set
            {
                if (_showRestoreReadinessIssues == value) return;
                _showRestoreReadinessIssues = value;
                OnPropertyChanged();
            }
        }

        public string RecoveryCoverageDetail
        {
            get => _recoveryCoverageDetail;
            private set
            {
                if (_recoveryCoverageDetail == value) return;
                _recoveryCoverageDetail = value;
                OnPropertyChanged();
            }
        }

        public string RecoveryCoverage24Label
        {
            get => _recoveryCoverage24Label;
            private set
            {
                if (_recoveryCoverage24Label == value) return;
                _recoveryCoverage24Label = value;
                OnPropertyChanged();
            }
        }

        public string RecoveryCoverage7Label
        {
            get => _recoveryCoverage7Label;
            private set
            {
                if (_recoveryCoverage7Label == value) return;
                _recoveryCoverage7Label = value;
                OnPropertyChanged();
            }
        }

        public string RecoveryCoverage30Label
        {
            get => _recoveryCoverage30Label;
            private set
            {
                if (_recoveryCoverage30Label == value) return;
                _recoveryCoverage30Label = value;
                OnPropertyChanged();
            }
        }

        public string RecoveryCoverage90Label
        {
            get => _recoveryCoverage90Label;
            private set
            {
                if (_recoveryCoverage90Label == value) return;
                _recoveryCoverage90Label = value;
                OnPropertyChanged();
            }
        }

        public string RequiredActionTitle
        {
            get => _requiredActionTitle;
            private set
            {
                if (_requiredActionTitle == value) return;
                _requiredActionTitle = value;
                OnPropertyChanged();
            }
        }

        public string RequiredActionDetail
        {
            get => _requiredActionDetail;
            private set
            {
                if (_requiredActionDetail == value) return;
                _requiredActionDetail = value;
                OnPropertyChanged();
            }
        }

        public string NextRunText
        {
            get => _nextRunText;
            private set
            {
                if (_nextRunText == value) return;
                _nextRunText = value;
                OnPropertyChanged();
            }
        }

        public string NextRunDetail
        {
            get => _nextRunDetail;
            private set
            {
                if (_nextRunDetail == value) return;
                _nextRunDetail = value;
                OnPropertyChanged();
            }
        }

        public string LatestKnownGoodTitle
        {
            get => _latestKnownGoodTitle;
            private set
            {
                if (_latestKnownGoodTitle == value) return;
                _latestKnownGoodTitle = value;
                OnPropertyChanged();
            }
        }

        public string LatestKnownGoodDetail
        {
            get => _latestKnownGoodDetail;
            private set
            {
                if (_latestKnownGoodDetail == value) return;
                _latestKnownGoodDetail = value;
                OnPropertyChanged();
            }
        }

        // Search / actions (your RelayCommand expects Action<object?>)
        public string? SearchText { get; set; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand NewSnapshotCommand { get; }
        public RelayCommand ToggleRestoreReadinessIssuesCommand { get; }
        public RelayCommand OpenBackupsCommand { get; }
        public RelayCommand OpenHistoryCommand { get; }
        public RelayCommand OpenRecoveryCommand { get; }
        public RelayCommand OpenScheduleCommand { get; }

        // Chart bindings
        public ISeries[] SnapshotSeries { get; private set; } = [];
        public Axis[] SnapshotXAxes { get; private set; } = [];
        public Axis[] SnapshotYAxes { get; private set; } = [];
        public ObservableCollection<SnapshotActivityPoint> WeeklySnapshotActivity { get; } = [];
        public double WeeklyChartHeight { get; private set; } = 180;
        public double WeeklyAverageLineOffset { get; private set; }
        public string WeeklyAverageLabel { get; private set; } = string.Empty;
        public string TotalSnapshotsWeek => _snapshotCountsByDay.Sum().ToString();
        public string TotalSnapshotsWeekLabel => string.Format(L("Dashboard.Hint.SnapshotsThisWeek", "{0} this week"), TotalSnapshotsWeek);

        // Donut bindings
        public ISeries[] StorageSeries { get; private set; } = [];
        public bool HasStorageSeries => StorageSeries is { Length: > 0 };
        public IEnumerable<LegendItem> StorageLegend { get; private set; } = [];
        public ObservableCollection<StorageLegendSortOption> StorageSortOptions { get; } = [];

        private StorageLegendSortOption? _selectedStorageSortOption;
        public StorageLegendSortOption? SelectedStorageSortOption
        {
            get => _selectedStorageSortOption;
            set
            {
                if (!Equals(_selectedStorageSortOption, value))
                {
                    _selectedStorageSortOption = value;
                    OnPropertyChanged();
                    if (value is not null)
                    {
                        RebuildStorageDonut();
                    }
                }
            }
        }

        // Activity items, populated from real data.
        public ObservableCollection<ActivityItem> ActivityItems { get; } = [];
        public ObservableCollection<RestoreReadinessIssueItem> RestoreReadinessIssues { get; } = [];
        public bool HasRestoreReadinessIssues => RestoreReadinessIssues.Count > 0;

        // Internal data for chart aggregation
        private readonly string[] _days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        private readonly double[] _snapshotCountsByDay = new double[7];
        private readonly int[] _autoCountsByDay = new int[7];
        private readonly int[] _manualCountsByDay = new int[7];
        private readonly int[] _importedCountsByDay = new int[7];
        private int _backupsThisWeekCount;
        private int _activeProjectsCount;
        private int _refreshInFlight;
        private int _refreshQueued;
        private DashboardData? _lastDashboardData;
        private DateTime _lastDashboardDataUtc = DateTime.MinValue;
        private static readonly TimeSpan DashboardDataTtl = TimeSpan.FromSeconds(30);
        private SqliteRepository? _repo;
        private string? _repoDbPath;
        private IReadOnlyList<(Project project, long bytes)> _lastStorageSlices = [];
        private readonly IAppConfigStore _configStore;
        private readonly IRepositoryFactory _repositoryFactory;
        private readonly ScheduleViewModel? _scheduleViewModel;
        private RecoveryCoverageSummary _lastRecoveryCoverageSummary = new();
        private double[]? _snapshotCountsByDayCache;
        private double _lastWeeklyAverage;

        public DashboardViewModel()
            : this(StaticAppConfigStore.Instance, new SqliteRepositoryFactory(StaticAppConfigStore.Instance))
        {
        }

        internal DashboardViewModel(
            IAppConfigStore configStore,
            IRepositoryFactory? repositoryFactory = null,
            ScheduleViewModel? scheduleViewModel = null)
        {
            _configStore = configStore;
            _repositoryFactory = repositoryFactory ?? new SqliteRepositoryFactory(_configStore);
            _scheduleViewModel = scheduleViewModel;
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync(force: true));
            NewSnapshotCommand = new RelayCommand(_ => { /* wired later from dashboard actions */ });
            ToggleRestoreReadinessIssuesCommand = new RelayCommand(_ => ShowRestoreReadinessIssues = !ShowRestoreReadinessIssues, _ => HasRestoreReadinessIssues);
            OpenBackupsCommand = new RelayCommand(
                _ => App.AppViewModelInstance?.NavigateBackups?.Execute(null),
                _ => App.AppViewModelInstance?.NavigateBackups?.CanExecute(null) == true);
            OpenHistoryCommand = new RelayCommand(
                _ => App.AppViewModelInstance?.NavigateHistory?.Execute(null),
                _ => App.AppViewModelInstance?.NavigateHistory?.CanExecute(null) == true);
            OpenRecoveryCommand = new RelayCommand(
                _ => App.AppViewModelInstance?.NavigateRecovery?.Execute(null),
                _ => App.AppViewModelInstance?.NavigateRecovery?.CanExecute(null) == true);
            OpenScheduleCommand = new RelayCommand(
                _ => App.AppViewModelInstance?.NavigateSchedule?.Execute(null),
                _ => App.AppViewModelInstance?.NavigateSchedule?.CanExecute(null) == true);

            BuildStaticAxes();
            RebuildStorageSortOptions();
            BuildDemoSeriesIfNeeded();
        }

        private void BuildStaticAxes()
        {
            var grid = new SKColor(255, 255, 255, 28);
            var text = new SKColor(255, 255, 255, 170);

            SnapshotXAxes = new[]
            {
                new Axis
                {
                    Labels = _days,
                    TextSize = 12,
                    LabelsPaint = new SolidColorPaint(text),
                    SeparatorsPaint = new SolidColorPaint(grid) { StrokeThickness = 1 },
                    TicksPaint = null,
                    Padding = new LiveChartsCore.Drawing.Padding(8, 0, 8, 0)
                }
            };

            SnapshotYAxes = new[]
            {
                new Axis
                {
                    MinLimit = 0,
                    MinStep = 1,
                    TextSize = 12,
                    LabelsPaint = new SolidColorPaint(text),
                    SeparatorsPaint = new SolidColorPaint(grid) { StrokeThickness = 1 },
                    TicksPaint = null,
                    Padding = new LiveChartsCore.Drawing.Padding(0, 8, 0, 8)
                }
            };
        }

        /// <summary>
        /// Called when the view is attached or when the user hits Refresh.
        /// Reads config, opens the shared DB, and populates KPIs + charts.
        /// </summary>
        public async System.Threading.Tasks.Task RefreshAsync(bool force = false)
        {
            if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
            {
                Interlocked.Exchange(ref _refreshQueued, 1);
                return;
            }

            using var refreshTiming = RuntimeTiming.Measure(force ? "Dashboard refresh forced" : "Dashboard refresh");
            try
            {
                DashboardData data = await Task.Run(() =>
                {
                    if (!force && _lastDashboardData is not null &&
                        (DateTime.UtcNow - _lastDashboardDataUtc) < DashboardDataTtl)
                    {
                        RuntimeLog.WriteVerbose("[Timing] Dashboard refresh reused cached data.");
                        return _lastDashboardData;
                    }

                    using var dataLoadTiming = RuntimeTiming.Measure("Dashboard refresh data load");
                    AppConfig cfg = _configStore.GetSnapshot();
                    (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold, string riskReason, BackupDiskUsageStatus status) diskUsage = ComputeBackupDiskUsageDetailed(cfg);

                    string dbPath = _repositoryFactory.ResolveDbPath(cfg);

                    if (_repo is null || !string.Equals(_repoDbPath, dbPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _repo = _repositoryFactory.Create(cfg);
                        _repoDbPath = dbPath;
                    }
                    SqliteRepository repo = _repo;
                    repo.EnsureSchema();
                    int remappedBackups = repo.RepairBackupProjectLinksFromSnapshots();
                    if (remappedBackups > 0)
                    {
                        RuntimeLog.WriteVerbose($"[Dashboard] Repaired {remappedBackups} backup-project links from snapshots.");
                    }

                    var projects = repo.GetAllProjects().ToList();
                    int backupCount = repo.GetBackupCount();

                    DateTime localStartDate = DateTime.Now.Date.AddDays(-6);
                    DateTime localEndDate = DateTime.Now.Date.AddDays(1).AddTicks(-1);
                    IReadOnlyDictionary<DateTime, (int AutoCount, int ManualCount, int ImportedCount)> backupCountsByDay = repo.GetBackupCountsByDayBreakdown(
                        localStartDate.ToUniversalTime(),
                        localEndDate.ToUniversalTime());

                    // Storage slices: total backups per project (incl. imported)
                    long totalLatestBytes = 0;
                    long totalLocalBytes = 0;
                    var storageSlices = new List<(Project project, long bytes)>();
                    IReadOnlyDictionary<int, long> backupsByProject = repo.GetBackupTotalsByProject(includeImported: true);
                    IReadOnlyDictionary<int, long> localBackupsByProject = repo.GetBackupTotalsByProject(includeImported: false);

                    foreach (Project? p in projects)
                    {
                        if (!backupsByProject.TryGetValue(p.Id, out long projectTotal))
                            continue;

                        totalLatestBytes += projectTotal;
                        storageSlices.Add((p, projectTotal));

                        if (localBackupsByProject.TryGetValue(p.Id, out long localTotal))
                        {
                            totalLocalBytes += localTotal;
                        }
                    }

                    // Fallback: if backup totals cannot be mapped to current project ids
                    // (e.g. imported/orphaned backup rows after destination/config churn),
                    // still show per-project storage using latest snapshot sizes.
                    if (storageSlices.Count == 0 && projects.Count > 0)
                    {
                        IReadOnlyDictionary<int, (DateTime CreatedUtc, long TotalBytes)> latestSnapshotsByProject = repo.GetLatestSnapshotInfoByProject();
                        foreach (Project? p in projects)
                        {
                            if (!latestSnapshotsByProject.TryGetValue(p.Id, out (DateTime CreatedUtc, long TotalBytes) info))
                                continue;

                            if (info.TotalBytes <= 0)
                                continue;

                            storageSlices.Add((p, info.TotalBytes));
                            totalLatestBytes += info.TotalBytes;
                            totalLocalBytes += info.TotalBytes;
                        }

                        if (storageSlices.Count > 0)
                        {
                            RuntimeLog.WriteVerbose("[Dashboard] Backup totals were unmapped; using latest snapshot sizes as storage fallback.");
                        }
                    }

                    string[] dayLabels = new string[_days.Length];
                    for (int i = 0; i < dayLabels.Length; i++)
                    {
                        DateTime d = localStartDate.AddDays(i);
                        dayLabels[i] = d.ToString("ddd");
                    }

                    double[] counts = new double[_snapshotCountsByDay.Length];
                    int[] autoCounts = new int[_snapshotCountsByDay.Length];
                    int[] manualCounts = new int[_snapshotCountsByDay.Length];
                    int[] importedCounts = new int[_snapshotCountsByDay.Length];
                    for (int i = 0; i < counts.Length; i++)
                    {
                        DateTime localDay = localStartDate.AddDays(i).Date;
                        DateTime utcBucket = localDay.ToUniversalTime().Date;
                        if (backupCountsByDay.TryGetValue(utcBucket, out (int AutoCount, int ManualCount, int ImportedCount) breakdown))
                        {
                            autoCounts[i] = breakdown.AutoCount;
                            manualCounts[i] = breakdown.ManualCount;
                            importedCounts[i] = breakdown.ImportedCount;
                            counts[i] = breakdown.AutoCount + breakdown.ManualCount + breakdown.ImportedCount;
                        }
                    }

                    // Activity list (newest first)
                    var activities = new List<DashboardActivity>();
                    List<(int projectId, DateTime createdUtc, string type)> recentBackups = repo.GetRecentBackups(12);
                    foreach ((int projectId, DateTime createdUtc, string type) b in recentBackups)
                    {
                        string subtitle = string.Equals(b.type, "auto", StringComparison.OrdinalIgnoreCase)
                            ? "auto"
                            : "manual";
                        activities.Add(new DashboardActivity(b.projectId, b.createdUtc, subtitle));
                    }

                    List<(int projectId, DateTime createdUtc)> recentSnapshots = repo.GetRecentSnapshotsWithoutBackup(12);
                    foreach ((int projectId, DateTime createdUtc) s in recentSnapshots)
                    {
                        activities.Add(new DashboardActivity(s.projectId, s.createdUtc, "snapshot"));
                    }

                    IReadOnlyList<RestoreHistoryEvent> recentRestores = repo.GetRecentRestoreHistoryEvents(12);
                    foreach (RestoreHistoryEvent restore in recentRestores)
                    {
                        string subtitle = string.Equals(restore.Status, RestoreHistoryEventStatus.Failed, StringComparison.OrdinalIgnoreCase)
                            ? "restore-failed"
                            : "restore";
                        activities.Add(new DashboardActivity(restore.ProjectId, restore.CreatedUtc, subtitle));
                    }

                    List<Backup> restoreReadinessBackups = repo.GetAllBackups().ToList();
                    IReadOnlyDictionary<int, SnapshotHistoryMetadata> restoreReadinessMetadata =
                        repo.GetSnapshotHistoryMetadataBySnapshotIds(restoreReadinessBackups.Select(backup => backup.SnapshotId));
                    Backup? latestKnownGoodBackup = restoreReadinessBackups
                        .Where(backup => restoreReadinessMetadata.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata) && metadata.IsKnownGood)
                        .OrderByDescending(backup => backup.CreatedUtc)
                        .ThenByDescending(backup => backup.Id)
                        .FirstOrDefault();
                    string? latestKnownGoodProjectName = latestKnownGoodBackup is null
                        ? null
                        : projects.FirstOrDefault(project => project.Id == latestKnownGoodBackup.ProjectId)?.Name;

                    var dashboardData = new DashboardData
                    {
                        Config = cfg,
                        DiskUsage = diskUsage,
                        Projects = projects,
                        Activities = activities,
                        StorageSlices = storageSlices,
                        TotalLatestBytes = totalLatestBytes,
                        TotalLocalBytes = totalLocalBytes,
                        BackupCount = backupCount,
                        BackupsThisWeekCount = (int)counts.Sum(),
                        DayLabels = dayLabels,
                        SnapshotCounts = counts,
                        AutoCounts = autoCounts,
                        ManualCounts = manualCounts,
                        ImportedCounts = importedCounts,
                        RestoreReadiness = new RestoreReadinessService().BuildSummary(
                            projects,
                            restoreReadinessBackups,
                            cfg,
                            cfg.Advanced.BackupIndexLastScan,
                            snapshotMetadataById: restoreReadinessMetadata),
                        RecoveryCoverage = new RecoveryCoverageService().BuildSummary(
                            projects,
                            restoreReadinessBackups),
                        LatestKnownGoodBackup = latestKnownGoodBackup,
                        LatestKnownGoodProjectName = latestKnownGoodProjectName
                    };
                    _lastDashboardData = dashboardData;
                    _lastDashboardDataUtc = DateTime.UtcNow;
                    return dashboardData;
                });

                RuntimeTimingScope uiQueueTiming = RuntimeTiming.Measure("Dashboard refresh dispatcher queue wait");
                bool uiQueueTimingDisposed = false;
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        uiQueueTiming.Dispose();
                        uiQueueTimingDisposed = true;
                        using var uiApplyTiming = RuntimeTiming.Measure("Dashboard refresh UI apply");
                        BackupDiskUsedPercent      = data.DiskUsage.UsedPercent;
                        BackupDiskFreeText         = data.DiskUsage.FreeText;
                        BackupDiskThresholdText    = data.DiskUsage.ThresholdText;
                        BackupDiskIsBelowThreshold = data.DiskUsage.IsBelowThreshold;
                        BackupDiskRiskReason       = data.DiskUsage.RiskReason;

                        ProjectCount = data.Projects.Count;
                        SnapshotCount = data.BackupCount;
                        _backupsThisWeekCount = data.BackupsThisWeekCount;
                        SnapshotsHint = string.Format(L("Dashboard.Hint.SnapshotsThisWeek", "{0} this week"), _backupsThisWeekCount);

                        _activeProjectsCount = data.StorageSlices.Count;
                        StorageUsed = FormatBytes(data.TotalLatestBytes);
                        StorageUsedLocal = Lf("Dashboard.Kpi.StorageLocal", "Local: {0}", FormatBytes(data.TotalLocalBytes));

                        if (data.Projects.Count == 0)
                        {
                            ProjectsHint = L("Dashboard.Hint.NoProjects", "No projects yet");
                        }
                        else if (_activeProjectsCount == 0)
                        {
                            ProjectsHint = L("Dashboard.Hint.NoSnapshots", "No snapshots yet");
                        }
                        else
                        {
                            ProjectsHint = _activeProjectsCount == 1
                                ? L("Dashboard.Hint.ActiveProjects.One", "1 active project")
                                : string.Format(L("Dashboard.Hint.ActiveProjects.Many", "{0} active projects"), _activeProjectsCount);
                        }

                        StorageHint = _activeProjectsCount == 0
                            ? L("Dashboard.Hint.StorageEmpty", "No storage used")
                            : L("Dashboard.Hint.StorageTotal", "Total across all backups");
                        ApplyRestoreReadinessSummary(data.RestoreReadiness);
                        ApplyRecoveryCoverageSummary(data.RecoveryCoverage);
                        ApplyPriorityOverview(data);

                        List<ActivityItem> activityItems = BuildRecentActivityItems(data);

                        ActivityItems.Clear();
                        foreach (ActivityItem item in activityItems)
                        {
                            ActivityItems.Add(item);
                        }

                        for (int i = 0; i < _days.Length && i < data.DayLabels.Length; i++)
                        {
                            _days[i] = data.DayLabels[i];
                        }
                        Array.Clear(_snapshotCountsByDay, 0, _snapshotCountsByDay.Length);
                        for (int i = 0; i < _snapshotCountsByDay.Length && i < data.SnapshotCounts.Length; i++)
                        {
                            _snapshotCountsByDay[i] = data.SnapshotCounts[i];
                        }
                        Array.Clear(_autoCountsByDay, 0, _autoCountsByDay.Length);
                        Array.Clear(_manualCountsByDay, 0, _manualCountsByDay.Length);
                        Array.Clear(_importedCountsByDay, 0, _importedCountsByDay.Length);
                        for (int i = 0; i < _autoCountsByDay.Length && i < data.AutoCounts.Length; i++)
                        {
                            _autoCountsByDay[i] = data.AutoCounts[i];
                        }
                        for (int i = 0; i < _manualCountsByDay.Length && i < data.ManualCounts.Length; i++)
                        {
                            _manualCountsByDay[i] = data.ManualCounts[i];
                        }
                        for (int i = 0; i < _importedCountsByDay.Length && i < data.ImportedCounts.Length; i++)
                        {
                            _importedCountsByDay[i] = data.ImportedCounts[i];
                        }

                        UpdateBackupSummaryPills();

                        BuildSnapshotSeries();
                        BuildWeeklyActivity();
                        BuildStorageDonut(data.StorageSlices);
                        BuildBackupUsageBar(data.Config, data.StorageSlices);
                        if (data.StorageSlices.Count > 0 &&
                            (BackupUsageSegments.Count == 0 ||
                             (BackupUsageSegments.Count == 1 &&
                              BackupUsageSegments[0].Name.StartsWith(
                                  L("Dashboard.Storage.Other", "Other"),
                                  StringComparison.OrdinalIgnoreCase))))
                        {
                            BuildBackupUsageBarFromVaultSync(
                                data.StorageSlices,
                                data.StorageSlices.Sum(x => Math.Max(0L, x.bytes)));
                        }

                        OnPropertyChanged(nameof(TotalSnapshotsWeek));
                        OnPropertyChanged(nameof(TotalSnapshotsWeekLabel));
                    });
                }
                finally
                {
                    if (!uiQueueTimingDisposed)
                    {
                        uiQueueTiming.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Refresh failed: {ex.Message}");
                BuildDemoSeriesIfNeeded();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
                if (Interlocked.Exchange(ref _refreshQueued, 0) == 1)
                {
                    await RefreshAsync(force: true);
                }
            }
        }

        private sealed class DashboardData
        {
            public AppConfig Config { get; init; } = new();
            public (double UsedPercent, string FreeText, string ThresholdText, bool IsBelowThreshold, string RiskReason, BackupDiskUsageStatus Status) DiskUsage;
            public List<Project> Projects { get; init; } = [];
            public List<DashboardActivity> Activities { get; init; } = [];
            public List<(Project project, long bytes)> StorageSlices { get; init; } = [];
            public long TotalLatestBytes { get; init; }
            public long TotalLocalBytes { get; init; }
            public int BackupCount { get; init; }
            public int BackupsThisWeekCount { get; init; }
            public string[] DayLabels { get; init; } = [];
            public double[] SnapshotCounts { get; init; } = [];
            public int[] AutoCounts { get; init; } = [];
            public int[] ManualCounts { get; init; } = [];
            public int[] ImportedCounts { get; init; } = [];
            public RestoreReadinessSummary RestoreReadiness { get; init; } = new();
            public RecoveryCoverageSummary RecoveryCoverage { get; init; } = new();
            public Backup? LatestKnownGoodBackup { get; init; }
            public string? LatestKnownGoodProjectName { get; init; }
        }

        private static List<ActivityItem> BuildRecentActivityItems(DashboardData data)
        {
            using var timing = RuntimeTiming.Measure("Dashboard recent activity rebuild");
            Color[] projectPalette =
            [
                Color.Parse("#4C8DFF"),
                Color.Parse("#22CC88"),
                Color.Parse("#FFB84C"),
                Color.Parse("#FF6B6B"),
                Color.Parse("#9B6BFF")
            ];

            var projectsById = data.Projects.ToDictionary(project => project.Id);
            var projectDotBrushes = new Dictionary<int, IBrush>();
            var activityItems = new List<ActivityItem>();
            int paletteIndex = 0;

            IBrush GetBrush(Color color) => new ImmutableSolidColorBrush(color);

            foreach (DashboardActivity activity in data.Activities
                         .OrderByDescending(item => item.WhenUtc)
                         .Take(5))
            {
                Project? project = activity.ProjectId.HasValue &&
                    projectsById.TryGetValue(activity.ProjectId.Value, out Project? matchedProject)
                        ? matchedProject
                        : null;

                string title = project != null
                    ? project.Name
                    : L("Dashboard.Activity.UnknownProject", "Unknown project");
                string subtitle = activity.Subtitle switch
                {
                    "auto" => L("Dashboard.Activity.AutoBackup", "Auto backup created"),
                    "manual" => L("Dashboard.Activity.ManualBackup", "Manual backup created"),
                    "restore" => L("Dashboard.Activity.RestoreCompleted", "Restore completed"),
                    "restore-failed" => L("Dashboard.Activity.RestoreFailed", "Restore needs review"),
                    _ => L("Dashboard.Activity.SnapshotCreated", "Snapshot created")
                };
                (string tagsDisplay, ProjectTagChip[] tagChips) = BuildActivityProjectTags(project);
                string when = activity.WhenUtc.ToLocalTime().ToString("g");

                IBrush dotBrush;
                if (project != null)
                {
                    if (!projectDotBrushes.TryGetValue(project.Id, out dotBrush!))
                    {
                        Color color = projectPalette[paletteIndex % projectPalette.Length];
                        dotBrush = GetBrush(color);
                        projectDotBrushes[project.Id] = dotBrush;
                        paletteIndex++;
                    }
                }
                else
                {
                    dotBrush = GetBrush(Colors.Gray);
                }

                activityItems.Add(new ActivityItem(title, subtitle, when, dotBrush, tagsDisplay, tagChips));
            }

            return activityItems;
        }

        private static (string TagsDisplay, ProjectTagChip[] TagChips) BuildActivityProjectTags(Project? project)
        {
            if (project is null)
                return (string.Empty, []);

            string[] tags = [.. (project.Tags ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(tag => tag.Trim())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)];
            string tagsDisplay = tags.Length > 0
                ? string.Join(" - ", tags)
                : string.Empty;
            ProjectTagChip[] tagChips = [.. ProjectTagAppearance.CreateChips(project.Tags, max: 3)];

            return (tagsDisplay, tagChips);
        }

        private void BuildSnapshotSeries()
        {
            using var timing = RuntimeTiming.Measure("Dashboard snapshot series rebuild");
            // Simple, readable chart: one bar per day showing how many backups ran.
            var accent = SKColor.Parse("#22CCFF");

            var dailyBackups = new ColumnSeries<double>
            {
                Values = _snapshotCountsByDay,
                Stroke = null,
                Fill = new SolidColorPaint(accent),
                MaxBarWidth = 26,
                DataLabelsPaint = null
            };

            SnapshotSeries = new ISeries[] { dailyBackups };
            OnPropertyChanged(nameof(SnapshotSeries));
        }

        private void BuildWeeklyActivity()
        {
            using var timing = RuntimeTiming.Measure("Dashboard weekly activity rebuild");
            WeeklySnapshotActivity.Clear();

            double max = _snapshotCountsByDay.DefaultIfEmpty(0d).Max();
            if (max < 1)
            {
                max = 1;
            }

            double chartHeight = max <= 2 ? 150d : (max <= 4 ? 170d : 188d);
            const double barBase = 14;
            double barRange = chartHeight - 30;
            WeeklyChartHeight = chartHeight;

            double avg = _snapshotCountsByDay.Length == 0 ? 0d : _snapshotCountsByDay.Average();
            _lastWeeklyAverage = avg;
            _snapshotCountsByDayCache = _snapshotCountsByDay.ToArray();
            double avgNormalized = avg / max;
            double avgHeight = avg <= 0 ? 0 : barBase + avgNormalized * barRange;
            const double labelOffset = 10;
            WeeklyAverageLineOffset = labelOffset + avgHeight;
            WeeklyAverageLabel = Lf("Dashboard.Chart.AvgLabel", "Avg {0:0.0}", avg);

            for (int i = 0; i < _snapshotCountsByDay.Length && i < _days.Length; i++)
            {
                int autoCount = _autoCountsByDay[i];
                int manualCount = _manualCountsByDay[i];
                int importedCount = _importedCountsByDay[i];
                int count = autoCount + manualCount + importedCount;
                double normalized = count / max;
                double totalHeight = count == 0 ? 0 : barBase + normalized * barRange;
                string dayLabel = _days[i];

                string tooltip = count == 0
                    ? Lf("Dashboard.Chart.TooltipNone", "{0}: No backups", dayLabel)
                    : Lf("Dashboard.Chart.TooltipBreakdown", "{0}: {1} auto, {2} manual, {3} imported", dayLabel, autoCount, manualCount, importedCount);

                double autoHeight = 0d;
                double manualHeight = 0d;
                double importedHeight = 0d;
                if (count > 0)
                {
                    autoHeight = autoCount == 0 ? 0 : Math.Max(6, totalHeight * autoCount / count);
                    manualHeight = manualCount == 0 ? 0 : Math.Max(6, totalHeight * manualCount / count);
                    importedHeight = importedCount == 0 ? 0 : Math.Max(6, totalHeight * importedCount / count);

                    double combined = autoHeight + manualHeight + importedHeight;
                    if (combined > totalHeight && combined > 0)
                    {
                        double scale = totalHeight / combined;
                        autoHeight *= scale;
                        manualHeight *= scale;
                        importedHeight *= scale;
                    }
                }

                WeeklySnapshotActivity.Add(new SnapshotActivityPoint
                {
                    DayLabel     = dayLabel,
                    ShowLabel    = true,
                    AutoCount    = autoCount,
                    ManualCount  = manualCount,
                    ImportedCount = importedCount,
                    TotalBytes   = 0,
                    AutoHeight   = autoHeight,
                    ManualHeight = manualHeight,
                    ImportedHeight = importedHeight,
                    TooltipText  = tooltip
                });
            }

            OnPropertyChanged(nameof(WeeklyAverageLineOffset));
            OnPropertyChanged(nameof(WeeklyAverageLabel));
            OnPropertyChanged(nameof(WeeklyChartHeight));
        }

        private void BuildStorageDonut(IReadOnlyList<(Project project, long bytes)> perProject)
        {
            using var timing = RuntimeTiming.Measure("Dashboard storage donut rebuild");
            _lastStorageSlices = perProject ?? [];

            // If we have no per-project data, show an empty donut.
            if (_lastStorageSlices.Count == 0)
            {
                StorageSeries = [];
                StorageLegend = [];
                OnPropertyChanged(nameof(StorageSeries));
                OnPropertyChanged(nameof(HasStorageSeries));
                OnPropertyChanged(nameof(StorageLegend));
                return;
            }

            long total = _lastStorageSlices.Sum(p => p.bytes);
            if (total <= 0)
            {
                StorageSeries = [];
                StorageLegend = [];
                OnPropertyChanged(nameof(StorageSeries));
                OnPropertyChanged(nameof(HasStorageSeries));
                OnPropertyChanged(nameof(StorageLegend));
                return;
            }
            List<(Project project, long bytes)> orderedSlices = (_selectedStorageSortOption?.Mode ?? StorageLegendSortMode.LargestFirst) switch
            {
                StorageLegendSortMode.Alphabetical => [.. _lastStorageSlices
                    .OrderBy(x => x.project.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenByDescending(x => x.bytes)],
                _ => [.. _lastStorageSlices
                    .OrderByDescending(x => x.bytes)
                    .ThenBy(x => x.project.Name, StringComparer.CurrentCultureIgnoreCase)]
            };

            var series = new List<ISeries>();
            var legend = new List<LegendItem>();

            for (int i = 0; i < orderedSlices.Count; i++)
            {
                (Project project, long bytes) = orderedSlices[i];
                if (bytes <= 0) continue;

                string colorHex = AvatarColorProvider.GetColor(project.Name, project.RootPath, project.ExternalId);
                SKColor color = SKColors.DodgerBlue;
                if (!SKColor.TryParse(colorHex, out color))
                {
                    color = SKColors.DodgerBlue;
                }
                string projectName = project.Name;
                string displayProjectName = TrimForTooltip(projectName, 28);
                long sliceBytes = bytes;

                series.Add(new PieSeries<double>
                {
                    Values      = new[] { (double)bytes },
                    Name        = projectName,
                    InnerRadius = 90,
                    Stroke      = null,
                    Fill        = new SolidColorPaint(color)
                });

                legend.Add(new LegendItem(
                    $"{displayProjectName} {FormatBytes(bytes)}",
                    $"{projectName} {FormatBytes(bytes)}",
                    new ImmutableSolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue))));
            }

            if (series.Count == 0)
            {
                StorageSeries = [];
                StorageLegend = [];
                OnPropertyChanged(nameof(StorageSeries));
                OnPropertyChanged(nameof(HasStorageSeries));
                OnPropertyChanged(nameof(StorageLegend));
                return;
            }

            StorageSeries = [.. series];
            StorageLegend = legend;

            OnPropertyChanged(nameof(StorageSeries));
            OnPropertyChanged(nameof(HasStorageSeries));
            OnPropertyChanged(nameof(StorageLegend));
        }

        private void RebuildStorageDonut()
        {
            BuildStorageDonut(_lastStorageSlices);
        }

        private void BuildDemoSeriesIfNeeded()
        {
            using var timing = RuntimeTiming.Measure("Dashboard empty series rebuild");
            if (SnapshotSeries is { Length: > 0 } && StorageSeries is { Length: > 0 })
                return;

            // No demo data when the DB is empty or unavailable.
            Array.Clear(_snapshotCountsByDay, 0, _snapshotCountsByDay.Length);
            Array.Clear(_autoCountsByDay, 0, _autoCountsByDay.Length);
            Array.Clear(_manualCountsByDay, 0, _manualCountsByDay.Length);
            Array.Clear(_importedCountsByDay, 0, _importedCountsByDay.Length);
            WeeklySnapshotActivity.Clear();
            SnapshotSeries = [];
            OnPropertyChanged(nameof(SnapshotSeries));

            ProjectCount   = 0;
            ProjectsHint   = L("Dashboard.Hint.NoProjects", "No projects yet");
            SnapshotCount  = 0;
            SnapshotsHint  = L("Dashboard.Hint.NoSnapshots", "No snapshots yet");
            StorageUsed    = "0 B";
            StorageUsedLocal = Lf("Dashboard.Kpi.StorageLocal", "Local: {0}", "0 B");
            StorageHint    = L("Dashboard.Hint.StorageEmpty", "No storage used");
            ApplyRestoreReadinessSummary(new RestoreReadinessSummary());
            ApplyRecoveryCoverageSummary(new RecoveryCoverageSummary());

            BuildStorageDonut([]);
            AppConfig cfg = _configStore.GetSnapshot();
            BuildBackupUsageBar(cfg, []);
            OnPropertyChanged(nameof(TotalSnapshotsWeek));
            OnPropertyChanged(nameof(TotalSnapshotsWeekLabel));
        }

        private void BuildBackupUsageBar(AppConfig config, IReadOnlyList<(Project project, long bytes)> perProject)
        {
            using var timing = RuntimeTiming.Measure("Dashboard backup usage bar rebuild");
            try
            {
                // Default to empty segments if backup root is not configured.
                string? backupRoot = config.Backups.BackupLocation;
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            BackupUsageSegments = [];
            BackupTopConsumers = [];
            BackupUsageSeries   = [];
            BackupUsageXAxes    = [];
            BackupUsageYAxes    = [];

            OnPropertyChanged(nameof(BackupUsageSegments));
            OnPropertyChanged(nameof(BackupTopConsumers));
            OnPropertyChanged(nameof(HasBackupTopConsumers));
            OnPropertyChanged(nameof(BackupUsageSeries));
            OnPropertyChanged(nameof(BackupUsageXAxes));
            OnPropertyChanged(nameof(BackupUsageYAxes));
            return;
        }

                long vaultSyncBytes = perProject.Sum(p => p.bytes);

        if (OperatingSystem.IsMacOS() && IsNetworkPath(backupRoot))
        {
            if (!TryResolveMountedSharePath(backupRoot, out string? mountedRoot))
            {
                BuildBackupUsageBarFromVaultSync(perProject, vaultSyncBytes);
                return;
            }

            backupRoot = mountedRoot;
        }
        else
        {
            backupRoot = Path.GetFullPath(backupRoot);
        }

        if (!TryGetDiskSpace(backupRoot, out long totalBytes, out long freeBytes) || totalBytes <= 0)
        {
            BuildBackupUsageBarFromVaultSync(perProject, vaultSyncBytes);
            return;
        }

                long usedBytes  = Math.Max(0L, totalBytes - freeBytes);

        if (totalBytes <= 0)
        {
            BackupUsageSegments = [];
            BackupUsageSeries   = [];
            BackupUsageXAxes    = [];
            BackupUsageYAxes    = [];

            OnPropertyChanged(nameof(BackupUsageSegments));
            OnPropertyChanged(nameof(BackupUsageSeries));
            OnPropertyChanged(nameof(BackupUsageXAxes));
            OnPropertyChanged(nameof(BackupUsageYAxes));
            return;
        }

        // Sum of the latest snapshot sizes per project (VaultSync usage approximation).
        if (vaultSyncBytes < 0) vaultSyncBytes = 0;

                // Percentages of the total backup disk.
                double usedPercentTotal = usedBytes        * 100d / totalBytes;
                double vaultSyncPercent = vaultSyncBytes   * 100d / totalBytes;
                double otherPercent     = Math.Max(0d, usedPercentTotal - vaultSyncPercent);

        var segments = new List<BackupUsageSegment>();
        const int maxProjectSegments = 5;

                // 1) Other segment (non-VaultSync usage on the backup drive).
                // This is both in the legend and in the overlay bar.
                long otherBytes = Math.Max(0L, usedBytes - vaultSyncBytes);
        if (otherPercent > 0)
        {
            segments.Add(new BackupUsageSegment(
                L("Dashboard.Storage.Other", "Other"),
                FormatBytes(otherBytes),
                otherPercent,
                new ImmutableSolidColorBrush(Color.Parse("#8E8E93")),
                Lf("Dashboard.Storage.SegmentTooltip", "{0}: {1}", L("Dashboard.Storage.Other", "Other"), FormatBytes(otherBytes))));
        }

                // 2) One segment per project for its latest snapshot size, as percent of total disk.
                int addedProjectSegments = 0;
            var orderedProjects = perProject
                .Where(p => p.bytes > 0)
                .OrderByDescending(p => p.bytes)
                .ToList();

            var visibleProjects = orderedProjects.Take(maxProjectSegments).ToList();
            var remainingProjects = orderedProjects.Skip(maxProjectSegments).ToList();

            foreach ((Project project, long bytes) in visibleProjects)
            {
                        double projectPercent = bytes * 100d / totalBytes;
                // Keep legend/segment presence stable even when disk is huge and
                // floating-point math yields near-zero percentages.
                if (projectPercent <= 0)
                {
                    projectPercent = 0.0001d;
                }

                        string colorHex = AvatarColorProvider.GetColor(project.Name, project.RootPath, project.ExternalId);
                var color = Color.Parse(colorHex);
                        string displayName = TrimForTooltip(project.Name, 26);

                segments.Add(new BackupUsageSegment(
                    displayName,
                    FormatBytes(bytes),
                    projectPercent,
                    new ImmutableSolidColorBrush(color),
                    Lf("Dashboard.Storage.SegmentTooltip", "{0}: {1}", project.Name, FormatBytes(bytes))));
                addedProjectSegments++;
            }

            if (remainingProjects.Count > 0)
            {
                        long remainingBytes = remainingProjects.Sum(x => x.bytes);
                        double remainingPercent = remainingBytes * 100d / totalBytes;
                if (remainingPercent <= 0)
                    remainingPercent = 0.0001d;

                segments.Add(new BackupUsageSegment(
                    Lf("Dashboard.Storage.MoreProjects", "+ {0} more", remainingProjects.Count),
                    FormatBytes(remainingBytes),
                    remainingPercent,
                    new ImmutableSolidColorBrush(Color.Parse("#5B6480")),
                    Lf("Dashboard.Storage.MoreProjectsTooltip", "{0} additional projects: {1}", remainingProjects.Count, FormatBytes(remainingBytes))));
                addedProjectSegments += remainingProjects.Count;
            }

        // Guard: if disk-based projection collapses to only "Other" while we do have
        // project bytes, switch to VaultSync-relative fallback so the breakdown is visible.
        if (addedProjectSegments == 0 && perProject.Any(p => p.bytes > 0))
        {
            BuildBackupUsageBarFromVaultSync(perProject, vaultSyncBytes);
            return;
        }

        BackupUsageSegments = segments;
        BackupTopConsumers = BuildTopConsumerList(segments);
        OnPropertyChanged(nameof(HasBackupUsageSegments));
        OnPropertyChanged(nameof(HasBackupTopConsumers));

        // Build stacked RowSeries for the colored bar (Other + VaultSync projects).
        if (segments.Count == 0)
        {
            BackupUsageSeries = [];
            BackupUsageXAxes  = new[]
            {
                new Axis
                {
                    IsVisible = false,
                    MinLimit  = 0,
                    MaxLimit  = 100
                }
            };
            BackupUsageYAxes  = new[]
            {
                new Axis
                {
                    IsVisible = false
                }
            };

            NotifyBackupUsageChanged();
            return;
        }

                double totalShown = segments.Sum(s => s.SizeBytes);
        if (totalShown <= 0)
        {
            BackupUsageSeries = [];
            BackupUsageXAxes  = new[]
            {
                new Axis
                {
                    IsVisible = false,
                    MinLimit  = 0,
                    MaxLimit  = 100
                }
            };
            BackupUsageYAxes  = new[]
            {
                new Axis
                {
                    IsVisible = false
                }
            };

            NotifyBackupUsageChanged();
            return;
        }

        var series = new List<ISeries>();
        foreach (BackupUsageSegment seg in segments)
        {
            if (seg.SizeBytes <= 0)
                continue;

            if (seg.Brush is not ISolidColorBrush solid)
                continue;

            var skColor = new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, solid.Color.A);

            series.Add(new StackedRowSeries<double>
            {
                Values        = new[] { seg.SizeBytes },
                Stroke        = null,
                Fill          = new SolidColorPaint(skColor),
                MaxBarWidth   = 20,
                IsHoverable   = false,
                DataLabelsPaint = null,
                StackGroup    = 0
            });
        }

        BackupUsageSeries = [.. series];

        BackupUsageXAxes = new[]
        {
            new Axis
            {
                IsVisible = false,
                MinLimit  = 0,
                MaxLimit  = 100
            }
        };

        BackupUsageYAxes = new[]
        {
            new Axis
            {
                IsVisible = false
            }
        };

        NotifyBackupUsageChanged();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Dashboard] Backup usage bar failed: {ex.Message}");

        BackupUsageSegments = [];
        BackupTopConsumers = [];
        BackupUsageSeries   = [];
        BackupUsageXAxes    = [];
        BackupUsageYAxes    = [];

        NotifyBackupUsageChanged();
    }
}

        private void BuildBackupUsageBarFromVaultSync(IReadOnlyList<(Project project, long bytes)> perProject, long vaultSyncBytes)
        {
            using var timing = RuntimeTiming.Measure("Dashboard backup usage fallback rebuild");
            if (vaultSyncBytes <= 0 || perProject == null || perProject.Count == 0)
            {
            BackupUsageSegments = [];
            BackupTopConsumers = [];
            BackupUsageSeries   = [];
            BackupUsageXAxes    = [];
            BackupUsageYAxes    = [];

            NotifyBackupUsageChanged();
            return;
            }

            var segments = new List<BackupUsageSegment>();
            const int maxProjectSegments = 5;
            Color[] projectPalette = new[]
            {
                Color.Parse("#4C8DFF"),
                Color.Parse("#FFB84C"),
                Color.Parse("#22CC88"),
                Color.Parse("#FF6B6B"),
                Color.Parse("#9B6BFF")
            };

            int index = 0;
            var orderedProjects = perProject
                .Where(p => p.bytes > 0)
                .OrderByDescending(p => p.bytes)
                .ToList();

            foreach ((Project project, long bytes) in orderedProjects.Take(maxProjectSegments))
            {
                double percent = bytes * 100d / vaultSyncBytes;
                if (percent <= 0) continue;

                Color color = projectPalette[index % projectPalette.Length];
                index++;

                segments.Add(new BackupUsageSegment(
                    TrimForTooltip(project.Name, 26),
                    FormatBytes(bytes),
                    percent,
                    new ImmutableSolidColorBrush(color),
                    Lf("Dashboard.Storage.SegmentTooltip", "{0}: {1}", project.Name, FormatBytes(bytes))));
            }

            var remainingProjects = orderedProjects.Skip(maxProjectSegments).ToList();
            if (remainingProjects.Count > 0)
            {
                long remainingBytes = remainingProjects.Sum(x => x.bytes);
                double remainingPercent = remainingBytes * 100d / vaultSyncBytes;
                if (remainingPercent > 0)
                {
                    segments.Add(new BackupUsageSegment(
                        Lf("Dashboard.Storage.MoreProjects", "+ {0} more", remainingProjects.Count),
                        FormatBytes(remainingBytes),
                        remainingPercent,
                        new ImmutableSolidColorBrush(Color.Parse("#5B6480")),
                        Lf("Dashboard.Storage.MoreProjectsTooltip", "{0} additional projects: {1}", remainingProjects.Count, FormatBytes(remainingBytes))));
                }
            }

            BackupUsageSegments = segments;
            BackupTopConsumers = BuildTopConsumerList(segments);
            OnPropertyChanged(nameof(HasBackupUsageSegments));
            OnPropertyChanged(nameof(HasBackupTopConsumers));

            if (segments.Count == 0)
            {
                BackupUsageSeries = [];
                BackupUsageXAxes  = new[]
                {
                    new Axis
                    {
                        IsVisible = false,
                        MinLimit  = 0,
                        MaxLimit  = 100
                    }
                };
                BackupUsageYAxes  = new[]
                {
                    new Axis
                    {
                        IsVisible = false
                    }
                };

                NotifyBackupUsageChanged();
                return;
            }

            var series = new List<ISeries>();
            foreach (BackupUsageSegment seg in segments)
            {
                if (seg.SizeBytes <= 0)
                    continue;

                if (seg.Brush is not ISolidColorBrush solid)
                    continue;

                var skColor = new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, solid.Color.A);

                series.Add(new StackedRowSeries<double>
                {
                    Values        = new[] { seg.SizeBytes },
                    Stroke        = null,
                    Fill          = new SolidColorPaint(skColor),
                    MaxBarWidth   = 20,
                    IsHoverable   = false,
                    DataLabelsPaint = null,
                    StackGroup    = 0
                });
            }

            BackupUsageSeries = [.. series];

            BackupUsageXAxes = new[]
            {
                new Axis
                {
                    IsVisible = false,
                    MinLimit  = 0,
                    MaxLimit  = 100
                }
            };

            BackupUsageYAxes = new[]
            {
                new Axis
                {
                    IsVisible = false
                }
            };

            NotifyBackupUsageChanged();
        }

        private void NotifyBackupUsageChanged() =>
            OnPropertiesChanged(
                nameof(BackupUsageSegments),
                nameof(BackupTopConsumers),
                nameof(HasBackupTopConsumers),
                nameof(HasBackupUsageSegments),
                nameof(BackupUsageSeries),
                nameof(BackupUsageXAxes),
                nameof(BackupUsageYAxes));

        private static IReadOnlyList<BackupUsageSegment> BuildTopConsumerList(IReadOnlyList<BackupUsageSegment> segments)
        {
            if (segments.Count == 0)
                return [];

            var projectSegments = segments
                .Where(s => !string.Equals(s.Name, L("Dashboard.Storage.Other", "Other"), StringComparison.Ordinal))
                .ToList();

            if (projectSegments.Count == 0)
                return [];

            const int maxVisibleRows = 5;
            if (projectSegments.Count <= maxVisibleRows)
                return projectSegments;

            bool hasAggregateTail = projectSegments[^1].Name.StartsWith("+ ", StringComparison.Ordinal);
            if (!hasAggregateTail)
                return projectSegments.Take(maxVisibleRows).ToList();

            var visible = projectSegments.Take(maxVisibleRows - 1).ToList();
            visible.Add(projectSegments[^1]);
            return visible;
        }


        /// <summary>
        /// Computes backup disk usage based on the current app config.
        /// Returns a tuple that can be reused by other view models (e.g. BackupsViewModel).
        /// </summary>
        public enum BackupDiskUsageStatus
        {
            Ok,
            NotConfigured,
            TargetUnavailable,
            SizeUnknown,
            Error
        }

        /// <summary>
        /// Computes backup disk usage with an availability status so callers can avoid
        /// resetting UI to 0% on transient target issues.
        /// </summary>
        public static (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold, string riskReason, BackupDiskUsageStatus status)
            ComputeBackupDiskUsageDetailed(AppConfig config)
        {
            try
            {
                string thresholdText = string.Format(
                    L("Dashboard.Storage.Threshold", "Keep at least {0}% free space"),
                    config.Storage.MinFreeSpacePercent);

                // Use the backup root from config; if not configured, show a hint.
                string? backupRoot = config.Backups.BackupLocation;
                if (string.IsNullOrWhiteSpace(backupRoot))
                {
                    return (
                        0d,
                        L("Dashboard.Storage.NotConfigured", "Backup root not configured"),
                        thresholdText,
                        false,
                        L("Dashboard.Storage.Risk.NotConfigured", "VaultSync cannot assess free space until a backup root is configured."),
                        BackupDiskUsageStatus.NotConfigured
                    );
                }

                if (OperatingSystem.IsMacOS() && IsNetworkPath(backupRoot))
                {
                    if (!TryResolveMountedSharePath(backupRoot, out string? mountedRoot))
                    {
                        return (
                            0d,
                            L("Dashboard.Storage.TargetUnavailable", "Backup target not available"),
                            thresholdText,
                            false,
                            L("Dashboard.Storage.Risk.TargetUnavailable", "The configured backup destination is currently unreachable, so free space and restore capacity cannot be verified."),
                            BackupDiskUsageStatus.TargetUnavailable
                        );
                    }

                    backupRoot = mountedRoot;
                }
                else
                {
                    backupRoot = Path.GetFullPath(backupRoot);
                }

                if (!TryGetDiskSpace(backupRoot, out long total, out long free))
                {
                    return (
                        0d,
                        L("Dashboard.Storage.TargetUnavailable", "Backup target not available"),
                        thresholdText,
                        false,
                        L("Dashboard.Storage.Risk.TargetUnavailable", "The configured backup destination is currently unreachable, so free space and restore capacity cannot be verified."),
                        BackupDiskUsageStatus.TargetUnavailable
                    );
                }

                if (total <= 0)
                {
                    return (
                        0d,
                        L("Dashboard.Storage.SizeUnknown", "Backup target size unknown"),
                        thresholdText,
                        false,
                        L("Dashboard.Storage.Risk.SizeUnknown", "VaultSync reached the backup target but could not determine total capacity."),
                        BackupDiskUsageStatus.SizeUnknown
                    );
                }

                long used        = total - free;
                double usedPercent = (double)used / total * 100d;
                double freePercent = (double)free / total * 100d;

                string freeText = string.Format(
                    L("Dashboard.Storage.FreeText", "Free {0} of {1} ({2}%)"),
                    FormatBytes(free),
                    FormatBytes(total),
                    freePercent.ToString("0.#"));
                bool isBelowThreshold = freePercent < config.Storage.MinFreeSpacePercent;

                string riskReason = isBelowThreshold
                    ? string.Format(
                        L("Dashboard.Storage.Risk.LowFreeSpace", "Free space dropped below the configured {0}% safety threshold, so future backups may fail or force retention cleanup."),
                        config.Storage.MinFreeSpacePercent)
                    : string.Empty;

                return (usedPercent, freeText, thresholdText, isBelowThreshold, riskReason, BackupDiskUsageStatus.Ok);
            }
            catch (Exception)
            {
                return (
                    0d,
                    L("Dashboard.Storage.UsageUnavailable", "Backup storage usage unavailable"),
                    string.Empty,
                    false,
                    L("Dashboard.Storage.Risk.Error", "VaultSync could not read backup storage usage because the destination check failed unexpectedly."),
                    BackupDiskUsageStatus.Error
                );
            }
        }

        public static (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold)
            ComputeBackupDiskUsage(AppConfig config)
        {
            (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold, string _, BackupDiskUsageStatus _) = ComputeBackupDiskUsageDetailed(config);
            return (usedPercent, freeText, thresholdText, isBelowThreshold);
        }

        /// <summary>
        /// Determines the path to pass into DriveInfo for a given backup path.
        /// On Windows we reduce to the drive root (C:\, V:\, etc).
        /// On macOS/Linux we keep the mount path (e.g., /Volumes/MyDisk) so external drives are honored.
        /// </summary>
        private static string GetDriveInfoPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return string.Empty;

            string normalized = Path.GetFullPath(fullPath);

            if (OperatingSystem.IsWindows())
            {
                return Path.GetPathRoot(normalized) ?? string.Empty;
            }

            // macOS external disks live under /Volumes/<Name>; keep the mount path.
            if (normalized.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    return "/Volumes/" + parts[1];
                }
            }

            // Fallback: DriveInfo can handle the full path on Unix-like systems.
            return normalized;
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveMountedSharePath(string originalPath, out string mountedPath)
        {
            mountedPath = string.Empty;

            if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(originalPath))
                return false;

            if (!TryParseShareWithSubpath(originalPath, out string? host, out string? share, out string? subPath))
                return false;

            string mountPoint = TryGetMountedSharePath(host, share);
            if (!string.IsNullOrWhiteSpace(mountPoint))
            {
                mountedPath = AppendShareSubPath(mountPoint, subPath);
                return true;
            }

            string mountRoot = GetMacMountRoot();
            if (TryFindMountByName(share, mountRoot, out string? rootMatch))
            {
                mountedPath = AppendShareSubPath(rootMatch, subPath);
                return true;
            }

            if (TryFindMountByName(share, "/Volumes", out string? volumesMatch))
            {
                mountedPath = AppendShareSubPath(volumesMatch, subPath);
                return true;
            }

            return false;
        }

        private static bool TryParseShareWithSubpath(string path, out string host, out string share, out string subPath)
        {
            host = string.Empty;
            share = string.Empty;
            subPath = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
                    return false;

                host = uri.Host;
                string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    return false;

                share = segments[0];
                if (segments.Length > 1)
                    subPath = string.Join('/', segments.Skip(1));

                return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(share);
            }

            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = path.TrimStart('\\', '/').Replace('\\', FormatSeparator);
                string[] parts = trimmed.Split(FormatSeparator, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return false;

                host = parts[0];
                share = parts[1];

                if (host.Contains('@'))
                    host = host.Split('@').Last();
                if (host.Contains(':'))
                    host = host.Split(':').Last();

                if (parts.Length > 2)
                    subPath = string.Join('/', parts.Skip(2));

                return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(share);
            }

            return false;
        }

        private static string AppendShareSubPath(string mountPoint, string subPath)
        {
            if (string.IsNullOrWhiteSpace(subPath))
                return mountPoint;

            string cleaned = subPath.Trim().TrimStart('/', '\\');
            if (string.IsNullOrWhiteSpace(cleaned))
                return mountPoint;

            string[] segments = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 0
                ? mountPoint
                : Path.Combine([mountPoint, .. segments]);
        }

        private const char FormatSeparator = '/';

        private static bool TryParseShare(string path, out string host, out string share)
        {
            host  = string.Empty;
            share = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
                    return false;

                host = uri.Host;
                string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    return false;

                share = segments[0];
                return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(share);
            }

            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = path.TrimStart('\\', '/').Replace('\\', '/');
                string[] parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return false;

                host  = parts[0];
                share = parts[1];

                if (host.Contains('@'))
                {
                    host = host.Split('@').Last();
                }

                if (host.Contains(':'))
                {
                    host = host.Split(':').Last();
                }

                return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(share);
            }

            return false;
        }

        private static string TryGetMountedSharePath(string host, string share)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(share))
                return string.Empty;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "/sbin/mount",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                    return string.Empty;

                proc.WaitForExit(3_000);
                string output = proc.StandardOutput.ReadToEnd();
                if (string.IsNullOrWhiteSpace(output))
                    return string.Empty;

                string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string line in lines)
                {
                    if (!line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                    if (onIndex <= 0)
                        continue;

                    string source = line.Substring(0, onIndex).Trim();
                    string rest = line.Substring(onIndex + 4);
                    string mountPoint = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                    if (string.IsNullOrWhiteSpace(mountPoint))
                        continue;

                    if (!TryParseShare(source, out string? mountedHost, out string? mountedShare))
                        continue;

                    if (string.Equals(host, mountedHost, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(share, mountedShare, StringComparison.OrdinalIgnoreCase))
                    {
                        return mountPoint;
                    }
                }
            }
            catch
            {
                return string.Empty;
            }

            return string.Empty;
        }

        private static bool TryFindMountByName(string share, string rootPath, out string mountedPath)
        {
            mountedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(share) || string.IsNullOrWhiteSpace(rootPath))
                return false;

            try
            {
                if (!Directory.Exists(rootPath))
                    return false;

                string exact = Path.Combine(rootPath, share);
                if (Directory.Exists(exact))
                {
                    mountedPath = exact;
                    return true;
                }

                string? match = Directory.EnumerateDirectories(rootPath)
                    .FirstOrDefault(dir =>
                        string.Equals(Path.GetFileName(dir), share, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(match))
                    return false;

                mountedPath = match;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetMacMountRoot()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "VaultSync", "mounts");
        }

        private static bool TryGetDiskSpace(string path, out long totalBytes, out long freeBytes)
        {
            totalBytes = 0;
            freeBytes  = 0;

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                string fullPath = Path.GetFullPath(path);

                if (OperatingSystem.IsWindows())
                {
                    if (!GetDiskFreeSpaceEx(
                            fullPath,
                            out ulong freeBytesAvailable,
                            out ulong totalNumberOfBytes,
                            out _))
                    {
                        return false;
                    }

                    totalBytes = (long)totalNumberOfBytes;
                    freeBytes  = (long)freeBytesAvailable;
                    return totalBytes > 0;
                }

                string drivePath = GetDriveInfoPath(fullPath);
                if (string.IsNullOrWhiteSpace(drivePath))
                    return false;

                var drive = new DriveInfo(drivePath);
                if (!drive.IsReady || drive.TotalSize <= 0)
                    return false;

                totalBytes = drive.TotalSize;
                freeBytes  = drive.AvailableFreeSpace;
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        private void UpdateBackupSummaryPills()
        {
            using var timing = RuntimeTiming.Measure("Dashboard backup summary rebuild");
            // Dashboard tracks the last 7 days (UTC date, including today) in the chart arrays.
            int todayCount = _snapshotCountsByDay.Length > 0 ? (int)_snapshotCountsByDay[^1] : 0;
            int autoWeek = _autoCountsByDay.Sum();
            int manualWeek = _manualCountsByDay.Sum();
            int importedWeek = _importedCountsByDay.Sum();
            int weekTotal = autoWeek + manualWeek + importedWeek;

            SnapshotsSummaryLine = Lf(
                "Backups.Summary.TodayWeek",
                "{0} backups today - {1} this week",
                todayCount,
                weekTotal);

            if (weekTotal == 0)
            {
                SnapshotActivitySummary = L("Backups.Summary.NoActivity", "No backups in the last 7 days");
            }
            else
            {
                SnapshotActivitySummary = Lf(
                    "Backups.Summary.ActivityTotals",
                    "{0} backups total - {1} auto - {2} manual - {3} imported",
                    weekTotal,
                    autoWeek,
                    manualWeek,
                    importedWeek);
            }
        }

        private static string TrimForTooltip(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (maxLength < 4 || value!.Length <= maxLength)
            {
                return value!;
            }

            return value!.Substring(0, maxLength - 3) + "...";
        }

        private static string L(string key, string fallback)
        {
            LocalizationService? loc = LocalizationProvider.Service;
            string? value = loc?.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string Lf(string key, string fallback, params object[] args)
        {
            return string.Format(L(key, fallback), args);
        }

        public void ReapplyLocalization()
        {
            RebuildStorageSortOptions();
            SnapshotsHint = string.Format(L("Dashboard.Hint.SnapshotsThisWeek", "{0} this week"), _backupsThisWeekCount);
            UpdateBackupSummaryPills();

            if (ProjectCount == 0)
            {
                ProjectsHint = L("Dashboard.Hint.NoProjects", "No projects yet");
            }
            else if (_activeProjectsCount == 0)
            {
                ProjectsHint = L("Dashboard.Hint.NoSnapshots", "No snapshots yet");
            }
            else
            {
                ProjectsHint = _activeProjectsCount == 1
                    ? L("Dashboard.Hint.ActiveProjects.One", "1 active project")
                    : string.Format(L("Dashboard.Hint.ActiveProjects.Many", "{0} active projects"), _activeProjectsCount);
            }

            StorageHint = _activeProjectsCount == 0
                ? L("Dashboard.Hint.StorageEmpty", "No storage used")
                : L("Dashboard.Hint.StorageTotal", "Total across all backups");

            RestoreReadinessHeadline = FormatRestoreReadinessHeadline(
                RestoreReadinessReadyCount,
                RestoreReadinessAttentionCount,
                RestoreReadinessRiskCount,
                RestoreReadinessUnavailableCount,
                RestoreReadinessReadyCount + RestoreReadinessAttentionCount + RestoreReadinessRiskCount + RestoreReadinessUnavailableCount);
            RestoreReadinessDetail = FormatRestoreReadinessDetail(
                RestoreReadinessReadyCount,
                RestoreReadinessAttentionCount,
                RestoreReadinessRiskCount,
                RestoreReadinessUnavailableCount);
            RestoreReadinessReadyLabel = Lf("RestoreReadiness.Count.Ready", "{0} ready", RestoreReadinessReadyCount);
            RestoreReadinessAttentionLabel = Lf("RestoreReadiness.Count.Attention", "{0} attention", RestoreReadinessAttentionCount);
            RestoreReadinessRiskLabel = Lf("RestoreReadiness.Count.Risk", "{0} risk", RestoreReadinessRiskCount);
            RestoreReadinessUnavailableLabel = Lf("RestoreReadiness.Count.Unavailable", "{0} unavailable", RestoreReadinessUnavailableCount);

            // Refresh recovery coverage labels with localized strings using cached summary data
            ApplyRecoveryCoverageSummary(_lastRecoveryCoverageSummary);
            if (_lastDashboardData is not null)
            {
                ApplyPriorityOverview(_lastDashboardData);
            }

            // Refresh weekly activity average label with cached data
            WeeklyAverageLabel = Lf("Dashboard.Chart.AvgLabel", "Avg {0:0.0}", _lastWeeklyAverage);

            OnPropertyChanged(nameof(TotalSnapshotsWeekLabel));
        }

        private void RebuildStorageSortOptions()
        {
            using var timing = RuntimeTiming.Measure("Dashboard storage sort options rebuild");
            StorageLegendSortMode selectedMode = _selectedStorageSortOption?.Mode ?? StorageLegendSortMode.LargestFirst;
            StorageSortOptions.Clear();
            StorageSortOptions.Add(new StorageLegendSortOption(
                StorageLegendSortMode.LargestFirst,
                L("Dashboard.Storage.Sort.Largest", "Largest first")));
            StorageSortOptions.Add(new StorageLegendSortOption(
                StorageLegendSortMode.Alphabetical,
                L("Dashboard.Storage.Sort.Alphabetical", "A-Z")));

            StorageLegendSortOption match = StorageSortOptions.FirstOrDefault(x => x.Mode == selectedMode) ?? StorageSortOptions[0];
            _selectedStorageSortOption = match;
            OnPropertyChanged(nameof(SelectedStorageSortOption));
            RebuildStorageDonut();
        }

        private void ApplyRestoreReadinessSummary(RestoreReadinessSummary summary)
        {
            using var timing = RuntimeTiming.Measure("Dashboard restore readiness rebuild");
            RestoreReadinessReadyCount = summary.ReadyCount;
            RestoreReadinessAttentionCount = summary.AttentionCount;
            RestoreReadinessRiskCount = summary.RiskCount;
            RestoreReadinessUnavailableCount = summary.UnavailableCount;
            RestoreReadinessHeadline = FormatRestoreReadinessHeadline(
                summary.ReadyCount,
                summary.AttentionCount,
                summary.RiskCount,
                summary.UnavailableCount,
                summary.ProjectCount);
            RestoreReadinessDetail = FormatRestoreReadinessDetail(
                summary.ReadyCount,
                summary.AttentionCount,
                summary.RiskCount,
                summary.UnavailableCount);
            RestoreReadinessReadyLabel = Lf("RestoreReadiness.Count.Ready", "{0} ready", summary.ReadyCount);
            RestoreReadinessAttentionLabel = Lf("RestoreReadiness.Count.Attention", "{0} attention", summary.AttentionCount);
            RestoreReadinessRiskLabel = Lf("RestoreReadiness.Count.Risk", "{0} risk", summary.RiskCount);
            RestoreReadinessUnavailableLabel = Lf("RestoreReadiness.Count.Unavailable", "{0} unavailable", summary.UnavailableCount);

            RestoreReadinessIssues.Clear();
            foreach (ProjectRestoreReadiness? item in summary.Projects
                         .Where(project => project.State != RestoreReadinessState.Ready)
                         .OrderByDescending(project => project.State == RestoreReadinessState.Risk)
                         .ThenByDescending(project => project.State == RestoreReadinessState.Unavailable)
                         .ThenBy(project => project.ProjectName, StringComparer.CurrentCultureIgnoreCase)
                         .Take(6))
            {
                RestoreReadinessIssues.Add(new RestoreReadinessIssueItem(
                    item.ProjectName,
                    LocalizeRestoreReadinessState(item.State),
                    item.Reason,
                    GetRestoreReadinessBrush(item.State)));
            }

            if (RestoreReadinessIssues.Count == 0)
                ShowRestoreReadinessIssues = false;

            OnPropertyChanged(nameof(HasRestoreReadinessIssues));
            ToggleRestoreReadinessIssuesCommand.RaiseCanExecuteChanged();
        }

        private void ApplyRecoveryCoverageSummary(RecoveryCoverageSummary summary)
        {
            _lastRecoveryCoverageSummary = summary;
            using var timing = RuntimeTiming.Measure("Dashboard recovery coverage rebuild");
            int total = Math.Max(0, summary.ProjectCount);
            RecoveryCoverageDetail = total == 0
                ? L("RecoveryCoverage.Detail.Empty", "No tracked projects to measure recovery coverage.")
                : Lf(
                    "RecoveryCoverage.Detail",
                    "{0} of {1} project(s) have a backup within 24 hours; {2} are covered within 7 days.",
                    summary.Within24Hours,
                    total,
                    summary.Within7Days);

            RecoveryCoverage24Label = Lf("RecoveryCoverage.Window.24h", "24h: {0}/{1}", summary.Within24Hours, total);
            RecoveryCoverage7Label = Lf("RecoveryCoverage.Window.7d", "7d: {0}/{1}", summary.Within7Days, total);
            RecoveryCoverage30Label = Lf("RecoveryCoverage.Window.30d", "30d: {0}/{1}", summary.Within30Days, total);
            RecoveryCoverage90Label = Lf("RecoveryCoverage.Window.90d", "90d: {0}/{1}", summary.Within90Days, total);
        }

        private void ApplyPriorityOverview(DashboardData data)
        {
            ProjectRestoreReadiness? requiredAction = data.RestoreReadiness.Projects
                .Where(project => project.State != RestoreReadinessState.Ready)
                .OrderBy(project => project.Score)
                .ThenBy(project => project.ProjectName, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();

            RequiredActionTitle = requiredAction?.ProjectName
                ?? L("RestoreReadiness.ReviewEmpty", "Everything currently looks restore-ready.");
            RequiredActionDetail = requiredAction?.Reason ?? RestoreReadinessHeadline;

            if (_scheduleViewModel is null)
            {
                BackupScheduleProjection projection = BackupSchedulePolicy.Project(
                    data.Config.Backups.EnableAutoBackups,
                    data.Config.Backups.IntervalMinutes,
                    data.Config.Backups.EnableQuietHours,
                    data.Config.Backups.QuietHoursStart,
                    data.Config.Backups.QuietHoursEnd,
                    DateTimeOffset.Now,
                    timerDueAtLocal: null);
                NextRunText = projection.NextRunAtLocal is { } nextRun
                    ? nextRun.ToString("ddd, d MMM · HH:mm", System.Globalization.CultureInfo.CurrentCulture)
                    : L("Schedule.NextRun.None", "No automatic run scheduled");
                NextRunDetail = projection.Status == BackupScheduleStatus.ManualOnly
                    ? L("Schedule.Delay.Manual", "Automatic backups are off. You can still start a backup at any time.")
                    : Lf("Schedule.Delay.Interval", "VaultSync checks for work every {0} minutes.", data.Config.Backups.IntervalMinutes);
            }
            else
            {
                _scheduleViewModel.Refresh();
                NextRunText = _scheduleViewModel.NextRunText;
                NextRunDetail = _scheduleViewModel.DelayExplanation;
            }

            if (data.LatestKnownGoodBackup is { } knownGood)
            {
                LatestKnownGoodTitle = string.IsNullOrWhiteSpace(data.LatestKnownGoodProjectName)
                    ? L("History.Status.KnownGood", "Known good restore point")
                    : data.LatestKnownGoodProjectName;
                LatestKnownGoodDetail = knownGood.CreatedUtc.ToLocalTime().ToString(
                    "ddd, d MMM yyyy · HH:mm",
                    System.Globalization.CultureInfo.CurrentCulture);
            }
            else
            {
                LatestKnownGoodTitle = L("History.Status.NotSelected", "No snapshot selected");
                LatestKnownGoodDetail = L(
                    "History.Status.NotSelectedDetail",
                    "Select an event in History to review and mark a reliable recovery point.");
            }
        }

        private static string LocalizeRestoreReadinessState(RestoreReadinessState state)
        {
            return state switch
            {
                RestoreReadinessState.Ready => L("RestoreReadiness.State.Ready", "Ready"),
                RestoreReadinessState.Attention => L("RestoreReadiness.State.Attention", "Attention"),
                RestoreReadinessState.Risk => L("RestoreReadiness.State.Risk", "Risk"),
                _ => L("RestoreReadiness.State.Unavailable", "Unavailable")
            };
        }

        private static IBrush GetRestoreReadinessBrush(RestoreReadinessState state)
        {
            return state switch
            {
                RestoreReadinessState.Ready => new ImmutableSolidColorBrush(Color.Parse("#22CC88")),
                RestoreReadinessState.Attention => new ImmutableSolidColorBrush(Color.Parse("#FFB84C")),
                RestoreReadinessState.Risk => new ImmutableSolidColorBrush(Color.Parse("#F56A5A")),
                _ => new ImmutableSolidColorBrush(Color.Parse("#7F8FA8"))
            };
        }

        private static string FormatRestoreReadinessHeadline(int ready, int attention, int risk, int unavailable, int projectCount)
        {
            if (projectCount <= 0)
                return L("RestoreReadiness.Headline.Empty", "No tracked projects yet");

            if (ready == projectCount)
                return L("RestoreReadiness.Headline.AllReady", "Restore ready across all tracked projects");

            if (unavailable > 0)
                return Lf("RestoreReadiness.Headline.Unavailable", "{0} project(s) are not currently restore-ready", unavailable);

            if (risk > 0)
                return Lf("RestoreReadiness.Headline.Risk", "{0} project(s) need restore-readiness attention", risk);

            if (attention > 0)
                return Lf("RestoreReadiness.Headline.Attention", "{0} project(s) should be reviewed", attention);

            return L("RestoreReadiness.Headline.Empty", "No tracked projects yet");
        }

        private static string FormatRestoreReadinessDetail(int ready, int attention, int risk, int unavailable)
        {
            return Lf("RestoreReadiness.Detail", "Ready {0} - Attention {1} - Risk {2} - Unavailable {3}", ready, attention, risk, unavailable);
        }

        private static string FormatBytes(long bytes) =>
            bytes <= 0 ? "0 B" : UiFormat.FormatBytes(bytes, "0.#");

        // Bindables
        public record LegendItem(string Label, string Tooltip, IBrush Brush);
        public record StorageLegendSortOption(StorageLegendSortMode Mode, string Label);
        public record BackupUsageSegment(string Name, string ValueText, double SizeBytes, IBrush Brush, string Tooltip);
        public record RestoreReadinessIssueItem(string ProjectName, string StateLabel, string Reason, IBrush StateBrush);
        private sealed record DashboardActivity(int? ProjectId, DateTime WhenUtc, string Subtitle);

        public enum Dot { Green, Blue, Purple, Gray }

        public class ActivityItem
        {
            private static readonly IBrush DotGreenBrush = new ImmutableSolidColorBrush(Color.Parse("#2ECC71"));
            private static readonly IBrush DotBlueBrush = new ImmutableSolidColorBrush(Color.Parse("#1ABCFE"));
            private static readonly IBrush DotPurpleBrush = new ImmutableSolidColorBrush(Color.Parse("#8E77FF"));
            private static readonly IBrush DotGrayBrush = new ImmutableSolidColorBrush(Colors.Gray);

            // New constructor: allow passing an explicit brush (used when we want per-project colors).
            public ActivityItem(string title, string subtitle, string when, IBrush dotBrush, string projectTagsDisplay = "", IEnumerable<ProjectTagChip>? projectTagChips = null)
            {
                Title    = title;
                Subtitle = subtitle;
                When     = when;
                DotBrush = dotBrush;
                ProjectTagsDisplay = projectTagsDisplay ?? string.Empty;
                ProjectTagChips = new ObservableCollection<ProjectTagChip>(projectTagChips ?? []);
            }

            // Backwards-compatible constructor for simple fixed dots.
            public ActivityItem(string title, string subtitle, string when, Dot dot)
                : this(title, subtitle, when,
                    dot switch
                    {
                        Dot.Green  => DotGreenBrush,
                        Dot.Blue   => DotBlueBrush,
                        Dot.Purple => DotPurpleBrush,
                        Dot.Gray   => DotGrayBrush,
                        _          => DotGrayBrush
                    },
                    string.Empty)
            {
            }

            public string Title { get; }
            public string Subtitle { get; }
            public string When { get; }
            public IBrush DotBrush { get; }
            public string ProjectTagsDisplay { get; }
            public ObservableCollection<ProjectTagChip> ProjectTagChips { get; }
            public bool HasProjectTags => ProjectTagChips.Count > 0;
        }
    }
}
