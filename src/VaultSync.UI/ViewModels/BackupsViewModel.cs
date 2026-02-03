using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using VaultSync.Core.Models;
using VaultSync.Core.Config;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    public class BackupsViewModel : ViewModelBase
    {
        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private static string Lf(string key, string fallback, params object[] args)
        {
            var fmt = L(key, fallback);
            return args.Length == 0
                ? fmt
                : string.Format(CultureInfo.CurrentCulture, fmt, args);
        }

        private static readonly IBrush HealthOkBrush = new ImmutableSolidColorBrush(Colors.LimeGreen);
        private static readonly IBrush HealthWarningBrush = new ImmutableSolidColorBrush(Colors.Orange);
        private static readonly IBrush HealthFailingBrush = new ImmutableSolidColorBrush(Colors.Tomato);
        private static readonly IBrush HealthUnknownBrush = new ImmutableSolidColorBrush(Colors.Gray);
        private static readonly ConcurrentDictionary<string, ImmutableSolidColorBrush> AccentBrushCache = new(StringComparer.OrdinalIgnoreCase);
        // Simple SetProperty helper - note: no PropertyChanged here, we just need
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
        private readonly List<BackupSnapshotItem> _filteredSnapshots = new();

        private int _snapshotRevision;
        private int _lastProjectSignature;
        private int _lastBackupSignature;
        private int _lastAutoBackupSignature;
        private int _lastFilterRevision = -1;
        private SnapshotFilterState _lastFilterState = SnapshotFilterState.Empty;
        private bool _showSummaryCharts = true;
        private bool _showActivityPanel = true;
        private GridLength _activityColumnWidth = new GridLength(360);
        private double _summaryColumnSpacing = 12;

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
        public string HistoryFilterProjectLabel { get; private set; } = L("Backups.Section.HistoryFilterAllProjects", "All projects");
        private bool _onlyErrorsFilter;
        private bool _onlyManualFilter;

        public bool OnlyErrorsFilter
        {
            get => _onlyErrorsFilter;
            set
            {
                if (SetProperty(ref _onlyErrorsFilter, value))
                {
                    OnPropertyChanged(nameof(OnlyErrorsFilter));
                    RefreshSnapshotsView(false);
                }
            }
        }

        public bool OnlyManualFilter
        {
            get => _onlyManualFilter;
            set
            {
                if (SetProperty(ref _onlyManualFilter, value))
                {
                    OnPropertyChanged(nameof(OnlyManualFilter));
                    RefreshSnapshotsView(false);
                }
            }
        }

        // Per-project backup status
        public ObservableCollection<ProjectBackupItem> ProjectBackups { get; } =
            new ObservableCollection<ProjectBackupItem>();
        public ObservableCollection<DestinationOption> DestinationOptions { get; } =
            new ObservableCollection<DestinationOption>();

        public event Action<int, bool>? AutoBackupPreferenceChanged;
        public event Action<DestinationStatusItem, bool>? DestinationActiveChanged;
        public event Action<int, string>? PreferredDestinationChanged;

        // Appearance
        public bool ShowProjectAvatars { get; private set; } = true;

        // Active per-project backup progress items (for running backups)
        public ObservableCollection<BackupProgressItem> ActiveBackups { get; } =
            new ObservableCollection<BackupProgressItem>();
        private readonly DispatcherTimer _activeBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        // Per-destination status for the current backup run
        public ObservableCollection<DestinationStatusItem> DestinationStatuses { get; } =
            new ObservableCollection<DestinationStatusItem>();
        public bool HasDestinationStatuses => DestinationStatuses.Count > 0;
        public ObservableCollection<DestinationStatusItem> ActiveDestinationStatuses { get; } =
            new ObservableCollection<DestinationStatusItem>();
        public bool HasActiveDestinationStatuses => ActiveDestinationStatuses.Count > 0;
        public bool CanToggleDestinations => !_isBusy;
        private bool _showDestinationToggles;
        public bool ShowDestinationToggles
        {
            get => _showDestinationToggles;
            private set
            {
                if (SetProperty(ref _showDestinationToggles, value))
                {
                    OnPropertyChanged(nameof(ShowDestinationToggles));
                }
            }
        }

        private int _diskUsageInFlight;
        private int _refreshSnapshotsInFlight;
        private int _refreshSnapshotsQueued;
        private bool _refreshSnapshotsForceResetQueued;
        private string? _preferredExpandedProjectId;

        private readonly struct SnapshotFilterState : IEquatable<SnapshotFilterState>
        {
            public static readonly SnapshotFilterState Empty = new("All", null, false, false);

            public readonly string TypeFilter;
            public readonly string? ProjectId;
            public readonly bool OnlyErrors;
            public readonly bool OnlyManual;

            public SnapshotFilterState(string typeFilter, string? projectId, bool onlyErrors, bool onlyManual)
            {
                TypeFilter = typeFilter ?? "All";
                ProjectId = projectId;
                OnlyErrors = onlyErrors;
                OnlyManual = onlyManual;
            }

            public bool Equals(SnapshotFilterState other)
            {
                return string.Equals(TypeFilter, other.TypeFilter, StringComparison.Ordinal) &&
                    string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal) &&
                    OnlyErrors == other.OnlyErrors &&
                    OnlyManual == other.OnlyManual;
            }

            public override bool Equals(object? obj)
            {
                return obj is SnapshotFilterState other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(
                    TypeFilter,
                    ProjectId ?? string.Empty,
                    OnlyErrors,
                    OnlyManual);
            }
        }

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
        public double SnapshotActivityChartHeight { get; private set; } = 160;

        // Summary properties (bound in the top cards)
        public int TotalSnapshots { get; private set; }
        public bool HasAnyBackups { get; private set; }
        public int SnapshotsThisWeek { get; private set; }
        public int SnapshotsToday { get; private set; }
        public int SnapshotsYesterday { get; private set; }

        public int AutoSnapshotsThisWeek { get; private set; }
        public int ManualSnapshotsThisWeek { get; private set; }

        public string SnapshotsSummaryLine { get; private set; } =
            Lf("Backups.Summary.TodayWeek", "{0} backups today - {1} this week", 0, 0);
        public string TotalSnapshotsSecondaryLine { get; private set; } =
            Lf("Backups.Summary.YesterdayAverage", "{0} yesterday - avg {1}", 0, "0 B");
        public string SnapshotActivitySummary { get; private set; } =
            L("Backups.Summary.NoActivity", "No backups in the last 7 days");

        public bool ShowSummaryCharts
        {
            get => _showSummaryCharts;
            private set
            {
                if (SetProperty(ref _showSummaryCharts, value))
                {
                    OnPropertyChanged(nameof(ShowSummaryCharts));
                }
            }
        }

        public bool ShowActivityPanel
        {
            get => _showActivityPanel;
            private set
            {
                if (SetProperty(ref _showActivityPanel, value))
                {
                    OnPropertyChanged(nameof(ShowActivityPanel));
                }
            }
        }

        public GridLength ActivityColumnWidth
        {
            get => _activityColumnWidth;
            private set
            {
                if (SetProperty(ref _activityColumnWidth, value))
                {
                    OnPropertyChanged(nameof(ActivityColumnWidth));
                }
            }
        }

        public double SummaryColumnSpacing
        {
            get => _summaryColumnSpacing;
            private set
            {
                if (SetProperty(ref _summaryColumnSpacing, value))
                {
                    OnPropertyChanged(nameof(SummaryColumnSpacing));
                }
            }
        }

        public string LastBackupDisplay { get; private set; } =
            L("Backups.Summary.NoBackups", "No backups yet");
        public string LastBackupRelative { get; private set; } = "-";
        public string LastBackupSecondaryLine { get; private set; } =
            L("Backups.Summary.LastBackupSize", "Size -");
        public string LastBackupSizeValueFormatted { get; private set; } = "0 B";
        public string TotalBackupSizeFormatted { get; private set; } = "0 B";
        public int LocalSnapshotsCount { get; private set; }
        public string TotalStoredLocalLine { get; private set; } =
            Lf("Backups.Summary.LocalTotal", "Local total: {0}", "0 B");
        public string TotalStoredLocalValueFormatted { get; private set; } = "0 B";
        public string TotalStoredImportedLine { get; private set; } =
            Lf("Backups.Summary.ImportedTotal", "Imported total: {0}", "0 B");
        public string TotalStoredImportedValueFormatted { get; private set; } = "0 B";
        public int ImportedSnapshotsCount { get; private set; }

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

        private string _backupDiskDriveLabel = string.Empty;
        public string BackupDiskDriveLabel
        {
            get => _backupDiskDriveLabel;
            private set
            {
                if (SetProperty(ref _backupDiskDriveLabel, value))
                {
                    OnPropertyChanged(nameof(BackupDiskDriveLabel));
                }
            }
        }

        // Backup disk SMART/health display
        private string _backupDiskHealthText = string.Empty;
        public string BackupDiskHealthText
        {
            get => _backupDiskHealthText;
            private set
            {
                if (SetProperty(ref _backupDiskHealthText, value))
                {
                    OnPropertyChanged(nameof(BackupDiskHealthText));
                }
            }
        }

        private IBrush _backupDiskHealthBrush = Brushes.Gray;
        public IBrush BackupDiskHealthBrush
        {
            get => _backupDiskHealthBrush;
            private set
            {
                if (SetProperty(ref _backupDiskHealthBrush, value))
                {
                    OnPropertyChanged(nameof(BackupDiskHealthBrush));
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
                    OnPropertyChanged(nameof(CanToggleDestinations));
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
        public event Action<BackupSnapshotItem?>? OpenBackupFolderRequested;
        public event Action<BackupProgressItem?>? CancelActiveBackupRequested;
        public event Action<int, bool>? BackupProtectionChanged;
        public event Action? OpenSettingsRequested;

        // Commands
        public ICommand CreateBackupCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand DeleteBackupCommand { get; }
        public ICommand OpenBackupFolderCommand { get; }
        public ICommand ToggleBackupProtectionCommand { get; }

        public ICommand BackupProjectCommand { get; }
        public ICommand ShowProjectHistoryCommand { get; }
        public ICommand FilterSnapshotsCommand { get; }
        public ICommand CloseVerificationPopupCommand { get; }
        public ICommand DeleteFailedBackupCommand { get; }
        public ICommand OpenSettingsCommand { get; }

        public BackupsViewModel()
        {
            // All-project backup
            CreateBackupCommand = new RelayCommand(_ => CreateBackupForAllProjects());

            // Global history actions
            RestoreBackupCommand = new RelayCommand(p => RestoreBackup(p as BackupSnapshotItem));
            DeleteBackupCommand  = new RelayCommand(p => DeleteBackup(p as BackupSnapshotItem));
            OpenBackupFolderCommand = new RelayCommand(p => OpenBackupFolder(p as BackupSnapshotItem));
            ToggleBackupProtectionCommand = new RelayCommand(p => ToggleBackupProtection(p as BackupSnapshotItem));

            // Per-project actions
            BackupProjectCommand      = new RelayCommand(p => BackupProject(p as ProjectBackupItem));
            ShowProjectHistoryCommand = new RelayCommand(p => ShowProjectHistory(p as ProjectBackupItem));

            // History type filter
            FilterSnapshotsCommand        = new RelayCommand(p => ApplyTypeFilter(p as string));
            CloseVerificationPopupCommand = new RelayCommand(_ => CloseVerificationPopup());
            DeleteFailedBackupCommand     = new RelayCommand(_ => DeleteFailedBackup());
            OpenSettingsCommand           = new RelayCommand(_ => OpenSettingsRequested?.Invoke());

            DestinationStatuses.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasDestinationStatuses));
                RebuildActiveDestinationStatuses();
            };
            ActiveDestinationStatuses.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasActiveDestinationStatuses));
            ActiveBackups.CollectionChanged += (_, _) => UpdateActiveBackupTimer();
            _activeBackupTimer.Tick += (_, _) => TickActiveBackupDurations();

            // NOTE:
            // Live data is now provided by LoadFromBackups(...) from the core layer.
            // We no longer seed design-time demo data here.

            InitializeLocalizationDefaults();
        }

        private void UpdateActiveBackupTimer()
        {
            if (ActiveBackups.Count > 0)
            {
                if (!_activeBackupTimer.IsEnabled)
                    _activeBackupTimer.Start();
            }
            else
            {
                if (_activeBackupTimer.IsEnabled)
                    _activeBackupTimer.Stop();
            }
        }

        private void TickActiveBackupDurations()
        {
            foreach (var item in ActiveBackups)
            {
                item.TickStageClock();
            }
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

        private void OpenBackupFolder(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            OpenBackupFolderRequested?.Invoke(snapshot);
        }

        private void ToggleBackupProtection(BackupSnapshotItem? item)
        {
            if (item is null)
                return;

            if (!int.TryParse(item.Id, out var backupId))
                return;

            var newValue = item.IsProtected;

            BackupProtectionChanged?.Invoke(backupId, newValue);
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

        private void InitializeLocalizationDefaults()
        {
            SnapshotsSummaryLine = Lf("Backups.Summary.TodayWeek", "{0} backups today - {1} this week", 0, 0);
            TotalSnapshotsSecondaryLine = Lf("Backups.Summary.YesterdayAverage", "{0} yesterday - avg {1}", 0, "0 B");
            SnapshotActivitySummary = L("Backups.Summary.NoActivity", "No backups in the last 7 days");
            LastBackupDisplay = L("Backups.Summary.NoBackups", "No backups yet");
            LastBackupSecondaryLine = L("Backups.Summary.LastBackupSize", "Size -");
            LastBackupSizeValueFormatted = "0 B";
            TotalStoredLocalLine = Lf("Backups.Summary.LocalTotal", "Local total: {0}", "0 B");
            TotalStoredLocalValueFormatted = "0 B";
            TotalStoredImportedLine = Lf("Backups.Summary.ImportedTotal", "Imported total: {0}", "0 B");
            TotalStoredImportedValueFormatted = "0 B";
            HistoryFilterProjectLabel = L("Backups.Section.HistoryFilterAllProjects", "All projects");

            var driveLabel = Lf("Backups.Health.DriveLabel", "Drive: {0}", L("DriveHealth.UnknownDrive", "drive"));
            BackupDiskDriveLabel = driveLabel;
            BackupDiskHealthText = Lf("Backups.Health.Status.Unavailable", "Health ({0}): {1}", driveLabel, L("Backups.Health.NotAvailable", "not available"));

            OnPropertyChanged(nameof(SnapshotsSummaryLine));
            OnPropertyChanged(nameof(TotalSnapshotsSecondaryLine));
            OnPropertyChanged(nameof(SnapshotActivitySummary));
            OnPropertyChanged(nameof(LastBackupDisplay));
            OnPropertyChanged(nameof(LastBackupSecondaryLine));
            OnPropertyChanged(nameof(TotalStoredLocalLine));
            OnPropertyChanged(nameof(TotalStoredImportedLine));
            OnPropertyChanged(nameof(HistoryFilterProjectLabel));
            OnPropertyChanged(nameof(BackupDiskDriveLabel));
            OnPropertyChanged(nameof(BackupDiskHealthText));
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
                HistoryFilterProjectLabel = L("Backups.Section.HistoryFilterAllProjects", "All projects");
                OnPropertyChanged(nameof(HistoryFilterProjectLabel));
                _preferredExpandedProjectId = null;
                RefreshSnapshotsView(true);
                return;
            }

            _currentProjectIdFilter   = project.Id;
            HistoryFilterProjectLabel = project.Name;
            OnPropertyChanged(nameof(HistoryFilterProjectLabel));
            _preferredExpandedProjectId = project.Id;
            RefreshSnapshotsView(true);
        }

        public void PinExpandedProject(string? projectId)
        {
            _preferredExpandedProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
        }

        // ---------- Active backup progress (per project) ----------

        /// <summary>
        /// Updates (or creates) a per-project backup progress item. Intended to be called
        /// from AppViewModel when BackupService reports progress for a specific project.
        /// This method marshals updates onto the UI thread to keep ObservableCollection
        /// changes safe and avoid UI-thread violations when progress is raised from
        /// background threads.
        /// </summary>
        public void UpdateActiveBackup(string projectId, string projectName, double progress, string currentFile, string etaText, bool allowCancel = true, string? destinationLabel = null)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => UpdateActiveBackup(projectId, projectName, progress, currentFile, etaText, allowCancel));
                return;
            }

            var item = ActiveBackups.FirstOrDefault(p => p.ProjectId == projectId);
            if (item == null)
            {
                item = new BackupProgressItem
                {
                    ProjectId        = projectId,
                    ProjectName      = string.IsNullOrWhiteSpace(projectName)
                        ? L("Dashboard.Activity.UnknownProject", "Unknown project")
                        : projectName,
                    CancelRequested  = OnCancelActiveBackup
                };
                ActiveBackups.Add(item);
            }
            else if (!string.IsNullOrWhiteSpace(projectName))
            {
                item.ProjectName = projectName;
            }

            if (destinationLabel != null)
            {
                item.DestinationLabel = destinationLabel;
            }

            item.AllowCancel = allowCancel;

            if (!string.IsNullOrWhiteSpace(currentFile))
                item.CurrentFile = currentFile;

            item.EtaText = etaText ?? string.Empty;
            item.Progress = progress;
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

        public void ResetDestinationStatuses(IEnumerable<BackupDestination> destinations, bool allowToggle)
        {
            var list = destinations.ToList();
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ResetDestinationStatuses(list, allowToggle));
                return;
            }

            ShowDestinationToggles = allowToggle;

            foreach (var item in DestinationStatuses)
            {
                item.PropertyChanged -= OnDestinationItemPropertyChanged;
            }
            DestinationStatuses.Clear();
            foreach (var dest in list)
            {
                var status = GetDestinationStatusText(dest.Active);
                var severity = "Info";
                var item = new DestinationStatusItem
                {
                    Id     = DestinationStatusItem.GetId(dest),
                    Alias  = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path,
                    Path   = dest.Path,
                    Status = status,
                    Severity = severity,
                    DotBrush = GetDestinationDotBrush(status, severity),
                    LastCheckedUtc = null,
                    IsActive = dest.Active,
                    IsConfigurable = allowToggle
                };
                item.PropertyChanged += OnDestinationItemPropertyChanged;
                DestinationStatuses.Add(item);
            }
            RebuildActiveDestinationStatuses();
            OnPropertyChanged(nameof(HasDestinationStatuses));
        }

        private void OnDestinationItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not DestinationStatusItem item)
                return;

            if (e.PropertyName == nameof(DestinationStatusItem.IsActive))
            {
                if (item.IsConfigurable)
                {
                    var status = GetDestinationStatusText(item.IsActive);
                    if (!string.Equals(item.Status, status, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = status;
                        item.Severity = "Info";
                        item.DotBrush = GetDestinationDotBrush(status, "Info");
                    }

                    DestinationActiveChanged?.Invoke(item, item.IsActive);
                    RebuildActiveDestinationStatuses();
                }
            }
        }

        private void RebuildActiveDestinationStatuses()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(RebuildActiveDestinationStatuses);
                return;
            }

            ActiveDestinationStatuses.Clear();
            foreach (var item in DestinationStatuses)
            {
                if (item.IsActive)
                {
                    ActiveDestinationStatuses.Add(item);
                }
            }
            OnPropertyChanged(nameof(HasActiveDestinationStatuses));
        }

        public void UpdateDestinationStatus(string id, string status, string severity = "Info")
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => UpdateDestinationStatus(id, status, severity));
                return;
            }

            var item = DestinationStatuses.FirstOrDefault(d => d.Id == id);
            if (item is null)
                return;

            var normalizedStatus = status ?? string.Empty;
            var reachableLabel = LocalizationProvider.Service?.GetString("Destinations.Test.Reachable") ?? "Reachable";
            var unavailableLabel = LocalizationProvider.Service?.GetString("Destinations.Test.Unavailable") ?? "Unavailable";
            var readOnlyLabel = LocalizationProvider.Service?.GetString("Destinations.Test.ReadOnly") ?? "Read-only";

            if (!string.IsNullOrWhiteSpace(normalizedStatus))
            {
                if (normalizedStatus.Contains("Using pre-mounted", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = reachableLabel;
                }
                else if (normalizedStatus.Contains("Reachable", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = reachableLabel;
                }
                else if (normalizedStatus.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
                         normalizedStatus.Contains("No changes", StringComparison.OrdinalIgnoreCase) ||
                         normalizedStatus.Contains("No backup", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = reachableLabel;
                }
                else if (normalizedStatus.Contains("Read-only", StringComparison.OrdinalIgnoreCase) ||
                         normalizedStatus.Contains("Read only", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = readOnlyLabel;
                }
                else if (normalizedStatus.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
                         normalizedStatus.Contains("Unreachable", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = unavailableLabel;
                }
            }
            else
            {
                normalizedStatus = severity switch
                {
                    "Success" => reachableLabel,
                    "Warning" => readOnlyLabel,
                    "Error" => unavailableLabel,
                    _ => normalizedStatus
                };
            }

            var severityToUse = severity;
            if (string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(normalizedStatus) &&
                    string.Equals(normalizedStatus, reachableLabel, StringComparison.OrdinalIgnoreCase))
                {
                    severityToUse = "Success";
                }
                else if (!string.IsNullOrWhiteSpace(normalizedStatus) &&
                         string.Equals(normalizedStatus, unavailableLabel, StringComparison.OrdinalIgnoreCase))
                {
                    severityToUse = "Error";
                }
                else if (!string.IsNullOrWhiteSpace(normalizedStatus) &&
                         string.Equals(normalizedStatus, readOnlyLabel, StringComparison.OrdinalIgnoreCase))
                {
                    severityToUse = "Warning";
                }
                else if (!string.Equals(item.Severity, "Info", StringComparison.OrdinalIgnoreCase))
                {
                    severityToUse = item.Severity;
                }
            }

            item.Status   = normalizedStatus;
            item.Severity = severityToUse;
            item.DotBrush = GetDestinationDotBrush(normalizedStatus, severityToUse);
            item.LastCheckedUtc = DateTime.UtcNow;
        }

        public void MarkDestinationComplete(string id, bool success, string status)
        {
            UpdateDestinationStatus(id, status, success ? "Success" : "Error");
        }

        private static string GetDestinationStatusText(bool isActive)
        {
            if (isActive)
            {
                return LocalizationProvider.Service?.GetString("Backups.Destinations.Pending")
                       ?? "Pending";
            }

            return LocalizationProvider.Service?.GetString("Backups.Destinations.Inactive")
                   ?? "Inactive";
        }

        private static IBrush GetDestinationDotBrush(string status, string severity)
        {
            return severity switch
            {
                "Success" => AccentBrush("#22CC88"),
                "Warning" => AccentBrush("#FFB84C"),
                "Error"   => AccentBrush("#FF6B6B"),
                _         => AccentBrush("#8E9BAF")
            };
        }

        private static IBrush AccentBrush(string hex) =>
            new ImmutableSolidColorBrush(Color.Parse(hex));

        /// <summary>
        /// Shows a non-cancellable transient operation (e.g., deleting a backup) in the active list.
        /// </summary>
        public void ShowTransientOperation(string operationId, string title, string detail)
        {
            UpdateActiveBackup(operationId, title, 0, detail, string.Empty, allowCancel: false);
        }

        /// <summary>
        /// Removes a transient operation card once completed.
        /// </summary>
        public void CompleteTransientOperation(string operationId, string finalDetail = "")
        {
            UpdateActiveBackup(operationId, string.Empty, 100, finalDetail, string.Empty, allowCancel: false);
            RemoveActiveBackup(operationId);
        }

        public void MarkBackupProtection(int backupId, bool isProtected)
        {
            var idStr = backupId.ToString();

            var snapshot = Snapshots.FirstOrDefault(s => s.Id == idStr);
            if (snapshot != null)
                snapshot.IsProtected = isProtected;

            var all = _allSnapshots.FirstOrDefault(s => s.Id == idStr);
            if (all != null)
                all.IsProtected = isProtected;

            foreach (var group in SnapshotGroups)
            {
                var gItem = group.Snapshots.FirstOrDefault(s => s.Id == idStr);
                if (gItem != null)
                    gItem.IsProtected = isProtected;
            }
        }

        // ---------- Snapshot management + filtering ----------

        private void AddSnapshot(BackupSnapshotItem snapshot)
        {
            _allSnapshots.Add(snapshot);
            Interlocked.Increment(ref _snapshotRevision);
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
            if (Interlocked.Exchange(ref _refreshSnapshotsInFlight, 1) == 1)
            {
                _refreshSnapshotsForceResetQueued |= forceResetCompare;
                Interlocked.Exchange(ref _refreshSnapshotsQueued, 1);
                return;
            }

            try
            {
                var filterState = new SnapshotFilterState(
                    _currentTypeFilter,
                    _currentProjectIdFilter,
                    OnlyErrorsFilter,
                    OnlyManualFilter);

                if (!forceResetCompare &&
                    _lastFilterRevision == _snapshotRevision &&
                    filterState.Equals(_lastFilterState))
                {
                    return;
                }

                _filteredSnapshots.Clear();
                var seenIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var snapshot in _allSnapshots)
                {
                    if (_currentTypeFilter == "Auto" && !string.Equals(snapshot.Type, "Auto", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (_currentTypeFilter == "Manual" && !string.Equals(snapshot.Type, "Manual", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (OnlyManualFilter && !string.Equals(snapshot.Type, "Manual", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (OnlyErrorsFilter && !string.Equals(snapshot.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrWhiteSpace(_currentProjectIdFilter) &&
                        !string.Equals(snapshot.ProjectId, _currentProjectIdFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(snapshot.Id) && !seenIds.Add(snapshot.Id))
                        continue;

                    _filteredSnapshots.Add(snapshot);
                }

                _lastFilterState = filterState;
                _lastFilterRevision = _snapshotRevision;

                ReplaceSnapshots(_filteredSnapshots, forceResetCompare);
                RebuildSnapshotGroups(_filteredSnapshots);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshSnapshotsInFlight, 0);
                if (Interlocked.Exchange(ref _refreshSnapshotsQueued, 0) == 1)
                {
                    var queuedForceReset = _refreshSnapshotsForceResetQueued;
                    _refreshSnapshotsForceResetQueued = false;
                    RefreshSnapshotsView(queuedForceReset);
                }
            }
        }

        private void RebuildSnapshotGroups(IReadOnlyList<BackupSnapshotItem> filtered)
        {
            SnapshotGroups.Clear();

            if (filtered.Count == 0)
                return;

            // Map project id -> name from the per-project list on the left
            var projectLookup = ProjectBackups
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var grouped = filtered
                .GroupBy(s => s.ProjectId ?? string.Empty)
                .OrderBy(g =>
                {
                    if (!string.IsNullOrWhiteSpace(g.Key) && projectLookup.TryGetValue(g.Key, out var nameSource))
                        return nameSource.Name;
                    return "zzzz_" + g.Key; // unknown/global go at the end
                });

            var latestOverall = filtered
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefault()
                ?.Timestamp ?? DateTime.MinValue;

            foreach (var g in grouped)
            {
                var key = g.Key ?? string.Empty;

                var ordered = g
                    .GroupBy(s => s.Id)
                    .Select(grp => grp.First())
                    .OrderByDescending(s => s.Timestamp)
                    .ToList();
                long totalBytes = ordered.Sum(s => s.SizeBytes);
                var latest = ordered.FirstOrDefault()?.Timestamp ?? DateTime.MinValue;

                string projectName;
                if (string.IsNullOrWhiteSpace(key))
                {
                    projectName = L("Backups.Section.Group.Global", "Global snapshots");
                }
                else if (!projectLookup.TryGetValue(key, out var nameSource))
                {
                    projectName = L("Backups.Section.Group.Unknown", "Unknown project");
                }
                else
                {
                    projectName = nameSource.Name;
                }

                var summaryKey = ordered.Count == 1
                    ? "Backups.Section.SnapshotCount.Singular"
                    : "Backups.Section.SnapshotCount.Plural";
                var summaryFallback = ordered.Count == 1 ? "{0} backup" : "{0} backups";

                var accentBrush = GetAccentBrush("#33405A");
                if (!string.IsNullOrWhiteSpace(key) && projectLookup.TryGetValue(key, out var colorSource))
                {
                    accentBrush = GetAccentBrush(colorSource.AvatarColor);
                }

                var isExpanded = !string.IsNullOrWhiteSpace(_preferredExpandedProjectId)
                    ? string.Equals(_preferredExpandedProjectId, key, StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrWhiteSpace(_currentProjectIdFilter)
                        ? string.Equals(_currentProjectIdFilter, key, StringComparison.OrdinalIgnoreCase)
                        : latest == latestOverall;

                var groupVm = new SnapshotProjectGroup
                {
                    ProjectId          = key,
                    ProjectName        = projectName,
                    Summary            = Lf(summaryKey, summaryFallback, ordered.Count),
                    TotalSizeFormatted = BackupSnapshotItem.FormatSize(totalBytes),
                    LatestBackupDisplay = latest == DateTime.MinValue ? "-" : latest.ToString("yyyy-MM-dd HH:mm"),
                    AccentBrush        = accentBrush,
                    IsExpanded         = isExpanded
                };

                foreach (var snap in ordered)
                    groupVm.Snapshots.Add(snap);

                SnapshotGroups.Add(groupVm);
            }

        }

        private static ImmutableSolidColorBrush GetAccentBrush(string? hexColor)
        {
            var normalized = string.IsNullOrWhiteSpace(hexColor) ? "#33405A" : hexColor;
            if (AccentBrushCache.TryGetValue(normalized, out var cached))
                return cached;

            try
            {
                var brush = new ImmutableSolidColorBrush(Color.Parse(normalized));
                AccentBrushCache[normalized] = brush;
                return brush;
            }
            catch
            {
                return AccentBrushCache.GetOrAdd("#33405A", _ => new ImmutableSolidColorBrush(Color.Parse("#33405A")));
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
        public void RefreshBackupDiskUsage(bool includeHealthProbe = false)
        {
            if (Interlocked.Exchange(ref _diskUsageInFlight, 1) == 1)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    var config = AppConfigStore.Load();
                    var (usedPercent, freeText, thresholdText, isBelowThreshold, status) =
                        DashboardViewModel.ComputeBackupDiskUsageDetailed(config);
                    var driveLabel = Lf("Backups.Health.DriveLabel", "Drive: {0}", FormatDriveLabel(config.Backups.BackupRoot));

                    string? healthText = null;
                    IBrush? healthBrush = null;

                    if (includeHealthProbe)
                    {
                        var healthService = new DriveHealthService();
                        var backupPath = config.Backups.BackupRoot ?? string.Empty;
                        var health = healthService.CheckPath(backupPath);

                        var fallbackMessage = string.IsNullOrWhiteSpace(health.Message)
                            ? L("Backups.Health.NotAvailable", "not available")
                            : health.Message!;
                        (healthText, healthBrush) = health.Status switch
                        {
                            DriveHealthStatus.Healthy => (Lf("Backups.Health.Status.Healthy", "Health ({0}): OK ({1})", driveLabel, health.Message ?? fallbackMessage), HealthOkBrush),
                            DriveHealthStatus.Warning => (Lf("Backups.Health.Status.Warning", "Health warning ({0}): {1}", driveLabel, health.Message ?? fallbackMessage), HealthWarningBrush),
                            DriveHealthStatus.Failing => (Lf("Backups.Health.Status.Failing", "Health failing ({0}): {1}", driveLabel, health.Message ?? fallbackMessage), HealthFailingBrush),
                            _ => (Lf("Backups.Health.Status.Unavailable", "Health ({0}): {1}", driveLabel, fallbackMessage), HealthUnknownBrush)
                        };
                    }

                    var displayUsedPercent = usedPercent;
                    var displayBelowThreshold = isBelowThreshold;
                    if (status != DashboardViewModel.BackupDiskUsageStatus.Ok && BackupDiskUsedPercent > 0)
                    {
                        displayUsedPercent = BackupDiskUsedPercent;
                        displayBelowThreshold = false;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateBackupDiskUsage(displayUsedPercent, freeText, thresholdText, displayBelowThreshold);
                        BackupDiskDriveLabel = driveLabel;
                        if (includeHealthProbe && healthText is not null && healthBrush is not null)
                        {
                            BackupDiskHealthText = healthText;
                            BackupDiskHealthBrush = healthBrush;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Backups] Disk usage refresh failed: {ex.Message}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateBackupDiskUsage(
                            0d,
                            L("Dashboard.Storage.UsageUnavailable", "Backup storage usage unavailable"),
                            string.Empty,
                            false);

                        var driveUnknown = L("DriveHealth.UnknownDrive", "drive");
                        BackupDiskDriveLabel = Lf("Backups.Health.DriveLabel", "Drive: {0}", driveUnknown);
                        BackupDiskHealthText = Lf("Backups.Health.Status.Unavailable", "Health ({0}): {1}", BackupDiskDriveLabel, L("Backups.Health.NotAvailable", "not available"));
                        BackupDiskHealthBrush = HealthUnknownBrush;
                    });
                }
                finally
                {
                    Interlocked.Exchange(ref _diskUsageInFlight, 0);
                }
            });
        }

        private static string FormatDriveLabel(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "unknown";

            try
            {
                var normalized = System.IO.Path.GetFullPath(path);

                // On Windows, keep the drive root; on macOS/Linux prefer the mount point under /Volumes.
                if (OperatingSystem.IsWindows())
                {
                    var root = System.IO.Path.GetPathRoot(normalized);
                    if (!string.IsNullOrWhiteSpace(root))
                        return root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                }
                else if (normalized.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        return parts[1];
                }
            }
            catch
            {
                // ignore and fall back
            }

            // UNC/SMB paths: include the share (and optional subpath) for clarity.
            if (path.StartsWith("\\\\") || path.StartsWith("//") || path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseShareWithSubpath(path, out var host, out var share, out var subPath))
                {
                    if (!string.IsNullOrWhiteSpace(subPath))
                        return $"\\\\{host}\\{share}\\{subPath.Replace('/', '\\')}";

                    return $"\\\\{host}\\{share}";
                }
            }

            return path;
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
                var trimmed = path.TrimStart('\\', '/').Replace('\\', '/');
                var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
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

        // ---------- Summary computation ----------

        /// <summary>
        /// Shows a notification banner in the Backups view.
        /// Severity can be "Info", "Warning", or "Error".
        /// This is a thin wrapper around the shared NotificationState model.
        /// </summary>
        public void ShowNotification(
            string message,
            string severity = "Info",
            string? actionLabel = null,
            ICommand? actionCommand = null)
        {
            var sev = severity switch
            {
                "Error"   => NotificationSeverity.Error,
                "Warning" => NotificationSeverity.Warning,
                _         => NotificationSeverity.Info
            };

            Notification.Show(message, sev, actionLabel: actionLabel, actionCommand: actionCommand);
        }

        private void OnAutoBackupChanged(ProjectBackupItem item)
        {
            if (int.TryParse(item.Id, out var projectId))
            {
                AutoBackupPreferenceChanged?.Invoke(projectId, item.AutoBackupEnabled);
            }
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
            Interlocked.Increment(ref _snapshotRevision);

            // Rebuild filtered views + summary so UI picks up the new status/tag color.
            RefreshSnapshotsView(false);
            RecalculateSummary();
        }

        private void RecalculateSummary()
        {
            var now       = DateTime.Now;
            var weekStart = now.Date.AddDays(-6);

            TotalSnapshots = _allSnapshots.Count;
            HasAnyBackups = TotalSnapshots > 0;

            SnapshotsToday = _allSnapshots.Count(s => s.Timestamp.Date == now.Date);
            SnapshotsYesterday = _allSnapshots.Count(s => s.Timestamp.Date == now.Date.AddDays(-1));
            SnapshotsThisWeek = _allSnapshots.Count(s =>
                s.Timestamp.Date >= weekStart);

            AutoSnapshotsThisWeek = _allSnapshots.Count(s =>
                s.Timestamp.Date >= weekStart &&
                string.Equals(s.Type, "Auto", StringComparison.OrdinalIgnoreCase));

            ManualSnapshotsThisWeek = _allSnapshots.Count(s =>
                s.Timestamp.Date >= weekStart &&
                string.Equals(s.Type, "Manual", StringComparison.OrdinalIgnoreCase));

            OnPropertyChanged(nameof(HasAnyBackups));

            SnapshotsSummaryLine = Lf(
                "Backups.Summary.TodayWeek",
                "{0} backups today - {1} this week",
                SnapshotsToday,
                SnapshotsThisWeek);

            if (SnapshotsThisWeek == 0)
            {
                SnapshotActivitySummary = L("Backups.Summary.NoActivity", "No backups in the last 7 days");
            }
            else
            {
                SnapshotActivitySummary = Lf(
                    "Backups.Summary.ActivityTotals",
                    "{0} backups total - {1} auto - {2} manual",
                    SnapshotsThisWeek,
                    AutoSnapshotsThisWeek,
                    ManualSnapshotsThisWeek);
            }

            if (_allSnapshots.Count > 0)
            {
                var last = _allSnapshots
                    .OrderByDescending(s => s.Timestamp)
                    .First();

                LastBackupDisplay  = last.Timestamp.ToString("yyyy-MM-dd HH:mm");
                LastBackupRelative = FormatRelative(now - last.Timestamp);
                LastBackupSecondaryLine = Lf(
                    "Backups.Summary.LastBackupSize",
                    "Size {0}",
                    BackupSnapshotItem.FormatSize(last.SizeBytes));
                LastBackupSizeValueFormatted = BackupSnapshotItem.FormatSize(last.SizeBytes);
            }
            else
            {
                LastBackupDisplay  = L("Backups.Summary.NoBackups", "No backups yet");
                LastBackupRelative = "-";
                LastBackupSecondaryLine = L("Backups.Summary.LastBackupSize", "Size -");
                LastBackupSizeValueFormatted = "0 B";
            }

            var totalBytes = _allSnapshots.Sum(s => s.SizeBytes);
            TotalBackupSizeFormatted = BackupSnapshotItem.FormatSize(totalBytes);
            var avgSize = _allSnapshots.Count > 0
                ? BackupSnapshotItem.FormatSize(totalBytes / _allSnapshots.Count)
                : "0 B";
            TotalSnapshotsSecondaryLine = Lf(
                "Backups.Summary.YesterdayAverage",
                "{0} yesterday - avg {1}",
                SnapshotsYesterday,
                avgSize);
            var localBytes = _allSnapshots.Where(s => !s.IsImported).Sum(s => s.SizeBytes);
            TotalStoredLocalLine = Lf(
                "Backups.Summary.LocalTotal",
                "Local total: {0}",
                BackupSnapshotItem.FormatSize(localBytes));
            TotalStoredLocalValueFormatted = BackupSnapshotItem.FormatSize(localBytes);

            var importedItems = _allSnapshots.Where(s => s.IsImported).ToList();
            var importedCount = importedItems.Count;
            ImportedSnapshotsCount = importedCount;
            LocalSnapshotsCount = Math.Max(0, _allSnapshots.Count - importedCount);
            var importedBytes = importedItems.Sum(s => s.SizeBytes);
            TotalStoredImportedLine = Lf(
                "Backups.Summary.ImportedTotal",
                "Imported total: {0}",
                BackupSnapshotItem.FormatSize(importedBytes));
            TotalStoredImportedValueFormatted = BackupSnapshotItem.FormatSize(importedBytes);

            RebuildSnapshotActivity(now);

            // Notify UI that summary properties changed
            OnPropertyChanged(nameof(TotalSnapshots));
            OnPropertyChanged(nameof(SnapshotsThisWeek));
            OnPropertyChanged(nameof(SnapshotsToday));
            OnPropertyChanged(nameof(SnapshotsYesterday));
            OnPropertyChanged(nameof(AutoSnapshotsThisWeek));
            OnPropertyChanged(nameof(ManualSnapshotsThisWeek));
            OnPropertyChanged(nameof(SnapshotsSummaryLine));
            OnPropertyChanged(nameof(TotalSnapshotsSecondaryLine));
            OnPropertyChanged(nameof(SnapshotActivitySummary));
            OnPropertyChanged(nameof(LastBackupDisplay));
            OnPropertyChanged(nameof(LastBackupRelative));
            OnPropertyChanged(nameof(LastBackupSecondaryLine));
            OnPropertyChanged(nameof(LastBackupSizeValueFormatted));
            OnPropertyChanged(nameof(TotalBackupSizeFormatted));
            OnPropertyChanged(nameof(LocalSnapshotsCount));
            OnPropertyChanged(nameof(TotalStoredLocalLine));
            OnPropertyChanged(nameof(TotalStoredLocalValueFormatted));
            OnPropertyChanged(nameof(TotalStoredImportedLine));
            OnPropertyChanged(nameof(TotalStoredImportedValueFormatted));
            OnPropertyChanged(nameof(ImportedSnapshotsCount));
        }

        public void UpdateSummaryLayout(double width)
        {
            const double chartThreshold = 1180;
            const double activityThreshold = 1400;

            var showCharts = width >= chartThreshold;
            var showActivity = width >= activityThreshold;

            ShowSummaryCharts = showCharts;
            ShowActivityPanel = showActivity;
            ActivityColumnWidth = showActivity ? new GridLength(360) : new GridLength(0);
            SummaryColumnSpacing = showActivity ? 12 : 0;
        }

        private static string FormatRelative(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1))
                return L("Backups.Relative.JustNow", "Just now");
            if (span < TimeSpan.FromHours(1))
                return Lf("Backups.Relative.MinutesAgo", "{0} min ago", (int)span.TotalMinutes);
            if (span < TimeSpan.FromDays(1))
                return Lf("Backups.Relative.HoursAgo", "{0} h ago", (int)span.TotalHours);
            if (span < TimeSpan.FromDays(7))
                return Lf("Backups.Relative.DaysAgo", "{0} days ago", (int)span.TotalDays);

            return L("Backups.Relative.OverAWeek", "Over a week ago");
        }

        // ---------- Weekly activity mini-chart ----------

        private void RebuildSnapshotActivity(DateTime now)
        {
            SnapshotActivity.Clear();
            const double chartHeight = 220;
            const double barBase = 14;
            const double barRange = chartHeight - 48;
            SnapshotActivityChartHeight = chartHeight;

            // Last 7 days, oldest -> newest
            var days = Enumerable.Range(0, 7)
                .Select(offset => now.Date.AddDays(-6 + offset))
                .ToArray();

            var autoByDate = _allSnapshots
                .Where(s => string.Equals(s.Type, "Auto", StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.Count());
            var manualByDate = _allSnapshots
                .Where(s => string.Equals(s.Type, "Manual", StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.Count());
            var importedByDate = _allSnapshots
                .Where(s => s.IsImported)
                .GroupBy(s => s.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.Count());
            var bytesByDate = _allSnapshots
                .GroupBy(s => s.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.SizeBytes));

            var totals = days
                .Select(d =>
                {
                    autoByDate.TryGetValue(d, out var autoCount);
                    manualByDate.TryGetValue(d, out var manualCount);
                    importedByDate.TryGetValue(d, out var importedCount);
                    return autoCount + manualCount + importedCount;
                })
                .ToList();

            int maxTotal = totals.DefaultIfEmpty(0).Max();
            if (maxTotal == 0)
                maxTotal = 1; // avoid divide-by-zero

            long maxBytes = bytesByDate.Values.DefaultIfEmpty(0L).Max();
            if (maxBytes == 0)
                maxBytes = 1;

            foreach (var day in days)
            {
                autoByDate.TryGetValue(day, out var autoCount);
                manualByDate.TryGetValue(day, out var manualCount);
                importedByDate.TryGetValue(day, out var importedCount);
                bytesByDate.TryGetValue(day, out var totalBytes);

                var totalCount = autoCount + manualCount + importedCount;
                var normalized = totalBytes > 0
                    ? totalBytes / (double)maxBytes
                    : totalCount / (double)maxTotal;
                var totalHeight = totalCount == 0 ? 0 : barBase + normalized * barRange;

                var autoHeight = 0d;
                var manualHeight = 0d;
                var importedHeight = 0d;
                if (totalCount > 0)
                {
                    autoHeight = autoCount == 0 ? 0 : Math.Max(3, totalHeight * autoCount / totalCount);
                    manualHeight = manualCount == 0 ? 0 : Math.Max(3, totalHeight * manualCount / totalCount);
                    importedHeight = importedCount == 0 ? 0 : Math.Max(3, totalHeight * importedCount / totalCount);

                    var combined = autoHeight + manualHeight + importedHeight;
                    if (combined > totalHeight && combined > 0)
                    {
                        var scale = totalHeight / combined;
                        autoHeight *= scale;
                        manualHeight *= scale;
                        importedHeight *= scale;
                    }
                }

                var dayLabel = day.ToString("ddd");
                var tooltip = totalCount == 0
                    ? Lf("Backups.Activity.TooltipNone", "{0}: No backups", dayLabel)
                    : Lf("Backups.Activity.Tooltip", "{0}: {1} backups - {2}", dayLabel, totalCount, BackupSnapshotItem.FormatSize(totalBytes));

                SnapshotActivity.Add(new SnapshotActivityPoint
                {
                    DayLabel     = dayLabel,
                    ShowLabel    = true,
                    AutoCount    = autoCount,
                    ManualCount  = manualCount,
                    ImportedCount = importedCount,
                    TotalBytes   = totalBytes,
                    AutoHeight   = autoHeight,
                    ManualHeight = manualHeight,
                    ImportedHeight = importedHeight,
                    TooltipText  = tooltip
                });
            }

            OnPropertyChanged(nameof(SnapshotActivityChartHeight));
        }

        /// <summary>
        /// Populates this view model from real projects and backups loaded from the core layer.
        /// Call this after performing any backup/restore/delete operations.
        /// </summary>
        public void LoadFromBackups(IEnumerable<Project> projects, IEnumerable<Backup> backups, ISet<int>? autoBackupDisabledProjects = null)
        {
            if (projects is null) throw new ArgumentNullException(nameof(projects));
            if (backups  is null) throw new ArgumentNullException(nameof(backups));

            var config = AppConfigStore.Load();
            ShowProjectAvatars = config.Appearance.ShowProjectAvatars;
            OnPropertyChanged(nameof(ShowProjectAvatars));
            RefreshDestinationOptionsInternal(config);

            var projectList = projects.ToList();
            var dedupBackups = new Dictionary<int, Backup>();
            foreach (var backup in backups)
            {
                if (!dedupBackups.ContainsKey(backup.Id))
                {
                    dedupBackups[backup.Id] = backup;
                }
            }
            var backupList = dedupBackups.Values.ToList();
            var projectSignature = ComputeProjectSignature(projectList);
            var backupSignature = ComputeBackupSignature(backupList);
            var autoSignature = ComputeAutoBackupSignature(autoBackupDisabledProjects);

            var dataChanged = projectSignature != _lastProjectSignature || backupSignature != _lastBackupSignature;
            var autoChanged = autoSignature != _lastAutoBackupSignature;
            if (!dataChanged && autoChanged)
            {
                UpdateAutoBackupFlags(autoBackupDisabledProjects);
                _lastAutoBackupSignature = autoSignature;
                return;
            }

            if (!dataChanged && !autoChanged)
                return;

            ProjectBackups.Clear();
            _allSnapshots.Clear();

            // Map per-project aggregates in a single pass.
            var projectStats = new Dictionary<int, (int Count, long TotalBytes, DateTime? LastBackupTime)>();
            foreach (var backup in backupList)
            {
                if (!projectStats.TryGetValue(backup.ProjectId, out var stats))
                    stats = (0, 0L, null);

                stats.Count++;
                stats.TotalBytes += backup.TotalBytes;
                if (!stats.LastBackupTime.HasValue || backup.CreatedUtc > stats.LastBackupTime.Value)
                    stats.LastBackupTime = backup.CreatedUtc;

                projectStats[backup.ProjectId] = stats;
            }

            foreach (var project in projectList)
            {
                projectStats.TryGetValue(project.Id, out var stats);

                var projectItem = new ProjectBackupItem
                {
                    Id                = project.Id.ToString(),
                    Name              = project.Name,
                    ExternalId        = project.ExternalId ?? string.Empty,
                    LastBackupTime    = stats.LastBackupTime,
                    SnapshotCount     = stats.Count,
                    TotalSizeBytes    = stats.TotalBytes,
                    AutoBackupEnabled = autoBackupDisabledProjects is null || !autoBackupDisabledProjects.Contains(project.Id),
                    AutoBackupChanged = OnAutoBackupChanged,
                    PreferredDestinationId = project.PreferredDestinationId ?? string.Empty,
                    PreferredDestinationChanged = OnPreferredDestinationChanged
                };
                projectItem.SetAvatarFromNameAndStore(project.Name, project.RootPath, project.ExternalId);
                UpdateProjectDestinationDisplay(projectItem, config);
                ProjectBackups.Add(projectItem);
            }

            // Map individual backups into the history list model
            var projectLookup = projectList.ToDictionary(p => p.Id);

            foreach (var backup in backupList)
            {
                projectLookup.TryGetValue(backup.ProjectId, out var project);

                var destinationDisplay = string.IsNullOrWhiteSpace(backup.DestinationAlias)
                    ? backup.DestinationPath
                    : backup.DestinationAlias;

            var isAutoSnapshot = string.Equals(backup.Type, "auto", StringComparison.OrdinalIgnoreCase);
            var importedLabel = L("Backups.Snapshot.Type.Imported", "Imported");
            if (backup.IsImported && !string.IsNullOrWhiteSpace(backup.OriginMachineName))
            {
                importedLabel = $"{importedLabel} \u00b7 {backup.OriginMachineName}";
            }
            var uiItem = new BackupSnapshotItem
            {
                Id        = backup.Id.ToString(),
                Timestamp = backup.CreatedUtc.ToLocalTime(),
                SizeBytes = backup.TotalBytes,
                Type      = isAutoSnapshot ? "Auto" : "Manual",
                IsImported = backup.IsImported,
                OriginMachineName = backup.OriginMachineName,
                ImportedLabel = importedLabel,
                TypeLabel = isAutoSnapshot
                    ? L("Backups.Snapshot.Type.Auto", "Auto")
                    : L("Backups.Snapshot.Type.Manual", "Manual"),
                    Status    = "Completed",
                    Label     = isAutoSnapshot
                        ? L("Backups.Snapshot.Label.Auto", "Auto backup")
                        : L("Backups.Snapshot.Label.Manual", "Manual backup"),
                    ProjectId = project?.Id.ToString(),
                    IsProtected = backup.IsProtected,
                    DestinationDisplay = destinationDisplay
                };

                _allSnapshots.Add(uiItem);
            }

            Interlocked.Increment(ref _snapshotRevision);

            // Rebuild the filtered history view + summary + mini-chart.
            RefreshSnapshotsView(true);
            RecalculateSummary();
            RefreshBackupDiskUsage();

            _lastProjectSignature = projectSignature;
            _lastBackupSignature = backupSignature;
            _lastAutoBackupSignature = autoSignature;
        }

        public void RefreshBackupDriveHealth()
        {
            RefreshBackupDiskUsage(includeHealthProbe: true);
        }

        private static int ComputeProjectSignature(IReadOnlyList<Project> projects)
        {
            unchecked
            {
                var hash = projects.Count;
                foreach (var project in projects)
                {
                    hash = (hash * 397) ^ project.Id;
                    hash = (hash * 397) ^ (project.Name?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.RootPath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                }
                return hash;
            }
        }

        private static int ComputeBackupSignature(IReadOnlyList<Backup> backups)
        {
            unchecked
            {
                var hash = backups.Count;
                foreach (var backup in backups)
                {
                    hash = (hash * 397) ^ backup.Id;
                    hash = (hash * 397) ^ backup.ProjectId;
                    hash = (hash * 397) ^ backup.CreatedUtc.GetHashCode();
                    hash = (hash * 397) ^ backup.TotalBytes.GetHashCode();
                    hash = (hash * 397) ^ (backup.IsImported ? 1 : 0);
                }
                return hash;
            }
        }

        private static int ComputeAutoBackupSignature(ISet<int>? disabledProjects)
        {
            if (disabledProjects is null || disabledProjects.Count == 0)
                return 0;

            unchecked
            {
                var hash = disabledProjects.Count;
                foreach (var id in disabledProjects.OrderBy(id => id))
                {
                    hash = (hash * 397) ^ id;
                }
                return hash;
            }
        }

        private void UpdateAutoBackupFlags(ISet<int>? disabledProjects)
        {
            var disabled = disabledProjects ?? new HashSet<int>();
            foreach (var item in ProjectBackups)
            {
                var parsed = int.TryParse(item.Id, out var projectId) ? projectId : -1;
                item.AutoBackupEnabled = parsed > 0 && !disabled.Contains(parsed);
            }
        }

    private void RefreshDestinationOptionsInternal(AppConfig config)
    {
        DestinationOptions.Clear();
            DestinationOptions.Add(new DestinationOption(
                string.Empty,
                L("Projects.Destination.Auto", "Auto (active destinations)")));
            DestinationOptions.Add(new DestinationOption(
                Project.DestinationAllId,
                L("Projects.Destination.All", "All destinations")));

            if (config.Backups.UseAdvancedDestinations && config.Backups.Destinations is { Count: > 0 })
            {
                foreach (var dest in config.Backups.Destinations)
                {
                    var label = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias;
                    if (!dest.Active)
                    {
                        var suffix = L("Projects.Destination.InactiveSuffix", " (inactive)");
                        label = $"{label}{suffix}";
                    }

                    var id = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias;
                    DestinationOptions.Add(new DestinationOption(id, label));
                }
            }
        }

        private void UpdateProjectDestinationDisplay(ProjectBackupItem item, AppConfig config)
        {
            var id = item.PreferredDestinationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                item.PreferredDestinationDisplay = L("Projects.Destination.Auto", "Auto (active destinations)");
                item.SetPreferredDestinationOption(DestinationOptions.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Id)));
                return;
            }

            if (string.Equals(id, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
            {
                item.PreferredDestinationDisplay = L("Projects.Destination.All", "All destinations");
                item.SetPreferredDestinationOption(DestinationOptions.FirstOrDefault(o =>
                    string.Equals(o.Id, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase)));
                return;
            }

            var match = config.Backups.Destinations.FirstOrDefault(d =>
                string.Equals(d.Alias ?? string.Empty, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Path ?? string.Empty, id, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                item.PreferredDestinationDisplay = string.IsNullOrWhiteSpace(match.Alias) ? match.Path : match.Alias;
            }
            else
            {
                item.PreferredDestinationDisplay = id;
            }

            var optionMatch = DestinationOptions.FirstOrDefault(o =>
                string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            if (optionMatch is null)
            {
                var fallback = DestinationOptions.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Id))
                               ?? DestinationOptions.FirstOrDefault();
                if (fallback != null)
                {
                    item.PreferredDestinationDisplay = fallback.Label;
                    item.SetPreferredDestinationOption(fallback);
                    return;
                }
            }

            item.SetPreferredDestinationOption(optionMatch);
        }

        private void OnPreferredDestinationChanged(ProjectBackupItem item)
        {
            if (!int.TryParse(item.Id, out var projectId) || projectId <= 0)
                return;

            var config = AppConfigStore.Load();
            UpdateProjectDestinationDisplay(item, config);
            PreferredDestinationChanged?.Invoke(projectId, item.PreferredDestinationId ?? string.Empty);
        }

        public void RefreshDestinationOptions(AppConfig config)
        {
            RefreshDestinationOptionsInternal(config);
            foreach (var project in ProjectBackups)
            {
                UpdateProjectDestinationDisplay(project, config);
            }
        }
    }

    // ---------- Models ----------

        public class BackupSnapshotItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public long SizeBytes { get; set; }
        private bool _isProtected;

        /// <summary>Snapshot type, e.g. "Auto" or "Manual".</summary>
        public string Type { get; set; } = "Manual";

        /// <summary>Status, e.g. "Completed", "Failed".</summary>
        public string Status { get; set; } = "Completed";

        /// <summary>Label shown inside the tag pill.</summary>
        public string? Label { get; set; }

        /// <summary>Localized type label for display (Auto/Manual).</summary>
        public string TypeLabel { get; set; } = string.Empty;

        /// <summary>Optional project id this snapshot belongs to; null for global.</summary>
        public string? ProjectId { get; set; }

        /// <summary>Destination endpoint that stored this backup.</summary>
        public string DestinationDisplay { get; set; } = string.Empty;

        public string SizeFormatted => FormatSize(SizeBytes);

        public bool IsImported { get; set; }

        /// <summary>Localized label for the imported tag.</summary>
        public string ImportedLabel { get; set; } = string.Empty;
        public string OriginMachineName { get; set; } = string.Empty;

        public bool IsProtected
        {
            get => _isProtected;
            set
            {
                if (_isProtected != value)
                {
                    _isProtected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProtected)));
                }
            }
        }

        // ---------- Tag pill background color ----------

        private static readonly IBrush DefaultBrush =
            new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF"));

        // Auto snapshots: blue-ish
        private static readonly IBrush AutoBrush =
            new ImmutableSolidColorBrush(Color.Parse("#333A7AFE"));

        // Manual snapshots: purple-ish
        private static readonly IBrush ManualBrush =
            new ImmutableSolidColorBrush(Color.Parse("#334568F2"));

        // Imported snapshots: teal-ish
        private static readonly IBrush ImportedBrush =
            new ImmutableSolidColorBrush(Color.Parse("#3346C6A1"));

        // Failed snapshots: red-ish
        private static readonly IBrush FailedBrush =
            new ImmutableSolidColorBrush(Color.Parse("#33FF4B4B"));

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

        public IBrush ImportedTagBackground => ImportedBrush;

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
        public string LatestBackupDisplay { get; set; } = string.Empty;
        public IBrush AccentBrush { get; set; } = new ImmutableSolidColorBrush(Color.Parse("#33405A"));
        public bool IsExpanded { get; set; }

        public ObservableCollection<BackupSnapshotItem> Snapshots { get; } =
            new ObservableCollection<BackupSnapshotItem>();
    }

    public class ProjectBackupItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetField<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;

        public DateTime? LastBackupTime { get; set; }
        public int       SnapshotCount  { get; set; }
        public long      TotalSizeBytes { get; set; }

        public bool AutoBackupEnabled
        {
            get => _autoBackupEnabled;
            set
            {
                if (!SetField(ref _autoBackupEnabled, value, nameof(AutoBackupEnabled)))
                    return;
                AutoBackupChanged?.Invoke(this);
            }
        }

        private bool _autoBackupEnabled = true;
        public Action<ProjectBackupItem>? AutoBackupChanged { get; set; }
        public Action<ProjectBackupItem>? PreferredDestinationChanged { get; set; }

        private string _preferredDestinationId = string.Empty;
        public string PreferredDestinationId
        {
            get => _preferredDestinationId;
            set => SetField(ref _preferredDestinationId, value ?? string.Empty, nameof(PreferredDestinationId));
        }

        private DestinationOption? _preferredDestinationOption;
        public DestinationOption? PreferredDestinationOption
        {
            get => _preferredDestinationOption;
            set
            {
                if (!SetField(ref _preferredDestinationOption, value, nameof(PreferredDestinationOption)))
                    return;

                PreferredDestinationId = value?.Id ?? string.Empty;
                PreferredDestinationChanged?.Invoke(this);
            }
        }

        private string _preferredDestinationDisplay = string.Empty;
        public string PreferredDestinationDisplay
        {
            get => _preferredDestinationDisplay;
            set => SetField(ref _preferredDestinationDisplay, value ?? string.Empty, nameof(PreferredDestinationDisplay));
        }

        public void SetPreferredDestinationOption(DestinationOption? option)
        {
            if (Equals(_preferredDestinationOption, option))
                return;

            _preferredDestinationOption = option;
            OnPropertyChanged(nameof(PreferredDestinationOption));
        }

        // Avatar
        public string AvatarInitials { get; private set; } = string.Empty;
        public string AvatarColor    { get; private set; } = "#33405A";
        public string? AvatarImagePath { get; private set; }
        public bool HasCustomAvatar => !string.IsNullOrWhiteSpace(AvatarImagePath);

        public void SetAvatarFromNameAndStore(string name, string projectPath, string? externalId)
        {
            AvatarInitials  = ComputeInitials(name);
            AvatarColor     = AvatarColorProvider.GetColor(name, projectPath, externalId);
            AvatarImagePath = AvatarStore.GetAvatarForProject(projectPath);
        }

        private static string ComputeInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "?";

            var parts = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
                return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();

            if (name.Length >= 2)
                return name.Substring(0, 2).ToUpperInvariant();

            return name.Substring(0, 1).ToUpperInvariant();
        }

        public string LastBackupDisplay =>
            LastBackupTime.HasValue
                ? LastBackupTime.Value.ToString("yyyy-MM-dd HH:mm")
                : LocalizationProvider.Service?.GetString("Backups.Summary.NoBackups") ?? "No backups yet";

        public string TotalSizeFormatted =>
            BackupSnapshotItem.FormatSize(TotalSizeBytes);
    }

    public class DestinationStatusItem : ViewModelBase
    {
        private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));
        private static readonly IBrush InfoBrush = new ImmutableSolidColorBrush(Color.Parse("#8E9BAF"));

        public string Id { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;

        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            set
            {
                var normalized = NormalizeDestinationStatus(value);
                if (_status == normalized)
                    return;
                _status = normalized;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsChecking));
            }
        }

        private static string NormalizeDestinationStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return string.Empty;

            var reachableLabel = LocalizationProvider.Service?.GetString("Destinations.Test.Reachable") ?? "Reachable";
            if (status.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("No changes", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("No backup", StringComparison.OrdinalIgnoreCase))
            {
                return reachableLabel;
            }

            return status;
        }

        public bool IsChecking =>
            string.Equals(Status, LocalizationProvider.Service?.GetString("Backups.Destinations.Pending") ?? "Pending", StringComparison.OrdinalIgnoreCase) ||
            Status.Contains("checking", StringComparison.OrdinalIgnoreCase) ||
            Status.Contains("testing", StringComparison.OrdinalIgnoreCase) ||
            Status.Contains("probing", StringComparison.OrdinalIgnoreCase);

        private bool _isActive = true;
        public bool IsActive
        {
            get => _isActive;
            set => SetField(ref _isActive, value);
        }

        private bool _isConfigurable = true;
        public bool IsConfigurable
        {
            get => _isConfigurable;
            set => SetField(ref _isConfigurable, value);
        }

        private string _severity = "Info";
        public string Severity
        {
            get => _severity;
            set
            {
                if (SetField(ref _severity, value))
                {
                    OnPropertyChanged(nameof(ReachabilityBrush));
                }
            }
        }

        private IBrush _dotBrush = InfoBrush;
        public IBrush DotBrush
        {
            get => _dotBrush;
            set => SetField(ref _dotBrush, value);
        }

        public IBrush ReachabilityBrush
        {
            get
            {
                return Severity switch
                {
                    "Success" => SuccessBrush,
                    "Warning" => WarningBrush,
                    "Error" => ErrorBrush,
                    _ => InfoBrush
                };
            }
        }

        private DateTime? _lastCheckedUtc;
        public DateTime? LastCheckedUtc
        {
            get => _lastCheckedUtc;
            set
            {
                if (SetField(ref _lastCheckedUtc, value))
                {
                    OnPropertyChanged(nameof(LastCheckedDisplay));
                }
            }
        }

        public string LastCheckedDisplay
        {
            get
            {
                if (!LastCheckedUtc.HasValue)
                {
                    return LocalizationProvider.Service?.GetString("Destinations.Status.LastCheckedNever")
                           ?? "Last checked: never";
                }

                var label = LocalizationProvider.Service?.GetString("Destinations.Status.LastChecked")
                           ?? "Last checked: {0}";
                var local = LastCheckedUtc.Value.ToLocalTime().ToString("HH:mm:ss");
                return string.Format(CultureInfo.CurrentCulture, label, local);
            }
        }

        public static string GetId(BackupDestination dest) =>
            string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
    }

    public class BackupProgressItem : ViewModelBase
    {
        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        public string ProjectId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;

        public Action<BackupProgressItem?>? CancelRequested { get; set; }

        private bool _allowCancel = true;
        public bool AllowCancel
        {
            get => _allowCancel;
            set
            {
                if (_allowCancel == value)
                    return;

                _allowCancel = value;
                OnPropertyChanged(nameof(AllowCancel));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(IsIndeterminate));
                NotifyProgressPresentationChanged();
            }
        }

        public ICommand CancelCommand { get; }

        public BackupProgressItem()
        {
            CancelCommand = new RelayCommand(_ => CancelRequested?.Invoke(this));
        }

        private string _destinationLabel = string.Empty;
        public string DestinationLabel
        {
            get => _destinationLabel;
            set
            {
                if (_destinationLabel == value)
                    return;

                _destinationLabel = value ?? string.Empty;
                OnPropertyChanged(nameof(DestinationLabel));
                OnPropertyChanged(nameof(DestinationDisplay));
                OnPropertyChanged(nameof(HasDestinationDisplay));
            }
        }

        public string DestinationDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_destinationLabel))
                    return string.Empty;

                var prefix = L("Projects.List.DestinationPrefix", "Destination: ");
                return $"{prefix}{_destinationLabel}";
            }
        }

        public bool HasDestinationDisplay => !string.IsNullOrWhiteSpace(DestinationDisplay);

        private double _progress;
        private double _displayProgress;
        private string _lastStageKey = string.Empty;
        private DateTime _stageStartUtc = DateTime.UtcNow;
        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) < 0.0001)
                    return;

                _progress = value;
                UpdateDisplayProgress();
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(IsEstimate));
                NotifyProgressPresentationChanged();
            }
        }

        public double DisplayProgress => _displayProgress;

        public bool HasProgress => HasRawProgress;

        private bool HasRawProgress => _progress > 0.1d;

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
                OnPropertyChanged(nameof(HasCurrentFile));
                OnPropertyChanged(nameof(CurrentFileDisplay));
                OnPropertyChanged(nameof(HasCurrentFileDisplay));
                OnPropertyChanged(nameof(StageLabel));
                OnPropertyChanged(nameof(IsEstimate));
                UpdateDisplayProgress();
                NotifyProgressPresentationChanged();
            }
        }

        public bool HasCurrentFile => !string.IsNullOrWhiteSpace(_currentFile);

        private string _etaText = string.Empty;
        private string _lastProgressDetail = string.Empty;
        public string EtaText
        {
            get => _etaText;
            set
            {
                if (_etaText == value)
                    return;

                _etaText = value ?? string.Empty;
                OnPropertyChanged(nameof(EtaText));
                OnPropertyChanged(nameof(HasEtaText));
                OnPropertyChanged(nameof(EtaDisplay));
                OnPropertyChanged(nameof(HasEtaDisplay));
                OnPropertyChanged(nameof(IsEstimate));
                var detail = ExtractProgressDetail(EtaDisplay);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    _lastProgressDetail = detail;
                }
                OnPropertyChanged(nameof(CurrentFileDisplay));
                OnPropertyChanged(nameof(HasCurrentFileDisplay));
                OnPropertyChanged(nameof(StageLabel));
                UpdateDisplayProgress();
                NotifyProgressPresentationChanged();
            }
        }

        public bool HasEtaText => !string.IsNullOrWhiteSpace(_etaText);

        public string EtaDisplay => NormalizeEtaText(_etaText);

        public bool HasEtaDisplay => !string.IsNullOrWhiteSpace(EtaDisplay);

        public bool IsEstimate =>
            !HasRawProgress &&
            ContainsToken(_currentFile, L("Backups.Progress.Estimating", "Estimating...")) &&
            HasEtaText;

        public string EstimateLabel => L("Backups.Preflight.Title", "Backup estimate");

        public string CurrentFileDisplay
        {
            get
            {
                if (TryExtractFileName(_currentFile, out var fileName))
                    return fileName;

                if (!string.IsNullOrWhiteSpace(_lastProgressDetail) && !ContainsSpeedOrEta(_lastProgressDetail))
                    return _lastProgressDetail;

                return string.Empty;
            }
        }

        public bool HasCurrentFileDisplay => !string.IsNullOrWhiteSpace(CurrentFileDisplay);

        public bool IsCompleted => string.Equals(GetStageKey(), "Completed", StringComparison.OrdinalIgnoreCase);

        public bool ShowEta => Progress < 100d && HasEtaDisplay;

        public bool CanCancel => AllowCancel && !IsCompleted;

        public bool ShowPercent => AllowCancel && HasProgress && IsProgressReliable;

        public bool IsIndeterminate => !AllowCancel || !IsProgressReliable || IsStageIndeterminate;

        public string ProgressLabel =>
            IsProgressReliable && HasProgress
                ? string.Format(CultureInfo.CurrentCulture, "{0:0}%", DisplayProgress)
                : L("Backups.Progress.Estimating", "Estimating...");

        public string StageLabel
        {
            get
            {
                var stageKey = GetStageKey();
                return stageKey switch
                {
                    "Completed" => L("Backups.Status.Completed", "Completed"),
                    "Cancelling" => L("Backups.Status.Cancelling", "Cancelling..."),
                    "Deleting" => L("Backups.Stage.Deleting", "Deleting"),
                    "Compressing" => L("Backups.Stage.Compressing", "Compressing archive"),
                    "Uploading" => L("Backups.Stage.Uploading", "Uploading archive"),
                    "Hashing" => L("Backups.Stage.Hashing", "Hashing files"),
                    "Copying" => L("Backups.Stage.Copying", "Copying files"),
                    "Preparing" => L("Backups.Stage.Preparing", "Preparing"),
                    "BackingUp" => L("Backups.Stage.BackingUp", "Backing up files"),
                    _ => L("Backups.Stage.Working", "Working...")
                };
            }
        }

        public string StageDisplay
            => IsCompleted ? StageLabel : $"{StageLabel} - {FormatElapsed(_stageStartUtc)}";

        public IBrush StageBrush => GetStageBrush();

        private bool IsProgressReliable
        {
            get
            {
                if (!HasRawProgress)
                    return false;

                return true;
            }
        }

        private bool IsStageIndeterminate
            => string.Equals(StageLabel, L("Backups.Stage.Preparing", "Preparing"), StringComparison.OrdinalIgnoreCase);

        private bool IsHashingStage => string.Equals(GetStageKey(), "Hashing", StringComparison.OrdinalIgnoreCase);

        private bool HasCompletionSignal()
        {
            if (ContainsToken(_etaText, "Completed"))
                return true;

            return ContainsToken(_currentFile, L("Backups.Status.Completed", "Completed"))
                || ContainsToken(_currentFile, L("Backups.Status.NoChanges", "No changes detected"))
                || ContainsToken(_currentFile, L("Backups.Status.Cancelled", "Cancelled"))
                || ContainsToken(_currentFile, L("Backups.Status.Deleted", "Deleted"));
        }

        private static bool ContainsToken(string? value, string token)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
                return false;

            return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();

            // Drop destination prefix like "[Alias]" if present.
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                var end = trimmed.IndexOf(']');
                if (end >= 0 && end + 1 < trimmed.Length)
                {
                    trimmed = trimmed[(end + 1)..].Trim();
                }
            }

            var candidate = trimmed;
            if (candidate.Contains('\\') || candidate.Contains('/'))
            {
                candidate = Path.GetFileName(candidate);
            }

            return candidate;
        }

        private static bool TryExtractFileName(string value, out string fileName)
        {
            fileName = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!value.Contains('\\') && !value.Contains('/'))
                return false;

            var extracted = ExtractFileName(value);
            if (string.IsNullOrWhiteSpace(extracted))
                return false;

            fileName = extracted;
            return true;
        }

        private static bool ContainsSpeedOrEta(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains("ETA", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("MB/s", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractProgressDetail(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();
            if (string.Equals(trimmed, L("Backups.Progress.CopyingRobocopy", "Copying files (robocopy)..."), StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (trimmed.StartsWith("Copying ", StringComparison.OrdinalIgnoreCase))
                return L("Backups.Progress.MovedPrefix", "moved ") + trimmed["Copying ".Length..];
            if (trimmed.StartsWith("Compressing ", StringComparison.OrdinalIgnoreCase))
                return trimmed["Compressing ".Length..];
            if (trimmed.StartsWith("Uploading ", StringComparison.OrdinalIgnoreCase))
                return trimmed["Uploading ".Length..];
            if (trimmed.StartsWith("Hashing ", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            return trimmed;
        }

        private static string NormalizeEtaText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();
            if (trimmed.Contains("Waiting for first file", StringComparison.OrdinalIgnoreCase))
                return L("Backups.Progress.WaitingForFirstFile", "Waiting for first file...");

            if (trimmed.Contains("Copying files (robocopy)", StringComparison.OrdinalIgnoreCase))
                return L("Backups.Progress.CopyingRobocopy", "Copying files (robocopy)...");

            return trimmed;
        }

        private void NotifyProgressPresentationChanged()
        {
            OnPropertyChanged(nameof(DisplayProgress));
            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(ProgressLabel));
            OnPropertyChanged(nameof(ShowPercent));
            OnPropertyChanged(nameof(ShowEta));
            OnPropertyChanged(nameof(StageLabel));
            OnPropertyChanged(nameof(StageDisplay));
            OnPropertyChanged(nameof(StageBrush));
            OnPropertyChanged(nameof(EtaDisplay));
            OnPropertyChanged(nameof(HasEtaDisplay));
        }

        private void UpdateDisplayProgress()
        {
            var stageKey = GetStageKey();
            if (!string.Equals(stageKey, _lastStageKey, StringComparison.OrdinalIgnoreCase))
            {
                _displayProgress = 0d;
                _lastStageKey = stageKey;
                _stageStartUtc = DateTime.UtcNow;
                OnPropertyChanged(nameof(StageDisplay));
            }

            if (!HasRawProgress || !IsProgressReliable)
            {
                _displayProgress = 0d;
                return;
            }

            var next = Math.Clamp(_progress, 0d, 100d);
            if (!HasCompletionSignal() && !IsHashingStage)
            {
                next = Math.Min(next, 99d);
            }

            if (IsCopyingStage && (DateTime.UtcNow - _stageStartUtc) < TimeSpan.FromSeconds(2) && next >= 99d)
            {
                next = Math.Min(next, 5d);
            }

            if (next < _displayProgress)
            {
                if (!HasCompletionSignal())
                {
                    _displayProgress = next;
                }
                return;
            }

            _displayProgress = next;
        }

        public void TickStageClock()
        {
            if (!IsCompleted)
            {
                OnPropertyChanged(nameof(StageDisplay));
            }
        }

        private static string FormatElapsed(DateTime startUtc)
        {
            var elapsed = DateTime.UtcNow - startUtc;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            return elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }

        private string GetStageKey()
        {
            if (ContainsToken(_currentFile, L("Backups.Status.Completed", "Completed")) ||
                ContainsToken(_currentFile, L("Backups.Status.NoChanges", "No changes detected")) ||
                ContainsToken(_currentFile, L("Backups.Status.Cancelled", "Cancelled")) ||
                ContainsToken(_currentFile, L("Backups.Status.Deleted", "Deleted")) ||
                (ContainsToken(_etaText, "Completed") && !ContainsToken(_etaText, "Hashing")))
                return "Completed";

            if (ContainsToken(_currentFile, L("Backups.Status.Cancelling", "Cancelling...")))
                return "Cancelling";

            if (ContainsToken(_etaText, "Compressing"))
                return "Compressing";

            if (ContainsToken(_etaText, "Uploading"))
                return "Uploading";

            if (ContainsToken(_etaText, "Hashing"))
                return "Hashing";

            if (ContainsToken(_etaText, "Copying"))
                return "Copying";

            if (ContainsToken(_currentFile, L("Backups.Status.Deleting", "Deleting backup files...")) ||
                ContainsToken(_currentFile, L("Backups.Stage.Deleting", "Deleting")))
                return "Deleting";

            if (ContainsToken(_currentFile, L("Backups.Status.Preparing", "Preparing backup...")))
                return "Preparing";

            if (ContainsToken(_currentFile, "Reusing existing snapshot") ||
                ContainsToken(_currentFile, "Creating snapshot") ||
                ContainsToken(_currentFile, "Hashing"))
                return "Hashing";

            if (ContainsToken(_currentFile, L("Backups.Status.Running", "Running backup...")) ||
                ContainsToken(_currentFile, L("Backups.Status.RunningMultiple", "Running backups...")))
                return "BackingUp";

            if (HasCurrentFile)
                return "BackingUp";

            return "Working";
        }

        private bool IsCopyingStage => string.Equals(GetStageKey(), "Copying", StringComparison.OrdinalIgnoreCase);

        private static readonly IBrush StageCompletedBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush StageCancelBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));
        private static readonly IBrush StageCompressBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush StageUploadBrush = new ImmutableSolidColorBrush(Color.Parse("#22CCFF"));
        private static readonly IBrush StageHashBrush = new ImmutableSolidColorBrush(Color.Parse("#9B6BFF"));
        private static readonly IBrush StageCopyBrush = new ImmutableSolidColorBrush(Color.Parse("#4C8DFF"));
        private static readonly IBrush StageBackupBrush = new ImmutableSolidColorBrush(Color.Parse("#3A7AFE"));
        private static readonly IBrush StagePrepareBrush = new ImmutableSolidColorBrush(Color.Parse("#8E9BAF"));

        private IBrush GetStageBrush()
        {
            var stageKey = GetStageKey();
            return stageKey switch
            {
                "Completed"   => StageCompletedBrush,
                "Cancelling"  => StageCancelBrush,
                "Deleting"    => StageCancelBrush,
                "Compressing" => StageCompressBrush,
                "Uploading"   => StageUploadBrush,
                "Hashing"     => StageHashBrush,
                "Copying"     => StageCopyBrush,
                "BackingUp"   => StageBackupBrush,
                "Preparing"   => StagePrepareBrush,
                _             => StagePrepareBrush
            };
        }
    }

    public class SnapshotActivityPoint
    {
        private static readonly IBrush SnapshotAutoBrush = new ImmutableSolidColorBrush(Color.Parse("#3A7AFE"));
        private static readonly IBrush SnapshotManualBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush SnapshotImportedBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush SnapshotEmptyBrush = new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF"));
        public string DayLabel { get; set; } = string.Empty;
        public bool ShowLabel { get; set; } = true;
        public int AutoCount { get; set; }
        public int ManualCount { get; set; }
        public int ImportedCount { get; set; }
        public long TotalBytes { get; set; }
        public double AutoHeight { get; set; }
        public double ManualHeight { get; set; }
        public double ImportedHeight { get; set; }
        public bool IsEmpty => AutoCount + ManualCount + ImportedCount == 0;
        public double EmptyHeight => IsEmpty ? 8 : 0;
        public bool HasAuto => AutoCount > 0;
        public bool HasManual => ManualCount > 0;
        public bool HasImported => ImportedCount > 0;
        public IBrush AutoBrush { get; set; } = SnapshotAutoBrush;
        public IBrush ManualBrush { get; set; } = SnapshotManualBrush;
        public IBrush ImportedBrush { get; set; } = SnapshotImportedBrush;
        public IBrush EmptyBrush { get; set; } = SnapshotEmptyBrush;
        public string TooltipText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Minimal ICommand implementation so we don't depend on any toolkit.
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


