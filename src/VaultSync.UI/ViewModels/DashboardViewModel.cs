using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Avalonia.Media; // for Brush in legend + activity
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

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
            new ActivityItem("Daily snapshot", "Completed", "Just now", Dot.Green),
            new ActivityItem("Manual sync", "Finished successfully", "Earlier today", Dot.Blue),
            new ActivityItem("Backup validation", "No issues found", "This week", Dot.Purple)
        };

        // Internal data for chart aggregation
        private readonly string[] _days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
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

                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : GetDefaultDbPath();

                var repo = new SqliteRepository(dbPath);
                repo.EnsureSchema();

                var projects = repo.GetAllProjects().ToList();
                var allSnapshots = repo.GetAllSnapshots().ToList();

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

                // Activity: rebuild from latest snapshots (keep top 5 newest)
                ActivityItems.Clear();
                foreach (var s in allSnapshots
                             .OrderByDescending(s => s.CreatedUtc)
                             .Take(5))
                {
                    var project = projects.FirstOrDefault(p => p.Id == s.ProjectId);
                    var title = project != null ? project.Name : "Unknown project";
                    var when = s.CreatedUtc.ToLocalTime().ToString("g");
                    ActivityItems.Add(new ActivityItem(title, "Snapshot completed", when, Dot.Blue));
                }

                // Snapshot chart: counts per day for last 7 days
                Array.Clear(_snapshotCountsByDay, 0, _snapshotCountsByDay.Length);
                foreach (var s in snapshotsThisWeek)
                {
                    var dayIndex = (int)((DateTime.UtcNow.Date - s.CreatedUtc.Date).TotalDays);
                    var idx = 6 - Math.Clamp(dayIndex, 0, 6); // put oldest at left, newest at right
                    if (idx >= 0 && idx < _snapshotCountsByDay.Length)
                        _snapshotCountsByDay[idx]++;
                }

                BuildSnapshotSeries();
                BuildStorageDonut(storageSlices);

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
            var accent        = SKColor.Parse("#22CCFF");
            var accentFillTop = new SKColor(0x22, 0xCC, 0xFF, 64);
            var accentFillBot = new SKColor(0x22, 0xCC, 0xFF, 12);
            var avgStroke     = new SKColor(255, 255, 255, 110);

            var line = new LineSeries<double>
            {
                Values         = _snapshotCountsByDay,
                LineSmoothness = 1,
                Stroke         = new SolidColorPaint(accent) { StrokeThickness = 3 },
                GeometrySize   = 8,
                GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 2 },
                Fill           = new LinearGradientPaint(new[] { accentFillTop, accentFillBot },
                                                         new SKPoint(0, 0), new SKPoint(0, 1))
            };

            var avgValues = MovingAverage(_snapshotCountsByDay, 3);
            var avg = new LineSeries<double>
            {
                Values         = avgValues,
                LineSmoothness = 1,
                GeometrySize   = 0,
                Fill           = null,
                Stroke         = new SolidColorPaint(avgStroke) { StrokeThickness = 2 }
            };

            SnapshotSeries = new ISeries[] { avg, line };
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
            OnPropertyChanged(nameof(TotalSnapshotsWeek));
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

        public enum Dot { Green, Blue, Purple }

        public class ActivityItem
        {
            public ActivityItem(string title, string subtitle, string when, Dot dot)
            {
                Title = title;
                Subtitle = subtitle;
                When = when;
                DotBrush = dot switch
                {
                    Dot.Green  => new SolidColorBrush(Color.Parse("#2ECC71")),
                    Dot.Blue   => new SolidColorBrush(Color.Parse("#1ABCFE")),
                    Dot.Purple => new SolidColorBrush(Color.Parse("#8E77FF")),
                    _          => new SolidColorBrush(Colors.Gray)
                };
            }

            public string Title { get; }
            public string Subtitle { get; }
            public string When { get; }
            public Brush DotBrush { get; }
        }
    }
}