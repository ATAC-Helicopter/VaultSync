using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using VaultSync.Core.Models;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    public class BackupsViewModel : ViewModelBase
    {
        private enum SnapshotSummaryExportFormat
        {
            Text,
            Json
        }

        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private const string AllProjectsKey = "Backups.Section.HistoryFilterAllProjects";
        private const string AllProjectsFallback = "All projects";
        private const string EncryptedPolicyKey = "Projects.EncryptionPolicy.Encrypted";
        private const string EncryptedFallback = "Encrypted";
        private const string ManualBackupType = "Manual";
        private const string NoBackupsKey = "Backups.Summary.NoBackups";
        private const string NoBackupsFallback = "No backups yet";
        private const string PlainPolicyKey = "Projects.EncryptionPolicy.Plain";
        private const string PlainFallback = "Plain";
        private const string TimestampMinuteFormat = "yyyy-MM-dd HH:mm";
        private const string UnknownProjectFallback = "Unknown project";
        private const string UnknownProjectGroupKey = "Backups.Section.Group.Unknown";
        private const string DefaultAccentColor = "#33405A";

        private static string Lf(string key, string fallback, params object[] args)
        {
            string fmt = L(key, fallback);
            return args.Length == 0
                ? fmt
                : string.Format(CultureInfo.CurrentCulture, fmt, args);
        }

        private static readonly IBrush HealthOkBrush = new ImmutableSolidColorBrush(Colors.LimeGreen);
        private static readonly IBrush HealthWarningBrush = new ImmutableSolidColorBrush(Colors.Orange);
        private static readonly IBrush HealthFailingBrush = new ImmutableSolidColorBrush(Colors.Tomato);
        private static readonly IBrush HealthUnknownBrush = new ImmutableSolidColorBrush(Colors.Gray);
        private static readonly IBrush FreshnessGoodBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush FreshnessModerateBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush FreshnessStaleBrush = new ImmutableSolidColorBrush(Color.Parse("#F56A5A"));
        private static readonly IBrush FreshnessUnknownBrush = new ImmutableSolidColorBrush(Color.Parse("#7F8FA8"));
        private static readonly ConcurrentDictionary<string, ImmutableSolidColorBrush> AccentBrushCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly IAppConfigStore _configStore;
        private readonly IRepositoryFactory _repositoryFactory;
        private readonly Func<int, int, CancellationToken, Task<SnapshotCompareResult>> _compareSnapshotsAsync;
        private readonly Func<Action, Task> _invokeOnUiAsync;
        // Simple SetProperty helper - note: no PropertyChanged here, we just need
        // equality checks + storage for our internal properties.
        protected static bool SetProperty<T>(ref T storage, T value)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;
            storage = value;
            return true;
        }

        public enum DestinationStatus
        {
            None,
            Pending,
            Inactive,
            Reachable,
            ReadOnly,
            Unavailable
        }

        public enum SeverityStatus
        {
            None,
            Success,
            Warning,
            Error
        }

        // Filtered view for the history list
        public ObservableCollection<BackupSnapshotItem> Snapshots { get; } =
            [];

        // Grouped view (per project) for the right-hand history panel
        public ObservableCollection<SnapshotProjectGroup> SnapshotGroups { get; } =
            [];

        // Internal full list for summary + filtering
        private readonly List<BackupSnapshotItem> _allSnapshots = [];
        private readonly List<BackupSnapshotItem> _filteredSnapshots = [];
        private readonly Dictionary<string, ProjectBackupItem> _projectLookupById =
            new Dictionary<string, ProjectBackupItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DestinationQuotaPlan> _destinationQuotaPlansById =
            new Dictionary<string, DestinationQuotaPlan>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PendingBackupUpdate> _pendingActiveBackupUpdates = new();
        private readonly DispatcherTimer _activeBackupFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

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
        private double _lastSummaryViewportWidth = 1400;
        private GridLength _mainAreaLeftColumnWidth = new GridLength(3, GridUnitType.Star);
        private GridLength _mainAreaRightColumnWidth = new GridLength(2, GridUnitType.Star);
        private int _mainAreaRightPanelColumn = 1;
        private int _mainAreaRightPanelRow = 0;

        private sealed record PendingBackupUpdate(
            string ProjectId,
            string ProjectName,
            double Progress,
            string CurrentFile,
            string EtaText,
            ProtectionActivityState ActivityState,
            bool AllowCancel,
            string? DestinationLabel,
            string PolicyText);

        private BackupSnapshotItem? _selectedSnapshotA;
        private readonly RelayCommand? _compareSelectedSnapshotsRelayCommand;
        private readonly RelayCommand? _selectPreviousDiffFileRelayCommand;
        private readonly RelayCommand? _selectNextDiffFileRelayCommand;
        public BackupSnapshotItem? SelectedSnapshotA
        {
            get => _selectedSnapshotA;
            set
            {
                if (SetProperty(ref _selectedSnapshotA, value))
                {
                    OnPropertyChanged(nameof(SelectedSnapshotA));
                    OnPropertyChanged(nameof(CanCompareSelectedSnapshots));
                    _compareSelectedSnapshotsRelayCommand?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(CompareSelectionHint));
                    SelectDefaultCompareCounterpart(value, selectPointB: true);
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
                    OnPropertyChanged(nameof(CanCompareSelectedSnapshots));
                    _compareSelectedSnapshotsRelayCommand?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(CompareSelectionHint));
                    SelectDefaultCompareCounterpart(value, selectPointB: false);
                }
            }
        }

        // Type + project filter state
        private string _currentTypeFilter = "All";
        private string? _currentProjectIdFilter = null;
        public string HistoryFilterProjectLabel { get; private set; } = L(AllProjectsKey, AllProjectsFallback);
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
            [];
        public ObservableCollection<BackupsProjectSortOption> ProjectSortOptions { get; } =
            [];
        public ObservableCollection<DestinationOption> DestinationOptions { get; } =
            [];
        public ObservableCollection<EncryptionPolicyOption> EncryptionPolicyOptions { get; } =
            [];
        public ObservableCollection<RestoreModeOption> RestoreModeOptions { get; } =
            [];
        public ObservableCollection<VerificationPolicyOption> VerificationPolicyOptions { get; } =
            [];

        public event Action<int, bool>? AutoBackupPreferenceChanged;
        public event Action<DestinationStatusItem, bool>? DestinationActiveChanged;
        public event Action<int, string>? PreferredDestinationChanged;
        public event Action<int, string>? ProjectEncryptionPolicyChanged;
        public event Action<int, string>? ProjectRestoreModeChanged;
        public event Action<int, string>? ProjectVerificationPolicyChanged;
        public event Action<int>? ManageProjectEncryptionRequested;

        // Appearance
        public bool ShowProjectAvatars { get; private set; } = true;

        // Active per-project backup progress items (for running backups)
        public ObservableCollection<BackupProgressItem> ActiveBackups { get; } =
            [];
        public bool HasActiveBackups => ActiveBackups.Count > 0;
        private readonly DispatcherTimer _activeBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        // Per-destination status for the current backup run
        public ObservableCollection<DestinationStatusItem> DestinationStatuses { get; } =
            [];
        public bool HasDestinationStatuses => DestinationStatuses.Count > 0;
        public ObservableCollection<DestinationStatusItem> ActiveDestinationStatuses { get; } =
            [];
        public bool HasActiveDestinationStatuses => ActiveDestinationStatuses.Count > 0;
        public bool CanToggleDestinations => !_isBusy;
        private readonly RelayCommand _toggleRestoreReadinessIssuesCommand;
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

        private bool _isActiveView;
        private string _projectSortMode = "latest";
        private BackupsProjectSortOption? _selectedProjectSortOption;
        public bool IsActiveView
        {
            get => _isActiveView;
            set
            {
                if (SetProperty(ref _isActiveView, value))
                {
                    OnPropertyChanged(nameof(IsActiveView));
                }
            }
        }

        public BackupsProjectSortOption? SelectedProjectSortOption
        {
            get => _selectedProjectSortOption;
            set
            {
                if (!SetProperty(ref _selectedProjectSortOption, value))
                    return;

                OnPropertyChanged(nameof(SelectedProjectSortOption));

                string nextMode = value?.Id ?? "latest";
                if (string.Equals(_projectSortMode, nextMode, StringComparison.OrdinalIgnoreCase))
                    return;

                _projectSortMode = nextMode;
                SortProjectBackups();
            }
        }

        private int _diskUsageInFlight;
        private readonly object _healthProbeGate = new();
        private DateTime _lastHealthProbeUtc = DateTime.MinValue;
        private static readonly TimeSpan HealthProbeCooldown = TimeSpan.FromMinutes(10);
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

        private sealed record SnapshotViewRefreshResult(
            SnapshotFilterState FilterState,
            int Revision,
            List<BackupSnapshotItem> FilteredSnapshots,
            List<SnapshotProjectGroup> SnapshotGroups);

        private sealed record SnapshotGroupText(
            string GlobalProjectName,
            string UnknownProjectName,
            string SingleSnapshotFormat,
            string MultipleSnapshotsFormat);

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
            [];
        public double SnapshotActivityChartHeight { get; private set; } = 160;

        // Summary properties (bound in the top cards)
        public int TotalSnapshots { get; private set; }
        public bool HasAnyBackups { get; private set; }
        public int SnapshotsThisWeek { get; private set; }
        public int SnapshotsToday { get; private set; }
        public int SnapshotsYesterday { get; private set; }

        public int AutoSnapshotsThisWeek { get; private set; }
        public int ManualSnapshotsThisWeek { get; private set; }
        public int ImportedSnapshotsThisWeek { get; private set; }

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

        public GridLength MainAreaLeftColumnWidth
        {
            get => _mainAreaLeftColumnWidth;
            private set
            {
                if (SetProperty(ref _mainAreaLeftColumnWidth, value))
                {
                    OnPropertyChanged(nameof(MainAreaLeftColumnWidth));
                }
            }
        }

        public GridLength MainAreaRightColumnWidth
        {
            get => _mainAreaRightColumnWidth;
            private set
            {
                if (SetProperty(ref _mainAreaRightColumnWidth, value))
                {
                    OnPropertyChanged(nameof(MainAreaRightColumnWidth));
                }
            }
        }

        public int MainAreaRightPanelColumn
        {
            get => _mainAreaRightPanelColumn;
            private set
            {
                if (SetProperty(ref _mainAreaRightPanelColumn, value))
                {
                    OnPropertyChanged(nameof(MainAreaRightPanelColumn));
                }
            }
        }

        public int MainAreaRightPanelRow
        {
            get => _mainAreaRightPanelRow;
            private set
            {
                if (SetProperty(ref _mainAreaRightPanelRow, value))
                {
                    OnPropertyChanged(nameof(MainAreaRightPanelRow));
                }
            }
        }

        public string LastBackupDisplay { get; private set; } =
            L(NoBackupsKey, NoBackupsFallback);
        public string LastBackupRelative { get; private set; } = "-";
        public string LastBackupSecondaryLine { get; private set; } =
            L("Backups.Summary.LastBackupSize", "Size -");
        public string LastBackupSizeValueFormatted { get; private set; } = "0 B";
        public string LastBackupProjectName { get; private set; } = "-";
        public string LastBackupTypeDisplay { get; private set; } = "-";
        public string LastBackupDestinationDisplay { get; private set; } = "-";
        public string LastBackupSecurityDisplay { get; private set; } = "-";
        public string TotalBackupSizeFormatted { get; private set; } = "0 B";
        public int LocalSnapshotsCount { get; private set; }
        public string TotalStoredLocalLine { get; private set; } =
            Lf("Backups.Summary.LocalTotal", "Local total: {0}", "0 B");
        public string TotalStoredLocalValueFormatted { get; private set; } = "0 B";
        public string TotalStoredImportedLine { get; private set; } =
            Lf("Backups.Summary.ImportedTotal", "Imported total: {0}", "0 B");
        public string TotalStoredImportedValueFormatted { get; private set; } = "0 B";
        public int ImportedSnapshotsCount { get; private set; }
        public double LastBackupFreshnessPercent { get; private set; }
        public string LastBackupFreshnessLabel { get; private set; } = "-";
        public string LastBackupFreshnessTooltip { get; private set; } = string.Empty;
        public IBrush LastBackupFreshnessBrush { get; private set; } = FreshnessUnknownBrush;
        public double ThisWeekAutoPercent { get; private set; }
        public double ThisWeekManualPercent { get; private set; }
        public double ThisWeekImportedPercent { get; private set; }
        public double StorageLocalPercent { get; private set; }
        public double StorageImportedPercent { get; private set; }
        public ObservableCollection<StorageConsumerItem> TopStorageConsumers { get; } =
            [];
        public bool HasTopStorageConsumers => TopStorageConsumers.Count > 0;
        public int HealthHealthyProjects { get; private set; }
        public int HealthAgingProjects { get; private set; }
        public int HealthStaleProjects { get; private set; }
        public int HealthNoBackupProjects { get; private set; }
        public double HealthHealthyPercent { get; private set; }
        public double HealthAgingPercent { get; private set; }
        public double HealthStalePercent { get; private set; }
        public double HealthNoBackupPercent { get; private set; }
        public string BackupHealthSummaryLine { get; private set; } = string.Empty;
        public int RestoreReadinessReadyProjects { get; private set; }
        public int RestoreReadinessAttentionProjects { get; private set; }
        public int RestoreReadinessRiskProjects { get; private set; }
        public int RestoreReadinessUnavailableProjects { get; private set; }
        public double RestoreReadinessReadyPercent { get; private set; }
        public double RestoreReadinessAttentionPercent { get; private set; }
        public double RestoreReadinessRiskPercent { get; private set; }
        public double RestoreReadinessUnavailablePercent { get; private set; }
        public string RestoreReadinessHeadline { get; private set; } = string.Empty;
        public string RestoreReadinessDetail { get; private set; } = string.Empty;
        public ObservableCollection<RestoreReadinessIssueItem> RestoreReadinessIssues { get; } = [];
        public bool HasRestoreReadinessIssues => RestoreReadinessIssues.Count > 0;
        private bool _showRestoreReadinessIssues;
        public bool ShowRestoreReadinessIssues
        {
            get => _showRestoreReadinessIssues;
            private set
            {
                if (SetProperty(ref _showRestoreReadinessIssues, value))
                    OnPropertyChanged(nameof(ShowRestoreReadinessIssues));
            }
        }
        public ICommand ToggleRestoreReadinessIssuesCommand => _toggleRestoreReadinessIssuesCommand;

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
                    BackupDiskHealthVisible = !string.IsNullOrWhiteSpace(_backupDiskHealthText);
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

        private bool _backupDiskHealthVisible = true;
        public bool BackupDiskHealthVisible
        {
            get => _backupDiskHealthVisible;
            private set
            {
                if (SetProperty(ref _backupDiskHealthVisible, value))
                {
                    OnPropertyChanged(nameof(BackupDiskHealthVisible));
                }
            }
        }

        // Notification state for the Backups view (reusable notification model)
        public NotificationState Notification { get; } = new NotificationState();

        // Popup dialog state for verification failures
        private string _verificationPopupMessage = string.Empty;
        private bool   _isVerificationPopupOpen;
        private string? _verificationFailedBackupId;
        private bool _isDiffPreviewOpen;
        private string _diffPreviewTitle = string.Empty;
        private string _diffPreviewText = string.Empty;
        private string _diffPreviewMetaLine = string.Empty;
        private string _diffPreviewTrigger = string.Empty;
        private string _diffPreviewMode = string.Empty;
        private string _diffPreviewImportedDisplay = string.Empty;
        private string _diffPreviewEncryptionDisplay = string.Empty;
        private int _diffPreviewAdded;
        private int _diffPreviewModified;
        private int _diffPreviewDeleted;
        private string _diffPreviewNet = string.Empty;
        private readonly List<DiffPreviewFileItem> _allDiffPreviewFiles = [];
        private readonly Dictionary<DiffPreviewFileItem, DiffPreviewTreeNode> _diffPreviewTreeNodes = [];
        private DiffPreviewFileItem? _selectedDiffPreviewFile;
        private DiffPreviewTreeNode? _selectedDiffPreviewTreeNode;
        private string _diffFileSearchText = string.Empty;
        private DiffPreviewKindFilterItem? _selectedDiffFileKindFilter;
        private string _diffFileContentText = string.Empty;
        private string _diffFileContentStatus = string.Empty;
        private int _diffFileAddedLines;
        private int _diffFileDeletedLines;
        private BackupSnapshotItem? _diffOlderSnapshot;
        private BackupSnapshotItem? _diffNewerSnapshot;
        private int _diffContentRequestVersion;
        private CancellationTokenSource? _diffContentCts;
        private bool _isSnapshotCompareBusy;
        private CancellationTokenSource? _snapshotCompareCts;
        private string _diffFileResultsLabel = string.Empty;
        private string _diffFileCompactResultsLabel = string.Empty;
        private string _diffPreviewEmptyTitle = string.Empty;
        private string _diffPreviewEmptyMessage = string.Empty;

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

        public bool IsDiffPreviewOpen
        {
            get => _isDiffPreviewOpen;
            set
            {
                if (SetProperty(ref _isDiffPreviewOpen, value))
                {
                    OnPropertyChanged(nameof(IsDiffPreviewOpen));
                }
            }
        }

        public string DiffPreviewTitle
        {
            get => _diffPreviewTitle;
            set
            {
                if (SetProperty(ref _diffPreviewTitle, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewTitle));
                }
            }
        }

        public string DiffPreviewText
        {
            get => _diffPreviewText;
            set
            {
                if (SetProperty(ref _diffPreviewText, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewText));
                }
            }
        }

        public string DiffPreviewMetaLine
        {
            get => _diffPreviewMetaLine;
            set
            {
                if (SetProperty(ref _diffPreviewMetaLine, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewMetaLine));
                }
            }
        }

        public string DiffPreviewTrigger
        {
            get => _diffPreviewTrigger;
            set
            {
                if (SetProperty(ref _diffPreviewTrigger, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewTrigger));
                }
            }
        }

        public string DiffPreviewMode
        {
            get => _diffPreviewMode;
            set
            {
                if (SetProperty(ref _diffPreviewMode, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewMode));
                }
            }
        }

        public string DiffPreviewImportedDisplay
        {
            get => _diffPreviewImportedDisplay;
            set
            {
                if (SetProperty(ref _diffPreviewImportedDisplay, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewImportedDisplay));
                }
            }
        }

        public string DiffPreviewEncryptionDisplay
        {
            get => _diffPreviewEncryptionDisplay;
            set
            {
                if (SetProperty(ref _diffPreviewEncryptionDisplay, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewEncryptionDisplay));
                }
            }
        }

        public int DiffPreviewAdded
        {
            get => _diffPreviewAdded;
            set
            {
                if (SetProperty(ref _diffPreviewAdded, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewAdded));
                }
            }
        }

        public int DiffPreviewModified
        {
            get => _diffPreviewModified;
            set
            {
                if (SetProperty(ref _diffPreviewModified, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewModified));
                }
            }
        }

        public int DiffPreviewDeleted
        {
            get => _diffPreviewDeleted;
            set
            {
                if (SetProperty(ref _diffPreviewDeleted, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewDeleted));
                }
            }
        }

        public string DiffPreviewNet
        {
            get => _diffPreviewNet;
            set
            {
                if (SetProperty(ref _diffPreviewNet, value))
                {
                    OnPropertyChanged(nameof(DiffPreviewNet));
                }
            }
        }

        public ObservableCollection<DiffPreviewPathItem> DiffPreviewTopPaths { get; } = [];
        public bool HasDiffPreviewTopPaths => DiffPreviewTopPaths.Count > 0;
        public ObservableCollection<DiffPreviewFileItem> DiffPreviewFiles { get; } = [];
        public ObservableCollection<DiffPreviewTreeNode> DiffPreviewTreeRoots { get; } = [];
        public IReadOnlyList<DiffPreviewLineItem> DiffFileContentLines { get; private set; } = [];
        public ObservableCollection<DiffPreviewKindFilterItem> DiffFileKindFilters { get; } = [];
        public bool HasDiffPreviewFiles => _allDiffPreviewFiles.Count > 0;
        public bool HasNoDiffPreviewFiles => !HasDiffPreviewFiles;
        public bool HasDiffFileContentLines => DiffFileContentLines.Count > 0;
        public bool HasNoDiffFileContentLines => !HasDiffFileContentLines;
        public bool HasDiffFileLineChanges => DiffFileAddedLines > 0 || DiffFileDeletedLines > 0;

        public string DiffPreviewEmptyTitle
        {
            get => _diffPreviewEmptyTitle;
            private set
            {
                if (SetProperty(ref _diffPreviewEmptyTitle, value))
                    OnPropertyChanged(nameof(DiffPreviewEmptyTitle));
            }
        }

        public string DiffPreviewEmptyMessage
        {
            get => _diffPreviewEmptyMessage;
            private set
            {
                if (SetProperty(ref _diffPreviewEmptyMessage, value))
                    OnPropertyChanged(nameof(DiffPreviewEmptyMessage));
            }
        }

        public DiffPreviewFileItem? SelectedDiffPreviewFile
        {
            get => _selectedDiffPreviewFile;
            set
            {
                if (SetProperty(ref _selectedDiffPreviewFile, value))
                {
                    OnPropertyChanged(nameof(SelectedDiffPreviewFile));
                    OnPropertyChanged(nameof(SelectedDiffPreviewPath));
                    SyncSelectedDiffPreviewTreeNode(value);
                    RaiseDiffFileNavigationCanExecuteChanged();
                    LoadSelectedDiffFile(value);
                }
            }
        }

        public string SelectedDiffPreviewPath =>
            SelectedDiffPreviewFile?.Path ?? string.Empty;

        public DiffPreviewTreeNode? SelectedDiffPreviewTreeNode
        {
            get => _selectedDiffPreviewTreeNode;
            set
            {
                if (!SetProperty(ref _selectedDiffPreviewTreeNode, value))
                    return;

                OnPropertyChanged(nameof(SelectedDiffPreviewTreeNode));
                if (value?.File is { } file && !ReferenceEquals(file, SelectedDiffPreviewFile))
                    SelectedDiffPreviewFile = file;
            }
        }

        public string DiffFileSearchText
        {
            get => _diffFileSearchText;
            set
            {
                if (SetProperty(ref _diffFileSearchText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DiffFileSearchText));
                    RefreshDiffPreviewFiles();
                }
            }
        }

        public DiffPreviewKindFilterItem? SelectedDiffFileKindFilter
        {
            get => _selectedDiffFileKindFilter;
            set
            {
                if (SetProperty(ref _selectedDiffFileKindFilter, value))
                {
                    OnPropertyChanged(nameof(SelectedDiffFileKindFilter));
                    RefreshDiffPreviewFiles();
                }
            }
        }

        public string DiffFileContentText
        {
            get => _diffFileContentText;
            private set
            {
                if (SetProperty(ref _diffFileContentText, value))
                    OnPropertyChanged(nameof(DiffFileContentText));
            }
        }

        public string DiffFileContentStatus
        {
            get => _diffFileContentStatus;
            private set
            {
                if (SetProperty(ref _diffFileContentStatus, value))
                    OnPropertyChanged(nameof(DiffFileContentStatus));
            }
        }

        public int DiffFileAddedLines
        {
            get => _diffFileAddedLines;
            private set
            {
                if (SetProperty(ref _diffFileAddedLines, value))
                    OnPropertyChanged(nameof(DiffFileAddedLines));
            }
        }

        public int DiffFileDeletedLines
        {
            get => _diffFileDeletedLines;
            private set
            {
                if (SetProperty(ref _diffFileDeletedLines, value))
                    OnPropertyChanged(nameof(DiffFileDeletedLines));
            }
        }

        public bool IsSnapshotCompareBusy
        {
            get => _isSnapshotCompareBusy;
            private set
            {
                if (SetProperty(ref _isSnapshotCompareBusy, value))
                {
                    OnPropertyChanged(nameof(IsSnapshotCompareBusy));
                    OnPropertyChanged(nameof(CanCompareSelectedSnapshots));
                    _compareSelectedSnapshotsRelayCommand?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(CompareSelectionHint));
                }
            }
        }

        public string DiffFileResultsLabel
        {
            get => _diffFileResultsLabel;
            private set
            {
                if (SetProperty(ref _diffFileResultsLabel, value))
                    OnPropertyChanged(nameof(DiffFileResultsLabel));
            }
        }

        public string DiffFileCompactResultsLabel
        {
            get => _diffFileCompactResultsLabel;
            private set
            {
                if (SetProperty(ref _diffFileCompactResultsLabel, value))
                    OnPropertyChanged(nameof(DiffFileCompactResultsLabel));
            }
        }

        // Events that external code (e.g. view or parent VM) can subscribe to
        // in order to run real backup/restore logic and then refresh this VM.
        public event Action? CreateBackupForAllProjectsRequested;
        public event Action<ProjectBackupItem?>? BackupProjectRequested;
        public event Action<BackupSnapshotItem?>? RestoreBackupRequested;
        public event Action<BackupSnapshotItem?>? DeleteBackupRequested;
        public event Action<BackupSnapshotItem?>? OpenBackupFolderRequested;
        public event Action<BackupSnapshotItem?>? ExploreBackupRequested;
        public event Action<BackupProgressItem?>? CancelActiveBackupRequested;
        public event Action<int, bool>? BackupProtectionChanged;
        public event Action? OpenSettingsRequested;

        // Commands
        public ICommand CreateBackupCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand DeleteBackupCommand { get; }
        public ICommand OpenBackupFolderCommand { get; }
        public ICommand ExploreBackupCommand { get; }
        public ICommand ToggleBackupProtectionCommand { get; }
        public ICommand ExportSnapshotSummaryTextCommand { get; }
        public ICommand ExportSnapshotSummaryJsonCommand { get; }
        public ICommand ShowSnapshotDiffPreviewCommand { get; }
        public ICommand CompareSelectedSnapshotsCommand { get; }
        public ICommand CancelSnapshotCompareCommand { get; }
        public ICommand CloseSnapshotDiffPreviewCommand { get; }
        public ICommand ClearDiffFileFiltersCommand { get; }
        public ICommand SelectPreviousDiffFileCommand { get; }
        public ICommand SelectNextDiffFileCommand { get; }

        public ICommand BackupProjectCommand { get; }
        public ICommand ManageProjectEncryptionCommand { get; }
        public ICommand ShowProjectHistoryCommand { get; }
        public ICommand FilterSnapshotsCommand { get; }
        public ICommand CloseVerificationPopupCommand { get; }
        public ICommand DeleteFailedBackupCommand { get; }
        public ICommand OpenSettingsCommand { get; }

        public bool IsTypeFilterAll => string.Equals(_currentTypeFilter, "All", StringComparison.OrdinalIgnoreCase);
        public bool IsTypeFilterAuto => string.Equals(_currentTypeFilter, "Auto", StringComparison.OrdinalIgnoreCase);
        public bool IsTypeFilterManual => string.Equals(_currentTypeFilter, ManualBackupType, StringComparison.OrdinalIgnoreCase);
        public bool CanCompareSelectedSnapshots =>
            !IsSnapshotCompareBusy &&
            SelectedSnapshotA is not null &&
            SelectedSnapshotB is not null &&
            SelectedSnapshotA.SnapshotId > 0 &&
            SelectedSnapshotB.SnapshotId > 0 &&
            !string.IsNullOrWhiteSpace(SelectedSnapshotA.ProjectId) &&
            string.Equals(SelectedSnapshotA.ProjectId, SelectedSnapshotB.ProjectId, StringComparison.OrdinalIgnoreCase) &&
            SelectedSnapshotA.SnapshotId != SelectedSnapshotB.SnapshotId;

        public string CompareSelectionHint
        {
            get
            {
                if (IsSnapshotCompareBusy)
                    return L("Backups.Compare.Busy", "Comparing restore points...");
                if (SelectedSnapshotA is null && SelectedSnapshotB is null)
                    return L("Backups.Compare.SelectFirst", "Select a restore point; VaultSync will suggest a nearby point from the same project.");
                if (SelectedSnapshotA is null || SelectedSnapshotB is null)
                    return L("Backups.Compare.SelectSecond", "Select a second restore point from the same project.");
                if (SelectedSnapshotA.SnapshotId == SelectedSnapshotB.SnapshotId)
                    return L("Backups.Compare.DifferentPoints", "Choose two different restore points.");
                if (string.IsNullOrWhiteSpace(SelectedSnapshotA.ProjectId) || string.IsNullOrWhiteSpace(SelectedSnapshotB.ProjectId))
                    return L("Backups.Compare.ProjectUnavailable", "Project information is unavailable for one of these restore points.");
                if (!string.Equals(SelectedSnapshotA.ProjectId, SelectedSnapshotB.ProjectId, StringComparison.OrdinalIgnoreCase))
                    return L("Backups.Compare.SameProject", "Restore points must belong to the same project.");

                BackupSnapshotItem older = SelectedSnapshotA.Timestamp <= SelectedSnapshotB.Timestamp
                    ? SelectedSnapshotA
                    : SelectedSnapshotB;
                BackupSnapshotItem newer = ReferenceEquals(older, SelectedSnapshotA)
                    ? SelectedSnapshotB
                    : SelectedSnapshotA;
                return Lf(
                    "Backups.Compare.ReadyRange",
                    "Ready to compare: {0} → {1}",
                    older.Timestamp.ToString(TimestampMinuteFormat, CultureInfo.CurrentCulture),
                    newer.Timestamp.ToString(TimestampMinuteFormat, CultureInfo.CurrentCulture));
            }
        }

        public BackupsViewModel()
            : this(StaticAppConfigStore.Instance, new SqliteRepositoryFactory(StaticAppConfigStore.Instance))
        {
        }

        internal BackupsViewModel(
            IAppConfigStore configStore,
            IRepositoryFactory? repositoryFactory = null,
            Func<int, int, CancellationToken, Task<SnapshotCompareResult>>? compareSnapshotsAsync = null,
            Func<Action, Task>? invokeOnUiAsync = null)
        {
            _configStore = configStore;
            _repositoryFactory = repositoryFactory ?? new SqliteRepositoryFactory(_configStore);
            _compareSnapshotsAsync = compareSnapshotsAsync ?? CompareSnapshotsFromRepositoryAsync;
            _invokeOnUiAsync = invokeOnUiAsync ?? (async action => await Dispatcher.UIThread.InvokeAsync(action));
            _activeBackupFlushTimer.Tick += (_, _) => FlushPendingActiveBackupUpdates();
            DiffFileKindFilters.Add(new DiffPreviewKindFilterItem(L("Backups.Compare.FilterAll", "All changes"), null));
            DiffFileKindFilters.Add(new DiffPreviewKindFilterItem(L("Backups.DiffSummary.Preview.Modified", "Modified"), SnapshotFileChangeKind.Modified));
            DiffFileKindFilters.Add(new DiffPreviewKindFilterItem(L("Backups.DiffSummary.Preview.Added", "Added"), SnapshotFileChangeKind.Added));
            DiffFileKindFilters.Add(new DiffPreviewKindFilterItem(L("Backups.DiffSummary.Preview.Deleted", "Deleted"), SnapshotFileChangeKind.Deleted));
            _selectedDiffFileKindFilter = DiffFileKindFilters[0];

            // All-project backup
            CreateBackupCommand = new RelayCommand(_ => CreateBackupForAllProjects());

            // Global history actions
            RestoreBackupCommand = new RelayCommand(p => RestoreBackup(p as BackupSnapshotItem));
            DeleteBackupCommand  = new RelayCommand(p => DeleteBackup(p as BackupSnapshotItem));
            OpenBackupFolderCommand = new RelayCommand(p => OpenBackupFolder(p as BackupSnapshotItem));
            ExploreBackupCommand = new RelayCommand(p => ExploreBackup(p as BackupSnapshotItem));
            ToggleBackupProtectionCommand = new RelayCommand(p => ToggleBackupProtection(p as BackupSnapshotItem));
            ExportSnapshotSummaryTextCommand = new RelayCommand(p => ExportSnapshotSummary(p as BackupSnapshotItem, SnapshotSummaryExportFormat.Text));
            ExportSnapshotSummaryJsonCommand = new RelayCommand(p => ExportSnapshotSummary(p as BackupSnapshotItem, SnapshotSummaryExportFormat.Json));
            ShowSnapshotDiffPreviewCommand = new AsyncRelayCommand(
                p => ShowSnapshotDiffPreviewAsync(p as BackupSnapshotItem),
                operationName: "snapshot-diff-preview");
            _compareSelectedSnapshotsRelayCommand = new RelayCommand(_ => CompareSelectedSnapshots(), _ => CanCompareSelectedSnapshots);
            CompareSelectedSnapshotsCommand = _compareSelectedSnapshotsRelayCommand;
            CancelSnapshotCompareCommand = new RelayCommand(_ => _snapshotCompareCts?.Cancel());
            CloseSnapshotDiffPreviewCommand = new RelayCommand(_ => CloseSnapshotDiffPreview());
            ClearDiffFileFiltersCommand = new RelayCommand(_ => ClearDiffFileFilters());
            _selectPreviousDiffFileRelayCommand = new RelayCommand(_ => SelectAdjacentDiffFile(-1), _ => CanSelectAdjacentDiffFile(-1));
            SelectPreviousDiffFileCommand = _selectPreviousDiffFileRelayCommand;
            _selectNextDiffFileRelayCommand = new RelayCommand(_ => SelectAdjacentDiffFile(1), _ => CanSelectAdjacentDiffFile(1));
            SelectNextDiffFileCommand = _selectNextDiffFileRelayCommand;
            _toggleRestoreReadinessIssuesCommand = new RelayCommand(
                _ => ShowRestoreReadinessIssues = !ShowRestoreReadinessIssues,
                _ => HasRestoreReadinessIssues);

            // Per-project actions
            BackupProjectCommand      = new RelayCommand(p => BackupProject(p as ProjectBackupItem));
            ManageProjectEncryptionCommand = new RelayCommand(p => ManageProjectEncryption(p as ProjectBackupItem));
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
            ActiveBackups.CollectionChanged += (_, _) => OnActiveBackupsCollectionChanged();
            _activeBackupTimer.Tick += (_, _) => TickActiveBackupDurations();

            // NOTE:
            // Live data is now provided by LoadFromBackups(...) from the core layer.
            // We no longer seed design-time demo data here.

            InitializeLocalizationDefaults();
            RefreshEncryptionPolicyOptions();
            RefreshRestoreModeOptions();
            RefreshVerificationPolicyOptions();
            RefreshDestinationOptionsInternal(_configStore.GetSnapshot());
            RefreshProjectSortOptions();
        }

        private void OnActiveBackupsCollectionChanged()
        {
            UpdateActiveBackupTimer();
            OnPropertyChanged(nameof(HasActiveBackups));
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
            foreach (BackupProgressItem item in ActiveBackups)
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

        private void ExploreBackup(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            ExploreBackupRequested?.Invoke(snapshot);
        }

        private void ExportSnapshotSummary(BackupSnapshotItem? snapshot, SnapshotSummaryExportFormat format)
        {
            _ = ExportSnapshotSummaryAsync(snapshot, format);
        }

        private async Task ExportSnapshotSummaryAsync(BackupSnapshotItem? snapshot, SnapshotSummaryExportFormat format)
        {
            if (snapshot is null)
                return;

            try
            {
                string path = await Task.Run(() => WriteSnapshotSummaryExport(snapshot, format));
                if (string.IsNullOrWhiteSpace(path))
                {
                    ShowNotification(
                        L("Backups.DiffSummary.ExportFailed", "Failed to export snapshot diff summary."),
                        "Warning");
                    return;
                }

                ShowNotification(
                    Lf("Backups.DiffSummary.ExportSuccess", "Diff summary exported to {0}", path),
                    "Info");
            }
            catch (Exception ex)
            {
                ShowNotification(
                    Lf("Backups.DiffSummary.ExportFailedWithReason", "Failed to export snapshot diff summary: {0}", ex.Message),
                    "Error");
            }
        }

        private string WriteSnapshotSummaryExport(BackupSnapshotItem snapshot, SnapshotSummaryExportFormat format)
        {
            string exportDir = GetSnapshotSummaryExportDirectory();
            Directory.CreateDirectory(exportDir);

            string baseName = BuildSnapshotSummaryFileName(snapshot);
            string extension = format == SnapshotSummaryExportFormat.Json ? ".json" : ".txt";
            string path = EnsureUniqueExportPath(Path.Combine(exportDir, $"{baseName}{extension}"));
            SnapshotSummaryExportPayload payload = BuildSnapshotSummaryExportPayload(snapshot);

            if (format == SnapshotSummaryExportFormat.Json)
            {
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return path;
            }

            string text = BuildSnapshotDiffExportText(payload);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        private async Task ShowSnapshotDiffPreviewAsync(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            ShowSnapshotDiffSummary(snapshot);
            if (snapshot.SnapshotId <= 0 || string.IsNullOrWhiteSpace(snapshot.ProjectId))
            {
                ShowSnapshotDiffUnavailable(
                    L("Backups.Compare.InventoryUnavailable", "File inventory is unavailable for this restore point."));
                return;
            }

            BackupSnapshotItem? older = FindPreviousSnapshotForDiff(
                snapshot,
                _allSnapshots.Count > 0 ? _allSnapshots : Snapshots,
                candidate => ResolveBackupContentRoot(candidate) is not null);
            if (older is null)
            {
                ShowSnapshotDiffUnavailable(L(
                    "Backups.Compare.NoEarlierPoint",
                    "This is the first available restore point for the project. Create another backup to inspect file-by-file changes."));
                return;
            }

            DiffFileContentStatus = L("Backups.Compare.Busy", "Comparing restore points...");
            DiffPreviewEmptyTitle = DiffFileContentStatus;
            DiffPreviewEmptyMessage = L(
                "Backups.Compare.LoadingInventory",
                "Loading changed files from the previous restore point.");
            IsSnapshotCompareBusy = true;
            _snapshotCompareCts?.Cancel();
            _snapshotCompareCts?.Dispose();
            var compareCts = new CancellationTokenSource();
            _snapshotCompareCts = compareCts;
            await CompareSelectedSnapshotsAsync(
                older,
                snapshot,
                compareCts,
                preserveStoredSummaryWhenInventoryMissing: true).ConfigureAwait(false);
        }

        private void ShowSnapshotDiffSummary(BackupSnapshotItem snapshot)
        {
            SnapshotSummaryExportPayload payload = BuildSnapshotSummaryExportPayload(snapshot);
            DiffPreviewTitle = Lf(
                "Backups.DiffSummary.PreviewTitle",
                "Diff summary - {0}",
                payload.ProjectName);
            DiffPreviewMetaLine = Lf(
                "Backups.DiffSummary.PreviewMeta",
                "{0} · {1}",
                payload.TimestampLocal,
                payload.Destination);
            DiffPreviewTrigger = payload.TriggerType;
            DiffPreviewMode = payload.ModeLabel;
            DiffPreviewImportedDisplay = payload.IsImported
                ? L("Backups.Section.TypeImported", "Imported")
                : L("Backups.Summary.LocalLabel", "Local");
            DiffPreviewEncryptionDisplay = payload.IsEncrypted
                ? L("Backups.Section.Encryption.Encrypted", "Encrypted")
                : L("Backups.Section.Encryption.Plain", "Plain");
            DiffPreviewAdded = payload.DiffAdded;
            DiffPreviewModified = payload.DiffModified;
            DiffPreviewDeleted = payload.DiffDeleted;
            DiffPreviewNet = FormatSignedSize(payload.DiffNetBytes);
            DiffPreviewTopPaths.Clear();
            foreach (SnapshotDiffPathExport? path in payload.TopPaths.Take(6))
            {
                DiffPreviewTopPaths.Add(new DiffPreviewPathItem(
                    path.Path,
                    path.Changes,
                    BackupSnapshotItem.FormatSize(path.ChangedBytes)));
            }
            OnPropertyChanged(nameof(HasDiffPreviewTopPaths));
            DiffPreviewText = BuildSnapshotDiffExportText(payload);
            _diffOlderSnapshot = null;
            _diffNewerSnapshot = null;
            _allDiffPreviewFiles.Clear();
            DiffPreviewFiles.Clear();
            ResetDiffPreviewTree();
            SelectedDiffPreviewFile = null;
            DiffFileContentStatus = L("Backups.Compare.SummaryOnly", "Snapshot change summary");
            DiffFileContentText = DiffPreviewText;
            DiffPreviewEmptyTitle = DiffFileContentStatus;
            DiffPreviewEmptyMessage = L(
                "Backups.DiffSummary.NoChanges",
                "No file changes detected or diff data is unavailable for this backup");
            NotifyDiffPreviewFileAvailabilityChanged();
            IsDiffPreviewOpen = true;
        }

        private void ShowSnapshotDiffUnavailable(string message)
        {
            DiffFileContentStatus = L("Backups.DiffSummary.Unavailable", "File-by-file changes unavailable");
            DiffFileContentText = DiffPreviewText;
            DiffPreviewEmptyTitle = DiffFileContentStatus;
            DiffPreviewEmptyMessage = message;
        }

        internal static BackupSnapshotItem? FindPreviousSnapshotForDiff(
            BackupSnapshotItem selected,
            IEnumerable<BackupSnapshotItem> candidates,
            Func<BackupSnapshotItem, bool>? isContentReachable = null)
        {
            ArgumentNullException.ThrowIfNull(selected);
            ArgumentNullException.ThrowIfNull(candidates);

            var nearestGroup = candidates
                .Where(candidate => candidate.SnapshotId > 0 &&
                                    candidate.SnapshotId != selected.SnapshotId &&
                                    string.Equals(candidate.ProjectId, selected.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                                    CompareRestorePointOrder(candidate, selected) < 0)
                .GroupBy(candidate => candidate.SnapshotId)
                .Select(group => new
                {
                    Rows = group.ToArray(),
                    Order = group
                        .OrderByDescending(candidate => candidate.Timestamp)
                        .ThenByDescending(candidate => candidate.SnapshotId)
                        .First()
                })
                .OrderByDescending(group => group.Order.Timestamp)
                .ThenByDescending(group => group.Order.SnapshotId)
                .FirstOrDefault();

            return nearestGroup?.Rows
                .OrderByDescending(candidate => isContentReachable?.Invoke(candidate) ?? false)
                .ThenBy(candidate => candidate.IsEncrypted)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .First();
        }

        private static int CompareRestorePointOrder(BackupSnapshotItem left, BackupSnapshotItem right)
        {
            int timestampOrder = left.Timestamp.CompareTo(right.Timestamp);
            return timestampOrder != 0
                ? timestampOrder
                : left.SnapshotId.CompareTo(right.SnapshotId);
        }

        private void CompareSelectedSnapshots()
        {
            BackupSnapshotItem? pointA = SelectedSnapshotA;
            BackupSnapshotItem? pointB = SelectedSnapshotB;
            if (pointA is null || pointB is null || !CanCompareSelectedSnapshots)
                return;

            IsSnapshotCompareBusy = true;
            _snapshotCompareCts?.Cancel();
            _snapshotCompareCts?.Dispose();
            var compareCts = new CancellationTokenSource();
            _snapshotCompareCts = compareCts;
            DetachedTask.Run(
                () => CompareSelectedSnapshotsAsync(pointA, pointB, compareCts),
                "snapshot-file-compare");
        }

        private async Task CompareSelectedSnapshotsAsync(
            BackupSnapshotItem pointA,
            BackupSnapshotItem pointB,
            CancellationTokenSource compareCts,
            bool preserveStoredSummaryWhenInventoryMissing = false)
        {
            bool pointAIsNewer = CompareRestorePointOrder(pointA, pointB) > 0;
            BackupSnapshotItem newer = pointAIsNewer ? pointA : pointB;
            BackupSnapshotItem older = ReferenceEquals(newer, pointA) ? pointB : pointA;
            try
            {
                SnapshotCompareResult result = await _compareSnapshotsAsync(
                    older.SnapshotId,
                    newer.SnapshotId,
                    compareCts.Token)
                    .ConfigureAwait(false);
                bool storedSummaryMatches = StoredDiffSummaryMatches(newer, result);
                bool inventoryAvailable = result.Unchanged + result.ChangedCount > 0 ||
                                          (preserveStoredSummaryWhenInventoryMissing && storedSummaryMatches);
                bool shouldRecoverInventory = result.Unchanged + result.ChangedCount == 0 ||
                                              (preserveStoredSummaryWhenInventoryMissing && !storedSummaryMatches);
                bool comparedReachableContents = false;
                if (shouldRecoverInventory)
                {
                    (result, inventoryAvailable) = await CompareReachableBackupContentsAsync(
                            older,
                            newer,
                            result,
                            compareCts.Token)
                        .ConfigureAwait(false);
                    comparedReachableContents = inventoryAvailable;
                }

                if (!comparedReachableContents && result.Modified > 0)
                {
                    result = await IgnoreReachableTextEquivalentModificationsAsync(
                            older,
                            newer,
                            result,
                            compareCts.Token)
                        .ConfigureAwait(false);
                }

                await _invokeOnUiAsync(() =>
                {
                    if (compareCts.IsCancellationRequested || !ReferenceEquals(_snapshotCompareCts, compareCts))
                        return;
                    ApplySnapshotComparisonResult(
                        older,
                        newer,
                        result,
                        preserveStoredSummaryWhenInventoryMissing,
                        inventoryAvailable);
                });
            }
            catch (OperationCanceledException) when (compareCts.IsCancellationRequested)
            {
                // User cancellation is expected and should not open an error dialog.
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record(
                    $"Snapshot file compare failed: older={older.SnapshotId}, newer={newer.SnapshotId}, error={ex.GetType().Name} - {ex.Message}");
                await _invokeOnUiAsync(() =>
                {
                    if (compareCts.IsCancellationRequested || !ReferenceEquals(_snapshotCompareCts, compareCts))
                        return;
                    DiffPreviewTitle = L("Backups.Compare.FailedTitle", "Comparison unavailable");
                    DiffPreviewText = Lf(
                        "Backups.Compare.FailedMessage",
                        "VaultSync could not compare these restore points: {0}",
                        ex.Message);
                    _allDiffPreviewFiles.Clear();
                    DiffPreviewFiles.Clear();
                    ResetDiffPreviewTree();
                    SelectedDiffPreviewFile = null;
                    DiffFileContentStatus = DiffPreviewTitle;
                    DiffFileContentText = DiffPreviewText;
                    DiffPreviewEmptyTitle = DiffPreviewTitle;
                    DiffPreviewEmptyMessage = DiffPreviewText;
                    NotifyDiffPreviewFileAvailabilityChanged();
                    IsDiffPreviewOpen = true;
                });
            }
            finally
            {
                await _invokeOnUiAsync(() =>
                {
                    if (!ReferenceEquals(_snapshotCompareCts, compareCts))
                        return;
                    _snapshotCompareCts = null;
                    IsSnapshotCompareBusy = false;
                    compareCts.Dispose();
                });
            }
        }

        private async Task<(SnapshotCompareResult Result, bool InventoryAvailable)> CompareReachableBackupContentsAsync(
            BackupSnapshotItem older,
            BackupSnapshotItem newer,
            SnapshotCompareResult databaseResult,
            CancellationToken cancellationToken)
        {
            if (older.IsEncrypted || newer.IsEncrypted)
                return (databaseResult, false);

            string? olderRoot = ResolveBackupContentRoot(older);
            string? newerRoot = ResolveBackupContentRoot(newer);
            if (olderRoot is null || newerRoot is null)
                return (databaseResult, false);

            try
            {
                SnapshotCompareResult contentResult = await Task.Run(
                    () => CompareBackupContentInventories(
                        olderRoot,
                        newerRoot,
                        databaseResult,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                bool inventoryAvailable = !ReferenceEquals(contentResult, databaseResult);
                return (contentResult, inventoryAvailable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                DiagnosticsLogger.Record(
                    $"Snapshot content inventory fallback unavailable: older={older.SnapshotId}, newer={newer.SnapshotId}, error={ex.GetType().Name} - {ex.Message}");
                return (databaseResult, false);
            }
        }

        private async Task<SnapshotCompareResult> IgnoreReachableTextEquivalentModificationsAsync(
            BackupSnapshotItem older,
            BackupSnapshotItem newer,
            SnapshotCompareResult result,
            CancellationToken cancellationToken)
        {
            if (older.IsEncrypted || newer.IsEncrypted)
                return result;

            string? olderRoot = ResolveBackupContentRoot(older);
            string? newerRoot = ResolveBackupContentRoot(newer);
            if (olderRoot is null || newerRoot is null)
                return result;

            try
            {
                return await Task.Run(
                    () => SnapshotCompareService.IgnoreTextEquivalentModifications(
                        olderRoot,
                        newerRoot,
                        result,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                DiagnosticsLogger.Record(
                    $"Snapshot text-equivalence refinement unavailable: older={older.SnapshotId}, newer={newer.SnapshotId}, error={ex.GetType().Name} - {ex.Message}");
                return result;
            }
        }

        internal static SnapshotCompareResult CompareBackupContentInventories(
            string olderRoot,
            string newerRoot,
            SnapshotCompareResult databaseResult,
            CancellationToken cancellationToken = default)
        {
            SnapshotFileInventory olderInventory = SnapshotExplorerService.BuildFileInventory(
                olderRoot,
                cancellationToken: cancellationToken);
            SnapshotFileInventory newerInventory = SnapshotExplorerService.BuildFileInventory(
                newerRoot,
                cancellationToken: cancellationToken);
            if (olderInventory.IsTruncated || newerInventory.IsTruncated ||
                olderInventory.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive ||
                newerInventory.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive)
            {
                return databaseResult;
            }

            SnapshotCompareResult result = SnapshotCompareService.Compare(
                olderInventory.Files,
                newerInventory.Files,
                cancellationToken: cancellationToken);
            return SnapshotCompareService.IgnoreTextEquivalentModifications(
                olderRoot,
                newerRoot,
                result,
                cancellationToken);
        }

        internal void ApplySnapshotComparisonResult(
            BackupSnapshotItem older,
            BackupSnapshotItem newer,
            SnapshotCompareResult result,
            bool preserveStoredSummaryWhenInventoryMissing,
            bool inventoryAvailable = false)
        {
            if (preserveStoredSummaryWhenInventoryMissing &&
                HasStoredDiffSummary(newer) &&
                !inventoryAvailable &&
                !StoredDiffSummaryMatches(newer, result))
            {
                ShowSnapshotDiffSummary(newer);
                ShowSnapshotDiffUnavailable(newer.IsImported
                    ? L(
                        "Backups.Compare.ImportedInventoryUnavailable",
                        "This restore point was imported without file details. Choose two backups whose storage is connected to inspect individual files.")
                    : L(
                        "Backups.Compare.InventoryUnavailableWithSummary",
                        "The change totals are available, but individual file details are not. Choose two backups whose storage is connected."));
                return;
            }

            ShowSnapshotComparison(older, newer, result);
        }

        private static bool HasStoredDiffSummary(BackupSnapshotItem snapshot) =>
            snapshot.DiffAdded > 0 ||
            snapshot.DiffModified > 0 ||
            snapshot.DiffDeleted > 0 ||
            snapshot.DiffNetBytes != 0 ||
            SnapshotDiffSummary.ParseTopChangedPaths(snapshot.DiffTopPathsJson).Count > 0;

        private static bool StoredDiffSummaryMatches(
            BackupSnapshotItem snapshot,
            SnapshotCompareResult result) =>
            snapshot.DiffAdded == result.Added &&
            snapshot.DiffModified == result.Modified &&
            snapshot.DiffDeleted == result.Deleted;

        private Task<SnapshotCompareResult> CompareSnapshotsFromRepositoryAsync(
            int olderSnapshotId,
            int newerSnapshotId,
            CancellationToken cancellationToken)
        {
            SqliteRepository repository = _repositoryFactory.Create();
            var compareService = new SnapshotCompareService(repository);
            return compareService.CompareAsync(olderSnapshotId, newerSnapshotId, cancellationToken);
        }

        private void ShowSnapshotComparison(
            BackupSnapshotItem older,
            BackupSnapshotItem newer,
            SnapshotCompareResult result)
        {
            TimeSpan elapsed = newer.Timestamp - older.Timestamp;
            string projectName = ResolveCompareProjectName(newer, older);

            DiffPreviewTitle = Lf(
                "Backups.Compare.Title",
                "Changes in {0}",
                projectName);
            DiffPreviewMetaLine = Lf(
                "Backups.Compare.Range",
                "{0} → {1}",
                older.Timestamp.ToString(TimestampMinuteFormat, CultureInfo.CurrentCulture),
                newer.Timestamp.ToString(TimestampMinuteFormat, CultureInfo.CurrentCulture));
            DiffPreviewTrigger = Lf(
                "Backups.Compare.ChangeIntelligenceLine",
                "{0} files checked",
                (result.Unchanged + result.ChangedCount).ToString(CultureInfo.CurrentCulture));
            DiffPreviewMode = Lf(
                "Backups.Compare.ChangeCountLine",
                "{0} changes across {1}",
                result.ChangedCount.ToString(CultureInfo.CurrentCulture),
                FormatElapsed(elapsed));
            DiffPreviewImportedDisplay = older.IsImported || newer.IsImported
                ? L("Backups.Snapshot.Type.Imported", "Imported")
                : L("Backups.Summary.LocalLabel", "Local");
            DiffPreviewEncryptionDisplay = older.IsEncrypted || newer.IsEncrypted
                ? L(EncryptedPolicyKey, EncryptedFallback)
                : L(PlainPolicyKey, PlainFallback);
            DiffPreviewAdded = result.Added;
            DiffPreviewModified = result.Modified;
            DiffPreviewDeleted = result.Deleted;
            DiffPreviewNet = FormatSignedSize(result.NetSizeBytes);
            DiffPreviewTopPaths.Clear();
            foreach (SnapshotDiffPathStat path in result.TopChangedPaths.Take(6))
            {
                DiffPreviewTopPaths.Add(new DiffPreviewPathItem(
                    path.Path,
                    path.Changes,
                    BackupSnapshotItem.FormatSize(path.ChangedBytes)));
            }
            OnPropertyChanged(nameof(HasDiffPreviewTopPaths));

            _diffOlderSnapshot = older;
            _diffNewerSnapshot = newer;
            _allDiffPreviewFiles.Clear();
            _allDiffPreviewFiles.AddRange(result.Changes.Take(5_000).Select(change => new DiffPreviewFileItem(change)));
            NotifyDiffPreviewFileAvailabilityChanged();
            DiffFileSearchText = string.Empty;
            SelectedDiffFileKindFilter = DiffFileKindFilters[0];
            RefreshDiffPreviewFiles();

            var compareText = new StringBuilder();
            compareText.AppendLine(L("Backups.Compare.DocumentTitle", "# VaultSync backup comparison"));
            compareText.AppendLine();
            compareText.AppendLine(Lf("Backups.Compare.PointA", "A: {0} · {1} · {2}",
                older.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                older.TypeLabel,
                older.SizeFormatted));
            compareText.AppendLine(Lf("Backups.Compare.PointB", "B: {0} · {1} · {2}",
                newer.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                newer.TypeLabel,
                newer.SizeFormatted));
            compareText.AppendLine(Lf("Backups.Compare.ElapsedLine", "Elapsed: {0}", FormatElapsed(elapsed)));
            compareText.AppendLine(Lf("Backups.Compare.SnapshotSizeDeltaLine", "Backup size change: {0}", FormatSignedSize(result.NetSizeBytes)));
            compareText.AppendLine(Lf("Backups.Compare.ChangedBytesLine", "Changed file data: {0}", BackupSnapshotItem.FormatSize(result.ChangedBytes)));
            compareText.AppendLine();
            compareText.AppendLine(L("Backups.Compare.FileSummary", "File changes:"));
            compareText.AppendLine(Lf("Backups.Compare.NewerAdded", "+ added {0}", result.Added.ToString(CultureInfo.CurrentCulture)));
            compareText.AppendLine(Lf("Backups.Compare.NewerModified", "~ modified {0}", result.Modified.ToString(CultureInfo.CurrentCulture)));
            compareText.AppendLine(Lf("Backups.Compare.NewerDeleted", "- deleted {0}", result.Deleted.ToString(CultureInfo.CurrentCulture)));
            compareText.AppendLine(Lf("Backups.Compare.Unchanged", "= unchanged: {0}", result.Unchanged.ToString(CultureInfo.CurrentCulture)));

            AppendChangeSignals(compareText, result.Signals);
            compareText.AppendLine();
            compareText.AppendLine(L("Backups.Compare.ChangedFilesHeader", "## Changed files"));
            foreach (SnapshotFileChange change in result.Changes.Take(200))
            {
                string marker = change.Kind switch
                {
                    SnapshotFileChangeKind.Added => "+",
                    SnapshotFileChangeKind.Modified => "~",
                    SnapshotFileChangeKind.Deleted => "-",
                    _ => "?"
                };
                compareText.AppendLine($"{marker} {change.Path} ({FormatSignedSize(change.SizeDeltaBytes)})");
            }

            if (result.Changes.Count > 200)
            {
                compareText.AppendLine(Lf(
                    "Backups.Compare.AdditionalFiles",
                    "... {0} more changed files not shown",
                    result.Changes.Count - 200));
            }

            DiffPreviewText = compareText.ToString().TrimEnd();
            DiffFileContentText = DiffPreviewText;
            DiffFileContentStatus = L(
                "Backups.Compare.SelectFile",
                "Select a changed text file to review its changes.");
            SelectedDiffPreviewFile = DiffPreviewFiles.FirstOrDefault(file => file.Kind == SnapshotFileChangeKind.Modified)
                ?? DiffPreviewFiles.FirstOrDefault();
            if (result.ChangedCount == 0)
            {
                int filesExamined = result.Unchanged + result.ChangedCount;
                bool hasInventory = filesExamined > 0;
                DiffFileContentStatus = hasInventory
                    ? L("Backups.Compare.NoChanges", "No file-level changes between these restore points.")
                    : L("Backups.DiffSummary.Unavailable", "Diff summary unavailable");
                DiffFileContentText = DiffPreviewText;
                DiffPreviewEmptyTitle = DiffFileContentStatus;
                DiffPreviewEmptyMessage = hasInventory
                    ? DiffPreviewTrigger
                    : L(
                        "Backups.DiffSummary.NoChanges",
                        "No file changes detected or diff data is unavailable for this backup");
            }
            IsDiffPreviewOpen = true;
        }

        private void NotifyDiffPreviewFileAvailabilityChanged()
        {
            OnPropertyChanged(nameof(HasDiffPreviewFiles));
            OnPropertyChanged(nameof(HasNoDiffPreviewFiles));
        }

        private void RefreshDiffPreviewFiles()
        {
            string search = DiffFileSearchText.Trim();
            DiffPreviewFileItem? selected = SelectedDiffPreviewFile;
            DiffPreviewFiles.Clear();
            foreach (DiffPreviewFileItem file in _allDiffPreviewFiles.Where(file =>
                         (SelectedDiffFileKindFilter?.Kind is null || file.Kind == SelectedDiffFileKindFilter.Kind) &&
                         (search.Length == 0 || file.Path.Contains(search, StringComparison.OrdinalIgnoreCase))))
            {
                DiffPreviewFiles.Add(file);
            }

            RebuildDiffPreviewTree(expandAll: search.Length > 0);

            int totalShown = _allDiffPreviewFiles.Count;
            int totalChanges = DiffPreviewAdded + DiffPreviewModified + DiffPreviewDeleted;
            DiffFileResultsLabel = totalChanges > totalShown
                ? Lf("Backups.Compare.ResultsCapped", "{0} matches · showing the first {1} of {2}", DiffPreviewFiles.Count, totalShown, totalChanges)
                : Lf("Backups.Compare.Results", "{0} of {1} changed files", DiffPreviewFiles.Count, totalChanges);
            DiffFileCompactResultsLabel = $"{DiffPreviewFiles.Count}/{totalShown}";

            if (selected is not null && DiffPreviewFiles.Contains(selected))
            {
                SyncSelectedDiffPreviewTreeNode(selected);
                RaiseDiffFileNavigationCanExecuteChanged();
                return;
            }
            SelectedDiffPreviewFile = null;
            RaiseDiffFileNavigationCanExecuteChanged();
        }

        private void RebuildDiffPreviewTree(bool expandAll)
        {
            DiffPreviewTreeRoots.Clear();
            _diffPreviewTreeNodes.Clear();
            foreach (DiffPreviewTreeNode root in DiffPreviewTreeNode.Build(DiffPreviewFiles, expandAll))
            {
                DiffPreviewTreeRoots.Add(root);
                IndexDiffPreviewTree(root);
            }

            OnPropertyChanged(nameof(DiffPreviewTreeRoots));
        }

        private void IndexDiffPreviewTree(DiffPreviewTreeNode node)
        {
            if (node.File is { } file)
                _diffPreviewTreeNodes[file] = node;
            foreach (DiffPreviewTreeNode child in node.Children)
                IndexDiffPreviewTree(child);
        }

        private void SyncSelectedDiffPreviewTreeNode(DiffPreviewFileItem? file)
        {
            DiffPreviewTreeNode? node = file is not null && _diffPreviewTreeNodes.TryGetValue(file, out DiffPreviewTreeNode? match)
                ? match
                : null;
            node?.ExpandAncestors();
            if (ReferenceEquals(_selectedDiffPreviewTreeNode, node))
                return;

            _selectedDiffPreviewTreeNode = node;
            OnPropertyChanged(nameof(SelectedDiffPreviewTreeNode));
        }

        private void ResetDiffPreviewTree()
        {
            DiffPreviewTreeRoots.Clear();
            _diffPreviewTreeNodes.Clear();
            if (_selectedDiffPreviewTreeNode is null)
                return;

            _selectedDiffPreviewTreeNode = null;
            OnPropertyChanged(nameof(SelectedDiffPreviewTreeNode));
        }

        private void ClearDiffFileFilters()
        {
            DiffFileSearchText = string.Empty;
            SelectedDiffFileKindFilter = DiffFileKindFilters[0];
        }

        private bool CanSelectAdjacentDiffFile(int offset)
        {
            int currentIndex = SelectedDiffPreviewFile is null
                ? -1
                : DiffPreviewFiles.IndexOf(SelectedDiffPreviewFile);
            int targetIndex = currentIndex + offset;
            return targetIndex >= 0 && targetIndex < DiffPreviewFiles.Count;
        }

        private void SelectAdjacentDiffFile(int offset)
        {
            if (!CanSelectAdjacentDiffFile(offset))
                return;

            int currentIndex = DiffPreviewFiles.IndexOf(SelectedDiffPreviewFile!);
            SelectedDiffPreviewFile = DiffPreviewFiles[currentIndex + offset];
        }

        private void RaiseDiffFileNavigationCanExecuteChanged()
        {
            _selectPreviousDiffFileRelayCommand?.RaiseCanExecuteChanged();
            _selectNextDiffFileRelayCommand?.RaiseCanExecuteChanged();
        }

        private void LoadSelectedDiffFile(DiffPreviewFileItem? file)
        {
            int requestVersion = Interlocked.Increment(ref _diffContentRequestVersion);
            _diffContentCts?.Cancel();
            _diffContentCts = null;
            ClearDiffFileContentLines();
            if (file is null || _diffOlderSnapshot is null || _diffNewerSnapshot is null)
            {
                if (_diffOlderSnapshot is not null && _diffNewerSnapshot is not null)
                {
                    DiffFileContentStatus = DiffPreviewFiles.Count == 0 && _allDiffPreviewFiles.Count > 0
                        ? L("Backups.Compare.NoMatches", "No changed files match the current filters.")
                        : L("Backups.Compare.SelectFile", "Select a changed text file to review its changes.");
                    DiffFileContentText = string.Empty;
                }
                return;
            }

            var contentCts = new CancellationTokenSource();
            _diffContentCts = contentCts;
            DiffFileContentStatus = L("Backups.Compare.LoadingFile", "Loading file changes...");
            DiffFileContentText = string.Empty;
            DetachedTask.Run(
                () => LoadSelectedDiffFileAsync(file, _diffOlderSnapshot, _diffNewerSnapshot, requestVersion, contentCts),
                "snapshot-text-diff");
        }

        private async Task LoadSelectedDiffFileAsync(
            DiffPreviewFileItem file,
            BackupSnapshotItem older,
            BackupSnapshotItem newer,
            int requestVersion,
            CancellationTokenSource contentCts)
        {
            try
            {
                await LoadSelectedDiffFileCoreAsync(
                    file,
                    older,
                    newer,
                    requestVersion,
                    contentCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (contentCts.IsCancellationRequested)
            {
                // A newer file selection superseded this preview.
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_diffContentCts, contentCts))
                        _diffContentCts = null;
                });
                contentCts.Dispose();
            }
        }

        private async Task LoadSelectedDiffFileCoreAsync(
            DiffPreviewFileItem file,
            BackupSnapshotItem older,
            BackupSnapshotItem newer,
            int requestVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string? olderRoot, string? newerRoot) = (ResolveBackupContentRoot(older), ResolveBackupContentRoot(newer));
            string olderText = string.Empty;
            string newerText = string.Empty;
            bool previewTruncated = false;
            string? error = null;

            if (file.Kind != SnapshotFileChangeKind.Added)
            {
                (olderText, bool olderTruncated, error) = LoadSnapshotTextPreview(
                    olderRoot,
                    file.Path,
                    L("Backups.Compare.OlderUnavailable", "The earlier backup is unavailable. Reconnect its storage to view this file."),
                    cancellationToken);
                previewTruncated |= olderTruncated;
            }

            if (error is null && file.Kind != SnapshotFileChangeKind.Deleted)
            {
                (newerText, bool newerTruncated, error) = LoadSnapshotTextPreview(
                    newerRoot,
                    file.Path,
                    L("Backups.Compare.NewerUnavailable", "The later backup is unavailable. Reconnect its storage to view this file."),
                    cancellationToken);
                previewTruncated |= newerTruncated;
            }

            UnifiedTextDiffResult? diff = error is null
                ? UnifiedTextDiffService.Create(
                    olderText,
                    newerText,
                    $"a/{file.Path}",
                    $"b/{file.Path}",
                    cancellationToken: cancellationToken)
                : null;
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (requestVersion != _diffContentRequestVersion || !ReferenceEquals(file, SelectedDiffPreviewFile))
                    return;

                if (diff is null)
                {
                    DiffFileContentStatus = L("Backups.Compare.PreviewUnavailable", "File preview unavailable");
                    DiffFileContentText = error ?? string.Empty;
                    ClearDiffFileContentLines();
                    return;
                }

                DiffFileContentStatus = previewTruncated || diff.IsTruncated
                    ? L("Backups.Compare.TextChangesShortened", "Text changes (preview shortened)")
                    : L("Backups.Compare.TextChanges", "Text changes");
                DiffFileContentText = diff.Text;
                SetDiffFileContentLines(diff);
            });
        }

        private void SetDiffFileContentLines(UnifiedTextDiffResult diff)
        {
            var lines = DiffPreviewLineItem.ParseUnified(diff.Text).ToList();
            if (diff.IsTruncated)
            {
                lines.Add(DiffPreviewLineItem.Notice(L(
                    "Backups.Compare.PreviewShortened",
                    "Preview shortened to keep comparison responsive.")));
            }

            DiffFileContentLines = lines.Count > 0
                ? lines
                : [DiffPreviewLineItem.Notice(L(
                    "Backups.Compare.MetadataChangedOnly",
                    "The text is identical; only file details such as its timestamp or size changed."))];
            DiffFileAddedLines = diff.AddedLines;
            DiffFileDeletedLines = diff.DeletedLines;
            OnPropertyChanged(nameof(DiffFileContentLines));
            NotifyDiffFileContentLineAvailabilityChanged();
        }

        private void ClearDiffFileContentLines()
        {
            DiffFileContentLines = [];
            DiffFileAddedLines = 0;
            DiffFileDeletedLines = 0;
            OnPropertyChanged(nameof(DiffFileContentLines));
            NotifyDiffFileContentLineAvailabilityChanged();
        }

        private void NotifyDiffFileContentLineAvailabilityChanged()
        {
            OnPropertyChanged(nameof(HasDiffFileContentLines));
            OnPropertyChanged(nameof(HasNoDiffFileContentLines));
            OnPropertyChanged(nameof(HasDiffFileLineChanges));
        }

        private static (string Text, bool Truncated, string? Error) LoadSnapshotTextPreview(
            string? contentRoot,
            string relativePath,
            string unavailableMessage,
            CancellationToken cancellationToken)
        {
            if (contentRoot is null)
                return (string.Empty, false, unavailableMessage);

            SnapshotPreviewResult preview = SnapshotExplorerService.PreviewText(contentRoot, relativePath);
            cancellationToken.ThrowIfCancellationRequested();
            return preview.Success
                ? (preview.Text, preview.Truncated, null)
                : (string.Empty, false, preview.Error);
        }

        private string? ResolveBackupContentRoot(BackupSnapshotItem snapshot)
        {
            if (snapshot.IsEncrypted || string.IsNullOrWhiteSpace(snapshot.BackupRelativePath))
                return null;

            AppConfig config = _configStore.GetSnapshot();
            IEnumerable<string> roots = new[] { snapshot.DestinationRootPath }
                .Concat((config.Backups.Destinations ?? [])
                    .Where(destination => string.Equals(destination.Alias, snapshot.DestinationAlias, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(destination.Path, snapshot.DestinationRootPath, StringComparison.OrdinalIgnoreCase))
                    .Select(destination => destination.Path))
                .Append(config.Backups.BackupRoot)
                .OfType<string>()
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string root in roots)
            {
                if (BackupSafetyService.TryCombinePathUnderRoot(root, snapshot.BackupRelativePath, out string fullPath) &&
                    Directory.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            return null;
        }

        private static void AppendChangeSignals(
            StringBuilder compareText,
            IReadOnlyList<SnapshotChangeSignal> signals)
        {
            if (signals.Count == 0)
                return;

            compareText.AppendLine();
            compareText.AppendLine(L("Backups.Compare.AttentionSignalsHeader", "## Notable changes"));
            foreach (SnapshotChangeSignal signal in signals)
            {
                string line = signal.Kind switch
                {
                    SnapshotChangeSignalKind.MassDeletion =>
                        Lf("Backups.Compare.SignalMassDeletion", "! Mass deletion: {0} files removed ({1:P0})", signal.AffectedFiles, signal.Ratio),
                    SnapshotChangeSignalKind.SignificantGrowth =>
                        Lf("Backups.Compare.SignalGrowth", "! Large growth: {0} ({1:P0})", FormatSignedSize(signal.SizeDeltaBytes), signal.Ratio),
                    SnapshotChangeSignalKind.HighChurn =>
                        Lf("Backups.Compare.SignalHighChurn", "! Widespread changes: {0} files changed ({1:P0})", signal.AffectedFiles, signal.Ratio),
                    _ => string.Empty
                };
                if (line.Length > 0)
                    compareText.AppendLine(line);
            }
        }

        private string ResolveCompareProjectName(BackupSnapshotItem a, BackupSnapshotItem b)
        {
            string projectA = ResolveProjectNameFromSnapshot(a);
            string projectB = ResolveProjectNameFromSnapshot(b);
            if (string.Equals(projectA, projectB, StringComparison.OrdinalIgnoreCase))
                return projectA;

            return L(AllProjectsKey, AllProjectsFallback);
        }

        private string ResolveProjectNameFromSnapshot(BackupSnapshotItem snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.ProjectId))
            {
                ProjectBackupItem? match = ProjectBackups.FirstOrDefault(project =>
                    string.Equals(project.Id, snapshot.ProjectId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match.Name;
            }

            return L(UnknownProjectGroupKey, UnknownProjectFallback);
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalDays >= 2)
                return $"{Math.Floor(elapsed.TotalDays):0}d {elapsed.Hours:00}h";
            if (elapsed.TotalHours >= 1)
                return $"{Math.Floor(elapsed.TotalHours):0}h {elapsed.Minutes:00}m";
            if (elapsed.TotalMinutes >= 1)
                return $"{Math.Floor(elapsed.TotalMinutes):0}m {elapsed.Seconds:00}s";
            return $"{Math.Max(0, elapsed.Seconds):0}s";
        }

        private void CloseSnapshotDiffPreview()
        {
            _snapshotCompareCts?.Cancel();
            _diffContentCts?.Cancel();
            IsDiffPreviewOpen = false;
            DiffPreviewTitle = string.Empty;
            DiffPreviewText = string.Empty;
            DiffPreviewMetaLine = string.Empty;
            DiffPreviewTrigger = string.Empty;
            DiffPreviewMode = string.Empty;
            DiffPreviewImportedDisplay = string.Empty;
            DiffPreviewEncryptionDisplay = string.Empty;
            DiffPreviewAdded = 0;
            DiffPreviewModified = 0;
            DiffPreviewDeleted = 0;
            DiffPreviewNet = string.Empty;
            DiffPreviewTopPaths.Clear();
            _allDiffPreviewFiles.Clear();
            DiffPreviewFiles.Clear();
            ResetDiffPreviewTree();
            SelectedDiffPreviewFile = null;
            DiffFileSearchText = string.Empty;
            SelectedDiffFileKindFilter = DiffFileKindFilters[0];
            DiffFileContentText = string.Empty;
            DiffFileContentStatus = string.Empty;
            DiffFileResultsLabel = string.Empty;
            DiffFileCompactResultsLabel = string.Empty;
            _diffOlderSnapshot = null;
            _diffNewerSnapshot = null;
            OnPropertyChanged(nameof(HasDiffPreviewTopPaths));
        }

        private void SelectDefaultCompareCounterpart(BackupSnapshotItem? selected, bool selectPointB)
        {
            if (selected is null || string.IsNullOrWhiteSpace(selected.ProjectId))
                return;
            if (selectPointB && SelectedSnapshotB is not null)
                return;
            if (!selectPointB && SelectedSnapshotA is not null)
                return;

            BackupSnapshotItem? candidate = Snapshots
                .Where(snapshot => snapshot.SnapshotId != selected.SnapshotId &&
                                   string.Equals(snapshot.ProjectId, selected.ProjectId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(snapshot => Math.Abs((snapshot.Timestamp - selected.Timestamp).Ticks))
                .FirstOrDefault();
            if (selectPointB)
            {
                if (candidate is not null && candidate.Timestamp < selected.Timestamp)
                {
                    SelectedSnapshotB = selected;
                    SelectedSnapshotA = candidate;
                }
                else
                {
                    SelectedSnapshotB = candidate;
                }
            }
            else
            {
                if (candidate is not null && candidate.Timestamp > selected.Timestamp)
                {
                    SelectedSnapshotA = selected;
                    SelectedSnapshotB = candidate;
                }
                else
                {
                    SelectedSnapshotA = candidate;
                }
            }
        }

        private SnapshotSummaryExportPayload BuildSnapshotSummaryExportPayload(BackupSnapshotItem snapshot)
        {
            int projectId = int.TryParse(snapshot.ProjectId, out int pid) ? pid : 0;
            string projectName = ProjectBackups
                .FirstOrDefault(project => string.Equals(project.Id, snapshot.ProjectId, StringComparison.OrdinalIgnoreCase))
                ?.Name ?? L(UnknownProjectGroupKey, UnknownProjectFallback);

            var topPaths = SnapshotDiffSummary.ParseTopChangedPaths(snapshot.DiffTopPathsJson)
                .Select(path => new SnapshotDiffPathExport(path.Path, path.Changes, path.ChangedBytes))
                .ToList();

            return new SnapshotSummaryExportPayload(
                snapshot.Id,
                projectId,
                projectName,
                snapshot.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                snapshot.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                snapshot.DestinationDisplay,
                snapshot.Type,
                snapshot.TypeLabel,
                snapshot.IsImported,
                snapshot.IsEncrypted,
                snapshot.DiffAdded,
                snapshot.DiffModified,
                snapshot.DiffDeleted,
                snapshot.DiffNetBytes,
                topPaths);
        }

        private static string GetSnapshotSummaryExportDirectory()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Path.GetTempPath();

            return Path.Combine(documents, "VaultSync", "Exports", "SnapshotDiff");
        }

        private static string BuildSnapshotSummaryFileName(BackupSnapshotItem snapshot)
        {
            string projectToken = string.IsNullOrWhiteSpace(snapshot.ProjectId) ? "global" : snapshot.ProjectId;
            string ts = snapshot.Timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return $"snapshot-diff-{projectToken}-backup-{snapshot.Id}-{ts}";
        }

        private static string EnsureUniqueExportPath(string path)
        {
            if (!File.Exists(path))
                return path;

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);

            for (int i = 1; i <= 999; i++)
            {
                string candidate = Path.Combine(directory, $"{fileName}-{i}{extension}");
                if (!File.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(directory, $"{fileName}-{Guid.NewGuid():N}{extension}");
        }

        private static string BuildSnapshotDiffExportText(SnapshotSummaryExportPayload payload)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# VaultSync Snapshot Diff Summary");
            sb.AppendLine($"backup_id: {payload.BackupId}");
            sb.AppendLine($"project: {payload.ProjectName} (id: {payload.ProjectId})");
            sb.AppendLine($"timestamp_local: {payload.TimestampLocal}");
            sb.AppendLine($"timestamp_utc: {payload.TimestampUtc}");
            sb.AppendLine($"destination: {payload.Destination}");
            sb.AppendLine($"trigger: {payload.TriggerType} | mode: {payload.ModeLabel}");
            sb.AppendLine($"imported: {payload.IsImported} | encrypted: {payload.IsEncrypted}");
            sb.AppendLine();
            sb.AppendLine("diff");
            sb.AppendLine($"+ added    {payload.DiffAdded}");
            sb.AppendLine($"~ modified {payload.DiffModified}");
            sb.AppendLine($"- deleted  {payload.DiffDeleted}");
            sb.AppendLine($"Δ net      {FormatSignedSize(payload.DiffNetBytes)}");

            if (payload.TopPaths.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("top_paths");
                foreach (SnapshotDiffPathExport? path in payload.TopPaths.Take(10))
                {
                    sb.AppendLine($"~ {path.Path}  (changes: {path.Changes}, bytes: {BackupSnapshotItem.FormatSize(path.ChangedBytes)})");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private void ToggleBackupProtection(BackupSnapshotItem? item)
        {
            if (item is null)
                return;

            if (!int.TryParse(item.Id, out int backupId))
                return;

            bool newValue = item.IsProtected;

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

        private void ManageProjectEncryption(ProjectBackupItem? project)
        {
            if (project is null)
                return;

            if (!int.TryParse(project.Id, out int projectId) || projectId <= 0)
                return;

            ManageProjectEncryptionRequested?.Invoke(projectId);
        }

        private void InitializeLocalizationDefaults()
        {
            SnapshotsSummaryLine = Lf("Backups.Summary.TodayWeek", "{0} backups today - {1} this week", 0, 0);
            TotalSnapshotsSecondaryLine = Lf("Backups.Summary.YesterdayAverage", "{0} yesterday - avg {1}", 0, "0 B");
            SnapshotActivitySummary = L("Backups.Summary.NoActivity", "No backups in the last 7 days");
            LastBackupDisplay = L(NoBackupsKey, NoBackupsFallback);
            LastBackupSecondaryLine = L("Backups.Summary.LastBackupSize", "Size -");
            LastBackupSizeValueFormatted = "0 B";
            TotalStoredLocalLine = Lf("Backups.Summary.LocalTotal", "Local total: {0}", "0 B");
            TotalStoredLocalValueFormatted = "0 B";
            TotalStoredImportedLine = Lf("Backups.Summary.ImportedTotal", "Imported total: {0}", "0 B");
            TotalStoredImportedValueFormatted = "0 B";
            HistoryFilterProjectLabel = L(AllProjectsKey, AllProjectsFallback);
            BackupHealthSummaryLine = L("Backups.Health.Center.Empty", "No project health data yet.");

            string driveLabel = Lf("Backups.Health.DriveLabel", "Drive: {0}", L("DriveHealth.UnknownDrive", "drive"));
            BackupDiskDriveLabel = driveLabel;
            BackupDiskHealthText = Lf("Backups.Health.Status.Unavailable", "Health ({0}): {1}", driveLabel, L("Backups.Health.NotAvailable", "not available"));

            OnPropertiesChanged(
                nameof(SnapshotsSummaryLine),
                nameof(TotalSnapshotsSecondaryLine),
                nameof(SnapshotActivitySummary),
                nameof(LastBackupDisplay),
                nameof(LastBackupSecondaryLine),
                nameof(TotalStoredLocalLine),
                nameof(TotalStoredImportedLine),
                nameof(HistoryFilterProjectLabel),
                nameof(BackupHealthSummaryLine),
                nameof(BackupDiskDriveLabel),
                nameof(BackupDiskHealthText));
            TopStorageConsumers.Clear();
            OnPropertyChanged(nameof(HasTopStorageConsumers));
            RefreshProjectSortOptions();
        }

        public void ReapplyLocalization()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(ReapplyLocalization);
                return;
            }

            _lastProjectSignature = -1;
            _lastBackupSignature = -1;
            _lastAutoBackupSignature = -1;
            _lastFilterRevision = -1;

            InitializeLocalizationDefaults();

            foreach (DestinationStatusItem des in DestinationStatuses)
            {
                des.RefreshLocalization();
            }
            OnPropertyChanged(string.Empty);
            _lastHealthProbeUtc = DateTime.MinValue;
            RefreshBackupDiskUsage(includeHealthProbe:true);
        }

        private void RefreshProjectSortOptions()
        {
            ProjectSortOptions.Clear();
            ProjectSortOptions.Add(new BackupsProjectSortOption("latest",
                L("Backups.Sort.LatestBackup", "Latest backup")));
            ProjectSortOptions.Add(new BackupsProjectSortOption("name",
                L("Backups.Sort.Name", "Project name")));
            ProjectSortOptions.Add(new BackupsProjectSortOption("size",
                L("Backups.Sort.TotalSize", "Total size")));
            ProjectSortOptions.Add(new BackupsProjectSortOption("count",
                L("Backups.Sort.BackupCount", "Backup count")));
            ProjectSortOptions.Add(new BackupsProjectSortOption("tags",
                L("Backups.Sort.Tags", "Tags")));

            SelectedProjectSortOption = ProjectSortOptions.FirstOrDefault(o =>
                                           string.Equals(o.Id, _projectSortMode, StringComparison.OrdinalIgnoreCase))
                                       ?? ProjectSortOptions.FirstOrDefault();

            OnPropertyChanged(nameof(ProjectSortOptions));
        }

        private void SortProjectBackups()
        {
            if (ProjectBackups.Count <= 1)
                return;

            IEnumerable<ProjectBackupItem> ordered = _projectSortMode switch
            {
                "name" => ProjectBackups
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
                "size" => ProjectBackups
                    .OrderByDescending(p => p.TotalSizeBytes)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
                "count" => ProjectBackups
                    .OrderByDescending(p => p.SnapshotCount)
                    .ThenByDescending(p => p.LastBackupTime ?? DateTime.MinValue)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
                "tags" => ProjectBackups
                    .OrderBy(p => p.PrimaryTagSortKey, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => ProjectBackups
                    .OrderByDescending(p => p.LastBackupTime ?? DateTime.MinValue)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            };

            string? selectedId = SelectedProject?.Id;
            var orderedList = ordered.ToList();
            ProjectBackups.SyncWith(orderedList);

            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                SelectedProject = ProjectBackups.FirstOrDefault(p =>
                    string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void RefreshEncryptionPolicyOptions()
        {
            EncryptionPolicyOptions.Clear();
            EncryptionPolicyOptions.Add(new EncryptionPolicyOption(
                ProjectEncryptionPolicy.Inherit,
                L("Projects.EncryptionPolicy.Inherit", "Inherit global")));
            EncryptionPolicyOptions.Add(new EncryptionPolicyOption(
                ProjectEncryptionPolicy.Encrypted,
                L(EncryptedPolicyKey, EncryptedFallback)));
            EncryptionPolicyOptions.Add(new EncryptionPolicyOption(
                ProjectEncryptionPolicy.Plain,
                L(PlainPolicyKey, PlainFallback)));
        }

        private void RefreshRestoreModeOptions()
        {
            RestoreModeOptions.Clear();
            RestoreModeOptions.Add(new RestoreModeOption(
                ProjectRestoreMode.Direct,
                L("Backups.Restore.Mode.Direct", "Direct (overwrite project path)")));
            RestoreModeOptions.Add(new RestoreModeOption(
                ProjectRestoreMode.Sandbox,
                L("Backups.Restore.Mode.Sandbox", "Sandbox (restore to preview folder)")));
        }

        private void RefreshVerificationPolicyOptions()
        {
            VerificationPolicyOptions.Clear();
            VerificationPolicyOptions.Add(new VerificationPolicyOption(
                ProjectVerificationPolicy.Always,
                L("Backups.Verification.Policy.Always", "Always")));
            VerificationPolicyOptions.Add(new VerificationPolicyOption(
                ProjectVerificationPolicy.Scheduled,
                L("Backups.Verification.Policy.Scheduled", "Scheduled")));
            VerificationPolicyOptions.Add(new VerificationPolicyOption(
                ProjectVerificationPolicy.Manual,
                L("Backups.Verification.Policy.Manual", "Manual only")));
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
                HistoryFilterProjectLabel = L(AllProjectsKey, AllProjectsFallback);
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
        public void UpdateActiveBackup(
            string projectId,
            string projectName,
            double progress,
            string currentFile,
            string etaText,
            bool allowCancel = true,
            string? destinationLabel = null,
            string? policyText = null,
            ProtectionActivityPhase? activityPhase = null,
            int? attempt = null,
            int? maxAttempts = null)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            ProtectionActivityPhase phase = activityPhase ??
                ProtectionActivityClassifier.Classify(progress, currentFile, etaText);
            double? semanticProgress = progress > 0.1d || phase == ProtectionActivityPhase.Completed
                ? Math.Clamp(progress, 0d, 100d)
                : null;

            var update = new PendingBackupUpdate(
                projectId,
                projectName,
                progress,
                currentFile,
                etaText,
                new ProtectionActivityState(phase, semanticProgress, attempt, maxAttempts),
                allowCancel,
                destinationLabel,
                policyText ?? string.Empty);

            _pendingActiveBackupUpdates[projectId] = update;

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_activeBackupFlushTimer.IsEnabled)
                        _activeBackupFlushTimer.Start();
                    if (progress >= 99.9 || !allowCancel)
                    {
                        FlushPendingActiveBackupUpdates();
                    }
                });
                return;
            }

            if (!_activeBackupFlushTimer.IsEnabled)
                _activeBackupFlushTimer.Start();

            if (progress >= 99.9 || !allowCancel)
            {
                FlushPendingActiveBackupUpdates();
            }
        }

        /// <summary>
        /// Removes a per-project backup progress item once the backup is finished
        /// or cancelled.
        /// </summary>
        public void RemoveActiveBackup(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            _pendingActiveBackupUpdates.TryRemove(projectId, out _);

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => RemoveActiveBackup(projectId));
                return;
            }

            BackupProgressItem? item = ActiveBackups.FirstOrDefault(p => p.ProjectId == projectId);
            if (item != null)
            {
                ActiveBackups.Remove(item);
            }
        }

        private void FlushPendingActiveBackupUpdates()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(FlushPendingActiveBackupUpdates);
                return;
            }

            if (_pendingActiveBackupUpdates.IsEmpty)
            {
                _activeBackupFlushTimer.Stop();
                return;
            }

            foreach (PendingBackupUpdate update in _pendingActiveBackupUpdates.Values)
            {
                ApplyActiveBackupUpdate(update);
            }

            _pendingActiveBackupUpdates.Clear();
        }

        private void ApplyActiveBackupUpdate(PendingBackupUpdate update)
        {
            BackupProgressItem? item = ActiveBackups.FirstOrDefault(p => p.ProjectId == update.ProjectId);
            if (item == null)
            {
                item = new BackupProgressItem
                {
                    ProjectId        = update.ProjectId,
                    ProjectName      = string.IsNullOrWhiteSpace(update.ProjectName)
                        ? L("Dashboard.Activity.UnknownProject", UnknownProjectFallback)
                        : update.ProjectName,
                    CancelRequested  = OnCancelActiveBackup
                };
                ActiveBackups.Add(item);
            }
            else if (!string.IsNullOrWhiteSpace(update.ProjectName))
            {
                item.ProjectName = update.ProjectName;
            }

            if (update.DestinationLabel is not null)
            {
                item.DestinationLabel = update.DestinationLabel;
            }
            item.PolicyText = update.PolicyText;

            item.ActivityState = update.ActivityState;
            item.AllowCancel = update.AllowCancel;

            if (!string.IsNullOrWhiteSpace(update.CurrentFile))
                item.CurrentFile = update.CurrentFile;

            item.EtaText = update.EtaText ?? string.Empty;
            item.Progress = update.Progress;
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

        public void ClearPrimaryBackupActivities()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(ClearPrimaryBackupActivities);
                return;
            }

            foreach (string projectId in _pendingActiveBackupUpdates.Keys.Where(IsPrimaryBackupActivityId))
                _pendingActiveBackupUpdates.TryRemove(projectId, out _);

            foreach (BackupProgressItem item in ActiveBackups.Where(item => IsPrimaryBackupActivityId(item.ProjectId)).ToList())
                ActiveBackups.Remove(item);
        }

        private static bool IsPrimaryBackupActivityId(string projectId) =>
            int.TryParse(projectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

        public void ResetDestinationStatuses(IEnumerable<BackupDestination> destinations, bool allowToggle)
        {
            var list = destinations.ToList();
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ResetDestinationStatuses(list, allowToggle));
                return;
            }

            ShowDestinationToggles = allowToggle;

            var activeIds = list.Select(DestinationStatusItem.GetId).ToHashSet();

            for (int i = DestinationStatuses.Count - 1; i >= 0; i--)
            {
                if (!activeIds.Contains(DestinationStatuses[i].Id))
                {
                    DestinationStatuses[i].PropertyChanged -= OnDestinationItemPropertyChanged;
                    DestinationStatuses.RemoveAt(i);
                }
            }

            foreach (BackupDestination? dest in list)
            {
                string id = DestinationStatusItem.GetId(dest);
                DestinationStatusItem? existing = DestinationStatuses.FirstOrDefault(x => x.Id == id);
                if (existing == null)
                {
                    DestinationStatus status = dest.Active ? DestinationStatus.Pending : DestinationStatus.Inactive;
                    SeverityStatus severity = SeverityStatus.None;
                    var item = new DestinationStatusItem
                    {
                        Id = DestinationStatusItem.GetId(dest),
                        Alias = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path,
                        Path = dest.Path,
                        Status = status,
                        Severity = severity,
                        DotBrush = GetDestinationDotBrush(status, severity),
                        LastCheckedUtc = null,
                        IsActive = dest.Active,
                        IsConfigurable = allowToggle
                    };
                    ApplyDestinationQuotaPlan(item);
                    item.PropertyChanged += OnDestinationItemPropertyChanged;
                    DestinationStatuses.Add(item);
                }
                else
                {
                    existing.IsActive = dest.Active;
                    existing.IsConfigurable = allowToggle;
                    existing.Alias = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;
                }
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
                    DestinationStatus newStatus = item.IsActive ? DestinationStatus.Pending : DestinationStatus.Inactive;
                    if (item.Status != newStatus)
                    {
                        item.Status = newStatus;
                        item.Severity = SeverityStatus.None;
                        item.DotBrush = GetDestinationDotBrush(newStatus, SeverityStatus.None);
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

            List<DestinationStatusItem> active = [.. DestinationStatuses.Where(item => item.IsActive)];
            ActiveDestinationStatuses.SyncWith(active);
            OnPropertyChanged(nameof(HasActiveDestinationStatuses));
        }

        public void UpdateDestinationStatus(string id, string status, SeverityStatus severity = SeverityStatus.None, string? alias = null)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => UpdateDestinationStatus(id, status, severity, alias));
                return;
            }

            DestinationStatusItem? item = DestinationStatuses.FirstOrDefault(d => d.Id == id);
            if (item is null && !string.IsNullOrWhiteSpace(alias))
            {
                item = DestinationStatuses.FirstOrDefault(d => string.Equals(d.Alias, alias, StringComparison.OrdinalIgnoreCase));
            }

            if (item is null)
                return;

            if (severity == SeverityStatus.None)
            {
                string rawText = status ?? string.Empty;
                if (rawText.Contains("Read-only", StringComparison.CurrentCultureIgnoreCase )||
                        rawText.Contains("ReadOnly", StringComparison.CurrentCultureIgnoreCase))
                {
                    severity = SeverityStatus.Warning;
                }
                else if (rawText.Contains("Unavailable", StringComparison.CurrentCultureIgnoreCase) ||
                          rawText.Contains("Unreachable", StringComparison.CurrentCultureIgnoreCase) ||
                          rawText.Contains("Error", StringComparison.CurrentCultureIgnoreCase))
                {
                    severity = SeverityStatus.Error;
                }
                else if (!string.IsNullOrWhiteSpace(rawText))
                {
                    severity = SeverityStatus.Success;
                }
            }
            DestinationStatus newStatus = severity switch
            {
                SeverityStatus.Success => DestinationStatus.Reachable,
                SeverityStatus.Warning => DestinationStatus.ReadOnly,
                SeverityStatus.Error => DestinationStatus.Unavailable,
                _ => DestinationStatus.None
            };

            item.Status   = newStatus;
            item.Severity = severity;
            item.DotBrush = GetDestinationDotBrush(newStatus, severity);
            item.LastCheckedUtc = DateTime.UtcNow;
        }

        public void MarkDestinationComplete(string id, bool success, string status)
        {
            UpdateDestinationStatus(id, status, success ? SeverityStatus.Success: SeverityStatus.Error);
        }

        private static IBrush GetDestinationDotBrush(DestinationStatus status, SeverityStatus severity)
        {
            return (status, severity) switch
            {
                (DestinationStatus.Inactive, _) => AccentBrush("#808080"),
                (_, SeverityStatus.Error)        => AccentBrush("#FF6B6B"),
                (DestinationStatus.Unavailable, _) => AccentBrush("#FF6B6B"),
                (DestinationStatus.ReadOnly, _)  => AccentBrush("#FFB84C"),
                (_, SeverityStatus.Warning)      => AccentBrush("#FFB84C"),
                (DestinationStatus.Reachable, _) => AccentBrush("#22CC88"),
                (_, SeverityStatus.Success)      => AccentBrush("#22CC88"),
                (_, SeverityStatus.None)         => AccentBrush("#8E9BAF"),
                _ => AccentBrush("#8E9BAF")
            };
        }

        private static IBrush AccentBrush(string hex) =>
            new ImmutableSolidColorBrush(Color.Parse(hex));

        /// <summary>
        /// Shows a non-cancellable transient operation (e.g., deleting a backup) in the active list.
        /// </summary>
        public void ShowTransientOperation(
            string operationId,
            string title,
            string detail,
            string etaText = "",
            string? destinationLabel = null)
        {
            UpdateActiveBackup(operationId, title, 0, detail, etaText, allowCancel: false, destinationLabel: destinationLabel);
        }

        /// <summary>
        /// Removes a transient operation card once completed.
        /// </summary>
        public void CompleteTransientOperation(string operationId, string finalDetail = "")
        {
            UpdateActiveBackup(operationId, string.Empty, 100, finalDetail, string.Empty, allowCancel: false);
            RemoveActiveBackup(operationId);
        }

        public void MarkSnapshotProtection(int snapshotId, bool isProtected)
        {
            foreach (BackupSnapshotItem item in _allSnapshots.Where(item => item.SnapshotId == snapshotId))
                item.IsProtected = isProtected;

            foreach (BackupSnapshotItem item in Snapshots.Where(item => item.SnapshotId == snapshotId))
                item.IsProtected = isProtected;

            foreach (SnapshotProjectGroup group in SnapshotGroups)
            {
                foreach (BackupSnapshotItem item in group.Snapshots.Where(item => item.SnapshotId == snapshotId))
                    item.IsProtected = isProtected;
            }
        }

        // ---------- Snapshot management + filtering ----------
        private void ApplyTypeFilter(string? type)
        {
            if (string.IsNullOrWhiteSpace(type) || type == "All")
            {
                // Reset to "All" types but keep the current project context.
                _currentTypeFilter = "All";

                // Only reset the label to "All projects" if we are not scoped to a project.
                if (string.IsNullOrWhiteSpace(_currentProjectIdFilter))
                {
                    HistoryFilterProjectLabel = L(AllProjectsKey, AllProjectsFallback);
                    OnPropertyChanged(nameof(HistoryFilterProjectLabel));
                }
            }
            else
            {
                // "Auto" or ManualBackupType while keeping the current project filter (if any).
                _currentTypeFilter = type;
            }

            RefreshSnapshotsView(false);
            OnPropertyChanged(nameof(IsTypeFilterAll));
            OnPropertyChanged(nameof(IsTypeFilterAuto));
            OnPropertyChanged(nameof(IsTypeFilterManual));
        }

        private void ReplaceSnapshots(IEnumerable<BackupSnapshotItem> newSnapshots, bool forceResetCompare = false)
        {
            string? keepAId = forceResetCompare ? null : SelectedSnapshotA?.Id;
            string? keepBId = forceResetCompare ? null : SelectedSnapshotB?.Id;

            var ordered = newSnapshots
                .OrderByDescending(s => s.Timestamp)
                .ToList();

            Snapshots.SyncWith(ordered);

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
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => RefreshSnapshotsView(forceResetCompare));
                return;
            }

            _ = RefreshSnapshotsViewAsync(forceResetCompare);
        }

        private async Task RefreshSnapshotsViewAsync(bool forceResetCompare = false)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(() => RefreshSnapshotsView(forceResetCompare));
                return;
            }

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

                List<BackupSnapshotItem> source = _allSnapshots.ToList();
                Dictionary<string, ProjectBackupItem> projectLookup = new(_projectLookupById, StringComparer.OrdinalIgnoreCase);
                string? preferredExpandedProjectId = _preferredExpandedProjectId;
                string? currentProjectIdFilter = _currentProjectIdFilter;
                int revision = _snapshotRevision;
                var groupText = new SnapshotGroupText(
                    L("Backups.Section.Group.Global", "Global snapshots"),
                    L(UnknownProjectGroupKey, UnknownProjectFallback),
                    L("Backups.Section.SnapshotCount.Singular", "{0} backup"),
                    L("Backups.Section.SnapshotCount.Plural", "{0} backups"));

                SnapshotViewRefreshResult result = await Task.Run(() => BuildSnapshotViewRefreshResult(
                    source,
                    projectLookup,
                    filterState,
                    revision,
                    preferredExpandedProjectId,
                    currentProjectIdFilter,
                    groupText)).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _filteredSnapshots.Clear();
                    _filteredSnapshots.AddRange(result.FilteredSnapshots);
                    _lastFilterState = result.FilterState;
                    _lastFilterRevision = result.Revision;
                    ReplaceSnapshots(result.FilteredSnapshots, forceResetCompare);
                    ReplaceSnapshotGroups(result.SnapshotGroups);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _refreshSnapshotsInFlight, 0);
                if (Interlocked.Exchange(ref _refreshSnapshotsQueued, 0) == 1)
                {
                    bool queuedForceReset = _refreshSnapshotsForceResetQueued;
                    _refreshSnapshotsForceResetQueued = false;
                    await RefreshSnapshotsViewAsync(queuedForceReset);
                }
            }
        }

        private SnapshotViewRefreshResult BuildSnapshotViewRefreshResult(
            IReadOnlyList<BackupSnapshotItem> source,
            IReadOnlyDictionary<string, ProjectBackupItem> projectLookup,
            SnapshotFilterState filterState,
            int revision,
            string? preferredExpandedProjectId,
            string? currentProjectIdFilter,
            SnapshotGroupText groupText)
        {
            var filtered = new List<BackupSnapshotItem>(source.Count);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (BackupSnapshotItem snapshot in source)
            {
                if (filterState.TypeFilter == "Auto" && !string.Equals(snapshot.Type, "Auto", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (filterState.TypeFilter == ManualBackupType && !string.Equals(snapshot.Type, ManualBackupType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (filterState.OnlyManual && !string.Equals(snapshot.Type, ManualBackupType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (filterState.OnlyErrors && !string.Equals(snapshot.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(filterState.ProjectId) &&
                    !string.Equals(snapshot.ProjectId, filterState.ProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(snapshot.Id) && !seenIds.Add(snapshot.Id))
                    continue;

                filtered.Add(snapshot);
            }

            return new SnapshotViewRefreshResult(
                filterState,
                revision,
                filtered,
                BuildSnapshotGroups(filtered, projectLookup, preferredExpandedProjectId, currentProjectIdFilter, groupText));
        }

        private void ReplaceSnapshotGroups(IReadOnlyList<SnapshotProjectGroup> groups)
        {
            SnapshotGroups.SyncWith(groups);
        }

        private List<SnapshotProjectGroup> BuildSnapshotGroups(
            IReadOnlyList<BackupSnapshotItem> filtered,
            IReadOnlyDictionary<string, ProjectBackupItem> projectLookup,
            string? preferredExpandedProjectId,
            string? currentProjectIdFilter,
            SnapshotGroupText groupText)
        {
            if (filtered.Count == 0)
                return [];

            IOrderedEnumerable<IGrouping<string, BackupSnapshotItem>> grouped = filtered
                .GroupBy(s => s.ProjectId ?? string.Empty)
                .OrderByDescending(g => g.Max(s => s.Timestamp))
                .ThenBy(g =>
                {
                    if (!string.IsNullOrWhiteSpace(g.Key) && projectLookup.TryGetValue(g.Key, out ProjectBackupItem? nameSource))
                        return nameSource.Name;
                    return "zzzz_" + g.Key;
                });

            DateTime latestOverall = filtered
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefault()
                ?.Timestamp ?? DateTime.MinValue;
            var groups = new List<SnapshotProjectGroup>();

            foreach (IGrouping<string, BackupSnapshotItem>? g in grouped)
            {
                string key = g.Key ?? string.Empty;

                var ordered = g
                    .GroupBy(s => s.Id)
                    .Select(grp => grp.First())
                    .OrderByDescending(s => s.Timestamp)
                    .ToList();
                long totalBytes = ordered.Sum(s => s.SizeBytes);
                DateTime latest = ordered.FirstOrDefault()?.Timestamp ?? DateTime.MinValue;

                string projectName;
                if (string.IsNullOrWhiteSpace(key))
                {
                    projectName = groupText.GlobalProjectName;
                }
                else if (!projectLookup.TryGetValue(key, out ProjectBackupItem? nameSource))
                {
                    projectName = groupText.UnknownProjectName;
                }
                else
                {
                    projectName = nameSource.Name;
                }

                string summaryFormat = ordered.Count == 1
                    ? groupText.SingleSnapshotFormat
                    : groupText.MultipleSnapshotsFormat;

                ImmutableSolidColorBrush accentBrush = GetAccentBrush(DefaultAccentColor);
                if (!string.IsNullOrWhiteSpace(key) && projectLookup.TryGetValue(key, out ProjectBackupItem? colorSource))
                {
                    accentBrush = GetAccentBrush(colorSource.AvatarColor);
                }

                bool isExpanded = !string.IsNullOrWhiteSpace(preferredExpandedProjectId)
                    ? string.Equals(preferredExpandedProjectId, key, StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrWhiteSpace(currentProjectIdFilter)
                        ? string.Equals(currentProjectIdFilter, key, StringComparison.OrdinalIgnoreCase)
                        : latest == latestOverall;

                var groupVm = new SnapshotProjectGroup
                {
                    ProjectId = key,
                    ProjectName = projectName,
                    ProjectTagsDisplay = !string.IsNullOrWhiteSpace(key) && projectLookup.TryGetValue(key, out ProjectBackupItem? tagSource)
                        ? tagSource.ProjectTagsDisplay
                        : string.Empty,
                    Summary = string.Format(CultureInfo.CurrentCulture, summaryFormat, ordered.Count),
                    TotalSizeFormatted = BackupSnapshotItem.FormatSize(totalBytes),
                    LatestBackupDisplay = latest == DateTime.MinValue ? "-" : latest.ToString(TimestampMinuteFormat),
                    AccentBrush = accentBrush,
                    IsExpanded = isExpanded
                };

                if (!string.IsNullOrWhiteSpace(key) && projectLookup.TryGetValue(key, out ProjectBackupItem? chipSource))
                {
                    foreach (ProjectTagChip chip in chipSource.ProjectTagChips)
                        groupVm.ProjectTagChips.Add(chip);
                }

                groupVm.SetSnapshots(ordered);

                groups.Add(groupVm);
            }

            return groups;
        }

        private static ImmutableSolidColorBrush GetAccentBrush(string? hexColor)
        {
            string normalized = string.IsNullOrWhiteSpace(hexColor) ? DefaultAccentColor : hexColor;
            if (AccentBrushCache.TryGetValue(normalized, out ImmutableSolidColorBrush? cached))
                return cached;

            try
            {
                var brush = new ImmutableSolidColorBrush(Color.Parse(normalized));
                AccentBrushCache[normalized] = brush;
                return brush;
            }
            catch
            {
                return AccentBrushCache.GetOrAdd(DefaultAccentColor, _ => new ImmutableSolidColorBrush(Color.Parse(DefaultAccentColor)));
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
                    AppConfig config = _configStore.GetSnapshot();
                    (double usedPercent, string freeText, string thresholdText, bool isBelowThreshold, string _, DashboardViewModel.BackupDiskUsageStatus status) =
                        DashboardViewModel.ComputeBackupDiskUsageDetailed(config);
                    string driveLabel = Lf("Backups.Health.DriveLabel", "Drive: {0}", FormatDriveLabel(config.Backups.BackupRoot));

                    string? healthText = null;
                    IBrush? healthBrush = null;

                    bool shouldProbeHealth = includeHealthProbe;
                    if (includeHealthProbe)
                    {
                        lock (_healthProbeGate)
                        {
                            DateTime now = DateTime.UtcNow;
                            if (now - _lastHealthProbeUtc < HealthProbeCooldown)
                            {
                                shouldProbeHealth = false;
                            }
                            else
                            {
                                _lastHealthProbeUtc = now;
                            }
                        }
                    }
                    if (shouldProbeHealth && DateTime.UtcNow - AppViewModel.AppStartUtc < TimeSpan.FromSeconds(20))
                    {
                        shouldProbeHealth = false;
                    }

                    if (shouldProbeHealth)
                    {
                        var healthService = new DriveHealthService();
                        string backupPath = config.Backups.BackupRoot ?? string.Empty;
                        DriveHealthResult health = healthService.CheckPath(backupPath);

                        string fallbackMessage = string.IsNullOrWhiteSpace(health.Message)
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

                    double displayUsedPercent = usedPercent;
                    bool displayBelowThreshold = isBelowThreshold;
                    if (status != DashboardViewModel.BackupDiskUsageStatus.Ok && BackupDiskUsedPercent > 0)
                    {
                        displayUsedPercent = BackupDiskUsedPercent;
                        displayBelowThreshold = false;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateBackupDiskUsage(displayUsedPercent, freeText, thresholdText, displayBelowThreshold);
                        BackupDiskDriveLabel = driveLabel;
                        if (shouldProbeHealth && healthText is not null && healthBrush is not null)
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

                        string driveUnknown = L("DriveHealth.UnknownDrive", "drive");
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
                string normalized = System.IO.Path.GetFullPath(path);

                // On Windows, keep the drive root; on macOS/Linux prefer the mount point under /Volumes.
                if (OperatingSystem.IsWindows())
                {
                    string? root = System.IO.Path.GetPathRoot(normalized);
                    if (!string.IsNullOrWhiteSpace(root))
                        return root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                }
                else if (normalized.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
                if (TryParseShareWithSubpath(path, out string? host, out string? share, out string? subPath))
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
                string trimmed = path.TrimStart('\\', '/').Replace('\\', '/');
                string[] parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
            NotificationSeverity sev = severity switch
            {
                "Error"   => NotificationSeverity.Error,
                "Warning" => NotificationSeverity.Warning,
                _         => NotificationSeverity.Info
            };

            Notification.Show(message, sev, actionLabel: actionLabel, actionCommand: actionCommand);
        }

        private void OnAutoBackupChanged(ProjectBackupItem item)
        {
            if (int.TryParse(item.Id, out int projectId))
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
            VerificationPopupMessage = string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "Backups.Verification.FailedForProject",
                    "Backup verification failed for {0}. The backup may be incomplete or corrupted."),
                projectName);
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

            BackupSnapshotItem? snapshot = _allSnapshots.FirstOrDefault(s => s.Id == VerificationFailedBackupId);
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

            BackupSnapshotItem? snapshot = _allSnapshots.FirstOrDefault(s => s.Id == backupId);
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
            DateTime now       = DateTime.Now;
            DateTime weekStart = now.Date.AddDays(-6);

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
                string.Equals(s.Type, ManualBackupType, StringComparison.OrdinalIgnoreCase));
            ImportedSnapshotsThisWeek = _allSnapshots.Count(s =>
                s.Timestamp.Date >= weekStart &&
                s.IsImported);

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
                    "{0} backups total - {1} auto - {2} manual - {3} imported",
                    SnapshotsThisWeek,
                    AutoSnapshotsThisWeek,
                    ManualSnapshotsThisWeek,
                    ImportedSnapshotsThisWeek);
            }

            if (_allSnapshots.Count > 0)
            {
                BackupSnapshotItem last = _allSnapshots
                    .OrderByDescending(s => s.Timestamp)
                    .First();

                LastBackupDisplay  = last.Timestamp.ToString(TimestampMinuteFormat);
                LastBackupRelative = FormatRelative(now - last.Timestamp);
                LastBackupSecondaryLine = Lf(
                    "Backups.Summary.LastBackupSize",
                    "Size {0}",
                    BackupSnapshotItem.FormatSize(last.SizeBytes));
                LastBackupSizeValueFormatted = BackupSnapshotItem.FormatSize(last.SizeBytes);
                string lastProjectName = ResolveProjectNameFromSnapshot(last);
                LastBackupProjectName = string.IsNullOrWhiteSpace(lastProjectName)
                    ? L("Backups.Summary.UnknownProject", UnknownProjectFallback)
                    : lastProjectName;
                LastBackupTypeDisplay = !string.IsNullOrWhiteSpace(last.TypeLabel)
                    ? last.TypeLabel
                    : last.Type;
                LastBackupDestinationDisplay = string.IsNullOrWhiteSpace(last.DestinationDisplay)
                    ? L("Backups.Summary.UnknownDestination", "Unknown")
                    : last.DestinationDisplay;
                if (last.IsImported)
                {
                    LastBackupSecurityDisplay = last.IsEncrypted
                        ? L("Backups.Summary.ImportedEncrypted", "Imported - Encrypted")
                        : L("Backups.Summary.ImportedPlain", "Imported - Plain");
                }
                else
                {
                    LastBackupSecurityDisplay = last.IsEncrypted
                        ? L("Backups.Summary.LocalEncrypted", "Local - Encrypted")
                        : L("Backups.Summary.LocalPlain", "Local - Plain");
                }
                double ageHours = Math.Max(0, (now - last.Timestamp).TotalHours);
                double freshness = Math.Clamp(100d - (ageHours / 72d * 100d), 0d, 100d);
                LastBackupFreshnessPercent = freshness;
                string freshnessStateLabel = freshness >= 80
                    ? L("Backups.Summary.Freshness.Good", "Fresh")
                    : freshness >= 40
                        ? L("Backups.Summary.Freshness.Moderate", "Aging")
                        : L("Backups.Summary.Freshness.Stale", "Stale");
                LastBackupFreshnessLabel = Lf("Backups.Summary.Freshness.WithAge", "{0} - {1}",
                    freshnessStateLabel,
                    LastBackupRelative);
                LastBackupFreshnessTooltip = L("Backups.Summary.Freshness.Tooltip", "Good: <24h | Moderate: 24-72h | Stale: >72h");
                LastBackupFreshnessBrush = freshness >= 80
                    ? FreshnessGoodBrush
                    : freshness >= 40
                        ? FreshnessModerateBrush
                        : FreshnessStaleBrush;
            }
            else
            {
                LastBackupDisplay  = L(NoBackupsKey, NoBackupsFallback);
                LastBackupRelative = "-";
                LastBackupSecondaryLine = L("Backups.Summary.LastBackupSize", "Size -");
                LastBackupSizeValueFormatted = "0 B";
                LastBackupProjectName = "-";
                LastBackupTypeDisplay = "-";
                LastBackupDestinationDisplay = "-";
                LastBackupSecurityDisplay = "-";
                LastBackupFreshnessPercent = 0;
                LastBackupFreshnessLabel = L(NoBackupsKey, NoBackupsFallback);
                LastBackupFreshnessTooltip = L(NoBackupsKey, NoBackupsFallback);
                LastBackupFreshnessBrush = FreshnessUnknownBrush;
            }

            long totalBytes = _allSnapshots.Sum(s => s.SizeBytes);
            TotalBackupSizeFormatted = BackupSnapshotItem.FormatSize(totalBytes);
            string avgSize = _allSnapshots.Count > 0
                ? BackupSnapshotItem.FormatSize(totalBytes / _allSnapshots.Count)
                : "0 B";
            TotalSnapshotsSecondaryLine = Lf(
                "Backups.Summary.YesterdayAverage",
                "{0} yesterday - avg {1}",
                SnapshotsYesterday,
                avgSize);
            long localBytes = _allSnapshots.Where(s => !s.IsImported).Sum(s => s.SizeBytes);
            TotalStoredLocalLine = Lf(
                "Backups.Summary.LocalTotal",
                "Local total: {0}",
                BackupSnapshotItem.FormatSize(localBytes));
            TotalStoredLocalValueFormatted = BackupSnapshotItem.FormatSize(localBytes);

            var importedItems = _allSnapshots.Where(s => s.IsImported).ToList();
            int importedCount = importedItems.Count;
            ImportedSnapshotsCount = importedCount;
            LocalSnapshotsCount = Math.Max(0, _allSnapshots.Count - importedCount);
            long importedBytes = importedItems.Sum(s => s.SizeBytes);
            TotalStoredImportedLine = Lf(
                "Backups.Summary.ImportedTotal",
                "Imported total: {0}",
                BackupSnapshotItem.FormatSize(importedBytes));
            TotalStoredImportedValueFormatted = BackupSnapshotItem.FormatSize(importedBytes);

            if (SnapshotsThisWeek <= 0)
            {
                ThisWeekAutoPercent = 0;
                ThisWeekManualPercent = 0;
                ThisWeekImportedPercent = 0;
            }
            else
            {
                ThisWeekAutoPercent = AutoSnapshotsThisWeek * 100d / SnapshotsThisWeek;
                ThisWeekManualPercent = ManualSnapshotsThisWeek * 100d / SnapshotsThisWeek;
                ThisWeekImportedPercent = ImportedSnapshotsThisWeek * 100d / SnapshotsThisWeek;
            }

            long safeLocal = Math.Max(0, localBytes);
            long safeImported = Math.Max(0, importedBytes);
            if (safeLocal + safeImported == 0)
            {
                StorageLocalPercent = 0;
                StorageImportedPercent = 0;
            }
            else
            {
                long totalStorage = safeLocal + safeImported;
                StorageLocalPercent = safeLocal * 100d / totalStorage;
                StorageImportedPercent = safeImported * 100d / totalStorage;
            }

            RebuildTopStorageConsumers();
            RebuildBackupHealthCenter(now);
            RebuildSnapshotActivity(now);

            // Notify UI that summary properties changed
            OnPropertiesChanged(
                nameof(TotalSnapshots),
                nameof(SnapshotsThisWeek),
                nameof(SnapshotsToday),
                nameof(SnapshotsYesterday),
                nameof(AutoSnapshotsThisWeek),
                nameof(ManualSnapshotsThisWeek),
                nameof(ImportedSnapshotsThisWeek),
                nameof(SnapshotsSummaryLine),
                nameof(TotalSnapshotsSecondaryLine),
                nameof(SnapshotActivitySummary),
                nameof(LastBackupDisplay),
                nameof(LastBackupRelative),
                nameof(LastBackupSecondaryLine),
                nameof(LastBackupSizeValueFormatted),
                nameof(LastBackupProjectName),
                nameof(LastBackupTypeDisplay),
                nameof(LastBackupDestinationDisplay),
                nameof(LastBackupSecurityDisplay),
                nameof(LastBackupFreshnessPercent),
                nameof(LastBackupFreshnessLabel),
                nameof(LastBackupFreshnessTooltip),
                nameof(LastBackupFreshnessBrush),
                nameof(TotalBackupSizeFormatted),
                nameof(LocalSnapshotsCount),
                nameof(TotalStoredLocalLine));
            OnPropertiesChanged(
                nameof(TotalStoredLocalValueFormatted),
                nameof(TotalStoredImportedLine),
                nameof(TotalStoredImportedValueFormatted),
                nameof(ImportedSnapshotsCount),
                nameof(ThisWeekAutoPercent),
                nameof(ThisWeekManualPercent),
                nameof(ThisWeekImportedPercent),
                nameof(StorageLocalPercent),
                nameof(StorageImportedPercent),
                nameof(HasTopStorageConsumers),
                nameof(HealthHealthyProjects),
                nameof(HealthAgingProjects),
                nameof(HealthStaleProjects),
                nameof(HealthNoBackupProjects),
                nameof(HealthHealthyPercent),
                nameof(HealthAgingPercent),
                nameof(HealthStalePercent),
                nameof(HealthNoBackupPercent),
                nameof(BackupHealthSummaryLine),
                nameof(RestoreReadinessReadyProjects),
                nameof(RestoreReadinessAttentionProjects),
                nameof(RestoreReadinessRiskProjects),
                nameof(RestoreReadinessUnavailableProjects),
                nameof(RestoreReadinessReadyPercent),
                nameof(RestoreReadinessAttentionPercent),
                nameof(RestoreReadinessRiskPercent),
                nameof(RestoreReadinessUnavailablePercent),
                nameof(RestoreReadinessHeadline),
                nameof(RestoreReadinessDetail));
        }

        public void UpdateSummaryLayout(double width)
        {
            const double chartThreshold = 1180;
            const double activityThreshold = 1460;

            bool showCharts = width >= chartThreshold;
            bool showActivity = width >= activityThreshold;

            ShowSummaryCharts = showCharts;
            ShowActivityPanel = showActivity;
            ActivityColumnWidth = showActivity
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            SummaryColumnSpacing = showActivity ? 14 : 0;

            // Keep both main panels visible at all viewport sizes and keep equal split.
            MainAreaLeftColumnWidth = new GridLength(1, GridUnitType.Star);
            MainAreaRightColumnWidth = new GridLength(1, GridUnitType.Star);
            MainAreaRightPanelColumn = 1;
            MainAreaRightPanelRow = 0;

            if (Math.Abs(_lastSummaryViewportWidth - width) > 8)
            {
                _lastSummaryViewportWidth = width;
                RebuildSnapshotActivity(DateTime.Now);
            }
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

        private void RebuildTopStorageConsumers()
        {
            var projectNameById = ProjectBackups
                .Where(project => !string.IsNullOrWhiteSpace(project.Id))
                .ToDictionary(project => project.Id, project => project.Name, StringComparer.OrdinalIgnoreCase);

            var totalsByProject = _allSnapshots
                .GroupBy(snapshot => string.IsNullOrWhiteSpace(snapshot.ProjectId) ? "__unknown__" : snapshot.ProjectId!)
                .Select(group => new
                {
                    ProjectId = group.Key,
                    TotalBytes = group.Sum(item => Math.Max(0, item.SizeBytes))
                })
                .Where(entry => entry.TotalBytes > 0)
                .OrderByDescending(entry => entry.TotalBytes)
                .Take(5)
                .ToList();

            TopStorageConsumers.Clear();
            if (totalsByProject.Count == 0)
                return;

            long totalBytes = totalsByProject.Sum(entry => entry.TotalBytes);
            foreach (var entry in totalsByProject)
            {
                string projectName = projectNameById.TryGetValue(entry.ProjectId, out string? foundName)
                    ? foundName
                    : L(UnknownProjectGroupKey, UnknownProjectFallback);

                double sharePercent = totalBytes <= 0
                    ? 0
                    : entry.TotalBytes * 100d / totalBytes;

                TopStorageConsumers.Add(new StorageConsumerItem(
                    projectName,
                    BackupSnapshotItem.FormatSize(entry.TotalBytes),
                    sharePercent));
            }
        }

        private void RebuildBackupHealthCenter(DateTime now)
        {
            int healthy = 0;
            int aging = 0;
            int stale = 0;
            int noBackup = 0;

            foreach (ProjectBackupItem project in ProjectBackups)
            {
                if (!project.LastBackupTime.HasValue)
                {
                    noBackup++;
                    continue;
                }

                TimeSpan age = now - project.LastBackupTime.Value;
                if (age.TotalHours < 24)
                    healthy++;
                else if (age.TotalHours <= 72)
                    aging++;
                else
                    stale++;
            }

            HealthHealthyProjects = healthy;
            HealthAgingProjects = aging;
            HealthStaleProjects = stale;
            HealthNoBackupProjects = noBackup;

            int total = Math.Max(0, healthy + aging + stale + noBackup);
            if (total == 0)
            {
                HealthHealthyPercent = 0;
                HealthAgingPercent = 0;
                HealthStalePercent = 0;
                HealthNoBackupPercent = 0;
                BackupHealthSummaryLine = L("Backups.Health.Center.Empty", "No project health data yet.");
                return;
            }

            HealthHealthyPercent = healthy * 100d / total;
            HealthAgingPercent = aging * 100d / total;
            HealthStalePercent = stale * 100d / total;
            HealthNoBackupPercent = noBackup * 100d / total;
            BackupHealthSummaryLine = Lf(
                "Backups.Health.Center.Summary",
                "Healthy {0} · Aging {1} · Stale {2} · No backup {3}",
                healthy.ToString(CultureInfo.CurrentCulture),
                aging.ToString(CultureInfo.CurrentCulture),
                stale.ToString(CultureInfo.CurrentCulture),
                noBackup.ToString(CultureInfo.CurrentCulture));
        }

        private void UpdateRestoreReadinessSummary(AppConfig config, IReadOnlyList<Project> projectList, IReadOnlyList<Backup> backupList)
        {
            var service = new RestoreReadinessService();
            RestoreReadinessSummary summary = service.BuildSummary(projectList, backupList, config, config.Advanced.BackupIndexLastScan);

            RestoreReadinessReadyProjects = summary.ReadyCount;
            RestoreReadinessAttentionProjects = summary.AttentionCount;
            RestoreReadinessRiskProjects = summary.RiskCount;
            RestoreReadinessUnavailableProjects = summary.UnavailableCount;
            RestoreReadinessHeadline = FormatRestoreReadinessHeadline(summary);
            RestoreReadinessDetail = FormatRestoreReadinessDetail(summary);

            foreach (ProjectRestoreReadiness result in summary.Projects)
            {
                if (!_projectLookupById.TryGetValue(result.ProjectId.ToString(CultureInfo.InvariantCulture), out ProjectBackupItem? item))
                    continue;

                item.RestoreReadinessLabel = LocalizeRestoreReadinessLabel(result.State);
                item.RestoreReadinessReason = result.Reason;
                item.RestoreReadinessBrush = result.State switch
                {
                    RestoreReadinessState.Ready => FreshnessGoodBrush,
                    RestoreReadinessState.Attention => FreshnessModerateBrush,
                    RestoreReadinessState.Risk => FreshnessStaleBrush,
                    _ => FreshnessUnknownBrush
                };
            }

            int total = summary.ProjectCount;
            if (total <= 0)
            {
                RestoreReadinessReadyPercent = 0;
                RestoreReadinessAttentionPercent = 0;
                RestoreReadinessRiskPercent = 0;
                RestoreReadinessUnavailablePercent = 0;
                RestoreReadinessIssues.Clear();
                ShowRestoreReadinessIssues = false;
                OnPropertyChanged(nameof(HasRestoreReadinessIssues));
                _toggleRestoreReadinessIssuesCommand.RaiseCanExecuteChanged();
                return;
            }

            RestoreReadinessReadyPercent = summary.ReadyCount * 100d / total;
            RestoreReadinessAttentionPercent = summary.AttentionCount * 100d / total;
            RestoreReadinessRiskPercent = summary.RiskCount * 100d / total;
            RestoreReadinessUnavailablePercent = summary.UnavailableCount * 100d / total;

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
                    LocalizeRestoreReadinessLabel(item.State),
                    item.Reason,
                    item.State switch
                    {
                        RestoreReadinessState.Ready => FreshnessGoodBrush,
                        RestoreReadinessState.Attention => FreshnessModerateBrush,
                        RestoreReadinessState.Risk => FreshnessStaleBrush,
                        _ => FreshnessUnknownBrush
                    }));
            }

            if (RestoreReadinessIssues.Count == 0)
                ShowRestoreReadinessIssues = false;

            OnPropertyChanged(nameof(HasRestoreReadinessIssues));
            _toggleRestoreReadinessIssuesCommand.RaiseCanExecuteChanged();
        }

        private static string LocalizeRestoreReadinessLabel(RestoreReadinessState state)
        {
            return state switch
            {
                RestoreReadinessState.Ready => L("RestoreReadiness.State.Ready", "Ready"),
                RestoreReadinessState.Attention => L("RestoreReadiness.State.Attention", "Attention"),
                RestoreReadinessState.Risk => L("RestoreReadiness.State.Risk", "Risk"),
                _ => L("RestoreReadiness.State.Unavailable", "Unavailable")
            };
        }

        private static string FormatRestoreReadinessHeadline(RestoreReadinessSummary summary)
        {
            if (summary.ProjectCount <= 0)
                return L("RestoreReadiness.Headline.Empty", "No tracked projects yet");

            if (summary.ReadyCount == summary.ProjectCount)
                return L("RestoreReadiness.Headline.AllReady", "Restore ready across all tracked projects");

            if (summary.UnavailableCount > 0)
                return Lf("RestoreReadiness.Headline.Unavailable", "{0} project(s) are not currently restore-ready", summary.UnavailableCount);

            if (summary.RiskCount > 0)
                return Lf("RestoreReadiness.Headline.Risk", "{0} project(s) need restore-readiness attention", summary.RiskCount);

            if (summary.AttentionCount > 0)
                return Lf("RestoreReadiness.Headline.Attention", "{0} project(s) should be reviewed", summary.AttentionCount);

            return L("RestoreReadiness.Headline.Empty", "No tracked projects yet");
        }

        private static string FormatRestoreReadinessDetail(RestoreReadinessSummary summary)
        {
            return Lf(
                "RestoreReadiness.Detail",
                "Ready {0} - Attention {1} - Risk {2} - Unavailable {3}",
                summary.ReadyCount,
                summary.AttentionCount,
                summary.RiskCount,
                summary.UnavailableCount);
        }

        private static bool IsComparableBackupDelta(Backup latest, Backup candidate)
        {
            if (latest.IsImported != candidate.IsImported)
                return false;

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    NormalizeDeltaScope(latest.OriginMachineName),
                    NormalizeDeltaScope(candidate.OriginMachineName)))
            {
                return false;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    NormalizeDeltaScope(latest.DestinationPath),
                    NormalizeDeltaScope(candidate.DestinationPath)))
            {
                return false;
            }

            return true;
        }

        private static string NormalizeDeltaScope(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }

        // ---------- Weekly activity mini-chart ----------

        private void RebuildSnapshotActivity(DateTime now)
        {
            SnapshotActivity.Clear();

            // Last 7 days, oldest -> newest
            DateTime[] days = [.. Enumerable.Range(0, 7).Select(offset => now.Date.AddDays(-6 + offset))];

            var autoByDate = _allSnapshots
                .Where(s => string.Equals(s.Type, "Auto", StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Timestamp.Date)
                .ToDictionary(g => g.Key, g => g.Count());
            var manualByDate = _allSnapshots
                .Where(s => string.Equals(s.Type, ManualBackupType, StringComparison.OrdinalIgnoreCase))
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
                    autoByDate.TryGetValue(d, out int autoCount);
                    manualByDate.TryGetValue(d, out int manualCount);
                    importedByDate.TryGetValue(d, out int importedCount);
                    return autoCount + manualCount + importedCount;
                })
                .ToList();

            int maxTotal = totals.DefaultIfEmpty(0).Max();
            if (maxTotal == 0)
                maxTotal = 1; // avoid divide-by-zero

            double chartHeight = maxTotal <= 2 ? 150d : (maxTotal <= 4 ? 172d : 192d);
            // Keep the activity chart proportionate on wide windowed layouts.
            double widthBoost = ShowActivityPanel
                ? Math.Clamp((_lastSummaryViewportWidth - 1380d) * 0.05d, 0d, 52d)
                : 0d;
            chartHeight += widthBoost;
            const double barBase = 12;
            double barRange = chartHeight - 36;
            SnapshotActivityChartHeight = chartHeight;

            long maxBytes = bytesByDate.Values.DefaultIfEmpty(0L).Max();
            if (maxBytes == 0)
                maxBytes = 1;

            foreach (DateTime day in days)
            {
                autoByDate.TryGetValue(day, out int autoCount);
                manualByDate.TryGetValue(day, out int manualCount);
                importedByDate.TryGetValue(day, out int importedCount);
                bytesByDate.TryGetValue(day, out long totalBytes);

                int totalCount = autoCount + manualCount + importedCount;
                double normalized = totalBytes > 0
                    ? totalBytes / (double)maxBytes
                    : totalCount / (double)maxTotal;
                double totalHeight = totalCount == 0 ? 0 : barBase + normalized * barRange;

                double autoHeight = 0d;
                double manualHeight = 0d;
                double importedHeight = 0d;
                if (totalCount > 0)
                {
                    autoHeight = autoCount == 0 ? 0 : Math.Max(5, totalHeight * autoCount / totalCount);
                    manualHeight = manualCount == 0 ? 0 : Math.Max(5, totalHeight * manualCount / totalCount);
                    importedHeight = importedCount == 0 ? 0 : Math.Max(5, totalHeight * importedCount / totalCount);

                    double combined = autoHeight + manualHeight + importedHeight;
                    if (combined > totalHeight && combined > 0)
                    {
                        double scale = totalHeight / combined;
                        autoHeight *= scale;
                        manualHeight *= scale;
                        importedHeight *= scale;
                    }
                }

                string dayLabel = day.ToString("ddd");
                string tooltip = totalCount == 0
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

            AppConfig config = _configStore.GetSnapshot();
            ShowProjectAvatars = config.Appearance.ShowProjectAvatars;
            OnPropertyChanged(nameof(ShowProjectAvatars));
            RefreshEncryptionPolicyOptions();
            RefreshRestoreModeOptions();
            RefreshVerificationPolicyOptions();
            RefreshDestinationOptions(config);

            var projectList = projects.ToList();
            var dedupBackups = new Dictionary<int, Backup>();
            foreach (Backup backup in backups)
            {
                if (!dedupBackups.ContainsKey(backup.Id))
                {
                    dedupBackups[backup.Id] = backup;
                }
            }
            var backupList = dedupBackups.Values.ToList();
            RefreshDestinationQuotaPlans(config, backupList);
            Dictionary<int, Snapshot> snapshotById = LoadSnapshotLookup(config, backupList);
            int projectSignature = ComputeProjectSignature(projectList);
            int backupSignature = ComputeBackupSignature(backupList);
            int autoSignature = ComputeAutoBackupSignature(autoBackupDisabledProjects);

            bool dataChanged = projectSignature != _lastProjectSignature || backupSignature != _lastBackupSignature;
            bool autoChanged = autoSignature != _lastAutoBackupSignature;
            if (!dataChanged && autoChanged)
            {
                UpdateAutoBackupFlags(autoBackupDisabledProjects);
                _lastAutoBackupSignature = autoSignature;
                return;
            }

            if (!dataChanged && !autoChanged)
                return;

            ProjectBackups.Clear();
            _projectLookupById.Clear();
            _allSnapshots.Clear();

            // Map per-project aggregates in a single pass.
            var projectStats = new Dictionary<int, (int Count, long TotalBytes, DateTime? LastBackupTime)>();
            foreach (Backup? backup in backupList)
            {
                if (!projectStats.TryGetValue(backup.ProjectId, out (int Count, long TotalBytes, DateTime? LastBackupTime) stats))
                    stats = (0, 0L, null);

                stats.Count++;
                stats.TotalBytes += backup.TotalBytes;
                if (!stats.LastBackupTime.HasValue || backup.CreatedUtc > stats.LastBackupTime.Value)
                    stats.LastBackupTime = backup.CreatedUtc;

                projectStats[backup.ProjectId] = stats;
            }

            // Compute per-project delta vs previous backup (latest - previous).
            var projectDeltaById = new Dictionary<int, long?>();
            foreach (IGrouping<int, Backup> group in backupList.GroupBy(b => b.ProjectId))
            {
                var ordered = group
                    .OrderByDescending(b => b.CreatedUtc)
                    .ThenByDescending(b => b.Id)
                    .ToList();

                Backup? latest = ordered.FirstOrDefault();
                Backup? previous = latest is null
                    ? null
                    : ordered.Skip(1).FirstOrDefault(candidate => IsComparableBackupDelta(latest, candidate));

                if (latest is not null && previous is not null)
                {
                    projectDeltaById[group.Key] = latest.TotalBytes - previous.TotalBytes;
                }
                else
                {
                    projectDeltaById[group.Key] = null;
                }
            }

            var orderedProjects = projectList
                .OrderByDescending(project =>
                    projectStats.TryGetValue(project.Id, out (int Count, long TotalBytes, DateTime? LastBackupTime) stats)
                        ? stats.LastBackupTime ?? DateTime.MinValue
                        : DateTime.MinValue)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projectGroupNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                SqliteRepository groupRepository = _repositoryFactory.Create(config);
                groupRepository.EnsureSchema();
                projectGroupNames = groupRepository.GetProjectGroups()
                    .ToDictionary(group => group.Id, group => group.Name, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Backup project folders unavailable: {ex.GetType().Name} - {ex.Message}");
            }

            foreach (Project? project in orderedProjects)
            {
                projectStats.TryGetValue(project.Id, out (int Count, long TotalBytes, DateTime? LastBackupTime) stats);

                string projectName = string.IsNullOrWhiteSpace(project.Name)
                    ? L(UnknownProjectGroupKey, UnknownProjectFallback)
                    : project.Name.Trim();

                var projectItem = new ProjectBackupItem
                {
                    Id                = project.Id.ToString(),
                    Name              = projectName,
                    FolderName        = !string.IsNullOrWhiteSpace(project.GroupId) &&
                                        projectGroupNames.TryGetValue(project.GroupId, out string? groupName)
                        ? groupName
                        : L("Projects.Folder.Ungrouped", "Ungrouped"),
                    ExternalId        = project.ExternalId ?? string.Empty,
                    ProjectTagsCsv    = project.Tags ?? string.Empty,
                    LastBackupTime    = stats.LastBackupTime,
                    SnapshotCount     = stats.Count,
                    TotalSizeBytes    = stats.TotalBytes,
                    AutoBackupEnabled = autoBackupDisabledProjects is null || !autoBackupDisabledProjects.Contains(project.Id),
                    AutoBackupChanged = OnAutoBackupChanged,
                    PreferredDestinationId = project.PreferredDestinationId ?? string.Empty,
                    PreferredDestinationChanged = OnPreferredDestinationChanged,
                    EncryptionPolicy = ProjectEncryptionPolicy.Normalize(project.EncryptionPolicy),
                    EncryptionKeyRef = project.EncryptionKeyRef ?? string.Empty,
                    EncryptionPolicyChanged = OnProjectEncryptionPolicyChanged,
                    RestoreMode = ProjectRestoreMode.Normalize(project.RestoreMode),
                    RestoreModeChanged = OnProjectRestoreModeChanged,
                    VerificationPolicy = ProjectVerificationPolicy.Normalize(project.VerificationPolicy),
                    VerificationPolicyChanged = OnProjectVerificationPolicyChanged,
                    StorageDeltaBytes = projectDeltaById.TryGetValue(project.Id, out long? deltaBytes)
                        ? deltaBytes
                        : null
                };
                projectItem.SetAvatarFromNameAndStore(projectName, project.RootPath, project.ExternalId);
                UpdateProjectDestinationDisplay(projectItem, config);
                UpdateProjectEncryptionDisplay(projectItem, config);
                UpdateProjectRestoreModeDisplay(projectItem);
                UpdateProjectVerificationPolicyDisplay(projectItem);
                ProjectBackups.Add(projectItem);
                _projectLookupById[projectItem.Id] = projectItem;
            }
            UpdateRestoreReadinessSummary(config, projectList, backupList);
            SortProjectBackups();

            // Map individual backups into the history list model
            var projectLookup = projectList.ToDictionary(p => p.Id);

            foreach (Backup? backup in backupList)
            {
                projectLookup.TryGetValue(backup.ProjectId, out Project? project);
                snapshotById.TryGetValue(backup.SnapshotId, out Snapshot? snapshotInfo);
                (string diffTopPathsDisplay, bool hasDiffTopPaths) = BuildSnapshotDiffTopPathsDisplay(snapshotInfo);

                string destinationDisplay = string.IsNullOrWhiteSpace(backup.DestinationAlias)
                    ? backup.DestinationPath
                    : backup.DestinationAlias;

                bool isAutoSnapshot = string.Equals(backup.Type, "auto", StringComparison.OrdinalIgnoreCase);
                string backupMode = BackupModes.Normalize(backup.BackupMode);
                bool isIncremental = string.Equals(backupMode, BackupModes.Incremental, StringComparison.OrdinalIgnoreCase);
                string importedLabel = L("Backups.Snapshot.Type.Imported", "Imported");
            if (backup.IsImported && !string.IsNullOrWhiteSpace(backup.OriginMachineName))
            {
                importedLabel = $"{importedLabel} \u00b7 {backup.OriginMachineName}";
            }
            var uiItem = new BackupSnapshotItem
            {
                Id        = backup.Id.ToString(),
                SnapshotId = backup.SnapshotId,
                Timestamp = backup.CreatedUtc.ToLocalTime(),
                SizeBytes = backup.TotalBytes,
                Type      = isAutoSnapshot ? "Auto" : ManualBackupType,
                IsImported = backup.IsImported,
                IsEncrypted = backup.IsEncrypted,
                OriginMachineName = backup.OriginMachineName,
                ImportedLabel = importedLabel,
                EncryptionLabel = backup.IsEncrypted
                    ? L(EncryptedPolicyKey, EncryptedFallback)
                    : L(PlainPolicyKey, PlainFallback),
                TypeLabel = isIncremental
                        ? L("Backups.Snapshot.Type.Incremental", "Incremental")
                        : L("Backups.Snapshot.Type.Full", "Full"),
                ModeChipLabel = Lf("Backups.Snapshot.ModeChip", "Mode: {0}",
                    isIncremental
                        ? L("Backups.Snapshot.Type.Incremental", "Incremental")
                        : L("Backups.Snapshot.Type.Full", "Full")),
                EncryptionChipLabel = Lf("Backups.Snapshot.EncryptionChip", "Encryption: {0}",
                    backup.IsEncrypted
                        ? L(EncryptedPolicyKey, EncryptedFallback)
                        : L(PlainPolicyKey, PlainFallback)),
                RetentionDefaultLabel = backup.IsImported
                    ? L("Backups.Retention.Outcome.Imported", "Retention: imported history entry")
                    : L("Backups.Retention.Outcome.Eligible", "Retention: eligible for pruning"),
                RetentionProtectedLabel = L("Backups.Retention.Outcome.Protected", "Retention: kept (protected)"),
                    Status    = "Completed",
                    Label     = isAutoSnapshot
                        ? L("Backups.Snapshot.Label.Auto", "Scheduled backup")
                        : L("Backups.Snapshot.Label.Manual", "On-demand backup"),
                    ProjectId = project?.Id.ToString(),
                    IsProtected = backup.IsProtected,
                    DestinationDisplay = destinationDisplay,
                    BackupRelativePath = backup.Path,
                    DestinationRootPath = backup.DestinationPath,
                    DestinationAlias = backup.DestinationAlias,
                    DiffAdded = snapshotInfo?.DiffAdded ?? 0,
                    DiffModified = snapshotInfo?.DiffModified ?? 0,
                    DiffDeleted = snapshotInfo?.DiffDeleted ?? 0,
                    DiffNetBytes = snapshotInfo?.DiffNetBytes ?? 0,
                    DiffTopPathsJson = snapshotInfo?.DiffTopPathsJson ?? "[]",
                    DiffSummaryDisplay = BuildSnapshotDiffSummaryDisplay(snapshotInfo),
                    DiffTopPathsDisplay = diffTopPathsDisplay,
                    HasDiffTopPaths = hasDiffTopPaths,
                    CanOpenDiffDetails = (snapshotInfo?.DiffAdded ?? 0) > 0
                        || (snapshotInfo?.DiffModified ?? 0) > 0
                        || (snapshotInfo?.DiffDeleted ?? 0) > 0
                        || (snapshotInfo?.DiffNetBytes ?? 0) != 0
                        || hasDiffTopPaths
                };

                _allSnapshots.Add(uiItem);
            }

            Interlocked.Increment(ref _snapshotRevision);

            // Rebuild the filtered history view + summary + mini-chart only when the view is active.
            if (IsActiveView)
            {
                RefreshActiveViewState();
            }

            _lastProjectSignature = projectSignature;
            _lastBackupSignature = backupSignature;
            _lastAutoBackupSignature = autoSignature;
        }

        private void RefreshDestinationQuotaPlans(AppConfig config, IEnumerable<Backup> backups)
        {
            _destinationQuotaPlansById.Clear();

            foreach (DestinationQuotaPlan plan in DestinationQuotaPlanner.BuildPlans(config.Backups.Destinations ?? [], backups))
            {
                _destinationQuotaPlansById[plan.DestinationId] = plan;
            }

            foreach (DestinationStatusItem item in DestinationStatuses)
            {
                ApplyDestinationQuotaPlan(item);
            }
        }

        private void ApplyDestinationQuotaPlan(DestinationStatusItem item)
        {
            if (!_destinationQuotaPlansById.TryGetValue(item.Id, out DestinationQuotaPlan? plan))
            {
                item.StoredBytesText = string.Empty;
                item.CleanupSuggestionText = string.Empty;
                return;
            }

            item.StoredBytesText = string.Format(
                CultureInfo.CurrentCulture,
                Lf("Backups.Destinations.StoredBytes", "Stored: {0}"),
                BackupSnapshotItem.FormatSize(plan.StoredBytes));

            if (!plan.SoftQuotaBytes.HasValue)
            {
                item.CleanupSuggestionText = string.Empty;
                return;
            }

            string usageLabel = string.Format(
                CultureInfo.CurrentCulture,
                Lf("Backups.Destinations.QuotaUsage", "Quota: {0} of {1} ({2}% warn)"),
                BackupSnapshotItem.FormatSize(plan.StoredBytes),
                BackupSnapshotItem.FormatSize(plan.SoftQuotaBytes.Value),
                plan.WarningPercent);

            if (!plan.ExceedsWarningThreshold)
            {
                item.CleanupSuggestionText = usageLabel;
                return;
            }

            if (plan.SuggestedCandidateCount <= 0)
            {
                item.CleanupSuggestionText = string.Format(
                    CultureInfo.CurrentCulture,
                    Lf(
                        "Backups.Destinations.CleanupBlocked",
                        "{0} No unprotected backups are currently available to get back under the warning threshold."),
                    usageLabel);
                return;
            }

            string cleanupLabel = string.Format(
                CultureInfo.CurrentCulture,
                Lf(
                    "Backups.Destinations.CleanupSuggestion",
                    "{0} Suggest deleting {1} unprotected backups to reclaim about {2}."),
                usageLabel,
                plan.SuggestedCandidateCount,
                BackupSnapshotItem.FormatSize(plan.SuggestedReclaimBytes));

            if (!plan.CanReachWarningThreshold)
            {
                cleanupLabel = string.Format(
                    CultureInfo.CurrentCulture,
                    Lf(
                        "Backups.Destinations.CleanupPartial",
                        "{0} Even deleting all eligible unprotected backups would still leave this destination above the warning threshold."),
                    cleanupLabel);
            }

            item.CleanupSuggestionText = cleanupLabel;
        }

        public void RefreshActiveViewState()
        {
            RefreshSnapshotsView(true);
            RecalculateSummary();
            RefreshBackupDiskUsage(includeHealthProbe: true);
        }

        public void RefreshBackupDriveHealth()
        {
            if (!IsActiveView)
                return;
            RefreshBackupDiskUsage(includeHealthProbe: true);
        }

        private static int ComputeProjectSignature(IReadOnlyList<Project> projects)
        {
            unchecked
            {
                int hash = projects.Count;
                foreach (Project project in projects)
                {
                    hash = (hash * 397) ^ project.Id;
                    hash = (hash * 397) ^ (project.Name?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.RootPath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.ExternalId?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.Tags?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.GroupId?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.PreferredDestinationId?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.EncryptionPolicy?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.EncryptionKeyRef?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.RestoreMode?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.VerificationPolicy?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                }
                return hash;
            }
        }

        private static int ComputeBackupSignature(IReadOnlyList<Backup> backups)
        {
            unchecked
            {
                int hash = backups.Count;
                foreach (Backup backup in backups)
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

        private Dictionary<int, Snapshot> LoadSnapshotLookup(AppConfig config, IReadOnlyList<Backup> backups)
        {
            var snapshotIds = backups
                .Select(backup => backup.SnapshotId)
                .Where(id => id > 0)
                .Distinct()
                .ToHashSet();

            if (snapshotIds.Count == 0)
                return [];

            try
            {
                var repo = _repositoryFactory.Create(config);
                return repo.GetSnapshotsByIds(snapshotIds)
                    .ToDictionary(snapshot => snapshot.Id);
            }
            catch
            {
                return [];
            }
        }

        private static string BuildSnapshotDiffSummaryDisplay(Snapshot? snapshot)
        {
            if (snapshot is null)
                return L("Backups.DiffSummary.Unavailable", "Diff summary unavailable");

            bool hasChanges = snapshot.DiffAdded > 0 || snapshot.DiffModified > 0 || snapshot.DiffDeleted > 0;
            if (!hasChanges && snapshot.DiffNetBytes == 0)
                return L("Backups.DiffSummary.NoChanges", "No file changes detected or diff data is unavailable for this backup");

            return Lf(
                "Backups.DiffSummary.Compact",
                "+{0} / ~{1} / -{2}  Δ {3}",
                snapshot.DiffAdded,
                snapshot.DiffModified,
                snapshot.DiffDeleted,
                FormatSignedSize(snapshot.DiffNetBytes));
        }

        private static (string Display, bool HasPaths) BuildSnapshotDiffTopPathsDisplay(Snapshot? snapshot)
        {
            if (snapshot is null)
                return (string.Empty, false);

            IReadOnlyList<SnapshotDiffPathStat> topPaths = SnapshotDiffSummary.ParseTopChangedPaths(snapshot.DiffTopPathsJson);
            if (topPaths.Count == 0)
                return (string.Empty, false);

            string preview = string.Join(
                ", ",
                topPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path.Path))
                    .Take(2)
                    .Select(path => $"{path.Path} ({path.Changes})"));

            if (string.IsNullOrWhiteSpace(preview))
                return (string.Empty, false);

            return (Lf("Backups.DiffSummary.TopPaths.Compact", "Top paths: {0}", preview), true);
        }

        private static string FormatSignedSize(long bytes)
            => UiFormat.FormatSignedBytes(bytes);

        private static int ComputeAutoBackupSignature(ISet<int>? disabledProjects)
        {
            if (disabledProjects is null || disabledProjects.Count == 0)
                return 0;

            unchecked
            {
                int hash = disabledProjects.Count;
                foreach (int id in disabledProjects.OrderBy(id => id))
                {
                    hash = (hash * 397) ^ id;
                }
                return hash;
            }
        }

        private void UpdateAutoBackupFlags(ISet<int>? disabledProjects)
        {
            ISet<int> disabled = disabledProjects ?? new HashSet<int>();
            foreach (ProjectBackupItem item in ProjectBackups)
            {
                int parsed = int.TryParse(item.Id, out int projectId) ? projectId : -1;
                item.AutoBackupEnabled = parsed > 0 && !disabled.Contains(parsed);
            }
        }

        public void RefreshAutoBackupFlagsFromConfig()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(RefreshAutoBackupFlagsFromConfig);
                return;
            }

            try
            {
                AppConfig cfg = _configStore.GetSnapshot();
                HashSet<int> disabled = cfg.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? [];
                UpdateAutoBackupFlags(disabled);
                _lastAutoBackupSignature = ComputeAutoBackupSignature(disabled);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Backups] Failed to refresh auto-backup flags: {ex.Message}");
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
                foreach (BackupDestination dest in config.Backups.Destinations)
                {
                    string label = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias;
                    if (!dest.Active)
                    {
                        string suffix = L("Projects.Destination.InactiveSuffix", " (inactive)");
                        label = $"{label}{suffix}";
                    }

                    string id = DestinationIdentityService.GetId(dest);
                    DestinationOptions.Add(new DestinationOption(id, label));
                }
            }
        }

        private void UpdateProjectEncryptionDisplay(ProjectBackupItem item, AppConfig config)
        {
            item.EncryptionPolicy = ProjectEncryptionPolicy.Normalize(item.EncryptionPolicy);
            EncryptionPolicyOption? optionMatch = EncryptionPolicyOptions.FirstOrDefault(o =>
                string.Equals(o.Id, item.EncryptionPolicy, StringComparison.OrdinalIgnoreCase));
            item.SetEncryptionPolicyOption(optionMatch ?? EncryptionPolicyOptions.FirstOrDefault());

            bool effectiveEncrypted = ProjectEncryptionPolicy.IsEncrypted(
                item.EncryptionPolicy,
                config.Backups.Encryption.Enabled);
            item.EffectiveEncryptionDisplay = effectiveEncrypted
                ? L("Projects.EncryptionPolicy.EffectiveEncrypted", "Effective: Encrypted")
                : L("Projects.EncryptionPolicy.EffectivePlain", "Effective: Plain");

            bool hasSecret = CredentialVault.Instance.HasStoredSecret(
                string.IsNullOrWhiteSpace(item.EncryptionKeyRef) ? null : item.EncryptionKeyRef);
            item.HasEncryptionSecret = hasSecret;
            item.EncryptionSecretStatus = hasSecret
                ? L("Settings.Encryption.SecretStatusAvailable", "Password is enrolled in secure storage.")
                : L("Settings.Encryption.SecretStatusMissing", "No encryption password enrolled yet.");
        }

        private void UpdateProjectRestoreModeDisplay(ProjectBackupItem item)
        {
            item.RestoreMode = ProjectRestoreMode.Normalize(item.RestoreMode);
            RestoreModeOption? optionMatch = RestoreModeOptions.FirstOrDefault(o =>
                string.Equals(o.Id, item.RestoreMode, StringComparison.OrdinalIgnoreCase));
            item.SetRestoreModeOption(optionMatch ?? RestoreModeOptions.FirstOrDefault());
        }

        private void UpdateProjectVerificationPolicyDisplay(ProjectBackupItem item)
        {
            item.VerificationPolicy = ProjectVerificationPolicy.Normalize(item.VerificationPolicy);
            VerificationPolicyOption? optionMatch = VerificationPolicyOptions.FirstOrDefault(o =>
                string.Equals(o.Id, item.VerificationPolicy, StringComparison.OrdinalIgnoreCase));
            item.SetVerificationPolicyOption(optionMatch ?? VerificationPolicyOptions.FirstOrDefault());
        }

        private void UpdateProjectDestinationDisplay(ProjectBackupItem item, AppConfig config)
        {
            string id = DestinationIdentityService.NormalizePreferredDestinationId(item.PreferredDestinationId, config.Backups.Destinations);
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

            BackupDestination? match = DestinationIdentityService.FindByPreferredDestinationId(config.Backups.Destinations, id);

            if (match != null)
            {
                item.PreferredDestinationDisplay = string.IsNullOrWhiteSpace(match.Alias) ? match.Path : match.Alias;
            }
            else
            {
                item.PreferredDestinationDisplay = id;
            }

            if (!string.Equals(item.PreferredDestinationId, id, StringComparison.OrdinalIgnoreCase))
            {
                item.PreferredDestinationId = id;
            }

            DestinationOption? optionMatch = DestinationOptions.FirstOrDefault(o =>
                string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            if (optionMatch is null)
            {
                DestinationOption? fallback = DestinationOptions.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Id))
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
            if (!int.TryParse(item.Id, out int projectId) || projectId <= 0)
                return;

            DetachedTask.Run(() =>
            {
                AppConfig config = _configStore.GetSnapshot();
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProjectDestinationDisplay(item, config);
                    PreferredDestinationChanged?.Invoke(projectId, item.PreferredDestinationId ?? string.Empty);
                });
            }, nameof(OnPreferredDestinationChanged));
        }

        private void OnProjectEncryptionPolicyChanged(ProjectBackupItem item)
        {
            if (!int.TryParse(item.Id, out int projectId) || projectId <= 0)
                return;

            DetachedTask.Run(() =>
            {
                AppConfig config = _configStore.GetSnapshot();
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProjectEncryptionDisplay(item, config);
                    ProjectEncryptionPolicyChanged?.Invoke(projectId, item.EncryptionPolicy);
                });
            }, nameof(OnProjectEncryptionPolicyChanged));
        }

        private void OnProjectRestoreModeChanged(ProjectBackupItem item)
        {
            if (!int.TryParse(item.Id, out int projectId) || projectId <= 0)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                UpdateProjectRestoreModeDisplay(item);
                ProjectRestoreModeChanged?.Invoke(projectId, item.RestoreMode);
            });
        }

        private void OnProjectVerificationPolicyChanged(ProjectBackupItem item)
        {
            if (!int.TryParse(item.Id, out int projectId) || projectId <= 0)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                UpdateProjectVerificationPolicyDisplay(item);
                ProjectVerificationPolicyChanged?.Invoke(projectId, item.VerificationPolicy);
            });
        }

        public void RefreshDestinationOptions(AppConfig config)
        {
            RefreshDestinationOptionsInternal(config);
            foreach (ProjectBackupItem project in ProjectBackups)
            {
                UpdateProjectDestinationDisplay(project, config);
                UpdateProjectEncryptionDisplay(project, config);
                UpdateProjectRestoreModeDisplay(project);
                UpdateProjectVerificationPolicyDisplay(project);
            }
        }

        private sealed record SnapshotSummaryExportPayload(
            string BackupId,
            int ProjectId,
            string ProjectName,
            string TimestampLocal,
            string TimestampUtc,
            string Destination,
            string TriggerType,
            string ModeLabel,
            bool IsImported,
            bool IsEncrypted,
            int DiffAdded,
            int DiffModified,
            int DiffDeleted,
            long DiffNetBytes,
            IReadOnlyList<SnapshotDiffPathExport> TopPaths);

        private sealed record SnapshotDiffPathExport(
            string Path,
            int Changes,
            long ChangedBytes);
    }
}
