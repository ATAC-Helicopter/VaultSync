using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.Core.Models;
using VaultSync.Core.Config;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels.Notifications;

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

        // Grouped view (per project) for the right-hand history panel
        public ObservableCollection<SnapshotProjectGroup> SnapshotGroups { get; } =
            new ObservableCollection<SnapshotProjectGroup>();

        // Internal full list for summary + filtering
        private readonly List<BackupSnapshotItem> _allSnapshots = new();

        private BackupSnapshotItem? _selectedSnapshotA;
        public BackupSnapshotItem? SelectedSnapshotA
        {
            get => _selectedSnapshotA;
            set
            {
                if (SetProperty(ref _selectedSnapshotA, value))
                {
                    OnPropertyChanged(nameof(SelectedSnapshotA));
                }
            }
        }

        private BackupSnapshotItem? _selectedSnapshotB;
        public BackupSnapshotItem? SelectedSnapshotB
        {
            get => _selectedSnapshotB;
            set
            {
                if (SetProperty(ref _selectedSnapshotB, value))
                {
                    OnPropertyChanged(nameof(SelectedSnapshotB));
                }
            }
        }

        // Type + project filter state
        private string _currentTypeFilter = "All";
        private string? _currentProjectIdFilter = null;
        public string HistoryFilterProjectLabel { get; private set; } = "All projects";

        // Per-project backup status
        public ObservableCollection<ProjectBackupItem> ProjectBackups { get; } =
            new ObservableCollection<ProjectBackupItem>();

        // Active per-project backup progress items (for running backups)
        public ObservableCollection<BackupProgressItem> ActiveBackups { get; } =
            new ObservableCollection<BackupProgressItem>();

        // Currently selected project in the per-project list
        private ProjectBackupItem? _selectedProject;
        public ProjectBackupItem? SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (SetProperty(ref _selectedProject, value))
                {
                    OnPropertyChanged(nameof(SelectedProject));
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
        public string SnapshotActivitySummary { get; private set; } = "No backups in the last 7 days";

        public string LastBackupDisplay { get; private set; } = "No backups yet";
        public string LastBackupRelative { get; private set; } = "—";
        public string TotalBackupSizeFormatted { get; private set; } = "0 B";

        // Mini backup storage card (for Backups page)
        private double _backupDiskUsedPercent;
        public double BackupDiskUsedPercent
        {
            get => _backupDiskUsedPercent;
            private set
            {
                if (SetProperty(ref _backupDiskUsedPercent, value))
                {
                    OnPropertyChanged(nameof(BackupDiskUsedPercent));
                }
            }
        }

        private string _backupDiskFreeText = string.Empty;
        public string BackupDiskFreeText
        {
            get => _backupDiskFreeText;
            private set
            {
                if (SetProperty(ref _backupDiskFreeText, value))
                {
                    OnPropertyChanged(nameof(BackupDiskFreeText));
                }
            }
        }

        private string _backupDiskThresholdText = string.Empty;
        public string BackupDiskThresholdText
        {
            get => _backupDiskThresholdText;
            private set
            {
                if (SetProperty(ref _backupDiskThresholdText, value))
                {
                    OnPropertyChanged(nameof(BackupDiskThresholdText));
                }
            }
        }

        private bool _backupDiskIsBelowThreshold;
        public bool BackupDiskIsBelowThreshold
        {
            get => _backupDiskIsBelowThreshold;
            private set
            {
                if (SetProperty(ref _backupDiskIsBelowThreshold, value))
                {
                    OnPropertyChanged(nameof(BackupDiskIsBelowThreshold));
                }
            }
        }

        // Notification state for the Backups view (reusable notification model)
        public NotificationState Notification { get; } = new NotificationState();

        // Popup dialog state for verification failures
        private string _verificationPopupMessage = string.Empty;
        private bool   _isVerificationPopupOpen;
        private string? _verificationFailedBackupId;

        // Backup progress details (for long-running operations)
        private double _backupProgress;
        public double BackupProgress
        {
            get => _backupProgress;
            set
            {
                if (SetProperty(ref _backupProgress, value))
                {
                    OnPropertyChanged(nameof(BackupProgress));
                    // Removed auto-hide behavior. AppViewModel now controls IsBusy.
                }
            }
        }

        private string _backupCurrentFile = string.Empty;
        public string BackupCurrentFile
        {
            get => _backupCurrentFile;
            set
            {
                if (SetProperty(ref _backupCurrentFile, value))
                {
                    OnPropertyChanged(nameof(BackupCurrentFile));
                }
            }
        }

        private string _backupEtaText = string.Empty;
        public string BackupEtaText
        {
            get => _backupEtaText;
            set
            {
                if (SetProperty(ref _backupEtaText, value))
                {
                    OnPropertyChanged(nameof(BackupEtaText));
                }
            }
        }

        // Busy / status state so the UI can show an overlay or progress indicator
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsBusy));
                }
            }
        }

        private string _busyMessage = string.Empty;
        public string BusyMessage
        {
            get => _busyMessage;
            set
            {
                if (SetProperty(ref _busyMessage, value))
                {
                    OnPropertyChanged(nameof(BusyMessage));
                }
            }
        }

        public string VerificationPopupMessage
        {
            get => _verificationPopupMessage;
            set
            {
                if (SetProperty(ref _verificationPopupMessage, value))
                {
                    OnPropertyChanged(nameof(VerificationPopupMessage));
                }
            }
        }

        public bool IsVerificationPopupOpen
        {
            get => _isVerificationPopupOpen;
            set
            {
                if (SetProperty(ref _isVerificationPopupOpen, value))
                {
                    OnPropertyChanged(nameof(IsVerificationPopupOpen));
                }
            }
        }

        public string? VerificationFailedBackupId
        {
            get => _verificationFailedBackupId;
            set
            {
                if (SetProperty(ref _verificationFailedBackupId, value))
                {
                    OnPropertyChanged(nameof(VerificationFailedBackupId));
                }
            }
        }

        // Events that external code (e.g. view or parent VM) can subscribe to
        // in order to run real backup/restore logic and then refresh this VM.
        public event Action? CreateBackupForAllProjectsRequested;
        public event Action<ProjectBackupItem?>? BackupProjectRequested;
        public event Action<BackupSnapshotItem?>? RestoreBackupRequested;
        public event Action<BackupSnapshotItem?>? DeleteBackupRequested;
        public event Action<BackupProgressItem?>? CancelActiveBackupRequested;

        // Commands
        public ICommand CreateBackupCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand DeleteBackupCommand { get; }

        public ICommand BackupProjectCommand { get; }
        public ICommand ShowProjectHistoryCommand { get; }
        public ICommand FilterSnapshotsCommand { get; }
        public ICommand CloseVerificationPopupCommand { get; }
        public ICommand DeleteFailedBackupCommand { get; }

        public BackupsViewModel()
        {
            // All-project backup
            CreateBackupCommand = new RelayCommand(_ => CreateBackupForAllProjects());

            // Global history actions
            RestoreBackupCommand = new RelayCommand(p => RestoreBackup(p as BackupSnapshotItem));
            DeleteBackupCommand  = new RelayCommand(p => DeleteBackup(p as BackupSnapshotItem));

            // Per-project actions
            BackupProjectCommand      = new RelayCommand(p => BackupProject(p as ProjectBackupItem));
            ShowProjectHistoryCommand = new RelayCommand(p => ShowProjectHistory(p as ProjectBackupItem));

            // History type filter
            FilterSnapshotsCommand        = new RelayCommand(p => ApplyTypeFilter(p as string));
            CloseVerificationPopupCommand = new RelayCommand(_ => CloseVerificationPopup());
            DeleteFailedBackupCommand     = new RelayCommand(_ => DeleteFailedBackup());

            // NOTE:
            // Live data is now provided by LoadFromBackups(...) from the core layer.
            // We no longer seed design-time demo data here.
        }

        // ---------- All-project backup ----------

        private void CreateBackupForAllProjects()
        {
            // Ask external code to perform a real backup for all projects,
            // then reload this VM's data from the database.
            CreateBackupForAllProjectsRequested?.Invoke();
        }

        // ---------- Global history operations ----------

        private void RestoreBackup(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            // Let external code handle the actual restore work.
            RestoreBackupRequested?.Invoke(snapshot);
        }

        private void DeleteBackup(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            // Let external code handle deletion (DB row + files), then refresh this VM.
            DeleteBackupRequested?.Invoke(snapshot);
        }

        // ---------- Per-project operations ----------

        private void BackupProject(ProjectBackupItem? project)
        {
            if (project is null)
                return;

            // Ask external code to run a real backup for the given project.
            BackupProjectRequested?.Invoke(project);
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
                OnPropertyChanged(nameof(HistoryFilterProjectLabel));
                RefreshSnapshotsView(true);
                return;
            }

            _currentProjectIdFilter   = project.Id;
            HistoryFilterProjectLabel = project.Name;
            OnPropertyChanged(nameof(HistoryFilterProjectLabel));
            RefreshSnapshotsView(true);
        }

        // ---------- Active backup progress (per project) ----------

        /// <summary>
        /// Updates (or creates) a per-project backup progress item. Intended to be called
        /// from AppViewModel when BackupService reports progress for a specific project.
        /// This method marshals updates onto the UI thread to keep ObservableCollection
        /// changes safe and avoid UI-thread violations when progress is raised from
        /// background threads.
        /// </summary>
        public void UpdateActiveBackup(string projectId, string projectName, double progress, string currentFile, string etaText)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => UpdateActiveBackup(projectId, projectName, progress, currentFile, etaText));
                return;
            }

            var item = ActiveBackups.FirstOrDefault(p => p.ProjectId == projectId);
            if (item == null)
            {
                item = new BackupProgressItem
                {
                    ProjectId        = projectId,
                    ProjectName      = string.IsNullOrWhiteSpace(projectName) ? "Unknown project" : projectName,
                    CancelRequested  = OnCancelActiveBackup
                };
                ActiveBackups.Add(item);
            }

            item.Progress = progress;

            if (!string.IsNullOrWhiteSpace(currentFile))
                item.CurrentFile = currentFile;

            item.EtaText = etaText ?? string.Empty;
        }

        /// <summary>
        /// Removes a per-project backup progress item once the backup is finished
        /// or cancelled.
        /// </summary>
        public void RemoveActiveBackup(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => RemoveActiveBackup(projectId));
                return;
            }

            var item = ActiveBackups.FirstOrDefault(p => p.ProjectId == projectId);
            if (item != null)
            {
                ActiveBackups.Remove(item);
            }
        }

        private void OnCancelActiveBackup(BackupProgressItem? item)
        {
            if (item is null)
                return;

            CancelActiveBackupRequested?.Invoke(item);
        }

        /// <summary>
        /// Clears all active backup progress items, e.g. after a full refresh.
        /// </summary>
        public void ClearActiveBackups()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(ClearActiveBackups);
                return;
            }

            ActiveBackups.Clear();
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
                {
                    HistoryFilterProjectLabel = "All projects";
                    OnPropertyChanged(nameof(HistoryFilterProjectLabel));
                }
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

            var filteredList = source.ToList();

            ReplaceSnapshots(filteredList, forceResetCompare);
            RebuildSnapshotGroups(filteredList);
        }

        private void RebuildSnapshotGroups(IReadOnlyList<BackupSnapshotItem> filtered)
        {
            SnapshotGroups.Clear();

            if (filtered.Count == 0)
                return;

            // Map project id -> name from the per-project list on the left
            var projectNameLookup = ProjectBackups
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First().Name);

            var grouped = filtered
                .GroupBy(s => s.ProjectId ?? string.Empty)
                .OrderBy(g =>
                {
                    if (!string.IsNullOrWhiteSpace(g.Key) && projectNameLookup.TryGetValue(g.Key, out var name))
                        return name;
                    return "zzzz_" + g.Key; // unknown/global go at the end
                });

            foreach (var g in grouped)
            {
                var ordered = g.OrderByDescending(s => s.Timestamp).ToList();
                long totalBytes = ordered.Sum(s => s.SizeBytes);

                string projectName;
                if (string.IsNullOrWhiteSpace(g.Key))
                {
                    projectName = "Global snapshots";
                }
                else if (!projectNameLookup.TryGetValue(g.Key, out projectName!))
                {
                    projectName = "Unknown project";
                }

                var groupVm = new SnapshotProjectGroup
                {
                    ProjectId          = g.Key,
                    ProjectName        = projectName,
                    Summary            = $"{ordered.Count} backup{(ordered.Count == 1 ? string.Empty : "s")}",
                    TotalSizeFormatted = BackupSnapshotItem.FormatSize(totalBytes)
                };

                foreach (var snap in ordered)
                    groupVm.Snapshots.Add(snap);

                SnapshotGroups.Add(groupVm);
            }
        }

        /// <summary>
        /// Updates the mini backup storage card values for the Backups page.
        /// Intended to be called from AppViewModel after computing disk usage
        /// (total/free/used and threshold) so this VM stays UI-only.
        /// </summary>
        public void UpdateBackupDiskUsage(double usedPercent, string freeText, string thresholdText, bool isBelowThreshold)
        {
            BackupDiskUsedPercent     = usedPercent;
            BackupDiskFreeText        = freeText ?? string.Empty;
            BackupDiskThresholdText   = thresholdText ?? string.Empty;
            BackupDiskIsBelowThreshold = isBelowThreshold;
        }

        /// <summary>
        /// Recomputes backup disk usage from the current app configuration and updates
        /// the mini backup storage card. This reuses the DashboardViewModel static helper
        /// so both views stay consistent.
        /// </summary>
        public void RefreshBackupDiskUsage()
        {
            try
            {
                var config = AppConfigStore.Load();
                var (usedPercent, freeText, thresholdText, isBelowThreshold) =
                    DashboardViewModel.ComputeBackupDiskUsage(config);

                UpdateBackupDiskUsage(usedPercent, freeText, thresholdText, isBelowThreshold);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupsViewModel] Failed to compute backup disk usage: {ex}");
                UpdateBackupDiskUsage(
                    0d,
                    "Backup storage usage unavailable",
                    string.Empty,
                    false);
            }
        }

        // ---------- Summary computation ----------

        /// <summary>
        /// Shows a notification banner in the Backups view.
        /// Severity can be "Info", "Warning", or "Error".
        /// This is a thin wrapper around the shared NotificationState model.
        /// </summary>
        public void ShowNotification(string message, string severity = "Info")
        {
            var sev = severity switch
            {
                "Error"   => NotificationSeverity.Error,
                "Warning" => NotificationSeverity.Warning,
                _         => NotificationSeverity.Info
            };

            Console.WriteLine($"[BackupsViewModel] SHOW NOTIFICATION: {sev} - {message}");
            Notification.Show(message, sev);
        }

        /// <summary>
        /// Opens the verification failure popup for a specific backup.
        /// </summary>
        public void ShowVerificationFailure(string backupId, string projectName)
        {
            VerificationFailedBackupId = backupId;
            VerificationPopupMessage   = $"Backup verification failed for {projectName}. The backup may be incomplete or corrupted.";
            IsVerificationPopupOpen    = true;
        }

        /// <summary>
        /// Closes the verification popup without deleting the backup.
        /// </summary>
        public void CloseVerificationPopup()
        {
            IsVerificationPopupOpen    = false;
            VerificationPopupMessage   = string.Empty;
            VerificationFailedBackupId = null;
        }

        /// <summary>
        /// Deletes the failed backup selected in the verification popup by reusing
        /// the normal DeleteBackupRequested event, then closes the popup.
        /// </summary>
        private void DeleteFailedBackup()
        {
            if (string.IsNullOrWhiteSpace(VerificationFailedBackupId))
            {
                CloseVerificationPopup();
                return;
            }

            var snapshot = _allSnapshots.FirstOrDefault(s => s.Id == VerificationFailedBackupId);
            if (snapshot == null)
            {
                CloseVerificationPopup();
                return;
            }

            // Let external code handle deletion (DB row + files), then refresh this VM.
            DeleteBackupRequested?.Invoke(snapshot);
            CloseVerificationPopup();
        }

        /// <summary>
        /// Marks a snapshot as failed (e.g. after verification) and rebuilds the views.
        /// Intended to be called from AppViewModel when verification detects mismatches.
        /// </summary>
        public void MarkSnapshotAsFailed(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
                return;

            var snapshot = _allSnapshots.FirstOrDefault(s => s.Id == backupId);
            if (snapshot == null)
                return;

            // Keep the original label ("Auto snapshot"/"Manual snapshot"), only mark status as failed.
            snapshot.Status = "Failed";

            // Rebuild filtered views + summary so UI picks up the new status/tag color.
            RefreshSnapshotsView(false);
            RecalculateSummary();
        }

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

            SnapshotsSummaryLine = $"{SnapshotsToday} backups today · {SnapshotsThisWeek} this week";

            if (SnapshotsThisWeek == 0)
            {
                SnapshotActivitySummary = "No backups in the last 7 days";
            }
            else
            {
                SnapshotActivitySummary =
                    $"{SnapshotsThisWeek} backups total · {AutoSnapshotsThisWeek} auto · {ManualSnapshotsThisWeek} manual";
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

            // Notify UI that summary properties changed
            OnPropertyChanged(nameof(TotalSnapshots));
            OnPropertyChanged(nameof(SnapshotsThisWeek));
            OnPropertyChanged(nameof(SnapshotsToday));
            OnPropertyChanged(nameof(AutoSnapshotsThisWeek));
            OnPropertyChanged(nameof(ManualSnapshotsThisWeek));
            OnPropertyChanged(nameof(SnapshotsSummaryLine));
            OnPropertyChanged(nameof(SnapshotActivitySummary));
            OnPropertyChanged(nameof(LastBackupDisplay));
            OnPropertyChanged(nameof(LastBackupRelative));
            OnPropertyChanged(nameof(TotalBackupSizeFormatted));
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

        /// <summary>
        /// Populates this view model from real projects and backups loaded from the core layer.
        /// Call this after performing any backup/restore/delete operations.
        /// </summary>
        public void LoadFromBackups(IEnumerable<Project> projects, IEnumerable<Backup> backups)
        {
            if (projects is null) throw new ArgumentNullException(nameof(projects));
            if (backups  is null) throw new ArgumentNullException(nameof(backups));

            var projectList = projects.ToList();
            var backupList  = backups.ToList();

            ProjectBackups.Clear();
            _allSnapshots.Clear();

            // Map per-project aggregates
            var backupsByProject = backupList
                .GroupBy(b => b.ProjectId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var project in projectList)
            {
                backupsByProject.TryGetValue(project.Id, out var projectBackups);
                projectBackups ??= new List<Backup>();

                var lastBackup = projectBackups
                    .OrderByDescending(b => b.CreatedUtc)
                    .FirstOrDefault();

                var projectItem = new ProjectBackupItem
                {
                    Id             = project.Id.ToString(),
                    Name           = project.Name,
                    LastBackupTime = lastBackup?.CreatedUtc,
                    SnapshotCount  = projectBackups.Count,
                    TotalSizeBytes = projectBackups.Sum(b => b.TotalBytes)
                };

                ProjectBackups.Add(projectItem);
            }

            // Map individual backups into the history list model
            var projectLookup = projectList.ToDictionary(p => p.Id);

            foreach (var backup in backupList)
            {
                projectLookup.TryGetValue(backup.ProjectId, out var project);

                var uiItem = new BackupSnapshotItem
                {
                    Id        = backup.Id.ToString(),
                    Timestamp = backup.CreatedUtc.ToLocalTime(),
                    SizeBytes = backup.TotalBytes,
                    Type      = string.Equals(backup.Type, "auto", StringComparison.OrdinalIgnoreCase)
                        ? "Auto"
                        : "Manual",
                    Status    = "Completed",
                    Label     = string.Equals(backup.Type, "auto", StringComparison.OrdinalIgnoreCase)
                        ? "Auto snapshot"
                        : "Manual snapshot",
                    ProjectId = project?.Id.ToString()
                };

                _allSnapshots.Add(uiItem);
            }

            // Rebuild the filtered history view + summary + mini-chart.
            RefreshSnapshotsView(true);
            RecalculateSummary();
            RefreshBackupDiskUsage();
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

    public class SnapshotProjectGroup
    {
        public string? ProjectId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string TotalSizeFormatted { get; set; } = string.Empty;

        public ObservableCollection<BackupSnapshotItem> Snapshots { get; } =
            new ObservableCollection<BackupSnapshotItem>();
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

    public class BackupProgressItem : ViewModelBase
    {
        public string ProjectId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;

        public Action<BackupProgressItem?>? CancelRequested { get; set; }

        public ICommand CancelCommand { get; }

        public BackupProgressItem()
        {
            CancelCommand = new RelayCommand(_ => CancelRequested?.Invoke(this));
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) < 0.0001)
                    return;

                _progress = value;
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(ShowEta));
                OnPropertyChanged(nameof(CanCancel));
            }
        }

        private string _currentFile = string.Empty;
        public string CurrentFile
        {
            get => _currentFile;
            set
            {
                if (_currentFile == value)
                    return;

                _currentFile = value ?? string.Empty;
                OnPropertyChanged(nameof(CurrentFile));
            }
        }

        private string _etaText = string.Empty;
        public string EtaText
        {
            get => _etaText;
            set
            {
                if (_etaText == value)
                    return;

                _etaText = value ?? string.Empty;
                OnPropertyChanged(nameof(EtaText));
            }
        }

        public bool IsCompleted => Progress >= 100d;

        public bool ShowEta => Progress < 100d;

        public bool CanCancel => !IsCompleted;
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