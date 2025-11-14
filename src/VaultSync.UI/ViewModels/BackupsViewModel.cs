using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;

namespace VaultSync.UI.ViewModels
{
    public class BackupsViewModel : ViewModelBase
    {
        // Simple SetProperty helper – note: no PropertyChanged here, we just need
        // equality checks + storage for our internal properties.
        protected bool SetProperty<T>(ref T storage, T value)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            return true;
        }

        // Filtered view for the history list
        public ObservableCollection<BackupSnapshotItem> Snapshots { get; } =
            new ObservableCollection<BackupSnapshotItem>();

        // Internal full list for summary + filtering
        private readonly List<BackupSnapshotItem> _allSnapshots = new();

        private BackupSnapshotItem? _selectedSnapshotA;
        public BackupSnapshotItem? SelectedSnapshotA
        {
            get => _selectedSnapshotA;
            set => SetProperty(ref _selectedSnapshotA, value);
        }

        private BackupSnapshotItem? _selectedSnapshotB;
        public BackupSnapshotItem? SelectedSnapshotB
        {
            get => _selectedSnapshotB;
            set => SetProperty(ref _selectedSnapshotB, value);
        }

        // Type + project filter state
        private string _currentTypeFilter = "All";
        private string? _currentProjectIdFilter = null;
        public string HistoryFilterProjectLabel { get; private set; } = "All projects";

        // Per-project backup status
        public ObservableCollection<ProjectBackupItem> ProjectBackups { get; } =
            new ObservableCollection<ProjectBackupItem>();

        // Currently selected project in the per-project list
        private ProjectBackupItem? _selectedProject;
        public ProjectBackupItem? SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (SetProperty(ref _selectedProject, value))
                {
                    OnSelectedProjectChanged(value);
                }
            }
        }

        // Weekly mini-chart data
        public ObservableCollection<SnapshotActivityPoint> SnapshotActivity { get; } =
            new ObservableCollection<SnapshotActivityPoint>();

        // Summary properties (bound in the top cards)
        public int TotalSnapshots { get; private set; }
        public int SnapshotsThisWeek { get; private set; }
        public int SnapshotsToday { get; private set; }

        public int AutoSnapshotsThisWeek { get; private set; }
        public int ManualSnapshotsThisWeek { get; private set; }

        public string SnapshotsSummaryLine { get; private set; } = "0 today · 0 this week";
        public string SnapshotActivitySummary { get; private set; } = "No snapshots in the last 7 days";

        public string LastBackupDisplay { get; private set; } = "No backups yet";
        public string LastBackupRelative { get; private set; } = "—";
        public string TotalBackupSizeFormatted { get; private set; } = "0 B";

        // Commands
        public ICommand CreateBackupCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand DeleteBackupCommand { get; }

        public ICommand BackupProjectCommand { get; }
        public ICommand ShowProjectHistoryCommand { get; }
        public ICommand FilterSnapshotsCommand { get; }

        public BackupsViewModel()
        {
            // All-project backup
            CreateBackupCommand = new ActionCommand(_ => CreateBackupForAllProjects());

            // Global history actions
            RestoreBackupCommand = new ActionCommand(p => RestoreBackup(p as BackupSnapshotItem));
            DeleteBackupCommand  = new ActionCommand(p => DeleteBackup(p as BackupSnapshotItem));

            // Per-project actions
            BackupProjectCommand      = new ActionCommand(p => BackupProject(p as ProjectBackupItem));
            ShowProjectHistoryCommand = new ActionCommand(p => ShowProjectHistory(p as ProjectBackupItem));

            // History type filter
            FilterSnapshotsCommand = new ActionCommand(p => ApplyTypeFilter(p as string));

            // Seed fake data for now
            LoadDesignTimeData();

            // Initial view
            RefreshSnapshotsView(true);
            RecalculateSummary();
        }

        // ---------- All-project backup ----------

        private void CreateBackupForAllProjects()
        {
            // TODO: hook into real backup engine.

            var snapshot = new BackupSnapshotItem
            {
                Id        = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.Now,
                SizeBytes = 750L * 1024 * 1024, // 750 MB demo
                Type      = "Manual",
                Status    = "Completed",
                Label     = "All projects snapshot",
                ProjectId = null
            };

            AddSnapshot(snapshot);

            foreach (var project in ProjectBackups)
            {
                project.LastBackupTime = DateTime.Now;
                project.SnapshotCount += 1;
                project.TotalSizeBytes += 250L * 1024 * 1024; // fake increment
            }
        }

        // ---------- Global history operations ----------

        private void RestoreBackup(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            // TODO: implement restore logic.
        }

        private void DeleteBackup(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            _allSnapshots.Remove(snapshot);
            RefreshSnapshotsView(false);
            RecalculateSummary();
        }

        // ---------- Per-project operations ----------

        private void BackupProject(ProjectBackupItem? project)
        {
            if (project is null)
                return;

            // TODO: real backup engine call per project.
            project.LastBackupTime = DateTime.Now;
            project.SnapshotCount += 1;
            project.TotalSizeBytes += 300L * 1024 * 1024; // fake increment

            var snapshot = new BackupSnapshotItem
            {
                Id        = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.Now,
                SizeBytes = 300L * 1024 * 1024,
                Type      = "Manual",
                Status    = "Completed",
                Label     = $"{project.Name} snapshot",
                ProjectId = project.Id
            };

            AddSnapshot(snapshot);
        }

        private void ShowProjectHistory(ProjectBackupItem? project)
        {
            // Reuse the same logic as list selection
            SelectedProject = project;
        }

        /// <summary>
        /// Called whenever the selected project in the per-project list changes.
        /// Updates the current project filter + label and refreshes the history.
        /// </summary>
        private void OnSelectedProjectChanged(ProjectBackupItem? project)
        {
            if (project is null)
            {
                _currentProjectIdFilter   = null;
                HistoryFilterProjectLabel = "All projects";
                RefreshSnapshotsView(true);
                return;
            }

            _currentProjectIdFilter   = project.Id;
            HistoryFilterProjectLabel = project.Name;
            RefreshSnapshotsView(true);
        }

        // ---------- Snapshot management + filtering ----------

        private void AddSnapshot(BackupSnapshotItem snapshot)
        {
            _allSnapshots.Add(snapshot);
            RefreshSnapshotsView(false);
            RecalculateSummary();
        }

        private void ApplyTypeFilter(string? type)
        {
            if (string.IsNullOrWhiteSpace(type) || type == "All")
            {
                // Reset to "All" types but keep the current project context.
                _currentTypeFilter = "All";

                // Only reset the label to "All projects" if we are not scoped to a project.
                if (string.IsNullOrWhiteSpace(_currentProjectIdFilter))
                    HistoryFilterProjectLabel = "All projects";
            }
            else
            {
                // "Auto" or "Manual" while keeping the current project filter (if any).
                _currentTypeFilter = type;
            }

            RefreshSnapshotsView(false);
        }

        private void ReplaceSnapshots(IEnumerable<BackupSnapshotItem> newSnapshots, bool forceResetCompare = false)
        {
            string? keepAId = forceResetCompare ? null : SelectedSnapshotA?.Id;
            string? keepBId = forceResetCompare ? null : SelectedSnapshotB?.Id;

            var ordered = newSnapshots
                .OrderByDescending(s => s.Timestamp)
                .ToList();

            Snapshots.Clear();
            foreach (var s in ordered)
                Snapshots.Add(s);

            // If we are not forcing a reset, try to restore previous selection by Id.
            if (!forceResetCompare)
            {
                if (keepAId != null)
                    SelectedSnapshotA = ordered.FirstOrDefault(s => s.Id == keepAId);

                if (keepBId != null)
                    SelectedSnapshotB = ordered.FirstOrDefault(s => s.Id == keepBId);
            }

            // If we still don't have a valid pair, default to newest + previous.
            if (SelectedSnapshotA == null || SelectedSnapshotB == null)
            {
                if (ordered.Count > 0)
                    SelectedSnapshotA ??= ordered[0];

                if (ordered.Count > 1)
                    SelectedSnapshotB ??= ordered[1];
                else if (ordered.Count == 1)
                    SelectedSnapshotB ??= ordered[0];
                else
                {
                    // No snapshots at all.
                    SelectedSnapshotA = null;
                    SelectedSnapshotB = null;
                }
            }
        }

        private void RefreshSnapshotsView(bool forceResetCompare = false)
        {
            IEnumerable<BackupSnapshotItem> source = _allSnapshots;

            // Type filter
            if (_currentTypeFilter == "Auto")
                source = source.Where(s => s.Type == "Auto");
            else if (_currentTypeFilter == "Manual")
                source = source.Where(s => s.Type == "Manual");

            // Project filter
            if (!string.IsNullOrWhiteSpace(_currentProjectIdFilter))
                source = source.Where(s => s.ProjectId == _currentProjectIdFilter);

            ReplaceSnapshots(source, forceResetCompare);
        }

        // ---------- Summary computation ----------

        private void RecalculateSummary()
        {
            var now       = DateTime.Now;
            var weekStart = now.Date.AddDays(-6);

            TotalSnapshots = _allSnapshots.Count;

            SnapshotsToday = _allSnapshots.Count(s => s.Timestamp.Date == now.Date);
            SnapshotsThisWeek = _allSnapshots.Count(s =>
                s.Timestamp.Date >= weekStart);

            AutoSnapshotsThisWeek = _allSnapshots.Count(s =>
                s.Timestamp.Date >= weekStart &&
                string.Equals(s.Type, "Auto", StringComparison.OrdinalIgnoreCase));

            ManualSnapshotsThisWeek = _allSnapshots.Count(s =>
                s.Timestamp.Date >= weekStart &&
                string.Equals(s.Type, "Manual", StringComparison.OrdinalIgnoreCase));

            SnapshotsSummaryLine = $"{SnapshotsToday} today · {SnapshotsThisWeek} this week";

            if (SnapshotsThisWeek == 0)
            {
                SnapshotActivitySummary = "No snapshots in the last 7 days";
            }
            else
            {
                SnapshotActivitySummary =
                    $"{SnapshotsThisWeek} total · {AutoSnapshotsThisWeek} auto · {ManualSnapshotsThisWeek} manual";
            }

            if (_allSnapshots.Count > 0)
            {
                var last = _allSnapshots
                    .OrderByDescending(s => s.Timestamp)
                    .First();

                LastBackupDisplay  = last.Timestamp.ToString("yyyy-MM-dd HH:mm");
                LastBackupRelative = FormatRelative(now - last.Timestamp);
            }
            else
            {
                LastBackupDisplay  = "No backups yet";
                LastBackupRelative = "—";
            }

            long totalBytes = _allSnapshots.Sum(s => s.SizeBytes);
            TotalBackupSizeFormatted = BackupSnapshotItem.FormatSize(totalBytes);

            RebuildSnapshotActivity(now);
        }

        private static string FormatRelative(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1))
                return "Just now";
            if (span < TimeSpan.FromHours(1))
                return $"{(int)span.TotalMinutes} min ago";
            if (span < TimeSpan.FromDays(1))
                return $"{(int)span.TotalHours} h ago";
            if (span < TimeSpan.FromDays(7))
                return $"{(int)span.TotalDays} days ago";

            return "Over a week ago";
        }

        // ---------- Weekly activity mini-chart ----------

        private void RebuildSnapshotActivity(DateTime now)
        {
            SnapshotActivity.Clear();

            // Last 7 days, oldest -> newest
            var days = Enumerable.Range(0, 7)
                .Select(offset => now.Date.AddDays(-6 + offset))
                .ToArray();

            var countsByDate = _allSnapshots
                .GroupBy(s => s.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.Count());

            int max = countsByDate.Values.DefaultIfEmpty(0).Max();
            if (max == 0)
                max = 1; // avoid divide-by-zero

            foreach (var day in days)
            {
                countsByDate.TryGetValue(day, out var count);

                double normalized = count / (double)max;
                // base height 4–8px plus up to ~40px for busy days
                double height = count == 0 ? 4 : 8 + normalized * 40;

                // Accent color, dim if no snapshots
                IBrush brush = count == 0
                    ? new SolidColorBrush(Color.Parse("#22FFFFFF"))
                    : new SolidColorBrush(Color.Parse("#3A7AFE"));

                SnapshotActivity.Add(new SnapshotActivityPoint
                {
                    DayLabel  = day.ToString("dd"),
                    Count     = count,
                    BarHeight = height,
                    BarBrush  = brush
                });
            }
        }

        private void LoadDesignTimeData()
        {
            // Per-project demo data
            if (ProjectBackups.Count == 0)
            {
                ProjectBackups.Add(new ProjectBackupItem
                {
                    Id             = "proj-1",
                    Name           = "Dumpster Fire Royale",
                    LastBackupTime = DateTime.Now.AddHours(-2),
                    SnapshotCount  = 5,
                    TotalSizeBytes = 5_000_000_000 // ~5 GB
                });

                ProjectBackups.Add(new ProjectBackupItem
                {
                    Id             = "proj-2",
                    Name           = "OverSteer",
                    LastBackupTime = DateTime.Now.AddDays(-1),
                    SnapshotCount  = 3,
                    TotalSizeBytes = 3_200_000_000 // ~3.2 GB
                });

                ProjectBackups.Add(new ProjectBackupItem
                {
                    Id             = "proj-3",
                    Name           = "VaultSync",
                    LastBackupTime = null,
                    SnapshotCount  = 0,
                    TotalSizeBytes = 0
                });
            }

            if (_allSnapshots.Count == 0)
            {
                var s1 = new BackupSnapshotItem
                {
                    Id        = "demo-1",
                    Timestamp = DateTime.Now.AddHours(-1),
                    SizeBytes = 2_147_483_648, // 2 GB
                    Type      = "Auto",
                    Status    = "Completed",
                    Label     = "Auto snapshot",
                    ProjectId = "proj-1"
                };

                var s2 = new BackupSnapshotItem
                {
                    Id        = "demo-2",
                    Timestamp = DateTime.Now.AddDays(-1),
                    SizeBytes = 4_294_967_296, // 4 GB
                    Type      = "Manual",
                    Status    = "Completed",
                    Label     = "Manual snapshot",
                    ProjectId = "proj-2"
                };

                _allSnapshots.Add(s1);
                _allSnapshots.Add(s2);
            }
        }
    }

    // ---------- Models ----------

    public class BackupSnapshotItem
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>Snapshot type, e.g. "Auto" or "Manual".</summary>
        public string Type { get; set; } = "Manual";

        /// <summary>Status, e.g. "Completed", "Failed".</summary>
        public string Status { get; set; } = "Completed";

        /// <summary>Label shown inside the tag pill.</summary>
        public string? Label { get; set; }

        /// <summary>Optional project id this snapshot belongs to; null for global.</summary>
        public string? ProjectId { get; set; }

        public string SizeFormatted => FormatSize(SizeBytes);

        // ---------- Tag pill background color ----------

        private static readonly IBrush DefaultBrush =
            new SolidColorBrush(Color.Parse("#22FFFFFF"));

        // Auto snapshots: blue-ish
        private static readonly IBrush AutoBrush =
            new SolidColorBrush(Color.Parse("#333A7AFE"));

        // Manual snapshots: purple-ish
        private static readonly IBrush ManualBrush =
            new SolidColorBrush(Color.Parse("#334568F2"));

        // Failed snapshots: red-ish
        private static readonly IBrush FailedBrush =
            new SolidColorBrush(Color.Parse("#33FF4B4B"));

        public IBrush TagBackground
        {
            get
            {
                if (string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    return FailedBrush;

                if (string.Equals(Type, "Auto", StringComparison.OrdinalIgnoreCase))
                    return AutoBrush;

                if (string.Equals(Type, "Manual", StringComparison.OrdinalIgnoreCase))
                    return ManualBrush;

                return DefaultBrush;
            }
        }

        internal static string FormatSize(long bytes)
        {
            const double kb = 1024;
            const double mb = kb * 1024;
            const double gb = mb * 1024;

            if (bytes >= gb)
                return $"{bytes / gb:0.##} GB";
            if (bytes >= mb)
                return $"{bytes / mb:0.##} MB";
            if (bytes >= kb)
                return $"{bytes / kb:0.##} KB";

            return $"{bytes} B";
        }
    }

    public class ProjectBackupItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public DateTime? LastBackupTime { get; set; }
        public int       SnapshotCount  { get; set; }
        public long      TotalSizeBytes { get; set; }

        public string LastBackupDisplay =>
            LastBackupTime.HasValue
                ? LastBackupTime.Value.ToString("yyyy-MM-dd HH:mm")
                : "No backups yet";

        public string TotalSizeFormatted =>
            BackupSnapshotItem.FormatSize(TotalSizeBytes);
    }

    public class SnapshotActivityPoint
    {
        public string DayLabel { get; set; } = string.Empty;
        public int Count { get; set; }
        public double BarHeight { get; set; }
        public IBrush BarBrush { get; set; } = new SolidColorBrush(Color.Parse("#3A7AFE"));
    }

    /// <summary>
    /// Minimal ICommand implementation so we don’t depend on any toolkit.
    /// </summary>
    internal sealed class ActionCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public ActionCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute    = execute  ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter)    => _execute(parameter);

        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}