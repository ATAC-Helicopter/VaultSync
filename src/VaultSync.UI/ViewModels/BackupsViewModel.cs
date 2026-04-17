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

        private const string BackupEncryptionSecretUsername = "vaultsync-backup-encryption";
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
        private static readonly IBrush FreshnessGoodBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush FreshnessModerateBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush FreshnessStaleBrush = new ImmutableSolidColorBrush(Color.Parse("#F56A5A"));
        private static readonly IBrush FreshnessUnknownBrush = new ImmutableSolidColorBrush(Color.Parse("#7F8FA8"));
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

        public enum DestinationStatus
        {
            None,
            Pending,
            Inactive,
            Reachable,
            ReadOnly,
            Unavailable
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
            bool AllowCancel,
            string? DestinationLabel,
            string PolicyText);

        private BackupSnapshotItem? _selectedSnapshotA;
        private RelayCommand? _compareSelectedSnapshotsRelayCommand;
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
        public ObservableCollection<BackupsProjectSortOption> ProjectSortOptions { get; } =
            new ObservableCollection<BackupsProjectSortOption>();
        public ObservableCollection<DestinationOption> DestinationOptions { get; } =
            new ObservableCollection<DestinationOption>();
        public ObservableCollection<EncryptionPolicyOption> EncryptionPolicyOptions { get; } =
            new ObservableCollection<EncryptionPolicyOption>();
        public ObservableCollection<RestoreModeOption> RestoreModeOptions { get; } =
            new ObservableCollection<RestoreModeOption>();
        public ObservableCollection<VerificationPolicyOption> VerificationPolicyOptions { get; } =
            new ObservableCollection<VerificationPolicyOption>();

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

                var nextMode = value?.Id ?? "latest";
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
            L("Backups.Summary.NoBackups", "No backups yet");
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
            new ObservableCollection<StorageConsumerItem>();
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
        public ObservableCollection<RestoreReadinessIssueItem> RestoreReadinessIssues { get; } = new();
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

        public ObservableCollection<DiffPreviewPathItem> DiffPreviewTopPaths { get; } = new();
        public bool HasDiffPreviewTopPaths => DiffPreviewTopPaths.Count > 0;

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
        public ICommand ExportSnapshotSummaryTextCommand { get; }
        public ICommand ExportSnapshotSummaryJsonCommand { get; }
        public ICommand ShowSnapshotDiffPreviewCommand { get; }
        public ICommand CompareSelectedSnapshotsCommand { get; }
        public ICommand CloseSnapshotDiffPreviewCommand { get; }

        public ICommand BackupProjectCommand { get; }
        public ICommand ManageProjectEncryptionCommand { get; }
        public ICommand ShowProjectHistoryCommand { get; }
        public ICommand FilterSnapshotsCommand { get; }
        public ICommand CloseVerificationPopupCommand { get; }
        public ICommand DeleteFailedBackupCommand { get; }
        public ICommand OpenSettingsCommand { get; }

        public bool IsTypeFilterAll => string.Equals(_currentTypeFilter, "All", StringComparison.OrdinalIgnoreCase);
        public bool IsTypeFilterAuto => string.Equals(_currentTypeFilter, "Auto", StringComparison.OrdinalIgnoreCase);
        public bool IsTypeFilterManual => string.Equals(_currentTypeFilter, "Manual", StringComparison.OrdinalIgnoreCase);
        public bool CanCompareSelectedSnapshots =>
            SelectedSnapshotA is not null &&
            SelectedSnapshotB is not null &&
            !string.Equals(SelectedSnapshotA.Id, SelectedSnapshotB.Id, StringComparison.Ordinal);

        public BackupsViewModel()
        {
            _activeBackupFlushTimer.Tick += (_, _) => FlushPendingActiveBackupUpdates();

            // All-project backup
            CreateBackupCommand = new RelayCommand(_ => CreateBackupForAllProjects());

            // Global history actions
            RestoreBackupCommand = new RelayCommand(p => RestoreBackup(p as BackupSnapshotItem));
            DeleteBackupCommand  = new RelayCommand(p => DeleteBackup(p as BackupSnapshotItem));
            OpenBackupFolderCommand = new RelayCommand(p => OpenBackupFolder(p as BackupSnapshotItem));
            ToggleBackupProtectionCommand = new RelayCommand(p => ToggleBackupProtection(p as BackupSnapshotItem));
            ExportSnapshotSummaryTextCommand = new RelayCommand(p => ExportSnapshotSummary(p as BackupSnapshotItem, SnapshotSummaryExportFormat.Text));
            ExportSnapshotSummaryJsonCommand = new RelayCommand(p => ExportSnapshotSummary(p as BackupSnapshotItem, SnapshotSummaryExportFormat.Json));
            ShowSnapshotDiffPreviewCommand = new RelayCommand(p => ShowSnapshotDiffPreview(p as BackupSnapshotItem));
            _compareSelectedSnapshotsRelayCommand = new RelayCommand(_ => CompareSelectedSnapshots(), _ => CanCompareSelectedSnapshots);
            CompareSelectedSnapshotsCommand = _compareSelectedSnapshotsRelayCommand;
            CloseSnapshotDiffPreviewCommand = new RelayCommand(_ => CloseSnapshotDiffPreview());
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
            ActiveBackups.CollectionChanged += (_, _) => UpdateActiveBackupTimer();
            _activeBackupTimer.Tick += (_, _) => TickActiveBackupDurations();

            // NOTE:
            // Live data is now provided by LoadFromBackups(...) from the core layer.
            // We no longer seed design-time demo data here.

            InitializeLocalizationDefaults();
            RefreshEncryptionPolicyOptions();
            RefreshRestoreModeOptions();
            RefreshVerificationPolicyOptions();
            RefreshDestinationOptionsInternal(AppConfigStore.GetSnapshot());
            RefreshProjectSortOptions();
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
                var path = await Task.Run(() => WriteSnapshotSummaryExport(snapshot, format));
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
            var exportDir = GetSnapshotSummaryExportDirectory();
            Directory.CreateDirectory(exportDir);

            var baseName = BuildSnapshotSummaryFileName(snapshot);
            var extension = format == SnapshotSummaryExportFormat.Json ? ".json" : ".txt";
            var path = EnsureUniqueExportPath(Path.Combine(exportDir, $"{baseName}{extension}"));
            var payload = BuildSnapshotSummaryExportPayload(snapshot);

            if (format == SnapshotSummaryExportFormat.Json)
            {
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return path;
            }

            var text = BuildGitStyleDiffText(payload);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        private void ShowSnapshotDiffPreview(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            var payload = BuildSnapshotSummaryExportPayload(snapshot);
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
            foreach (var path in payload.TopPaths.Take(8))
            {
                DiffPreviewTopPaths.Add(new DiffPreviewPathItem(
                    path.Path,
                    path.Changes,
                    BackupSnapshotItem.FormatSize(path.ChangedBytes)));
            }
            OnPropertyChanged(nameof(HasDiffPreviewTopPaths));
            DiffPreviewText = BuildGitStyleDiffText(payload);
            IsDiffPreviewOpen = true;
        }

        private void CompareSelectedSnapshots()
        {
            var pointA = SelectedSnapshotA;
            var pointB = SelectedSnapshotB;
            if (pointA is null || pointB is null)
                return;
            if (string.Equals(pointA.Id, pointB.Id, StringComparison.Ordinal))
                return;

            var newer = pointA.Timestamp >= pointB.Timestamp ? pointA : pointB;
            var older = ReferenceEquals(newer, pointA) ? pointB : pointA;
            var elapsed = newer.Timestamp - older.Timestamp;
            var sizeDelta = newer.SizeBytes - older.SizeBytes;
            var netDelta = newer.DiffNetBytes - older.DiffNetBytes;
            var projectName = ResolveCompareProjectName(newer, older);

            DiffPreviewTitle = Lf(
                "Backups.Compare.Title",
                "Restore point compare - {0}",
                projectName);
            DiffPreviewMetaLine = Lf(
                "Backups.Compare.Range",
                "{0} -> {1}",
                older.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                newer.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
            DiffPreviewTrigger = Lf(
                "Backups.Compare.TypeLine",
                "Type: {0} -> {1}",
                older.TypeLabel,
                newer.TypeLabel);
            DiffPreviewMode = Lf(
                "Backups.Compare.ElapsedLine",
                "Elapsed: {0}",
                FormatElapsed(elapsed));
            DiffPreviewImportedDisplay = older.IsImported || newer.IsImported
                ? L("Backups.Snapshot.Type.Imported", "Imported")
                : L("Backups.Summary.LocalLabel", "Local");
            DiffPreviewEncryptionDisplay = older.IsEncrypted || newer.IsEncrypted
                ? L("Projects.EncryptionPolicy.Encrypted", "Encrypted")
                : L("Projects.EncryptionPolicy.Plain", "Plain");
            DiffPreviewAdded = newer.DiffAdded;
            DiffPreviewModified = newer.DiffModified;
            DiffPreviewDeleted = newer.DiffDeleted;
            DiffPreviewNet = FormatSignedSize(newer.DiffNetBytes);
            DiffPreviewTopPaths.Clear();
            OnPropertyChanged(nameof(HasDiffPreviewTopPaths));

            var compareText = new StringBuilder();
            compareText.AppendLine("# VaultSync Restore Point Compare");
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
            compareText.AppendLine(Lf("Backups.Compare.SizeDeltaLine", "Backup size delta: {0}", FormatSignedSize(sizeDelta)));
            compareText.AppendLine(Lf("Backups.Compare.NetDeltaLine", "Net diff delta: {0}", FormatSignedSize(netDelta)));
            compareText.AppendLine();
            compareText.AppendLine(L("Backups.Compare.NewerSnapshotSummary", "Newest restore point diff summary:"));
            compareText.AppendLine(Lf("Backups.Compare.NewerAdded", "+ added {0}", newer.DiffAdded.ToString(CultureInfo.CurrentCulture)));
            compareText.AppendLine(Lf("Backups.Compare.NewerModified", "~ modified {0}", newer.DiffModified.ToString(CultureInfo.CurrentCulture)));
            compareText.AppendLine(Lf("Backups.Compare.NewerDeleted", "- deleted {0}", newer.DiffDeleted.ToString(CultureInfo.CurrentCulture)));
            compareText.AppendLine(Lf("Backups.Compare.NewerNet", "Δ net {0}", FormatSignedSize(newer.DiffNetBytes)));

            DiffPreviewText = compareText.ToString().TrimEnd();
            IsDiffPreviewOpen = true;
        }

        private string ResolveCompareProjectName(BackupSnapshotItem a, BackupSnapshotItem b)
        {
            var projectA = ResolveProjectNameFromSnapshot(a);
            var projectB = ResolveProjectNameFromSnapshot(b);
            if (string.Equals(projectA, projectB, StringComparison.OrdinalIgnoreCase))
                return projectA;

            return L("Backups.Section.HistoryFilterAllProjects", "All projects");
        }

        private string ResolveProjectNameFromSnapshot(BackupSnapshotItem snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.ProjectId))
            {
                var match = ProjectBackups.FirstOrDefault(project =>
                    string.Equals(project.Id, snapshot.ProjectId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match.Name;
            }

            return L("Backups.Section.Group.Unknown", "Unknown project");
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
            OnPropertyChanged(nameof(HasDiffPreviewTopPaths));
        }

        private SnapshotSummaryExportPayload BuildSnapshotSummaryExportPayload(BackupSnapshotItem snapshot)
        {
            var projectId = int.TryParse(snapshot.ProjectId, out var pid) ? pid : 0;
            var projectName = ProjectBackups
                .FirstOrDefault(project => string.Equals(project.Id, snapshot.ProjectId, StringComparison.OrdinalIgnoreCase))
                ?.Name ?? L("Backups.Section.Group.Unknown", "Unknown project");

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
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Path.GetTempPath();

            return Path.Combine(documents, "VaultSync", "Exports", "SnapshotDiff");
        }

        private static string BuildSnapshotSummaryFileName(BackupSnapshotItem snapshot)
        {
            var projectToken = string.IsNullOrWhiteSpace(snapshot.ProjectId) ? "global" : snapshot.ProjectId;
            var ts = snapshot.Timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return $"snapshot-diff-{projectToken}-backup-{snapshot.Id}-{ts}";
        }

        private static string EnsureUniqueExportPath(string path)
        {
            if (!File.Exists(path))
                return path;

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);

            for (var i = 1; i <= 999; i++)
            {
                var candidate = Path.Combine(directory, $"{fileName}-{i}{extension}");
                if (!File.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(directory, $"{fileName}-{Guid.NewGuid():N}{extension}");
        }

        private string BuildGitStyleDiffText(SnapshotSummaryExportPayload payload)
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
                foreach (var path in payload.TopPaths.Take(10))
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

        private void ManageProjectEncryption(ProjectBackupItem? project)
        {
            if (project is null)
                return;

            if (!int.TryParse(project.Id, out var projectId) || projectId <= 0)
                return;

            ManageProjectEncryptionRequested?.Invoke(projectId);
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
            BackupHealthSummaryLine = L("Backups.Health.Center.Empty", "No project health data yet.");

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
            OnPropertyChanged(nameof(BackupHealthSummaryLine));
            OnPropertyChanged(nameof(BackupDiskDriveLabel));
            OnPropertyChanged(nameof(BackupDiskHealthText));
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

            foreach (var des in DestinationStatuses)
            {
                des.RefreshLocalization();
            }
            OnPropertyChanged(nameof(string.Empty));
            lock (_healthProbeGate)
            {
                _lastHealthProbeUtc = DateTime.MinValue;
            }
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

            var selectedId = SelectedProject?.Id;
            var orderedList = ordered.ToList();
            ProjectBackups.Clear();
            foreach (var item in orderedList)
                ProjectBackups.Add(item);

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
                L("Projects.EncryptionPolicy.Encrypted", "Encrypted")));
            EncryptionPolicyOptions.Add(new EncryptionPolicyOption(
                ProjectEncryptionPolicy.Plain,
                L("Projects.EncryptionPolicy.Plain", "Plain")));
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
        public void UpdateActiveBackup(
            string projectId,
            string projectName,
            double progress,
            string currentFile,
            string etaText,
            bool allowCancel = true,
            string? destinationLabel = null,
            string? policyText = null)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            var update = new PendingBackupUpdate(
                projectId,
                projectName,
                progress,
                currentFile,
                etaText,
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

            foreach (var update in _pendingActiveBackupUpdates.Values)
            {
                ApplyActiveBackupUpdate(update);
            }

            _pendingActiveBackupUpdates.Clear();
        }

        private void ApplyActiveBackupUpdate(PendingBackupUpdate update)
        {
            var item = ActiveBackups.FirstOrDefault(p => p.ProjectId == update.ProjectId);
            if (item == null)
            {
                item = new BackupProgressItem
                {
                    ProjectId        = update.ProjectId,
                    ProjectName      = string.IsNullOrWhiteSpace(update.ProjectName)
                        ? L("Dashboard.Activity.UnknownProject", "Unknown project")
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
                var status = dest.Active ? DestinationStatus.Pending : DestinationStatus.Inactive;
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
                ApplyDestinationQuotaPlan(item);
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
                    DestinationStatus newStatus = item.IsActive ? DestinationStatus.Pending : DestinationStatus.Inactive;
                    if (item.Status != newStatus)
                    {
                        item.Status = newStatus;
                        item.Severity = "Info";
                        item.DotBrush = GetDestinationDotBrush(newStatus, "Info");
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

            string rawText = status;
            if (rawText == null)
            {
                rawText = string.Empty;
            }

            DestinationStatus newStatus = DestinationStatus.None;

            if(rawText.Contains("Using pre-mounted", StringComparison.OrdinalIgnoreCase) ||
               rawText.Contains("Reachable", StringComparison.OrdinalIgnoreCase) ||
               rawText.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
               rawText.Contains("No changes", StringComparison.OrdinalIgnoreCase)||
               rawText.Contains("No backup", StringComparison.OrdinalIgnoreCase))
            {
                newStatus = DestinationStatus.Reachable;
            }
            else if (rawText.Contains("Read-only", StringComparison.OrdinalIgnoreCase) ||
                     rawText.Contains("Read only", StringComparison.OrdinalIgnoreCase))
            {
                newStatus = DestinationStatus.ReadOnly;
            }
            else if (rawText.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
                     rawText.Contains("Unreachable", StringComparison.OrdinalIgnoreCase))
            {
                newStatus = DestinationStatus.Unavailable;
            }
            else
            {
                if (string.Equals(severity, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    newStatus = DestinationStatus.Reachable;
                }else if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase))
                {
                    newStatus = DestinationStatus.ReadOnly;
                }
                else if (string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    newStatus = DestinationStatus.Unavailable;
                }
            }

            var severityToUse = severity;
            if (string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase))
            {
                if (newStatus == DestinationStatus.Reachable)
                {
                    severityToUse = "Success";
                }
                else if (newStatus == DestinationStatus.Unavailable)
                {
                    severityToUse = "Error";
                }
                else if (newStatus == DestinationStatus.ReadOnly)
                {
                    severityToUse = "Warning";
                }
                else if (newStatus == DestinationStatus.None)
                {
                    severityToUse = item.Severity;
                }
            }

            item.Status   = newStatus;
            item.Severity = severityToUse;
            item.DotBrush = GetDestinationDotBrush(newStatus, severityToUse);
            item.LastCheckedUtc = DateTime.UtcNow;
        }

        public void MarkDestinationComplete(string id, bool success, string status)
        {
            UpdateDestinationStatus(id, status, success ? "Success" : "Error");
        }

        private static IBrush GetDestinationDotBrush(DestinationStatus status, string severity)
        {
            return (status, severity) switch
            {
                (DestinationStatus.Inactive, _) => AccentBrush("#808080"),
                (DestinationStatus.Reachable, _) => AccentBrush("#22CC88"),

                (_, "Success") => AccentBrush("#22CC88"),
                (_, "Warning") => AccentBrush("#FFB84C"),
                (_, "Error")   => AccentBrush("#FF6B6B"),
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
                    HistoryFilterProjectLabel = L("Backups.Section.HistoryFilterAllProjects", "All projects");
                    OnPropertyChanged(nameof(HistoryFilterProjectLabel));
                }
            }
            else
            {
                // "Auto" or "Manual" while keeping the current project filter (if any).
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

            var grouped = filtered
                .GroupBy(s => s.ProjectId ?? string.Empty)
                .OrderByDescending(g => g.Max(s => s.Timestamp))
                .ThenBy(g =>
                {
                    if (!string.IsNullOrWhiteSpace(g.Key) && _projectLookupById.TryGetValue(g.Key, out var nameSource))
                        return nameSource.Name;
                    return "zzzz_" + g.Key;
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
                else if (!_projectLookupById.TryGetValue(key, out var nameSource))
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
                if (!string.IsNullOrWhiteSpace(key) && _projectLookupById.TryGetValue(key, out var colorSource))
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
                    ProjectTagsDisplay = !string.IsNullOrWhiteSpace(key) && _projectLookupById.TryGetValue(key, out var tagSource)
                        ? tagSource.ProjectTagsDisplay
                        : string.Empty,
                    Summary            = Lf(summaryKey, summaryFallback, ordered.Count),
                    TotalSizeFormatted = BackupSnapshotItem.FormatSize(totalBytes),
                    LatestBackupDisplay = latest == DateTime.MinValue ? "-" : latest.ToString("yyyy-MM-dd HH:mm"),
                    AccentBrush        = accentBrush,
                    IsExpanded         = isExpanded
                };

                if (!string.IsNullOrWhiteSpace(key) && _projectLookupById.TryGetValue(key, out var chipSource))
                {
                    foreach (var chip in chipSource.ProjectTagChips)
                        groupVm.ProjectTagChips.Add(chip);
                }

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
                    var config = AppConfigStore.GetSnapshot();
                    var (usedPercent, freeText, thresholdText, isBelowThreshold, _, status) =
                        DashboardViewModel.ComputeBackupDiskUsageDetailed(config);
                    var driveLabel = Lf("Backups.Health.DriveLabel", "Drive: {0}", FormatDriveLabel(config.Backups.BackupRoot));

                    string? healthText = null;
                    IBrush? healthBrush = null;

                    var shouldProbeHealth = includeHealthProbe;
                    if (includeHealthProbe)
                    {
                        lock (_healthProbeGate)
                        {
                            var now = DateTime.UtcNow;
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

        private static bool IsNetworkHealthPath(DriveHealthResult health)
        {
            var id = health.DriveId ?? string.Empty;
            if (id.StartsWith("//", StringComparison.OrdinalIgnoreCase))
                return true;
            if (id.Contains("://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!id.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase) && id.Contains(':'))
                return true;

            var path = health.Path ?? string.Empty;
            return path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("//", StringComparison.OrdinalIgnoreCase);
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
                var lastProjectName = ResolveProjectNameFromSnapshot(last);
                LastBackupProjectName = string.IsNullOrWhiteSpace(lastProjectName)
                    ? L("Backups.Summary.UnknownProject", "Unknown project")
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
                var ageHours = Math.Max(0, (now - last.Timestamp).TotalHours);
                var freshness = Math.Clamp(100d - (ageHours / 72d * 100d), 0d, 100d);
                LastBackupFreshnessPercent = freshness;
                var freshnessStateLabel = freshness >= 80
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
                LastBackupDisplay  = L("Backups.Summary.NoBackups", "No backups yet");
                LastBackupRelative = "-";
                LastBackupSecondaryLine = L("Backups.Summary.LastBackupSize", "Size -");
                LastBackupSizeValueFormatted = "0 B";
                LastBackupProjectName = "-";
                LastBackupTypeDisplay = "-";
                LastBackupDestinationDisplay = "-";
                LastBackupSecurityDisplay = "-";
                LastBackupFreshnessPercent = 0;
                LastBackupFreshnessLabel = L("Backups.Summary.NoBackups", "No backups yet");
                LastBackupFreshnessTooltip = L("Backups.Summary.NoBackups", "No backups yet");
                LastBackupFreshnessBrush = FreshnessUnknownBrush;
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

            var safeLocal = Math.Max(0, localBytes);
            var safeImported = Math.Max(0, importedBytes);
            if (safeLocal + safeImported == 0)
            {
                StorageLocalPercent = 0;
                StorageImportedPercent = 0;
            }
            else
            {
                var totalStorage = safeLocal + safeImported;
                StorageLocalPercent = safeLocal * 100d / totalStorage;
                StorageImportedPercent = safeImported * 100d / totalStorage;
            }

            RebuildTopStorageConsumers();
            RebuildBackupHealthCenter(now);
            RebuildSnapshotActivity(now);

            // Notify UI that summary properties changed
            OnPropertyChanged(nameof(TotalSnapshots));
            OnPropertyChanged(nameof(SnapshotsThisWeek));
            OnPropertyChanged(nameof(SnapshotsToday));
            OnPropertyChanged(nameof(SnapshotsYesterday));
            OnPropertyChanged(nameof(AutoSnapshotsThisWeek));
            OnPropertyChanged(nameof(ManualSnapshotsThisWeek));
            OnPropertyChanged(nameof(ImportedSnapshotsThisWeek));
            OnPropertyChanged(nameof(SnapshotsSummaryLine));
            OnPropertyChanged(nameof(TotalSnapshotsSecondaryLine));
            OnPropertyChanged(nameof(SnapshotActivitySummary));
            OnPropertyChanged(nameof(LastBackupDisplay));
            OnPropertyChanged(nameof(LastBackupRelative));
            OnPropertyChanged(nameof(LastBackupSecondaryLine));
            OnPropertyChanged(nameof(LastBackupSizeValueFormatted));
            OnPropertyChanged(nameof(LastBackupProjectName));
            OnPropertyChanged(nameof(LastBackupTypeDisplay));
            OnPropertyChanged(nameof(LastBackupDestinationDisplay));
            OnPropertyChanged(nameof(LastBackupSecurityDisplay));
            OnPropertyChanged(nameof(LastBackupFreshnessPercent));
            OnPropertyChanged(nameof(LastBackupFreshnessLabel));
            OnPropertyChanged(nameof(LastBackupFreshnessTooltip));
            OnPropertyChanged(nameof(LastBackupFreshnessBrush));
            OnPropertyChanged(nameof(TotalBackupSizeFormatted));
            OnPropertyChanged(nameof(LocalSnapshotsCount));
            OnPropertyChanged(nameof(TotalStoredLocalLine));
            OnPropertyChanged(nameof(TotalStoredLocalValueFormatted));
            OnPropertyChanged(nameof(TotalStoredImportedLine));
            OnPropertyChanged(nameof(TotalStoredImportedValueFormatted));
            OnPropertyChanged(nameof(ImportedSnapshotsCount));
            OnPropertyChanged(nameof(ThisWeekAutoPercent));
            OnPropertyChanged(nameof(ThisWeekManualPercent));
            OnPropertyChanged(nameof(ThisWeekImportedPercent));
            OnPropertyChanged(nameof(StorageLocalPercent));
            OnPropertyChanged(nameof(StorageImportedPercent));
            OnPropertyChanged(nameof(HasTopStorageConsumers));
            OnPropertyChanged(nameof(HealthHealthyProjects));
            OnPropertyChanged(nameof(HealthAgingProjects));
            OnPropertyChanged(nameof(HealthStaleProjects));
            OnPropertyChanged(nameof(HealthNoBackupProjects));
            OnPropertyChanged(nameof(HealthHealthyPercent));
            OnPropertyChanged(nameof(HealthAgingPercent));
            OnPropertyChanged(nameof(HealthStalePercent));
            OnPropertyChanged(nameof(HealthNoBackupPercent));
            OnPropertyChanged(nameof(BackupHealthSummaryLine));
            OnPropertyChanged(nameof(RestoreReadinessReadyProjects));
            OnPropertyChanged(nameof(RestoreReadinessAttentionProjects));
            OnPropertyChanged(nameof(RestoreReadinessRiskProjects));
            OnPropertyChanged(nameof(RestoreReadinessUnavailableProjects));
            OnPropertyChanged(nameof(RestoreReadinessReadyPercent));
            OnPropertyChanged(nameof(RestoreReadinessAttentionPercent));
            OnPropertyChanged(nameof(RestoreReadinessRiskPercent));
            OnPropertyChanged(nameof(RestoreReadinessUnavailablePercent));
            OnPropertyChanged(nameof(RestoreReadinessHeadline));
            OnPropertyChanged(nameof(RestoreReadinessDetail));
        }

        public void UpdateSummaryLayout(double width)
        {
            const double chartThreshold = 1180;
            const double activityThreshold = 1460;

            var showCharts = width >= chartThreshold;
            var showActivity = width >= activityThreshold;

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

            var totalBytes = totalsByProject.Sum(entry => entry.TotalBytes);
            foreach (var entry in totalsByProject)
            {
                var projectName = projectNameById.TryGetValue(entry.ProjectId, out var foundName)
                    ? foundName
                    : L("Backups.Section.Group.Unknown", "Unknown project");

                var sharePercent = totalBytes <= 0
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
            var healthy = 0;
            var aging = 0;
            var stale = 0;
            var noBackup = 0;

            foreach (var project in ProjectBackups)
            {
                if (!project.LastBackupTime.HasValue)
                {
                    noBackup++;
                    continue;
                }

                var age = now - project.LastBackupTime.Value;
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

            var total = Math.Max(0, healthy + aging + stale + noBackup);
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
            var summary = service.BuildSummary(projectList, backupList, config, config.Advanced.BackupIndexLastScan);

            RestoreReadinessReadyProjects = summary.ReadyCount;
            RestoreReadinessAttentionProjects = summary.AttentionCount;
            RestoreReadinessRiskProjects = summary.RiskCount;
            RestoreReadinessUnavailableProjects = summary.UnavailableCount;
            RestoreReadinessHeadline = FormatRestoreReadinessHeadline(summary);
            RestoreReadinessDetail = FormatRestoreReadinessDetail(summary);

            foreach (var result in summary.Projects)
            {
                if (!_projectLookupById.TryGetValue(result.ProjectId.ToString(CultureInfo.InvariantCulture), out var item))
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

            var total = summary.ProjectCount;
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
            foreach (var item in summary.Projects
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

        // ---------- Weekly activity mini-chart ----------

        private void RebuildSnapshotActivity(DateTime now)
        {
            SnapshotActivity.Clear();

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

            var chartHeight = maxTotal <= 2 ? 150d : (maxTotal <= 4 ? 172d : 192d);
            // Keep the activity chart proportionate on wide windowed layouts.
            var widthBoost = ShowActivityPanel
                ? Math.Clamp((_lastSummaryViewportWidth - 1380d) * 0.05d, 0d, 52d)
                : 0d;
            chartHeight += widthBoost;
            const double barBase = 12;
            var barRange = chartHeight - 36;
            SnapshotActivityChartHeight = chartHeight;

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
                    autoHeight = autoCount == 0 ? 0 : Math.Max(5, totalHeight * autoCount / totalCount);
                    manualHeight = manualCount == 0 ? 0 : Math.Max(5, totalHeight * manualCount / totalCount);
                    importedHeight = importedCount == 0 ? 0 : Math.Max(5, totalHeight * importedCount / totalCount);

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

            var config = AppConfigStore.GetSnapshot();
            ShowProjectAvatars = config.Appearance.ShowProjectAvatars;
            OnPropertyChanged(nameof(ShowProjectAvatars));
            RefreshEncryptionPolicyOptions();
            RefreshRestoreModeOptions();
            RefreshVerificationPolicyOptions();
            RefreshDestinationOptions(config);

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
            RefreshDestinationQuotaPlans(config, backupList);
            var snapshotById = LoadSnapshotLookup(config, backupList);
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
            _projectLookupById.Clear();
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

            // Compute per-project delta vs previous backup (latest - previous).
            var projectDeltaById = new Dictionary<int, long?>();
            foreach (var group in backupList.GroupBy(b => b.ProjectId))
            {
                var ordered = group
                    .OrderByDescending(b => b.CreatedUtc)
                    .ThenByDescending(b => b.Id)
                    .Take(2)
                    .ToList();

                if (ordered.Count >= 2)
                {
                    projectDeltaById[group.Key] = ordered[0].TotalBytes - ordered[1].TotalBytes;
                }
                else
                {
                    projectDeltaById[group.Key] = null;
                }
            }

            var orderedProjects = projectList
                .OrderByDescending(project =>
                    projectStats.TryGetValue(project.Id, out var stats)
                        ? stats.LastBackupTime ?? DateTime.MinValue
                        : DateTime.MinValue)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var project in orderedProjects)
            {
                projectStats.TryGetValue(project.Id, out var stats);

                var projectName = string.IsNullOrWhiteSpace(project.Name)
                    ? L("Backups.Section.Group.Unknown", "Unknown project")
                    : project.Name.Trim();

                var projectItem = new ProjectBackupItem
                {
                    Id                = project.Id.ToString(),
                    Name              = projectName,
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
                    StorageDeltaBytes = projectDeltaById.TryGetValue(project.Id, out var deltaBytes)
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

            foreach (var backup in backupList)
            {
                projectLookup.TryGetValue(backup.ProjectId, out var project);
                snapshotById.TryGetValue(backup.SnapshotId, out var snapshotInfo);
                var (diffTopPathsDisplay, hasDiffTopPaths) = BuildSnapshotDiffTopPathsDisplay(snapshotInfo);

                var destinationDisplay = string.IsNullOrWhiteSpace(backup.DestinationAlias)
                    ? backup.DestinationPath
                    : backup.DestinationAlias;

            var isAutoSnapshot = string.Equals(backup.Type, "auto", StringComparison.OrdinalIgnoreCase);
            var backupMode = BackupModes.Normalize(backup.BackupMode);
            var isIncremental = string.Equals(backupMode, BackupModes.Incremental, StringComparison.OrdinalIgnoreCase);
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
                IsEncrypted = backup.IsEncrypted,
                OriginMachineName = backup.OriginMachineName,
                ImportedLabel = importedLabel,
                EncryptionLabel = backup.IsEncrypted
                    ? L("Projects.EncryptionPolicy.Encrypted", "Encrypted")
                    : L("Projects.EncryptionPolicy.Plain", "Plain"),
                TypeLabel = isIncremental
                        ? L("Backups.Snapshot.Type.Incremental", "Incremental")
                        : L("Backups.Snapshot.Type.Full", "Full"),
                ModeChipLabel = Lf("Backups.Snapshot.ModeChip", "Mode: {0}",
                    isIncremental
                        ? L("Backups.Snapshot.Type.Incremental", "Incremental")
                        : L("Backups.Snapshot.Type.Full", "Full")),
                EncryptionChipLabel = Lf("Backups.Snapshot.EncryptionChip", "Encryption: {0}",
                    backup.IsEncrypted
                        ? L("Projects.EncryptionPolicy.Encrypted", "Encrypted")
                        : L("Projects.EncryptionPolicy.Plain", "Plain")),
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

            var planner = new DestinationQuotaPlanner();
            foreach (var plan in planner.BuildPlans(config.Backups.Destinations ?? new List<BackupDestination>(), backups))
            {
                _destinationQuotaPlansById[plan.DestinationId] = plan;
            }

            foreach (var item in DestinationStatuses)
            {
                ApplyDestinationQuotaPlan(item);
            }
        }

        private void ApplyDestinationQuotaPlan(DestinationStatusItem item)
        {
            if (!_destinationQuotaPlansById.TryGetValue(item.Id, out var plan))
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

            var usageLabel = string.Format(
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

            var cleanupLabel = string.Format(
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
                var hash = projects.Count;
                foreach (var project in projects)
                {
                    hash = (hash * 397) ^ project.Id;
                    hash = (hash * 397) ^ (project.Name?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                    hash = (hash * 397) ^ (project.RootPath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
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

        private static Dictionary<int, Snapshot> LoadSnapshotLookup(AppConfig config, IReadOnlyList<Backup> backups)
        {
            var snapshotIds = backups
                .Select(backup => backup.SnapshotId)
                .Where(id => id > 0)
                .Distinct()
                .ToHashSet();

            if (snapshotIds.Count == 0)
                return new Dictionary<int, Snapshot>();

            try
            {
                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : AppConfigStore.GetDefaultDbPath();
                var repo = new SqliteRepository(dbPath);
                return repo.GetSnapshotsByIds(snapshotIds)
                    .ToDictionary(snapshot => snapshot.Id);
            }
            catch
            {
                return new Dictionary<int, Snapshot>();
            }
        }

        private static string BuildSnapshotDiffSummaryDisplay(Snapshot? snapshot)
        {
            if (snapshot is null)
                return L("Backups.DiffSummary.Unavailable", "Diff summary unavailable");

            var hasChanges = snapshot.DiffAdded > 0 || snapshot.DiffModified > 0 || snapshot.DiffDeleted > 0;
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

            var topPaths = SnapshotDiffSummary.ParseTopChangedPaths(snapshot.DiffTopPathsJson);
            if (topPaths.Count == 0)
                return (string.Empty, false);

            var preview = string.Join(
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
        {
            var absolute = BackupSnapshotItem.FormatSize(Math.Abs(bytes));
            if (bytes > 0)
                return $"+{absolute}";
            if (bytes < 0)
                return $"-{absolute}";
            return absolute;
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

        public void RefreshAutoBackupFlagsFromConfig()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(RefreshAutoBackupFlagsFromConfig);
                return;
            }

            try
            {
                var cfg = AppConfigStore.GetSnapshot();
                var disabled = cfg.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();
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
                foreach (var dest in config.Backups.Destinations)
                {
                    var label = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias;
                    if (!dest.Active)
                    {
                        var suffix = L("Projects.Destination.InactiveSuffix", " (inactive)");
                        label = $"{label}{suffix}";
                    }

                    var id = DestinationIdentityService.GetId(dest);
                    DestinationOptions.Add(new DestinationOption(id, label));
                }
            }
        }

        private void UpdateProjectEncryptionDisplay(ProjectBackupItem item, AppConfig config)
        {
            item.EncryptionPolicy = ProjectEncryptionPolicy.Normalize(item.EncryptionPolicy);
            var optionMatch = EncryptionPolicyOptions.FirstOrDefault(o =>
                string.Equals(o.Id, item.EncryptionPolicy, StringComparison.OrdinalIgnoreCase));
            item.SetEncryptionPolicyOption(optionMatch ?? EncryptionPolicyOptions.FirstOrDefault());

            var effectiveEncrypted = ProjectEncryptionPolicy.IsEncrypted(
                item.EncryptionPolicy,
                config.Backups.Encryption.Enabled);
            item.EffectiveEncryptionDisplay = effectiveEncrypted
                ? L("Projects.EncryptionPolicy.EffectiveEncrypted", "Effective: Encrypted")
                : L("Projects.EncryptionPolicy.EffectivePlain", "Effective: Plain");

            var hasSecret = !string.IsNullOrWhiteSpace(CredentialVault.Instance.GetSecret(
                string.IsNullOrWhiteSpace(item.EncryptionKeyRef) ? null : item.EncryptionKeyRef,
                BackupEncryptionSecretUsername,
                preferKeychain: true,
                fallbackPlaintext: null));
            item.HasEncryptionSecret = hasSecret;
            item.EncryptionSecretStatus = hasSecret
                ? L("Settings.Encryption.SecretStatusAvailable", "Password is enrolled in secure storage.")
                : L("Settings.Encryption.SecretStatusMissing", "No encryption password enrolled yet.");
        }

        private void UpdateProjectRestoreModeDisplay(ProjectBackupItem item)
        {
            item.RestoreMode = ProjectRestoreMode.Normalize(item.RestoreMode);
            var optionMatch = RestoreModeOptions.FirstOrDefault(o =>
                string.Equals(o.Id, item.RestoreMode, StringComparison.OrdinalIgnoreCase));
            item.SetRestoreModeOption(optionMatch ?? RestoreModeOptions.FirstOrDefault());
        }

        private void UpdateProjectVerificationPolicyDisplay(ProjectBackupItem item)
        {
            item.VerificationPolicy = ProjectVerificationPolicy.Normalize(item.VerificationPolicy);
            var optionMatch = VerificationPolicyOptions.FirstOrDefault(o =>
                string.Equals(o.Id, item.VerificationPolicy, StringComparison.OrdinalIgnoreCase));
            item.SetVerificationPolicyOption(optionMatch ?? VerificationPolicyOptions.FirstOrDefault());
        }

        private void UpdateProjectDestinationDisplay(ProjectBackupItem item, AppConfig config)
        {
            var id = DestinationIdentityService.NormalizePreferredDestinationId(item.PreferredDestinationId, config.Backups.Destinations);
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

            var match = DestinationIdentityService.FindByPreferredDestinationId(config.Backups.Destinations, id);

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

            _ = Task.Run(() =>
            {
                var config = AppConfigStore.GetSnapshot();
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProjectDestinationDisplay(item, config);
                    PreferredDestinationChanged?.Invoke(projectId, item.PreferredDestinationId ?? string.Empty);
                });
            });
        }

        private void OnProjectEncryptionPolicyChanged(ProjectBackupItem item)
        {
            if (!int.TryParse(item.Id, out var projectId) || projectId <= 0)
                return;

            _ = Task.Run(() =>
            {
                var config = AppConfigStore.GetSnapshot();
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProjectEncryptionDisplay(item, config);
                    ProjectEncryptionPolicyChanged?.Invoke(projectId, item.EncryptionPolicy);
                });
            });
        }

        private void OnProjectRestoreModeChanged(ProjectBackupItem item)
        {
            if (!int.TryParse(item.Id, out var projectId) || projectId <= 0)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                UpdateProjectRestoreModeDisplay(item);
                ProjectRestoreModeChanged?.Invoke(projectId, item.RestoreMode);
            });
        }

        private void OnProjectVerificationPolicyChanged(ProjectBackupItem item)
        {
            if (!int.TryParse(item.Id, out var projectId) || projectId <= 0)
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
            foreach (var project in ProjectBackups)
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

    // ---------- Models ----------

        public sealed class StorageConsumerItem
        {
            public StorageConsumerItem(string projectName, string totalSize, double sharePercent)
            {
                ProjectName = projectName;
                TotalSize = totalSize;
                SharePercent = sharePercent;
            }

            public string ProjectName { get; }
            public string TotalSize { get; }
            public double SharePercent { get; }
            public string SharePercentLabel => $"{SharePercent:0}%";
        }

        public sealed class DiffPreviewPathItem
        {
            public DiffPreviewPathItem(string path, int changes, string changedBytes)
            {
                Path = path;
                Changes = changes;
                ChangedBytes = changedBytes;
            }

            public string Path { get; }
            public int Changes { get; }
            public string ChangedBytes { get; }
        }

        public class BackupSnapshotItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public long SizeBytes { get; set; }
        private bool _isProtected;

        /// <summary>Run trigger type, e.g. "Auto" or "Manual".</summary>
        public string Type { get; set; } = "Manual";

        /// <summary>Status, e.g. "Completed", "Failed".</summary>
        public string Status { get; set; } = "Completed";

        /// <summary>Label shown inside the tag pill.</summary>
        public string? Label { get; set; }

        /// <summary>Localized backup mode label for display (Full/Incremental/Imported context).</summary>
        public string TypeLabel { get; set; } = string.Empty;
        public string ModeChipLabel { get; set; } = string.Empty;
        public string EncryptionChipLabel { get; set; } = string.Empty;
        public string RetentionDefaultLabel { get; set; } = "Retention: eligible for pruning";
        public string RetentionProtectedLabel { get; set; } = "Retention: kept (protected)";
        public string RetentionOutcomeLabel => IsProtected ? RetentionProtectedLabel : RetentionDefaultLabel;

        /// <summary>Optional project id this snapshot belongs to; null for global.</summary>
        public string? ProjectId { get; set; }

        /// <summary>Destination endpoint that stored this backup.</summary>
        public string DestinationDisplay { get; set; } = string.Empty;
        public string DiffSummaryDisplay { get; set; } = string.Empty;
        public string DiffTopPathsDisplay { get; set; } = string.Empty;
        public bool HasDiffTopPaths { get; set; }
        public bool CanOpenDiffDetails { get; set; }
        public int DiffAdded { get; set; }
        public int DiffModified { get; set; }
        public int DiffDeleted { get; set; }
        public long DiffNetBytes { get; set; }
        public string DiffTopPathsJson { get; set; } = "[]";

        public string SizeFormatted => FormatSize(SizeBytes);
        public string TimelineSelectionLabel =>
            $"{Timestamp:yyyy-MM-dd HH:mm} \u00b7 {TypeLabel} \u00b7 {SizeFormatted}";

        public bool IsImported { get; set; }
        public bool IsEncrypted { get; set; }

        /// <summary>Localized label for the imported tag.</summary>
        public string ImportedLabel { get; set; } = string.Empty;
        public string EncryptionLabel { get; set; } = string.Empty;
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
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RetentionOutcomeLabel)));
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
        public string ProjectTagsDisplay { get; set; } = string.Empty;
        public ObservableCollection<ProjectTagChip> ProjectTagChips { get; } = new ObservableCollection<ProjectTagChip>();
        public bool HasProjectTags => ProjectTagChips.Count > 0;
        public string Summary { get; set; } = string.Empty;
        public string TotalSizeFormatted { get; set; } = string.Empty;
        public string LatestBackupDisplay { get; set; } = string.Empty;
        public IBrush AccentBrush { get; set; } = new ImmutableSolidColorBrush(Color.Parse("#33405A"));
        public bool IsExpanded { get; set; }

        public ObservableCollection<BackupSnapshotItem> Snapshots { get; } =
            new ObservableCollection<BackupSnapshotItem>();
    }

    public sealed class BackupsProjectSortOption
    {
        public BackupsProjectSortOption(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }
        public string Label { get; }

        public override string ToString() => Label;
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
        private string _projectTagsCsv = string.Empty;
        public string ProjectTagsCsv
        {
            get => _projectTagsCsv;
            set
            {
                if (!SetField(ref _projectTagsCsv, value ?? string.Empty, nameof(ProjectTagsCsv)))
                    return;

                RebuildProjectTags();
                OnPropertyChanged(nameof(HasProjectTags));
                OnPropertyChanged(nameof(ProjectTagsDisplay));
                OnPropertyChanged(nameof(PrimaryTagSortKey));
            }
        }

        public ObservableCollection<ProjectTagChip> ProjectTagChips { get; } = new ObservableCollection<ProjectTagChip>();
        public bool HasProjectTags => ProjectTagChips.Count > 0;
        public string ProjectTagsDisplay => string.Join(", ", ProjectTagChips.Select(tag => tag.Value));
        public string PrimaryTagSortKey => ProjectTagChips.FirstOrDefault()?.Value ?? string.Empty;

        public DateTime? LastBackupTime { get; set; }
        public int       SnapshotCount  { get; set; }
        public long      TotalSizeBytes { get; set; }
        public long?     StorageDeltaBytes { get; set; }
        private string _restoreReadinessLabel = string.Empty;
        public string RestoreReadinessLabel
        {
            get => _restoreReadinessLabel;
            set => SetField(ref _restoreReadinessLabel, value ?? string.Empty, nameof(RestoreReadinessLabel));
        }

        private string _restoreReadinessReason = string.Empty;
        public string RestoreReadinessReason
        {
            get => _restoreReadinessReason;
            set => SetField(ref _restoreReadinessReason, value ?? string.Empty, nameof(RestoreReadinessReason));
        }

        private IBrush _restoreReadinessBrush = new ImmutableSolidColorBrush(Color.Parse("#7F8FA8"));
        public IBrush RestoreReadinessBrush
        {
            get => _restoreReadinessBrush;
            set => SetField(ref _restoreReadinessBrush, value, nameof(RestoreReadinessBrush));
        }

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
        public Action<ProjectBackupItem>? EncryptionPolicyChanged { get; set; }
        public Action<ProjectBackupItem>? RestoreModeChanged { get; set; }
        public Action<ProjectBackupItem>? VerificationPolicyChanged { get; set; }

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
                // Ignore transient null selection events fired while destination options refresh.
                // Real "Auto" selection is represented by a non-null option with empty Id.
                if (value is null)
                    return;

                if (ReferenceEquals(_preferredDestinationOption, value))
                    return;

                var previousId = _preferredDestinationOption?.Id ?? string.Empty;
                _preferredDestinationOption = value;
                OnPropertyChanged(nameof(PreferredDestinationOption));

                var nextId = value.Id ?? string.Empty;
                if (string.Equals(previousId, nextId, StringComparison.OrdinalIgnoreCase))
                    return;

                PreferredDestinationId = nextId;
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
            if (ReferenceEquals(_preferredDestinationOption, option))
                return;

            _preferredDestinationOption = option;
            OnPropertyChanged(nameof(PreferredDestinationOption));
        }

        private string _encryptionPolicy = ProjectEncryptionPolicy.Inherit;
        public string EncryptionPolicy
        {
            get => _encryptionPolicy;
            set => SetField(ref _encryptionPolicy, ProjectEncryptionPolicy.Normalize(value), nameof(EncryptionPolicy));
        }

        private string _encryptionKeyRef = string.Empty;
        public string EncryptionKeyRef
        {
            get => _encryptionKeyRef;
            set => SetField(ref _encryptionKeyRef, value ?? string.Empty, nameof(EncryptionKeyRef));
        }

        private EncryptionPolicyOption? _encryptionPolicyOption;
        public EncryptionPolicyOption? EncryptionPolicyOption
        {
            get => _encryptionPolicyOption;
            set
            {
                if (!SetField(ref _encryptionPolicyOption, value, nameof(EncryptionPolicyOption)))
                    return;

                // Ignore transient null selection events fired while option sources refresh.
                // Real "inherit" selection is represented by a non-null option with Id="inherit".
                if (value is null)
                    return;

                EncryptionPolicy = value.Id;
                EncryptionPolicyChanged?.Invoke(this);
            }
        }

        public void SetEncryptionPolicyOption(EncryptionPolicyOption? option)
        {
            if (ReferenceEquals(_encryptionPolicyOption, option))
                return;

            _encryptionPolicyOption = option;
            OnPropertyChanged(nameof(EncryptionPolicyOption));
        }

        private string _restoreMode = ProjectRestoreMode.Direct;
        public string RestoreMode
        {
            get => _restoreMode;
            set => SetField(ref _restoreMode, ProjectRestoreMode.Normalize(value), nameof(RestoreMode));
        }

        private RestoreModeOption? _restoreModeOption;
        public RestoreModeOption? RestoreModeOption
        {
            get => _restoreModeOption;
            set
            {
                if (!SetField(ref _restoreModeOption, value, nameof(RestoreModeOption)))
                    return;

                if (value is null)
                    return;

                RestoreMode = value.Id;
                RestoreModeChanged?.Invoke(this);
            }
        }

        public void SetRestoreModeOption(RestoreModeOption? option)
        {
            if (ReferenceEquals(_restoreModeOption, option))
                return;

            _restoreModeOption = option;
            OnPropertyChanged(nameof(RestoreModeOption));
        }

        private string _verificationPolicy = ProjectVerificationPolicy.Always;
        public string VerificationPolicy
        {
            get => _verificationPolicy;
            set => SetField(ref _verificationPolicy, ProjectVerificationPolicy.Normalize(value), nameof(VerificationPolicy));
        }

        private VerificationPolicyOption? _verificationPolicyOption;
        public VerificationPolicyOption? VerificationPolicyOption
        {
            get => _verificationPolicyOption;
            set
            {
                if (!SetField(ref _verificationPolicyOption, value, nameof(VerificationPolicyOption)))
                    return;

                if (value is null)
                    return;

                VerificationPolicy = value.Id;
                VerificationPolicyChanged?.Invoke(this);
            }
        }

        public void SetVerificationPolicyOption(VerificationPolicyOption? option)
        {
            if (ReferenceEquals(_verificationPolicyOption, option))
                return;

            _verificationPolicyOption = option;
            OnPropertyChanged(nameof(VerificationPolicyOption));
        }

        private string _effectiveEncryptionDisplay = string.Empty;
        public string EffectiveEncryptionDisplay
        {
            get => _effectiveEncryptionDisplay;
            set => SetField(ref _effectiveEncryptionDisplay, value ?? string.Empty, nameof(EffectiveEncryptionDisplay));
        }

        private bool _hasEncryptionSecret;
        public bool HasEncryptionSecret
        {
            get => _hasEncryptionSecret;
            set => SetField(ref _hasEncryptionSecret, value, nameof(HasEncryptionSecret));
        }

        private string _encryptionSecretStatus = string.Empty;
        public string EncryptionSecretStatus
        {
            get => _encryptionSecretStatus;
            set => SetField(ref _encryptionSecretStatus, value ?? string.Empty, nameof(EncryptionSecretStatus));
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

        public bool HasStorageDelta => StorageDeltaBytes.HasValue;

        public string StorageDeltaFormatted
        {
            get
            {
                if (!StorageDeltaBytes.HasValue)
                    return "Δ -";

                var value = StorageDeltaBytes.Value;
                if (Math.Abs(value) < 1024)
                    return "Δ ~0 B";

                var sign = value >= 0 ? "+" : "-";
                return $"Δ {sign}{BackupSnapshotItem.FormatSize(Math.Abs(value))}";
            }
        }

        private void RebuildProjectTags()
        {
            ProjectTagChips.Clear();
            foreach (var chip in ProjectTagAppearance.CreateChips(_projectTagsCsv))
                ProjectTagChips.Add(chip);
        }
    }

    public class DestinationStatusItem : ViewModelBase
    {
        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));
        private static readonly IBrush InfoBrush = new ImmutableSolidColorBrush(Color.Parse("#8E9BAF"));

        public string Id { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;

        private BackupsViewModel.DestinationStatus _status = BackupsViewModel.DestinationStatus.None;
        public BackupsViewModel.DestinationStatus Status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;
                _status = value;

                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(IsChecking));
            }
        }

        public string StatusDisplay => _status switch
        {
            BackupsViewModel.DestinationStatus.Pending => L("Backups.Destinations.Pending", "Pending"),
            BackupsViewModel.DestinationStatus.Inactive => L("Backups.Destinations.Inactive", "Inactive"),
            BackupsViewModel.DestinationStatus.Reachable => L("Destinations.Test.Reachable", "Reachable"),
            BackupsViewModel.DestinationStatus.ReadOnly => L("Destinations.Test.ReadOnly", "ReadOnly"),
            BackupsViewModel.DestinationStatus.Unavailable => L("Destinations.Test.Unavailable", "Unavailable"),
            BackupsViewModel.DestinationStatus.None => string.Empty,

            _ => string.Empty
        };
        public void RefreshLocalization()
        {
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(LastCheckedDisplay));
        }

        public bool IsChecking => _status ==  BackupsViewModel.DestinationStatus.Pending;

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

        private string _storedBytesText = string.Empty;
        public string StoredBytesText
        {
            get => _storedBytesText;
            set
            {
                if (SetField(ref _storedBytesText, value))
                {
                    OnPropertyChanged(nameof(HasStoredBytesText));
                }
            }
        }

        public bool HasStoredBytesText => !string.IsNullOrWhiteSpace(StoredBytesText);

        private string _cleanupSuggestionText = string.Empty;
        public string CleanupSuggestionText
        {
            get => _cleanupSuggestionText;
            set
            {
                if (SetField(ref _cleanupSuggestionText, value))
                {
                    OnPropertyChanged(nameof(HasCleanupSuggestionText));
                }
            }
        }

        public bool HasCleanupSuggestionText => !string.IsNullOrWhiteSpace(CleanupSuggestionText);

        public static string GetId(BackupDestination dest) =>
            DestinationIdentityService.GetId(dest);
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

        private string _policyText = string.Empty;
        public string PolicyText
        {
            get => _policyText;
            set
            {
                var normalized = value ?? string.Empty;
                if (_policyText == normalized)
                    return;

                _policyText = normalized;
                OnPropertyChanged(nameof(PolicyText));
                OnPropertyChanged(nameof(HasPolicyText));
            }
        }

        public bool HasPolicyText => !string.IsNullOrWhiteSpace(PolicyText);

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
                    "Restoring" => L("Backups.Status.Restoring", "Restoring backup..."),
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

            if (ContainsToken(_etaText, "Restoring") ||
                ContainsToken(_currentFile, "Restoring") ||
                ContainsToken(_currentFile, "Decrypting") ||
                ContainsToken(_currentFile, L("Backups.Status.Restoring", "Restoring backup...")))
                return "Restoring";

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
                "Restoring"   => StageCopyBrush,
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

    public sealed class RestoreReadinessIssueItem
    {
        public RestoreReadinessIssueItem(string projectName, string stateLabel, string reason, IBrush stateBrush)
        {
            ProjectName = projectName;
            StateLabel = stateLabel;
            Reason = reason;
            StateBrush = stateBrush;
        }

        public string ProjectName { get; }
        public string StateLabel { get; }
        public string Reason { get; }
        public IBrush StateBrush { get; }
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
