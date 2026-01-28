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
using VaultSync.UI.Infrastructure; // for RelayCommand
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    public class DashboardViewModel : ViewModelBase
    {
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
        private bool _backupDiskIsBelowThreshold;

        // Backup storage segmented usage bar (Other + per-project)
        public IReadOnlyList<BackupUsageSegment> BackupUsageSegments { get; private set; } =
            Array.Empty<BackupUsageSegment>();

        public ISeries[] BackupUsageSeries { get; private set; } = Array.Empty<ISeries>();
        public Axis[] BackupUsageXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] BackupUsageYAxes { get; private set; } = Array.Empty<Axis>();
        public bool HasBackupUsageSegments => BackupUsageSegments.Count > 0;

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

        // Search / actions (your RelayCommand expects Action<object?>)
        public string? SearchText { get; set; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand NewSnapshotCommand { get; }

        // Chart bindings
        public ISeries[] SnapshotSeries { get; private set; } = Array.Empty<ISeries>();
        public Axis[] SnapshotXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] SnapshotYAxes { get; private set; } = Array.Empty<Axis>();
        public ObservableCollection<SnapshotActivityPoint> WeeklySnapshotActivity { get; } = new();
        public double WeeklyChartHeight { get; private set; } = 180;
        public double WeeklyAverageLineOffset { get; private set; }
        public string WeeklyAverageLabel { get; private set; } = string.Empty;
        public string TotalSnapshotsWeek => _snapshotCountsByDay.Sum().ToString();
        public string TotalSnapshotsWeekLabel => string.Format(L("Dashboard.Hint.SnapshotsThisWeek", "{0} this week"), TotalSnapshotsWeek);

        // Donut bindings
        public ISeries[] StorageSeries { get; private set; } = Array.Empty<ISeries>();
        public IEnumerable<LegendItem> StorageLegend { get; private set; } = Array.Empty<LegendItem>();

        // Activity items, populated from real data.
        public ObservableCollection<ActivityItem> ActivityItems { get; } = new();

        // Internal data for chart aggregation
        private string[] _days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        private readonly double[] _snapshotCountsByDay = new double[7];
        private readonly int[] _autoCountsByDay = new int[7];
        private readonly int[] _manualCountsByDay = new int[7];
        private readonly int[] _importedCountsByDay = new int[7];
        private int _backupsThisWeekCount;
        private int _activeProjectsCount;
        private int _refreshInFlight;
        private int _refreshQueued;

        public DashboardViewModel()
        {
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            NewSnapshotCommand = new RelayCommand(_ => { /* wired later from dashboard actions */ });

            BuildStaticAxes();
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
        public async System.Threading.Tasks.Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
            {
                Interlocked.Exchange(ref _refreshQueued, 1);
                return;
            }

            try
            {
                var data = await Task.Run(() =>
                {
                    var cfg = AppConfigStore.Load();
                    var diskUsage = ComputeBackupDiskUsage(cfg);

                    var dbPath = !string.IsNullOrWhiteSpace(cfg.DbPath)
                        ? cfg.DbPath
                        : GetDefaultDbPath();

                    var repo = new SqliteRepository(dbPath);

                    var projects = repo.GetAllProjects().ToList();
                    var backupCount = repo.GetBackupCount();

                    var startDate = DateTime.UtcNow.Date.AddDays(-6);
                    var endDate = DateTime.UtcNow;
                    var backupCountsByDay = repo.GetBackupCountsByDayBreakdown(startDate, endDate);

                    // Storage slices: total backups per project (incl. imported)
                    long totalLatestBytes = 0;
                    long totalLocalBytes = 0;
                    var storageSlices = new List<(Project project, long bytes)>();
                    var backupsByProject = repo.GetBackupTotalsByProject(includeImported: true);
                    var localBackupsByProject = repo.GetBackupTotalsByProject(includeImported: false);

                    foreach (var p in projects)
                    {
                        if (!backupsByProject.TryGetValue(p.Id, out var projectTotal))
                            continue;

                        totalLatestBytes += projectTotal;
                        storageSlices.Add((p, projectTotal));

                        if (localBackupsByProject.TryGetValue(p.Id, out var localTotal))
                        {
                            totalLocalBytes += localTotal;
                        }
                    }

                    var dayLabels = new string[_days.Length];
                    for (var i = 0; i < dayLabels.Length; i++)
                    {
                        var d = startDate.AddDays(i);
                        dayLabels[i] = d.ToString("ddd");
                    }

                    var counts = new double[_snapshotCountsByDay.Length];
                    var autoCounts = new int[_snapshotCountsByDay.Length];
                    var manualCounts = new int[_snapshotCountsByDay.Length];
                    var importedCounts = new int[_snapshotCountsByDay.Length];
                    for (var i = 0; i < counts.Length; i++)
                    {
                        var d = startDate.AddDays(i);
                        if (backupCountsByDay.TryGetValue(d, out var breakdown))
                        {
                            autoCounts[i] = breakdown.AutoCount;
                            manualCounts[i] = breakdown.ManualCount;
                            importedCounts[i] = breakdown.ImportedCount;
                            counts[i] = breakdown.AutoCount + breakdown.ManualCount + breakdown.ImportedCount;
                        }
                    }

                    // Activity list (newest first)
                    var activities = new List<(int? ProjectId, DateTime WhenUtc, string Subtitle)>();
                    var recentBackups = repo.GetRecentBackups(12);
                    foreach (var b in recentBackups)
                    {
                        var subtitle = string.Equals(b.type, "auto", StringComparison.OrdinalIgnoreCase)
                            ? "auto"
                            : "manual";
                        activities.Add((b.projectId, b.createdUtc, subtitle));
                    }

                    var recentSnapshots = repo.GetRecentSnapshotsWithoutBackup(12);
                    foreach (var s in recentSnapshots)
                    {
                        activities.Add((s.projectId, s.createdUtc, "snapshot"));
                    }

                    return new DashboardData
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
                        ImportedCounts = importedCounts
                    };
                });

                // Apply results on UI thread
                BackupDiskUsedPercent      = data.DiskUsage.UsedPercent;
                BackupDiskFreeText         = data.DiskUsage.FreeText;
                BackupDiskThresholdText    = data.DiskUsage.ThresholdText;
                BackupDiskIsBelowThreshold = data.DiskUsage.IsBelowThreshold;

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

                // Activity
                var activityItems = new List<ActivityItem>();
                var projectPalette = new[]
                {
                    Color.Parse("#4C8DFF"),
                    Color.Parse("#22CC88"),
                    Color.Parse("#FFB84C"),
                    Color.Parse("#FF6B6B"),
                    Color.Parse("#9B6BFF")
                };
                var projectDotBrushes = new Dictionary<int, IBrush>();
                var paletteIndex = 0;

                IBrush GetBrush(Color color) => new ImmutableSolidColorBrush(color);

                foreach (var a in data.Activities
                             .OrderByDescending(a => a.WhenUtc)
                             .Take(5))
                {
                    Project? project = null;
                    if (a.ProjectId.HasValue)
                    {
                        project = data.Projects.FirstOrDefault(p => p.Id == a.ProjectId.Value);
                    }

                    var title = project != null ? project.Name : L("Dashboard.Activity.UnknownProject", "Unknown project");
                    var subtitle = a.Subtitle switch
                    {
                        "auto"     => L("Dashboard.Activity.AutoBackup", "Auto backup created"),
                        "manual"   => L("Dashboard.Activity.ManualBackup", "Manual backup created"),
                        _          => L("Dashboard.Activity.SnapshotCreated", "Snapshot created")
                    };
                    var when = a.WhenUtc.ToLocalTime().ToString("g");

                    IBrush dotBrush;
                    if (project != null)
                    {
                        if (!projectDotBrushes.TryGetValue(project.Id, out dotBrush!))
                        {
                            var color = projectPalette[paletteIndex % projectPalette.Length];
                            dotBrush = GetBrush(color);
                            projectDotBrushes[project.Id] = dotBrush;
                            paletteIndex++;
                        }
                    }
                    else
                    {
                        dotBrush = GetBrush(Colors.Gray);
                    }

                    activityItems.Add(new ActivityItem(title, subtitle, when, dotBrush));
                }

                ActivityItems.Clear();
                foreach (var item in activityItems)
                {
                    ActivityItems.Add(item);
                }

                // Backup chart
                for (var i = 0; i < _days.Length && i < data.DayLabels.Length; i++)
                {
                    _days[i] = data.DayLabels[i];
                }
                Array.Clear(_snapshotCountsByDay, 0, _snapshotCountsByDay.Length);
                for (var i = 0; i < _snapshotCountsByDay.Length && i < data.SnapshotCounts.Length; i++)
                {
                    _snapshotCountsByDay[i] = data.SnapshotCounts[i];
                }
                Array.Clear(_autoCountsByDay, 0, _autoCountsByDay.Length);
                Array.Clear(_manualCountsByDay, 0, _manualCountsByDay.Length);
                Array.Clear(_importedCountsByDay, 0, _importedCountsByDay.Length);
                for (var i = 0; i < _autoCountsByDay.Length && i < data.AutoCounts.Length; i++)
                {
                    _autoCountsByDay[i] = data.AutoCounts[i];
                }
                for (var i = 0; i < _manualCountsByDay.Length && i < data.ManualCounts.Length; i++)
                {
                    _manualCountsByDay[i] = data.ManualCounts[i];
                }
                for (var i = 0; i < _importedCountsByDay.Length && i < data.ImportedCounts.Length; i++)
                {
                    _importedCountsByDay[i] = data.ImportedCounts[i];
                }

                BuildSnapshotSeries();
                BuildWeeklyActivity();
                BuildStorageDonut(data.StorageSlices);
                BuildBackupUsageBar(data.Config, data.StorageSlices);

                OnPropertyChanged(nameof(TotalSnapshotsWeek));
                OnPropertyChanged(nameof(TotalSnapshotsWeekLabel));
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
                    await RefreshAsync();
                }
            }
        }

        private sealed class DashboardData
        {
            public AppConfig Config { get; init; } = new();
            public (double UsedPercent, string FreeText, string ThresholdText, bool IsBelowThreshold) DiskUsage;
            public List<Project> Projects { get; init; } = new();
            public List<(int? ProjectId, DateTime WhenUtc, string Subtitle)> Activities { get; init; } = new();
            public List<(Project project, long bytes)> StorageSlices { get; init; } = new();
            public long TotalLatestBytes { get; init; }
            public long TotalLocalBytes { get; init; }
            public int BackupCount { get; init; }
            public int BackupsThisWeekCount { get; init; }
            public string[] DayLabels { get; init; } = Array.Empty<string>();
            public double[] SnapshotCounts { get; init; } = Array.Empty<double>();
            public int[] AutoCounts { get; init; } = Array.Empty<int>();
            public int[] ManualCounts { get; init; } = Array.Empty<int>();
            public int[] ImportedCounts { get; init; } = Array.Empty<int>();
        }

        private void BuildSnapshotSeries()
        {
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
            WeeklySnapshotActivity.Clear();

            const double chartHeight = 180;
            const double barBase = 20;
            const double barRange = chartHeight - 36;
            WeeklyChartHeight = chartHeight;

            var max = _snapshotCountsByDay.DefaultIfEmpty(0d).Max();
            if (max < 1)
            {
                max = 1;
            }

            var avg = _snapshotCountsByDay.Length == 0 ? 0d : _snapshotCountsByDay.Average();
            var avgNormalized = avg / max;
            var avgHeight = avg <= 0 ? 0 : barBase + avgNormalized * barRange;
            const double labelOffset = 12;
            WeeklyAverageLineOffset = labelOffset + avgHeight;
            WeeklyAverageLabel = Lf("Dashboard.Chart.AvgLabel", "Avg {0:0.0}", avg);

            for (var i = 0; i < _snapshotCountsByDay.Length && i < _days.Length; i++)
            {
                var autoCount = _autoCountsByDay[i];
                var manualCount = _manualCountsByDay[i];
                var importedCount = _importedCountsByDay[i];
                var count = autoCount + manualCount + importedCount;
                var normalized = count / max;
                var totalHeight = count == 0 ? 0 : barBase + normalized * barRange;
                var dayLabel = _days[i];

                var tooltip = count == 0
                    ? Lf("Dashboard.Chart.TooltipNone", "{0}: No backups", dayLabel)
                    : Lf("Dashboard.Chart.TooltipBreakdown", "{0}: {1} auto, {2} manual, {3} imported", dayLabel, autoCount, manualCount, importedCount);

                var autoHeight = 0d;
                var manualHeight = 0d;
                var importedHeight = 0d;
                if (count > 0)
                {
                    autoHeight = autoCount == 0 ? 0 : Math.Max(6, totalHeight * autoCount / count);
                    manualHeight = manualCount == 0 ? 0 : Math.Max(6, totalHeight * manualCount / count);
                    importedHeight = importedCount == 0 ? 0 : Math.Max(6, totalHeight * importedCount / count);

                    var combined = autoHeight + manualHeight + importedHeight;
                    if (combined > totalHeight && combined > 0)
                    {
                        var scale = totalHeight / combined;
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
            // If we have no per-project data, show an empty donut.
            if (perProject == null || perProject.Count == 0)
            {
                StorageSeries = Array.Empty<ISeries>();
                StorageLegend = Array.Empty<LegendItem>();
                OnPropertyChanged(nameof(StorageSeries));
                OnPropertyChanged(nameof(StorageLegend));
                return;
            }

            var total = perProject.Sum(p => p.bytes);
            if (total <= 0)
            {
                StorageSeries = Array.Empty<ISeries>();
                StorageLegend = Array.Empty<LegendItem>();
                OnPropertyChanged(nameof(StorageSeries));
                OnPropertyChanged(nameof(StorageLegend));
                return;
            }

            // Simple color palette that looks good in dark mode.
            var palette = new[]
            {
                SKColor.Parse("#4C8DFF"),
                SKColor.Parse("#22CC88"),
                SKColor.Parse("#FFB84C"),
                SKColor.Parse("#FF6B6B"),
                SKColor.Parse("#9B6BFF")
            };

            var series = new List<ISeries>();
                var legend = new List<LegendItem>();

            for (int i = 0; i < perProject.Count; i++)
            {
                var (project, bytes) = perProject[i];
                if (bytes <= 0) continue;

                var color = palette[i % palette.Length];
                var projectName = project.Name;
                var sliceBytes = bytes;

                series.Add(new PieSeries<double>
                {
                    Values      = new[] { (double)bytes },
                    Name        = projectName,
                    InnerRadius = 90,
                    Stroke      = null,
                    Fill        = new SolidColorPaint(color),
                    ToolTipLabelFormatter = point =>
                        $"{projectName} {FormatBytes(sliceBytes)}"
                });

                legend.Add(new LegendItem(
                    $"{projectName} {FormatBytes(bytes)}",
                    new ImmutableSolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue))));
            }

            StorageSeries = series.ToArray();
            StorageLegend = legend;

            OnPropertyChanged(nameof(StorageSeries));
            OnPropertyChanged(nameof(StorageLegend));
        }

        private void BuildDemoSeriesIfNeeded()
        {
            if (SnapshotSeries is { Length: > 0 } && StorageSeries is { Length: > 0 })
                return;

            // No demo data when the DB is empty or unavailable.
            Array.Clear(_snapshotCountsByDay, 0, _snapshotCountsByDay.Length);
            Array.Clear(_autoCountsByDay, 0, _autoCountsByDay.Length);
            Array.Clear(_manualCountsByDay, 0, _manualCountsByDay.Length);
            Array.Clear(_importedCountsByDay, 0, _importedCountsByDay.Length);
            WeeklySnapshotActivity.Clear();
            SnapshotSeries = Array.Empty<ISeries>();
            OnPropertyChanged(nameof(SnapshotSeries));

            ProjectCount   = 0;
            ProjectsHint   = L("Dashboard.Hint.NoProjects", "No projects yet");
            SnapshotCount  = 0;
            SnapshotsHint  = L("Dashboard.Hint.NoSnapshots", "No snapshots yet");
            StorageUsed    = "0 B";
            StorageUsedLocal = Lf("Dashboard.Kpi.StorageLocal", "Local: {0}", "0 B");
            StorageHint    = L("Dashboard.Hint.StorageEmpty", "No storage used");

            BuildStorageDonut(Array.Empty<(Project project, long bytes)>());
            BuildBackupUsageBar(AppConfigStore.Load(), Array.Empty<(Project project, long bytes)>());
            OnPropertyChanged(nameof(TotalSnapshotsWeek));
            OnPropertyChanged(nameof(TotalSnapshotsWeekLabel));
        }

        private void BuildBackupUsageBar(AppConfig config, IReadOnlyList<(Project project, long bytes)> perProject)
        {
            try
            {
                // Default to empty segments if backup root is not configured.
                var backupRoot = config.Backups.BackupLocation;
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            BackupUsageSegments = Array.Empty<BackupUsageSegment>();
            BackupUsageSeries   = Array.Empty<ISeries>();
            BackupUsageXAxes    = Array.Empty<Axis>();
            BackupUsageYAxes    = Array.Empty<Axis>();

            OnPropertyChanged(nameof(BackupUsageSegments));
            OnPropertyChanged(nameof(BackupUsageSeries));
            OnPropertyChanged(nameof(BackupUsageXAxes));
            OnPropertyChanged(nameof(BackupUsageYAxes));
            return;
        }

        var vaultSyncBytes = perProject?.Sum(p => p.bytes) ?? 0L;

        if (OperatingSystem.IsMacOS() && IsNetworkPath(backupRoot))
        {
            if (!TryResolveMountedSharePath(backupRoot, out var mountedRoot))
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

        if (!TryGetDiskSpace(backupRoot, out var totalBytes, out var freeBytes) || totalBytes <= 0)
        {
            BuildBackupUsageBarFromVaultSync(perProject, vaultSyncBytes);
            return;
        }

        var usedBytes  = Math.Max(0L, totalBytes - freeBytes);

        if (totalBytes <= 0)
        {
            BackupUsageSegments = Array.Empty<BackupUsageSegment>();
            BackupUsageSeries   = Array.Empty<ISeries>();
            BackupUsageXAxes    = Array.Empty<Axis>();
            BackupUsageYAxes    = Array.Empty<Axis>();

            OnPropertyChanged(nameof(BackupUsageSegments));
            OnPropertyChanged(nameof(BackupUsageSeries));
            OnPropertyChanged(nameof(BackupUsageXAxes));
            OnPropertyChanged(nameof(BackupUsageYAxes));
            return;
        }

        // Sum of the latest snapshot sizes per project (VaultSync usage approximation).
        if (vaultSyncBytes < 0) vaultSyncBytes = 0;

        // Percentages of the total backup disk.
        var usedPercentTotal = usedBytes        * 100d / totalBytes;
        var vaultSyncPercent = vaultSyncBytes   * 100d / totalBytes;
        var otherPercent     = Math.Max(0d, usedPercentTotal - vaultSyncPercent);

        var segments = new List<BackupUsageSegment>();

        // Palette for project colors.
        var projectPalette = new[]
        {
            Color.Parse("#4C8DFF"),
            Color.Parse("#FFB84C"),
            Color.Parse("#22CC88"),
            Color.Parse("#FF6B6B"),
            Color.Parse("#9B6BFF")
        };

        // 1) Other segment (non-VaultSync usage on the backup drive).
        // This is both in the legend and in the overlay bar.
        if (otherPercent > 0)
        {
            segments.Add(new BackupUsageSegment(
                L("Dashboard.Storage.Other", "Other"),
                otherPercent,
                new ImmutableSolidColorBrush(Color.Parse("#8E8E93"))));
        }

        // 2) One segment per project for its latest snapshot size, as percent of total disk.
        if (perProject != null)
        {
            var index = 0;
            foreach (var (project, bytes) in perProject)
            {
                var projectPercent = bytes * 100d / totalBytes;
                if (projectPercent <= 0) continue;

                var color = projectPalette[index % projectPalette.Length];
                index++;

                segments.Add(new BackupUsageSegment(
                    project.Name,
                    projectPercent,
                    new ImmutableSolidColorBrush(color)));
            }
        }

        BackupUsageSegments = segments;
        OnPropertyChanged(nameof(HasBackupUsageSegments));

        // Build stacked RowSeries for the colored bar (Other + VaultSync projects).
        if (segments.Count == 0)
        {
            BackupUsageSeries = Array.Empty<ISeries>();
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

            OnPropertyChanged(nameof(BackupUsageSegments));
            OnPropertyChanged(nameof(HasBackupUsageSegments));
            OnPropertyChanged(nameof(BackupUsageSeries));
            OnPropertyChanged(nameof(BackupUsageXAxes));
            OnPropertyChanged(nameof(BackupUsageYAxes));
            return;
        }

        var totalShown = segments.Sum(s => s.SizeBytes);
        if (totalShown <= 0)
        {
            BackupUsageSeries = Array.Empty<ISeries>();
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

            OnPropertyChanged(nameof(BackupUsageSegments));
            OnPropertyChanged(nameof(HasBackupUsageSegments));
            OnPropertyChanged(nameof(BackupUsageSeries));
            OnPropertyChanged(nameof(BackupUsageXAxes));
            OnPropertyChanged(nameof(BackupUsageYAxes));
            return;
        }

        var series = new List<ISeries>();
        foreach (var seg in segments)
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

        BackupUsageSeries = series.ToArray();

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

        OnPropertyChanged(nameof(BackupUsageSegments));
        OnPropertyChanged(nameof(HasBackupUsageSegments));
        OnPropertyChanged(nameof(BackupUsageSeries));
        OnPropertyChanged(nameof(BackupUsageXAxes));
        OnPropertyChanged(nameof(BackupUsageYAxes));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Dashboard] Backup usage bar failed: {ex.Message}");

        BackupUsageSegments = Array.Empty<BackupUsageSegment>();
        BackupUsageSeries   = Array.Empty<ISeries>();
        BackupUsageXAxes    = Array.Empty<Axis>();
        BackupUsageYAxes    = Array.Empty<Axis>();

        OnPropertyChanged(nameof(BackupUsageSegments));
        OnPropertyChanged(nameof(BackupUsageSeries));
        OnPropertyChanged(nameof(BackupUsageXAxes));
        OnPropertyChanged(nameof(BackupUsageYAxes));
    }
}

        private void BuildBackupUsageBarFromVaultSync(IReadOnlyList<(Project project, long bytes)> perProject, long vaultSyncBytes)
        {
            if (vaultSyncBytes <= 0 || perProject == null || perProject.Count == 0)
            {
                BackupUsageSegments = Array.Empty<BackupUsageSegment>();
                BackupUsageSeries   = Array.Empty<ISeries>();
                BackupUsageXAxes    = Array.Empty<Axis>();
                BackupUsageYAxes    = Array.Empty<Axis>();

                OnPropertyChanged(nameof(BackupUsageSegments));
                OnPropertyChanged(nameof(BackupUsageSeries));
                OnPropertyChanged(nameof(BackupUsageXAxes));
                OnPropertyChanged(nameof(BackupUsageYAxes));
                return;
            }

            var segments = new List<BackupUsageSegment>();
            var projectPalette = new[]
            {
                Color.Parse("#4C8DFF"),
                Color.Parse("#FFB84C"),
                Color.Parse("#22CC88"),
                Color.Parse("#FF6B6B"),
                Color.Parse("#9B6BFF")
            };

            var index = 0;
            foreach (var (project, bytes) in perProject)
            {
                if (bytes <= 0) continue;

                var percent = bytes * 100d / vaultSyncBytes;
                if (percent <= 0) continue;

                var color = projectPalette[index % projectPalette.Length];
                index++;

                segments.Add(new BackupUsageSegment(
                    project.Name,
                    percent,
                    new ImmutableSolidColorBrush(color)));
            }

            BackupUsageSegments = segments;
            OnPropertyChanged(nameof(HasBackupUsageSegments));

            if (segments.Count == 0)
            {
                BackupUsageSeries = Array.Empty<ISeries>();
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

                OnPropertyChanged(nameof(BackupUsageSegments));
                OnPropertyChanged(nameof(HasBackupUsageSegments));
                OnPropertyChanged(nameof(BackupUsageSeries));
                OnPropertyChanged(nameof(BackupUsageXAxes));
                OnPropertyChanged(nameof(BackupUsageYAxes));
                return;
            }

            var series = new List<ISeries>();
            foreach (var seg in segments)
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

            BackupUsageSeries = series.ToArray();

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

            OnPropertyChanged(nameof(BackupUsageSegments));
            OnPropertyChanged(nameof(HasBackupUsageSegments));
            OnPropertyChanged(nameof(BackupUsageSeries));
            OnPropertyChanged(nameof(BackupUsageXAxes));
            OnPropertyChanged(nameof(BackupUsageYAxes));
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
        public static (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold, BackupDiskUsageStatus status)
            ComputeBackupDiskUsageDetailed(AppConfig config)
        {
            try
            {
                var thresholdText = string.Format(
                    L("Dashboard.Storage.Threshold", "Keep at least {0}% free space"),
                    config.Storage.MinFreeSpacePercent);

                // Use the backup root from config; if not configured, show a hint.
                var backupRoot = config.Backups.BackupLocation;
                if (string.IsNullOrWhiteSpace(backupRoot))
                {
                    return (
                        0d,
                        L("Dashboard.Storage.NotConfigured", "Backup root not configured"),
                        thresholdText,
                        false,
                        BackupDiskUsageStatus.NotConfigured
                    );
                }

                if (OperatingSystem.IsMacOS() && IsNetworkPath(backupRoot))
                {
                    if (!TryResolveMountedSharePath(backupRoot, out var mountedRoot))
                    {
                        return (
                            0d,
                            L("Dashboard.Storage.TargetUnavailable", "Backup target not available"),
                            thresholdText,
                            false,
                            BackupDiskUsageStatus.TargetUnavailable
                        );
                    }

                    backupRoot = mountedRoot;
                }
                else
                {
                    backupRoot = Path.GetFullPath(backupRoot);
                }

                if (!TryGetDiskSpace(backupRoot, out var total, out var free))
                {
                    return (
                        0d,
                        L("Dashboard.Storage.TargetUnavailable", "Backup target not available"),
                        thresholdText,
                        false,
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
                        BackupDiskUsageStatus.SizeUnknown
                    );
                }

                var used        = total - free;
                var usedPercent = (double)used / total * 100d;
                var freePercent = (double)free / total * 100d;

                var freeText = string.Format(
                    L("Dashboard.Storage.FreeText", "Free {0} of {1} ({2}%)"),
                    FormatBytes(free),
                    FormatBytes(total),
                    freePercent.ToString("0.#"));
                var isBelowThreshold = freePercent < config.Storage.MinFreeSpacePercent;

                return (usedPercent, freeText, thresholdText, isBelowThreshold, BackupDiskUsageStatus.Ok);
            }
            catch (Exception)
            {
                return (
                    0d,
                    L("Dashboard.Storage.UsageUnavailable", "Backup storage usage unavailable"),
                    string.Empty,
                    false,
                    BackupDiskUsageStatus.Error
                );
            }
        }

        public static (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold)
            ComputeBackupDiskUsage(AppConfig config)
        {
            var (usedPercent, freeText, thresholdText, isBelowThreshold, _) = ComputeBackupDiskUsageDetailed(config);
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

            var normalized = Path.GetFullPath(fullPath);

            if (OperatingSystem.IsWindows())
            {
                return Path.GetPathRoot(normalized) ?? string.Empty;
            }

            // macOS external disks live under /Volumes/<Name>; keep the mount path.
            if (normalized.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
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

            if (!TryParseShareWithSubpath(originalPath, out var host, out var share, out var subPath))
                return false;

            var mountPoint = TryGetMountedSharePath(host, share);
            if (!string.IsNullOrWhiteSpace(mountPoint))
            {
                mountedPath = AppendShareSubPath(mountPoint, subPath);
                return true;
            }

            var mountRoot = GetMacMountRoot();
            if (TryFindMountByName(share, mountRoot, out var rootMatch))
            {
                mountedPath = AppendShareSubPath(rootMatch, subPath);
                return true;
            }

            if (TryFindMountByName(share, "/Volumes", out var volumesMatch))
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
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
                    return false;

                host = uri.Host;
                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
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
                var trimmed = path.TrimStart('\\', '/').Replace('\\', FormatSeparator);
                var parts = trimmed.Split(FormatSeparator, StringSplitOptions.RemoveEmptyEntries);
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

            var cleaned = subPath.Trim().TrimStart('/', '\\');
            if (string.IsNullOrWhiteSpace(cleaned))
                return mountPoint;

            var segments = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 0
                ? mountPoint
                : Path.Combine(new[] { mountPoint }.Concat(segments).ToArray());
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
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
                    return false;

                host = uri.Host;
                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    return false;

                share = segments[0];
                return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(share);
            }

            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = path.TrimStart('\\', '/').Replace('\\', '/');
                var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
                var output = proc.StandardOutput.ReadToEnd();
                if (string.IsNullOrWhiteSpace(output))
                    return string.Empty;

                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in lines)
                {
                    if (!line.Contains("smbfs", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var onIndex = line.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                    if (onIndex <= 0)
                        continue;

                    var source = line.Substring(0, onIndex).Trim();
                    var rest = line.Substring(onIndex + 4);
                    var mountPoint = rest.Split(" (", StringSplitOptions.None)[0].Trim();
                    if (string.IsNullOrWhiteSpace(mountPoint))
                        continue;

                    if (!TryParseShare(source, out var mountedHost, out var mountedShare))
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

                var exact = Path.Combine(rootPath, share);
                if (Directory.Exists(exact))
                {
                    mountedPath = exact;
                    return true;
                }

                var match = Directory.EnumerateDirectories(rootPath)
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
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

                var fullPath = Path.GetFullPath(path);

                if (OperatingSystem.IsWindows())
                {
                    if (!GetDiskFreeSpaceEx(
                            fullPath,
                            out var freeBytesAvailable,
                            out var totalNumberOfBytes,
                            out _))
                    {
                        return false;
                    }

                    totalBytes = (long)totalNumberOfBytes;
                    freeBytes  = (long)freeBytesAvailable;
                    return totalBytes > 0;
                }

                var drivePath = GetDriveInfoPath(fullPath);
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

        private void UpdateBackupDiskUsage(AppConfig config)
        {
            var (usedPercent, freeText, thresholdText, isBelowThreshold) =
                ComputeBackupDiskUsage(config);

            BackupDiskUsedPercent      = usedPercent;
            BackupDiskFreeText         = freeText;
            BackupDiskThresholdText    = thresholdText;
            BackupDiskIsBelowThreshold = isBelowThreshold;
        }

        private static double[] MovingAverage(IReadOnlyList<double> v, int window)
        {
            if (window <= 1) return v.ToArray();
            var r = new double[v.Count];
            for (var i = 0; i < v.Count; i++)
            {
                var start = Math.Max(0, i - (window - 1));
                var count = i - start + 1;
                double sum = 0;
                for (var j = start; j <= i; j++) sum += v[j];
                r[i] = sum / count;
            }
            return r;
        }

        private static string L(string key, string fallback)
        {
            var loc = LocalizationProvider.Service;
            var value = loc?.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string Lf(string key, string fallback, params object[] args)
        {
            return string.Format(L(key, fallback), args);
        }

        public void ReapplyLocalization()
        {
            SnapshotsHint = string.Format(L("Dashboard.Hint.SnapshotsThisWeek", "{0} this week"), _backupsThisWeekCount);

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

            OnPropertyChanged(nameof(TotalSnapshotsWeekLabel));
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            var order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.#} {sizes[order]}";
        }

        private static string GetDefaultDbPath()
        {
            return AppConfigStore.GetDefaultDbPath();
        }

        // Bindables
        public record LegendItem(string Label, IBrush Brush);
        public record BackupUsageSegment(string Label, double SizeBytes, IBrush Brush);

        public enum Dot { Green, Blue, Purple, Gray }

        public class ActivityItem
        {
            private static readonly IBrush DotGreenBrush = new ImmutableSolidColorBrush(Color.Parse("#2ECC71"));
            private static readonly IBrush DotBlueBrush = new ImmutableSolidColorBrush(Color.Parse("#1ABCFE"));
            private static readonly IBrush DotPurpleBrush = new ImmutableSolidColorBrush(Color.Parse("#8E77FF"));
            private static readonly IBrush DotGrayBrush = new ImmutableSolidColorBrush(Colors.Gray);

            // New constructor: allow passing an explicit brush (used when we want per-project colors).
            public ActivityItem(string title, string subtitle, string when, IBrush dotBrush)
            {
                Title    = title;
                Subtitle = subtitle;
                When     = when;
                DotBrush = dotBrush;
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
                    })
            {
            }

            public string Title { get; }
            public string Subtitle { get; }
            public string When { get; }
            public IBrush DotBrush { get; }
        }
    }
}
