using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Views;

namespace VaultSync.UI.ViewModels
{
    public enum NotificationLevel
    {
        Info,
        Warning,
        Error
    }

    public enum NotificationKind
    {
        System,
        Backup,
        Snapshot
    }

    public interface INotificationService
    {
        void ShowInfo(string title, string message, NotificationKind kind = NotificationKind.System);
        void ShowWarning(string title, string message, NotificationKind kind = NotificationKind.System);
        void ShowError(string title, string message, NotificationKind kind = NotificationKind.System);
    }

    /// <summary>
    /// Basic in-app notification service. For now it only logs to the console;
    /// later we can extend this to drive UI banners/toasts and OS notifications.
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        public void ShowInfo(string title, string message, NotificationKind kind = NotificationKind.System)
        {
        }

        public void ShowWarning(string title, string message, NotificationKind kind = NotificationKind.System)
        {
        }

        public void ShowError(string title, string message, NotificationKind kind = NotificationKind.System)
        {
        }
    }

    public partial class AppViewModel : ViewModelBase
    {
        public static DateTime AppStartUtc { get; } = DateTime.UtcNow;
        public sealed record DestinationProbeSummary(
            string Id,
            string Alias,
            string Path,
            string EffectivePath,
            bool Reachable,
            string Message,
            DateTime LastChecked,
            BackupsViewModel.SeverityStatus Severity);

        public event Action? TrayMenuRefreshRequested;

        private object? _currentView;
        private string _currentViewName = "Dashboard";
        private string _headerTitle = LStatic("Nav.Dashboard", "Dashboard");
        private string _headerKicker = LStatic("Main.HeaderOverview", "Overview");

        // Section view models (kept alive for entire app lifetime)
        private DashboardViewModel? _dashboardViewModel;
        private readonly ProjectsViewModel _projectsViewModel;
        private BackupsViewModel? _backupsViewModel;
        private readonly SettingsViewModel _settingsViewModel;
        private readonly IAppConfigStore _configStore;
        private AppConfig _config;
        private IBackupWidgetService? _backupWidgetService;

        // NAS monitor to move temp backups when the preferred network root becomes reachable again.
        private Timer? _nasMonitorTimer;
        private int _nasMonitorInFlight;
        private Timer? _autoBackupTimer;
        private int _autoBackupInFlight;
        private Timer? _maintenanceTimer;
        private int _maintenanceInFlight;
        private int _destinationProbeInFlight;
        private int _startupDestinationProbeQueued;

        // Core services for live data
        private readonly SqliteRepository _repo;
        private readonly BackupService _backupService;
        private readonly NetworkMountService _networkMountService;
        private readonly MetadataSyncService _metadataSyncService;
        private readonly CredentialVault _credentialVault;
        private readonly ProjectEncryptionEnrollmentService _projectEncryptionEnrollmentService;
        private readonly INotificationService _notificationService;
        private readonly IPowerStatusProvider _powerStatusProvider;
        private readonly IDriveHealthService _driveHealthService;
        private readonly BackupIndexConsistencyService _backupIndexConsistencyService;
        private readonly HashSet<BackupDestinationViewModel> _observedDestinations = [];
        private readonly LogConsoleService _logConsoleService;
        private LogConsoleWindow? _logConsoleWindow;
        private readonly ConcurrentDictionary<string, DestinationProbeSummary> _destinationProbeSummaries = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<int?>>> _archiveUploadBufferTuneTasks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan DestinationProbeMinInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan DestinationProbeFailureBackoff = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan DestinationScanInterval = TimeSpan.FromMinutes(10);
        private const string BackupProtectionMarkerFileName = ".vaultsync_keep";
        private const int DefaultEncryptedOpenTimeoutMinutes = 10;
        private static readonly TimeSpan EncryptedOpenStaleRetention = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _encryptedOpenCleanup = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, EncryptedOpenUnlockSession> _encryptedOpenSessions = new();
        private const string BackupEncryptionSecretUsername = "vaultsync-backup-encryption";
        private DateTime _lastDestinationScanUtc = DateTime.MinValue;
        private int _destinationScanInFlight;
        private int _destinationOverviewRefreshInFlight;
        private readonly ConcurrentDictionary<string, DateTime> _metadataImportAttempts = new();
        private readonly ConcurrentDictionary<int, byte> _manualBackupInFlight = new();
        private readonly ConcurrentDictionary<int, byte> _backupCancelRequested = new();
        private readonly ConcurrentDictionary<int, byte> _restoreAdvisoryShown = new();
        private readonly ConcurrentDictionary<int, byte> _projectRootMissingNotified = new();
        private readonly ConcurrentDictionary<int, byte> _lowDiskWarningShown = new();
        private readonly object _groupedBackupNotificationGate = new();
        private readonly Dictionary<string, GroupedBackupNotificationBatch> _groupedBackupNotifications = new(StringComparer.Ordinal);
        private static readonly TimeSpan GroupedBackupNotificationDelay = TimeSpan.FromMilliseconds(900);
        private int _metadataUiRefreshInFlight;
        private int _metadataUiRefreshQueued;
        private readonly ConcurrentDictionary<int, DateTime> _backupProgressLogTimestamps = new();
        private int _configReloadInFlight;
        private int _configReloadQueued;
        private readonly object _backupPolicyStateGate = new();
        private string _lastBackupPolicySignature = string.Empty;
        private int _manualBackupInFlightCount;
        private int _backupAllInProgress;
        private bool _trayInitiatedBackup;
        private string _currentViewKey = string.Empty;
        private DateTime _lastDashboardRefreshUtc;
        private List<Project>? _backupsCacheProjects;
        private List<Backup>? _backupsCacheBackups;
        private HashSet<int>? _backupsCacheDisabledAuto;
        private DateTime _backupsCacheUpdatedUtc;
        private bool _backupsCachePartial;

        private sealed class GroupedBackupNotificationBatch
        {
            public required string Key { get; init; }
            public required NotificationSeverity Severity { get; init; }
            public required string Title { get; init; }
            public required Func<IReadOnlyList<string>, string> MessageFactory { get; init; }
            public List<string> ProjectNames { get; } = [];
            public HashSet<string> ProjectNameSet { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
        private static readonly TimeSpan BackupsCacheTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DashboardRefreshTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan InitialDataLoadDelay = TimeSpan.FromMilliseconds(750);
        private int _dashboardWarmLoadQueued;
        private int _backupsWarmLoadQueued;
        private readonly GitHubUpdateService _updateService = new();
        private readonly PatchUpdateService _patchService = new();
        private readonly LocalizationService _localizationService = new();
        private readonly Stopwatch _startupDiagnosticsStopwatch = Stopwatch.StartNew();
        private readonly object _startupDiagnosticsGate = new();
        private readonly List<StartupDiagnosticsPhase> _startupDiagnosticsPhases = [];
        private static readonly HttpClient s_installerClient = CreateInstallerHttpClient();
        private readonly string _currentVersionString;
        private CancellationTokenSource? _updateCheckCts;
        private Timer? _updateCheckTimer;
        private Timer? _updateCheckRetryTimer;
        private readonly DateTime _appStartUtc = DateTime.UtcNow;
        private int _dashboardWarmLoadScheduled;
        private int _backupsWarmLoadScheduled;
        private static readonly TimeSpan WarmLoadStartupDelay = TimeSpan.FromSeconds(2);
        private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
        private static readonly TimeSpan UpdateCheckMinInterval = TimeSpan.FromMinutes(2);
        private readonly ConcurrentDictionary<string, DateTime> _metadataImportRetryAfter = new();
        private DateTime _metadataRootImportRetryAfterUtc = DateTime.MinValue;
        private readonly ConcurrentDictionary<int, DateTime> _backupProgressUiTimestamps = new();
        private readonly object _destinationProbeCacheGate = new();
        private IReadOnlyList<DestinationProbeSummary> _cachedDestinationProbeSummaries = [];
        private string _cachedDestinationProbeSignature = string.Empty;
        private int _updateCheckInFlight;
        private DateTimeOffset? _lastUpdateCheckAt;
        private string? _lastUpdateCheckError;
        private UpdateCheckResult? _pendingUpdateResult;
        private int _updateCheckLogCaptureSuppressed;
        private bool _updateCheckLogServiceSuppressed;
        private bool _updateCheckPrevLogEnabled;
        private bool _updateCheckPrevSaveToFile;
        private bool _patchBlocked;
        private bool _patchFailed;
        private bool _isUpdateAvailable;
        private bool _isUpdateBannerDismissed;
        private bool _isInstallerDownloading;
        private string _updateBannerMessage = string.Empty;
        private string _updateReleaseNotes = string.Empty;
        private string _updateReleaseUrl = string.Empty;
        private readonly RelayCommand _installPatchCommand;
        private readonly RelayCommand _openReleaseCommand;
        private readonly RelayCommand _skipUpdateCommand;
        private readonly RelayCommand _dismissUpdateBannerCommand;
        private readonly RelayCommand _dismissSoftCrashBannerCommand;
        private readonly RelayCommand _copySoftCrashLogCommand;
        private int _reloadBackupsInFlight;
        private int _reloadBackupsQueued;
        private bool _isPatchInstalling;
        private string _patchStatusMessage = string.Empty;
        private BackupIndexConsistencyReport? _backupIndexConsistencyReport;
        private string _backupIndexConsistencyStatus = string.Empty;
        private bool _showSoftCrashBanner;
        private string _softCrashBannerMessage = string.Empty;
        private string? _softCrashLogPath;

        // Helper property to detect when the Backups page is active
        private bool IsOnBackupsPage => CurrentView == _backupsViewModel;

        // Helper property to respect global notifications setting from SettingsViewModel
        private bool NotificationsEnabled => _settingsViewModel?.NotificationsEnabled ?? true;

        // Determine if system notifications should be raised, based on user settings.
        private bool ShouldRaiseSystemNotification
        {
            get
            {
                if (_settingsViewModel is null)
                    return true;

                if (!_settingsViewModel.NotificationsEnabled)
                    return false;

                if (!_settingsViewModel.UseOsNotifications)
                    return false;

                if (_settingsViewModel.NotifyOnlyWhenInactive && VaultSync.UI.MainWindow.IsForeground)
                    return false;

                return true;
            }
        }

        private bool ShouldShowBackupWidget =>
            _settingsViewModel?.ShowTrayBackupWidget ?? true;

        private static bool IsRemoteDestinationPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (IsNetworkDrivePath(path))
                return true;

            if (path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("//", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!OperatingSystem.IsMacOS())
                return false;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string mountRoot = Path.Combine(home, "Library", "Application Support", "VaultSync", "mounts");
            if (path.StartsWith(mountRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static bool IsSmbPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (IsNetworkDrivePath(path))
                return true;

            return path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("//", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNetworkDrivePath(string path)
        {
            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                if (path.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("//", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string? root = Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(root))
                    return false;

                // DriveInfo accepts drive roots like C:\ or C:
                string normalizedRoot = root.TrimEnd('\\', '/');
                if (normalizedRoot.Length != 2 || normalizedRoot[1] != ':')
                    return false;

                var drive = new DriveInfo(root);
                return drive.DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }

        private void LogBackupProgress(int projectId, string projectName, double percent, string label, string etaText)
        {
            if (!_settingsViewModel.EnableVerboseLogging)
                return;

            DateTime now = DateTime.UtcNow;
            DateTime last = _backupProgressLogTimestamps.GetOrAdd(projectId, DateTime.MinValue);
            if ((now - last) < TimeSpan.FromSeconds(1))
                return;

            _backupProgressLogTimestamps[projectId] = now;
            Console.WriteLine($"[BackupUI] '{projectName}' progress={percent:0.0} label='{label}' eta='{etaText}'");
        }

        private bool ShouldUpdateBackupUi(int projectId, double percent, string etaText)
        {
            if (percent >= 100)
                return true;

            if (!string.IsNullOrWhiteSpace(etaText) &&
                etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            DateTime now = DateTime.UtcNow;
            DateTime last = _backupProgressUiTimestamps.GetOrAdd(projectId, DateTime.MinValue);
            if ((now - last) < TimeSpan.FromMilliseconds(200))
                return false;

            _backupProgressUiTimestamps[projectId] = now;
            return true;
        }

        private GitHubReleaseChannel CurrentUpdateChannel =>
            _settingsViewModel?.BetaChannelEnabled is true
                ? GitHubReleaseChannel.Beta
                : GitHubReleaseChannel.Stable;

        private static bool CanUseSelfUpdate => !DistributionChannelService.Current.IsStore;

        public SettingsViewModel SettingsViewModel => _settingsViewModel;
        public BackupsViewModel BackupsViewModel => _backupsViewModel ??= CreateBackupsViewModel();

        private static List<BackupDestination> GetActiveDestinations(AppConfig cfg)
        {
            if (cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 })
            {
                return [.. cfg.Backups.Destinations.Where(d => d.Active)];
            }

            string? backupRoot = cfg.Backups.BackupLocation;
            if (!string.IsNullOrWhiteSpace(backupRoot))
            {
                return
                [
                    new BackupDestination
                    {
                        Alias       = "Primary",
                        Path        = backupRoot,
                        Active      = true,
                        PreMounted  = true,
                        AutoMount   = false,
                        AutoUnmount = false
                    }
                ];
            }

            return [];
        }

        private static List<BackupDestination> GetAllDestinations(AppConfig cfg)
        {
            if (cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 })
            {
                return [.. cfg.Backups.Destinations];
            }

            string? backupRoot = cfg.Backups.BackupLocation;
            if (!string.IsNullOrWhiteSpace(backupRoot))
            {
                return
                [
                    new BackupDestination
                    {
                        Alias       = "Primary",
                        Path        = backupRoot,
                        Active      = true,
                        PreMounted  = true,
                        AutoMount   = false,
                        AutoUnmount = false
                    }
                ];
            }

            return [];
        }

        private sealed record ProjectDestinationSelection(
            List<BackupDestination> Destinations,
            string? WarningMessage,
            string? WarningCode);

        private ProjectDestinationSelection ResolveDestinationsForProject(Project project, AppConfig cfg)
        {
            List<BackupDestination> activeDestinations = GetActiveDestinations(cfg);
            List<BackupDestination> allDestinations = GetAllDestinations(cfg);
            string preferredId = DestinationIdentityService.NormalizePreferredDestinationId(project.PreferredDestinationId, allDestinations);

            if (string.IsNullOrWhiteSpace(preferredId))
            {
                return new ProjectDestinationSelection(activeDestinations, null, null);
            }

            if (string.Equals(preferredId, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
            {
                return new ProjectDestinationSelection(allDestinations, null, null);
            }

            BackupDestination? match = DestinationIdentityService.FindByPreferredDestinationId(allDestinations, preferredId);

            if (match is null)
            {
                string message = Lf(
                    "Backups.Notification.PreferredDestinationMissing",
                    "Preferred destination '{0}' not found. Using active destinations.",
                    preferredId);
                return new ProjectDestinationSelection(activeDestinations, message, "preferred_missing");
            }

            if (!match.Active)
            {
                string label = string.IsNullOrWhiteSpace(match.Alias) ? match.Path : match.Alias;
                string message = Lf(
                    "Backups.Notification.PreferredDestinationInactive",
                    "Preferred destination '{0}' is inactive. Using active destinations.",
                    label);
                return new ProjectDestinationSelection(activeDestinations, message, "preferred_inactive");
            }

            return new ProjectDestinationSelection([match], null, null);
        }

        private void RefreshDestinationStatusOverview()
        {
            QueueDestinationOverviewRefresh(_backupsViewModel);
        }


        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set => SetField(ref _isUpdateAvailable, value);
        }

        public bool ShowUpdateBanner => CanUseSelfUpdate && IsUpdateAvailable && !_isUpdateBannerDismissed;

        public string UpdateBannerMessage
        {
            get => _updateBannerMessage;
            private set => SetField(ref _updateBannerMessage, value);
        }

        public string UpdateTooltip => string.IsNullOrWhiteSpace(_updateReleaseNotes)
            ? L("Shell.OpenReleaseTooltip", "Open the latest release on GitHub")
            : _updateReleaseNotes;

        public bool IsPatchAvailable => CanUseSelfUpdate && (_pendingUpdateResult?.HasPatch ?? false) && !_patchBlocked;

        public bool ShowPatchButton => IsPatchAvailable;

        public bool ShowInstallerFallback =>
            CanUseSelfUpdate &&
            _pendingUpdateResult is not null &&
            (!IsPatchAvailable || _patchFailed || _patchBlocked);

        public bool CanSkipUpdate => _pendingUpdateResult is not null;

        public string InstallButtonText => IsPatchInstalling
            ? L("Patch.InstallButton.Preparing", "Preparing patch...")
            : L("Patch.InstallButton.Install", "Install patch");

        public string ReleaseActionText => !CanUseSelfUpdate
            ? L("Update.Store.OpenStore", "Open Microsoft Store")
            : _pendingUpdateResult?.HasInstaller is true
            ? (IsInstallerDownloading
                ? L("Shell.OpenInstaller.Downloading", "Downloading installer...")
                : L("Shell.OpenInstaller", "Install update"))
            : L("Shell.OpenRelease", "Open release");

        public bool IsReleaseActionEnabled => !IsInstallerDownloading;

        public bool ShowPatchStatus => !string.IsNullOrWhiteSpace(PatchStatusMessage);

        public string PatchStatusMessage
        {
            get => _patchStatusMessage;
            private set
            {
                if (SetField(ref _patchStatusMessage, value))
                {
                    OnPropertyChanged(nameof(ShowPatchStatus));
                }
            }
        }

        private bool IsPatchInstalling
        {
            get => _isPatchInstalling;
            set
            {
                if (SetField(ref _isPatchInstalling, value))
                {
                    OnPropertyChanged(nameof(InstallButtonText));
                    _installPatchCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private bool IsInstallerDownloading
        {
            get => _isInstallerDownloading;
            set
            {
                if (SetField(ref _isInstallerDownloading, value))
                {
                    OnPropertyChanged(nameof(ReleaseActionText));
                    OnPropertyChanged(nameof(IsReleaseActionEnabled));
                    _openReleaseCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string CurrentViewName
        {
            get => _currentViewName;
            set
            {
                if (_currentViewName == value)
                {
                    return;
                }
                _currentViewName = value;
                OnPropertyChanged(nameof(CurrentViewName));
                OnPropertyChanged(nameof(IsDashboardActive));
                OnPropertyChanged(nameof(IsProjectsViewActive));
                OnPropertyChanged(nameof(IsBackupsViewActive));
                OnPropertyChanged(nameof(IsSettingsActive));
            }
        }

        public OnboardingTourViewModel OnboardingTour
        {
            get;
        }
        public bool IsDashboardActive => CurrentViewName == "Dashboard";
        public bool IsProjectsViewActive => CurrentViewName == "Projects";
        public bool IsBackupsViewActive => CurrentViewName == "Backups";
        public bool IsSettingsActive => CurrentViewName == "Settings";
        public ProjectsViewModel ProjectsViewModel => _projectsViewModel;
        public DashboardViewModel DashboardViewModel => _dashboardViewModel ??= new DashboardViewModel(_configStore);
        public ICommand OpenReleasePageCommand => _openReleaseCommand;
        public ICommand InstallPatchCommand => _installPatchCommand;
        public ICommand SkipUpdateCommand => _skipUpdateCommand;
        public ICommand DismissUpdateBannerCommand => _dismissUpdateBannerCommand;
        public ICommand DismissSoftCrashBannerCommand => _dismissSoftCrashBannerCommand;
        public ICommand CopySoftCrashLogCommand => _copySoftCrashLogCommand;
        public string CurrentVersionDisplay => $"v{StripBuildMetadata(_currentVersionString)}";
        public string FooterProductDisplay => $"VaultSync · {CurrentVersionDisplay}";
        public static string FooterCopyrightDisplay => $"© {DateTime.UtcNow.Year} Flavio Giacchetti";
        public bool ShowSoftCrashBanner => _showSoftCrashBanner;
        public BackupIndexConsistencyReport? BackupIndexConsistencyReport => _backupIndexConsistencyReport;
        public bool HasBackupIndexConsistencyIssues => (_backupIndexConsistencyReport?.HasIssues).GetValueOrDefault();
        public string BackupIndexConsistencyStatus
        {
            get => _backupIndexConsistencyStatus;
            private set => SetField(ref _backupIndexConsistencyStatus, value);
        }
        public string SoftCrashBannerMessage
        {
            get => _softCrashBannerMessage;
            private set => SetField(ref _softCrashBannerMessage, value);
        }
        public bool CanCopySoftCrashLog => !string.IsNullOrWhiteSpace(_softCrashLogPath);
        public string SoftCrashDismissLabel => L("Errors.SoftCrash.Dismiss", "Dismiss");
        public string SoftCrashCopyLabel => L("Errors.SoftCrash.CopyLogPath", "Copy log path");

    }
}
