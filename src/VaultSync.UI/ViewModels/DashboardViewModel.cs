using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Avalonia.Media; // for Brush in legend + activity
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.UI.Infrastructure; // for RelayCommand

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
        public string TotalSnapshotsWeek => _snapshotCountsByDay.Sum().ToString();

        // Donut bindings
        public ISeries[] StorageSeries { get; private set; } = Array.Empty<ISeries>();
        public IEnumerable<LegendItem> StorageLegend { get; private set; } = Array.Empty<LegendItem>();

        // Activity (for now, keep simple demo items)
        public ObservableCollection<ActivityItem> ActivityItems { get; } = new()
        {
            new ActivityItem("Daily backup", "Completed", "Just now", Dot.Green),
            new ActivityItem("Manual backup", "Finished successfully", "Earlier today", Dot.Blue),
            new ActivityItem("Backup validation", "No issues found", "This week", Dot.Purple)
        };

        // Internal data for chart aggregation
        private string[] _days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        private readonly double[] _snapshotCountsByDay = new double[7];

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
            try
            {
                var config = AppConfigStore.Load();
                UpdateBackupDiskUsage(config);

                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : GetDefaultDbPath();

                var repo = new SqliteRepository(dbPath);
                repo.EnsureSchema();

                var projects = repo.GetAllProjects().ToList();
                var allSnapshots = repo.GetAllSnapshots().ToList();
                var allBackups = repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();

                // KPIs
                ProjectCount = projects.Count;
                SnapshotCount = allSnapshots.Count;

                var weekAgoUtc = DateTime.UtcNow.AddDays(-7);
                var snapshotsThisWeek = allSnapshots.Where(s => s.CreatedUtc >= weekAgoUtc).ToList();
                SnapshotsHint = $"{snapshotsThisWeek.Count} this week";

                // Storage: sum latest snapshot per project and capture per-project slices.
                long totalLatestBytes = 0;
                var storageSlices = new List<(Project project, long bytes)>();

                foreach (var p in projects)
                {
                    var latest = repo.GetLatestSnapshot(p.Id);
                    if (latest != null)
                    {
                        totalLatestBytes += latest.TotalBytes;
                        storageSlices.Add((p, latest.TotalBytes));
                    }
                }

                StorageUsed = FormatBytes(totalLatestBytes);

                var activeProjects = storageSlices.Count;
                if (projects.Count == 0)
                {
                    ProjectsHint = "No projects yet";
                }
                else if (activeProjects == 0)
                {
                    ProjectsHint = "No snapshots yet";
                }
                else
                {
                    ProjectsHint = activeProjects == 1
                        ? "1 active project"
                        : $"{activeProjects} active projects";
                }

                StorageHint = "Total across latest snapshots";

                // Activity: rebuild from latest backups and snapshots (keep top 5 newest)
                ActivityItems.Clear();

                // Build a unified activity list:
                //  - one entry per backup (auto/manual backup created)
                //  - one entry per snapshot that does NOT have a backup row
                var activities = new List<(int? ProjectId, DateTime WhenUtc, string Subtitle)>();

                // 1) Backups
                foreach (var b in allBackups)
                {
                    var subtitle = string.Equals(b.Type, "auto", StringComparison.OrdinalIgnoreCase)
                        ? "Auto backup created"
                        : "Manual backup created";

                    activities.Add((b.ProjectId, b.CreatedUtc, subtitle));
                }

                // 2) Snapshots without any backup record ("snapshot-only" events)
                var snapshotIdsWithBackup = new HashSet<int>(allBackups.Select(x => x.SnapshotId));
                foreach (var s in allSnapshots)
                {
                    if (snapshotIdsWithBackup.Contains(s.Id))
                        continue; // this snapshot already has a backup entry

                    activities.Add((s.ProjectId, s.CreatedUtc, "Snapshot created"));
                }

                // Now render the 5 most recent activities, newest first.
                var projectPalette = new[]
                {
                    Color.Parse("#4C8DFF"),
                    Color.Parse("#22CC88"),
                    Color.Parse("#FFB84C"),
                    Color.Parse("#FF6B6B"),
                    Color.Parse("#9B6BFF")
                };

                var projectDotBrushes = new Dictionary<int, Brush>();
                var paletteIndex = 0;

                foreach (var a in activities
                             .OrderByDescending(a => a.WhenUtc)
                             .Take(5))
                {
                    Project? project = null;
                    if (a.ProjectId.HasValue)
                    {
                        project = projects.FirstOrDefault(p => p.Id == a.ProjectId.Value);
                    }

                    var title = project != null ? project.Name : "Unknown project";
                    var when = a.WhenUtc.ToLocalTime().ToString("g");

                    Brush dotBrush;
                    if (project != null)
                    {
                        if (projectDotBrushes.TryGetValue(project.Id, out var cached))
                        {
                            dotBrush = cached ?? new SolidColorBrush(Colors.Gray);
                        }
                        else
                        {
                            var color = projectPalette[paletteIndex % projectPalette.Length];
                            dotBrush = new SolidColorBrush(color);
                            projectDotBrushes[project.Id] = dotBrush;
                            paletteIndex++;
                        }
                    }
                    else
                    {
                        dotBrush = new SolidColorBrush(Colors.Gray);
                    }

                    ActivityItems.Add(new ActivityItem(title, a.Subtitle, when, dotBrush));
                }

                // Backup chart: backups per day for the last 7 days (oldest on the left, today on the right).
                var startDate = DateTime.UtcNow.Date.AddDays(-6); // 7 days inclusive
                for (var i = 0; i < _days.Length; i++)
                {
                    var d = startDate.AddDays(i);
                    _days[i] = d.ToString("ddd");
                }

                Array.Clear(_snapshotCountsByDay, 0, _snapshotCountsByDay.Length);
                foreach (var s in snapshotsThisWeek)
                {
                    var dayIndex = (int)(s.CreatedUtc.Date - startDate).TotalDays;
                    if (dayIndex < 0 || dayIndex >= _snapshotCountsByDay.Length)
                        continue;

                    _snapshotCountsByDay[dayIndex]++;
                }

                BuildSnapshotSeries();
                BuildStorageDonut(storageSlices);
                BuildBackupUsageBar(config, storageSlices);

                OnPropertyChanged(nameof(TotalSnapshotsWeek));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DashboardViewModel] Refresh failed, falling back to demo data: " + ex);
                BuildDemoSeriesIfNeeded();
            }
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

                series.Add(new PieSeries<double>
                {
                    Values      = new[] { (double)bytes },
                    Name        = project.Name,
                    InnerRadius = 90,
                    Stroke      = null,
                    Fill        = new SolidColorPaint(color)
                });

                legend.Add(new LegendItem(
                    $"{project.Name} {FormatBytes(bytes)}",
                    new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue))));
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

            // Fallback demo data if DB is empty or unavailable.
            for (int i = 0; i < _snapshotCountsByDay.Length; i++)
                _snapshotCountsByDay[i] = new[] { 1d, 3d, 2d, 5d, 4d, 6d, 2d }[i];

            ProjectCount   = 0;
            ProjectsHint   = "No projects yet";
            SnapshotCount  = 0;
            SnapshotsHint  = "No snapshots yet";
            StorageUsed    = "0 B";
            StorageHint    = "No storage used";

            BuildSnapshotSeries();
            BuildStorageDonut(Array.Empty<(Project project, long bytes)>());
            BuildBackupUsageBar(AppConfigStore.Load(), Array.Empty<(Project project, long bytes)>());
            OnPropertyChanged(nameof(TotalSnapshotsWeek));
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

        backupRoot = Path.GetFullPath(backupRoot);

        // Always reduce to a drive/root so DriveInfo is happy (especially on Windows).
        var driveRoot = Path.GetPathRoot(backupRoot);
        if (string.IsNullOrWhiteSpace(driveRoot))
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

        // On Windows this will be e.g. "C:\" or "V:\"
        // On macOS this will be "/" (root volume), which still maps to the correct disk.
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady || drive.TotalSize <= 0)
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

        var totalBytes = drive.TotalSize;
        var freeBytes  = drive.AvailableFreeSpace;
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
        var vaultSyncBytes = perProject?.Sum(p => p.bytes) ?? 0L;
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
                "Other",
                otherPercent,
                new SolidColorBrush(Color.Parse("#8E8E93"))));
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
                    new SolidColorBrush(color)));
            }
        }

        BackupUsageSegments = segments;

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

            if (seg.Brush is not SolidColorBrush solid)
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
        OnPropertyChanged(nameof(BackupUsageSeries));
        OnPropertyChanged(nameof(BackupUsageXAxes));
        OnPropertyChanged(nameof(BackupUsageYAxes));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DashboardViewModel] Failed to build backup usage bar: {ex}");

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


        /// <summary>
        /// Computes backup disk usage based on the current app config.
        /// Returns a tuple that can be reused by other view models (e.g. BackupsViewModel).
        /// </summary>
        public static (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold)
            ComputeBackupDiskUsage(AppConfig config)
        {
            try
            {
                // Use the backup root from config; if not configured, show a hint.
                var backupRoot = config.Backups.BackupLocation;
                if (string.IsNullOrWhiteSpace(backupRoot))
                {
                    return (
                        0d,
                        "Backup root not configured",
                        $"Reserve at least {config.Storage.MinFreeSpacePercent}% free space",
                        false
                    );
                }

                backupRoot = Path.GetFullPath(backupRoot);
                var driveRoot = Path.GetPathRoot(backupRoot);
                if (string.IsNullOrWhiteSpace(driveRoot))
                {
                    return (
                        0d,
                        "Backup target not available",
                        $"Reserve at least {config.Storage.MinFreeSpacePercent}% free space",
                        false
                    );
                }

                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady)
                {
                    return (
                        0d,
                        "Backup target not available",
                        $"Reserve at least {config.Storage.MinFreeSpacePercent}% free space",
                        false
                    );
                }

                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;

                if (total <= 0)
                {
                    return (
                        0d,
                        "Backup target size unknown",
                        $"Reserve at least {config.Storage.MinFreeSpacePercent}% free space",
                        false
                    );
                }

                var used        = total - free;
                var usedPercent = (double)used / total * 100d;
                var freePercent = (double)free / total * 100d;

                var freeText = $"Free {FormatBytes(free)} of {FormatBytes(total)} ({freePercent:0.#}%)";
                var threshold = config.Storage.MinFreeSpacePercent;
                var thresholdText = $"Reserve at least {threshold}% free space";
                var isBelowThreshold = freePercent < threshold;

                return (usedPercent, freeText, thresholdText, isBelowThreshold);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DashboardViewModel] Failed to compute backup disk usage: {ex}");
                return (
                    0d,
                    "Backup storage usage unavailable",
                    string.Empty,
                    false
                );
            }
        }

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
        public record LegendItem(string Label, Brush Brush);
        public record BackupUsageSegment(string Label, double SizeBytes, Brush Brush);

        public enum Dot { Green, Blue, Purple, Gray }

        public class ActivityItem
        {
            // New constructor: allow passing an explicit brush (used when we want per-project colors).
            public ActivityItem(string title, string subtitle, string when, Brush dotBrush)
            {
                Title = title;
                Subtitle = subtitle;
                When = when;
                DotBrush = dotBrush;
            }

            // Backwards-compatible constructor for simple fixed dots.
            public ActivityItem(string title, string subtitle, string when, Dot dot)
                : this(title, subtitle, when,
                    dot switch
                    {
                        Dot.Green  => new SolidColorBrush(Color.Parse("#2ECC71")),
                        Dot.Blue   => new SolidColorBrush(Color.Parse("#1ABCFE")),
                        Dot.Purple => new SolidColorBrush(Color.Parse("#8E77FF")),
                        Dot.Gray   => new SolidColorBrush(Colors.Gray),
                        _          => new SolidColorBrush(Colors.Gray)
                    })
            {
            }

            public string Title { get; }
            public string Subtitle { get; }
            public string When { get; }
            public Brush DotBrush { get; }
        }
    }
}
