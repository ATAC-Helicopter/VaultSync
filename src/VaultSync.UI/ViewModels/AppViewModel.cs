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

    public class AppViewModel : ViewModelBase
    {
        public static DateTime AppStartUtc { get; } = DateTime.UtcNow;
        public sealed record DestinationProbeSummary(
            string Id,
            string Alias,
            string Path,
            bool Reachable,
            string Message,
            DateTime LastChecked);

        public event Action? TrayMenuRefreshRequested;

        private object? _currentView;
        private string _headerTitle = LStatic("Nav.Dashboard", "Dashboard");
        private string _headerKicker = LStatic("Main.HeaderOverview", "Overview");

        // Section view models (kept alive for entire app lifetime)
        private DashboardViewModel? _dashboardViewModel;
        private readonly ProjectsViewModel  _projectsViewModel;
        private BackupsViewModel? _backupsViewModel;
        private readonly SettingsViewModel  _settingsViewModel;
        private AppConfig                   _config;
        private IBackupWidgetService?       _backupWidgetService;

        // NAS monitor to move temp backups when the preferred network root becomes reachable again.
        private Timer? _nasMonitorTimer;
        private int _nasMonitorInFlight;
        private Timer? _autoBackupTimer;
        private int _autoBackupInFlight;
        private Timer? _destinationProbeTimer;
        private int _destinationProbeInFlight;

        // Core services for live data
        private readonly SqliteRepository _repo;
        private readonly BackupService    _backupService;
        private readonly NetworkMountService _networkMountService;
        private readonly MetadataSyncService _metadataSyncService;
        private readonly CredentialVault _credentialVault;
        private readonly INotificationService _notificationService;
        private readonly IPowerStatusProvider _powerStatusProvider;
        private readonly IDriveHealthService _driveHealthService;
        private readonly HashSet<BackupDestinationViewModel> _observedDestinations = new();
        private readonly LogConsoleService _logConsoleService;
        private LogConsoleWindow? _logConsoleWindow;
        private readonly ConcurrentDictionary<string, DestinationProbeSummary> _destinationProbeSummaries = new();
        private static readonly TimeSpan DestinationProbeMinInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan DestinationScanInterval = TimeSpan.FromMinutes(10);
        private const string BackupProtectionMarkerFileName = ".vaultsync_keep";
        private DateTime _lastDestinationScanUtc = DateTime.MinValue;
        private int _destinationScanInFlight;
        private int _destinationOverviewRefreshInFlight;
        private readonly ConcurrentDictionary<string, DateTime> _metadataImportAttempts = new();
        private readonly ConcurrentDictionary<int, byte> _manualBackupInFlight = new();
        private readonly ConcurrentDictionary<int, byte> _backupCancelRequested = new();
        private readonly ConcurrentDictionary<int, byte> _restoreAdvisoryShown = new();
        private readonly ConcurrentDictionary<int, byte> _projectRootMissingNotified = new();
        private int _metadataUiRefreshInFlight;
        private int _metadataUiRefreshQueued;
        private readonly ConcurrentDictionary<int, DateTime> _backupProgressLogTimestamps = new();
        private int _configReloadInFlight;
        private int _configReloadQueued;
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
        private static readonly TimeSpan BackupsCacheTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DashboardRefreshTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan InitialDataLoadDelay = TimeSpan.Zero;
        private int _dashboardWarmLoadQueued;
        private int _backupsWarmLoadQueued;
        private readonly GitHubUpdateService _updateService = new();
        private readonly PatchUpdateService _patchService = new();
        private readonly LocalizationService _localizationService = new();
        private static readonly HttpClient s_installerClient = CreateInstallerHttpClient();
        private readonly string _currentVersionString;
        private CancellationTokenSource? _updateCheckCts;
        private Timer? _updateCheckTimer;
        private Timer? _updateCheckRetryTimer;
        private readonly DateTime _appStartUtc = DateTime.UtcNow;
        private int _dashboardWarmLoadScheduled;
        private int _backupsWarmLoadScheduled;
        private static readonly TimeSpan WarmLoadStartupDelay = TimeSpan.Zero;
        private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
        private static readonly TimeSpan UpdateCheckMinInterval = TimeSpan.FromMinutes(2);
        private readonly ConcurrentDictionary<string, DateTime> _metadataImportRetryAfter = new();
        private DateTime _metadataRootImportRetryAfterUtc = DateTime.MinValue;
        private readonly ConcurrentDictionary<int, DateTime> _backupProgressUiTimestamps = new();
        private readonly object _destinationProbeCacheGate = new();
        private IReadOnlyList<DestinationProbeSummary> _cachedDestinationProbeSummaries = Array.Empty<DestinationProbeSummary>();
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

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var mountRoot = Path.Combine(home, "Library", "Application Support", "VaultSync", "mounts");
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
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(root))
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

            var now = DateTime.UtcNow;
            var last = _backupProgressLogTimestamps.GetOrAdd(projectId, DateTime.MinValue);
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
                return true;

            var now = DateTime.UtcNow;
            var last = _backupProgressUiTimestamps.GetOrAdd(projectId, DateTime.MinValue);
            if ((now - last) < TimeSpan.FromMilliseconds(200))
                return false;

            _backupProgressUiTimestamps[projectId] = now;
            return true;
        }

        private GitHubReleaseChannel CurrentUpdateChannel =>
            _settingsViewModel?.BetaChannelEnabled == true
                ? GitHubReleaseChannel.Beta
                : GitHubReleaseChannel.Stable;

        public SettingsViewModel SettingsViewModel => _settingsViewModel;
        public BackupsViewModel BackupsViewModel => _backupsViewModel ??= CreateBackupsViewModel();

        private List<BackupDestination> GetActiveDestinations(AppConfig cfg)
        {
            if (cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 })
            {
                return cfg.Backups.Destinations
                    .Where(d => d.Active)
                    .ToList();
            }

            var backupRoot = cfg.Backups.BackupLocation;
            if (!string.IsNullOrWhiteSpace(backupRoot))
            {
                return new List<BackupDestination>
                {
                    new BackupDestination
                    {
                        Alias       = "Primary",
                        Path        = backupRoot,
                        Active      = true,
                        PreMounted  = true,
                        AutoMount   = false,
                        AutoUnmount = false
                    }
                };
            }

            return new List<BackupDestination>();
        }

        private List<BackupDestination> GetAllDestinations(AppConfig cfg)
        {
            if (cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 })
            {
                return cfg.Backups.Destinations.ToList();
            }

            var backupRoot = cfg.Backups.BackupLocation;
            if (!string.IsNullOrWhiteSpace(backupRoot))
            {
                return new List<BackupDestination>
                {
                    new BackupDestination
                    {
                        Alias       = "Primary",
                        Path        = backupRoot,
                        Active      = true,
                        PreMounted  = true,
                        AutoMount   = false,
                        AutoUnmount = false
                    }
                };
            }

            return new List<BackupDestination>();
        }

        private sealed record ProjectDestinationSelection(
            List<BackupDestination> Destinations,
            string? WarningMessage,
            string? WarningCode);

        private ProjectDestinationSelection ResolveDestinationsForProject(Project project, AppConfig cfg)
        {
            var activeDestinations = GetActiveDestinations(cfg);
            var allDestinations = GetAllDestinations(cfg);
            var preferredId = project.PreferredDestinationId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(preferredId))
            {
                return new ProjectDestinationSelection(activeDestinations, null, null);
            }

            if (string.Equals(preferredId, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
            {
                return new ProjectDestinationSelection(allDestinations, null, null);
            }

            var match = allDestinations.FirstOrDefault(d =>
                string.Equals(d.Alias ?? string.Empty, preferredId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Path ?? string.Empty, preferredId, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                var message = Lf(
                    "Backups.Notification.PreferredDestinationMissing",
                    "Preferred destination '{0}' not found. Using active destinations.",
                    preferredId);
                return new ProjectDestinationSelection(activeDestinations, message, "preferred_missing");
            }

            if (!match.Active)
            {
                var label = string.IsNullOrWhiteSpace(match.Alias) ? match.Path : match.Alias;
                var message = Lf(
                    "Backups.Notification.PreferredDestinationInactive",
                    "Preferred destination '{0}' is inactive. Using active destinations.",
                    label);
                return new ProjectDestinationSelection(activeDestinations, message, "preferred_inactive");
            }

            return new ProjectDestinationSelection(new List<BackupDestination> { match }, null, null);
        }

        private void RefreshDestinationStatusOverview()
        {
            QueueDestinationOverviewRefresh(_backupsViewModel);
        }

        public object? CurrentView
        {
            get => _currentView;
            set
            {
                if (!Equals(_currentView, value))
                {
                    _currentView = value;
                    OnPropertyChanged(nameof(CurrentView));
                }
            }
        }

        public string CurrentViewKey
        {
            get => _currentViewKey;
            private set
            {
                if (_currentViewKey != value)
                {
                    _currentViewKey = value;
                    OnPropertyChanged(nameof(CurrentViewKey));
                }
            }
        }

        public void EnsureInitialView()
        {
            if (CurrentView is not null)
                return;

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(EnsureInitialView);
                return;
            }

            SetCurrentView("Dashboard", remember: false);
        }

        public string HeaderTitle
        {
            get => _headerTitle;
            set
            {
                if (_headerTitle != value)
                {
                    _headerTitle = value;
                    OnPropertyChanged(nameof(HeaderTitle));
                }
            }
        }

        public string HeaderKicker
        {
            get => _headerKicker;
            set
            {
                if (_headerKicker != value)
                {
                    _headerKicker = value;
                    OnPropertyChanged(nameof(HeaderKicker));
                }
            }
        }

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set => SetField(ref _isUpdateAvailable, value);
        }

        public bool ShowUpdateBanner => IsUpdateAvailable && !_isUpdateBannerDismissed;

        public string UpdateBannerMessage
        {
            get => _updateBannerMessage;
            private set => SetField(ref _updateBannerMessage, value);
        }

        public string UpdateTooltip => string.IsNullOrWhiteSpace(_updateReleaseNotes)
            ? L("Shell.OpenReleaseTooltip", "Open the latest release on GitHub")
            : _updateReleaseNotes;

        public bool IsPatchAvailable => (_pendingUpdateResult?.HasPatch ?? false) && !_patchBlocked;

        public bool ShowPatchButton => IsPatchAvailable;

        public bool ShowInstallerFallback =>
            _pendingUpdateResult is not null &&
            (!IsPatchAvailable || _patchFailed || _patchBlocked);

        public bool CanSkipUpdate => _pendingUpdateResult is not null;

        public string InstallButtonText => IsPatchInstalling
            ? L("Patch.InstallButton.Preparing", "Preparing patch...")
            : L("Patch.InstallButton.Install", "Install patch");

        public string ReleaseActionText => _pendingUpdateResult?.HasInstaller == true
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

        // Commands used by the shell / main window
        public ICommand NavigateDashboard { get; }
        public ICommand NavigateProjects  { get; }
        public ICommand NavigateBackups   { get; }
        public ICommand NavigateSettings  { get; }

        public OnboardingTourViewModel OnboardingTour { get; }

        public ProjectsViewModel ProjectsViewModel => _projectsViewModel;
        public DashboardViewModel DashboardViewModel => _dashboardViewModel ??= new DashboardViewModel();
        public ICommand OpenReleasePageCommand => _openReleaseCommand;
        public ICommand InstallPatchCommand => _installPatchCommand;
        public ICommand SkipUpdateCommand => _skipUpdateCommand;
        public ICommand DismissUpdateBannerCommand => _dismissUpdateBannerCommand;
        public ICommand DismissSoftCrashBannerCommand => _dismissSoftCrashBannerCommand;
        public ICommand CopySoftCrashLogCommand => _copySoftCrashLogCommand;
        public string CurrentVersionDisplay => $"v{StripBuildMetadata(_currentVersionString)}";
        public string FooterProductDisplay => $"VaultSync · {CurrentVersionDisplay}";
        public string FooterCopyrightDisplay => $"© {DateTime.UtcNow.Year} Flavio Giacchetti";
        public bool ShowSoftCrashBanner => _showSoftCrashBanner;
        public string SoftCrashBannerMessage
        {
            get => _softCrashBannerMessage;
            private set => SetField(ref _softCrashBannerMessage, value);
        }
        public bool CanCopySoftCrashLog => !string.IsNullOrWhiteSpace(_softCrashLogPath);
        public string SoftCrashDismissLabel => L("Errors.SoftCrash.Dismiss", "Dismiss");
        public string SoftCrashCopyLabel => L("Errors.SoftCrash.CopyLogPath", "Copy log path");

        public AppViewModel()
        {
            _currentVersionString = GetCurrentVersionString();

            // 1) Config + DB + services
            _config = AppConfigStore.Load();
            if (string.IsNullOrWhiteSpace(_config.Advanced.Language))
            {
                var systemLang = ResolveSystemLanguageCode(_localizationService);
                _config.Advanced.Language = systemLang;
                AppConfigStore.Save(_config);
            }

            var targetLang = string.IsNullOrWhiteSpace(_config.Advanced.Language)
                ? _localizationService.CurrentLanguage
                : _config.Advanced.Language;
            _localizationService.SetLanguage(targetLang);
            LocalizationProvider.Initialize(_localizationService);
            _localizationService.LanguageChanged += OnLanguageChanged;

            _repo = new SqliteRepository(_config.DbPath ?? string.Empty);
            _ = Task.Run(() =>
            {
                try
                {
                    _repo.EnsureSchema();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB] EnsureSchema failed: {ex.Message}");
                }
            });

            _backupService       = new BackupService(_repo);
            _backupService.BackupRetentionDeleted += OnBackupRetentionDeleted;
            _metadataSyncService = new MetadataSyncService(_repo);
            MetadataSyncService.ProjectColorResolver = project =>
                AvatarColorProvider.GetColor(project.Name, project.RootPath, project.ExternalId);
            MetadataSyncService.ProjectColorApplier = (externalId, color) =>
                AvatarColorProvider.SetColorForExternalId(externalId, color);
            _networkMountService = new NetworkMountService();
            _credentialVault     = CredentialVault.Instance;
            _notificationService = new NotificationService();
            _powerStatusProvider = new PowerStatusProvider();
            _driveHealthService  = new DriveHealthService();

            // 2) Section viewmodels
            _dashboardViewModel = null;
            _projectsViewModel  = new ProjectsViewModel();
            _backupsViewModel   = null;
            _settingsViewModel  = new SettingsViewModel(_localizationService);
            _settingsViewModel.PropertyChanged += OnSettingsChanged;
            _settingsViewModel.OpenLogConsoleRequested += OnOpenLogConsoleRequested;
            _settingsViewModel.UpdateCheckRequested += OnUpdateCheckRequested;
            _settingsViewModel.RefreshHistoryRequested += OnRefreshHistoryRequested;
            _settingsViewModel.UpdateUpdateCheckStatus(null, null);
            _settingsViewModel.Destinations.CollectionChanged += OnDestinationsCollectionChanged;
            foreach (var dest in _settingsViewModel.Destinations)
            {
                TrackDestinationViewModel(dest);
            }

            _logConsoleService = new LogConsoleService();
            LogConsoleProvider.Initialize(_logConsoleService);
            UpdateLogConsoleSettings();
            ScheduleLogCaptureInstall();

            _ = Task.Run(() => CleanupIncompleteBackupsOnStartup());
            _ = Task.Run(() => EnforceRetentionOnStartup());

            // 3) BackupsViewModel is created lazily; wiring happens when instantiated.

            // 4) Initial load is deferred until views are shown to reduce startup impact.
            RefreshDestinationStatusOverview();

            // 5) Default route
            // Default route (may be overridden by resume-last-session)
            SetCurrentView("Dashboard", remember: false);

            if (_config.ResumeLastSession)
            {
                ApplyLastSessionView();
            }

            // Ensure launch-on-login matches config
            _ = Task.Run(() => AutoStartService.SetLaunchOnLogin(_config.Behavior.LaunchOnLogin));
            ConfigureAutoBackupTimer();

            // 6) Navigation commands (using cached VMs)
            NavigateDashboard = new RelayCommand(_ => SetCurrentView("Dashboard"));
            NavigateProjects  = new RelayCommand(_ => SetCurrentView("Projects"));
            NavigateBackups   = new RelayCommand(_ => SetCurrentView("Backups"));
            NavigateSettings  = new RelayCommand(_ => SetCurrentView("Settings"));

            OnboardingTour = new OnboardingTourViewModel(this);
            _openReleaseCommand = new RelayCommand(_ => _ = OpenUpdateReleaseAsync(), _ => IsReleaseActionEnabled);
            _installPatchCommand = new RelayCommand(
                _ => _ = StartPatchInstallAsync(),
                _ => IsPatchAvailable && !IsPatchInstalling);
            _skipUpdateCommand = new RelayCommand(_ => SkipUpdateVersion());
            _dismissUpdateBannerCommand = new RelayCommand(_ => DismissUpdateBanner());
            _dismissSoftCrashBannerCommand = new RelayCommand(_ => DismissSoftCrashBanner());
            _copySoftCrashLogCommand = new RelayCommand(_ => _ = CopySoftCrashLogAsync(), _ => CanCopySoftCrashLog);

            StartDeferredStartupTasks();
        }

        private BackupsViewModel CreateBackupsViewModel()
        {
            var vm = new BackupsViewModel();
            vm.BackupProjectRequested += OnBackupProjectRequested;
            vm.CreateBackupForAllProjectsRequested += OnCreateBackupForAllProjectsRequested;
            vm.DeleteBackupRequested += OnDeleteBackupRequested;
            vm.RestoreBackupRequested += OnRestoreBackupRequested; // stub for later
            vm.OpenBackupFolderRequested += OnOpenBackupFolderRequested;
            vm.CancelActiveBackupRequested += OnCancelActiveBackupRequested;
            vm.AutoBackupPreferenceChanged += OnAutoBackupPreferenceChanged;
            vm.BackupProtectionChanged += OnBackupProtectionChanged;
            vm.DestinationActiveChanged += OnDestinationActiveChanged;
            vm.PreferredDestinationChanged += OnPreferredDestinationChanged;
            vm.OpenSettingsRequested += OnOpenSettingsRequested;
            InitializeDestinationStatusOverview(vm);
            return vm;
        }

        private void InitializeDestinationStatusOverview(BackupsViewModel vm)
        {
            var cfg = _config;
            var destinations = GetAllDestinations(cfg);
            var allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
            vm.ResetDestinationStatuses(destinations, allowToggle);
            QueueDestinationOverviewRefresh(vm);
        }

        private void QueueConfigReload(Action<AppConfig> apply, string context)
        {
            if (Interlocked.Exchange(ref _configReloadInFlight, 1) == 1)
            {
                Interlocked.Exchange(ref _configReloadQueued, 1);
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    Dispatcher.UIThread.Post(() => apply(cfg));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Config] Failed to reload for {context}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _configReloadInFlight, 0);
                    if (Interlocked.Exchange(ref _configReloadQueued, 0) == 1)
                    {
                        QueueConfigReload(apply, context);
                    }
                }
            });
        }

        private void QueueDestinationOverviewRefresh(BackupsViewModel? vm)
        {
            if (vm is null)
                return;

            if (Interlocked.Exchange(ref _destinationOverviewRefreshInFlight, 1) == 1)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    var destinations = GetAllDestinations(cfg);
                    var allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
                    vm.ResetDestinationStatuses(destinations, allowToggle);

                    EnsureDestinationProbeStarted();
                    _ = Task.Run(ProbeDestinationsAsync);

                    var summaries = GetDestinationProbeSummaries(cfg);
                    foreach (var summary in summaries)
                    {
                        var severity = summary.Reachable ? "Success" : "Error";
                        vm.UpdateDestinationStatus(summary.Id, summary.Message, severity);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Destinations] Failed to refresh overview: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _destinationOverviewRefreshInFlight, 0);
                }
            });
        }

        public void NotifySoftCrashBanner(string? logPath)
        {
            SoftCrashBannerMessage = L(
                "Errors.SoftCrash.Message",
                "VaultSync hit an unexpected error but kept running. A log was saved.");
            _softCrashLogPath = logPath;
            _showSoftCrashBanner = true;
            OnPropertyChanged(nameof(ShowSoftCrashBanner));
            OnPropertyChanged(nameof(CanCopySoftCrashLog));
            _copySoftCrashLogCommand.RaiseCanExecuteChanged();
        }

        private void DismissSoftCrashBanner()
        {
            _showSoftCrashBanner = false;
            OnPropertyChanged(nameof(ShowSoftCrashBanner));
        }

        private async Task CopySoftCrashLogAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_softCrashLogPath))
                    return;

                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                    lifetime.MainWindow?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(_softCrashLogPath);
                }
            }
            catch
            {
                // Best effort: ignore clipboard failures.
            }
        }

        private ICommand CreateCopyLogSnippetCommand(string contextLabel)
        {
            return new RelayCommand(async _ =>
            {
                var snippet = _logConsoleService.GetRecentSnippet(30, contextLabel);
                if (string.IsNullOrWhiteSpace(snippet))
                    return;

                await ClipboardHelper.TryCopyAsync(snippet);
            });
        }

        private void EnforceRetentionOnStartup()
        {
            try
            {
                var cfg = _config;
                var maxToKeep = cfg.Backups.MaxSnapshotsPerProject;
                if (maxToKeep <= 0)
                    return;

                var backupRoot = cfg.Backups.BackupLocation ?? string.Empty;
                var projects = _repo.GetAllProjects().ToList();
                foreach (var project in projects)
                {
                    _backupService.EnforceRetentionForProject(project.Id, backupRoot, maxToKeep);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupRetention] Startup retention failed: {ex.Message}");
            }
        }

        private void OnBackupRetentionDeleted(Backup backup)
        {
            if (backup is null)
                return;

            if (string.IsNullOrWhiteSpace(backup.ExternalId))
                return;

            if (string.IsNullOrWhiteSpace(backup.DestinationPath))
                return;

            var machineId = Environment.MachineName;
            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    if (!cfg.Backups.EnableMetadataSync)
                        return;

                    Console.WriteLine($"[MetadataSync] Export tombstone for backup {backup.Id} -> '{backup.DestinationPath}'.");
                    _metadataSyncService.ExportBackupTombstoneToStore(
                        backup.DestinationPath,
                        backup.ExternalId,
                        _currentVersionString,
                        machineId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Export tombstone failed for backup {backup.Id}: {ex.Message}");
                }
            });
        }

        private void CleanupIncompleteBackupsOnStartup()
        {
            try
            {
                var cfg = _config;
                var destinations = GetActiveDestinations(cfg);
                if (destinations.Count == 0)
                    return;

                foreach (var dest in destinations)
                {
                    var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                        ? null
                        : cfg.Network.Credentials.FirstOrDefault(c =>
                            c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                    var resolution = _networkMountService.PrepareDestination(dest, profile);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    var projects = _repo.GetAllProjects();
                    var projectFolders = projects
                        .Select(p => BackupService.GetProjectBackupFolderName(p.Name))
                        .ToList();
                    var removed = _backupService.CleanupIncompleteBackups(resolution.EffectivePath, projectFolders);
                    if (removed > 0)
                    {
                        Console.WriteLine($"[BackupCleanup] Removed {removed} incomplete backup(s) under '{resolution.EffectivePath}'.");
                    }

                    _networkMountService.Cleanup(resolution);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupCleanup] Startup cleanup failed: {ex.Message}");
            }
        }

        public void AttachBackupWidgetService(IBackupWidgetService? service)
        {
            _backupWidgetService = service;
        }

        // ---------- Backups wiring ----------

        private void ReloadBackupsVmData()
        {
            _ = ReloadBackupsVmDataAsync(force: false);
        }

        private Task ReloadBackupsVmDataAsync(bool force)
        {
            if (Interlocked.Exchange(ref _reloadBackupsInFlight, 1) == 1)
            {
                Interlocked.Exchange(ref _reloadBackupsQueued, 1);
                return Task.CompletedTask;
            }

            // Fetch and materialize data off the UI thread to reduce perceived hangs,
            // then marshal the lightweight ViewModel update back to the UI thread.
            return Task.Run(() =>
            {
                try
                {
                    _repo.EnsureSchema();
                    var onBackupsPage = IsOnBackupsPage;
                    var now = DateTime.UtcNow;
                    var cacheFresh = _backupsCacheProjects is not null
                        && _backupsCacheBackups is not null
                        && (now - _backupsCacheUpdatedUtc) < BackupsCacheTtl;

                    if (!force && !onBackupsPage && cacheFresh)
                    {
                        return;
                    }

                    var projects = _repo.GetAllProjects().ToList();
                    var useLightweight = !force && !onBackupsPage;
                    var backups = useLightweight
                        ? _repo.GetRecentBackupsByProject(limitPerProject: 5).ToList()
                        : _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();

                    var disabledAuto = _config.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();

                    _backupsCacheProjects = projects;
                    _backupsCacheBackups = backups;
                    _backupsCacheDisabledAuto = disabledAuto;
                    _backupsCacheUpdatedUtc = DateTime.UtcNow;
                    _backupsCachePartial = useLightweight;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (onBackupsPage || force)
                        {
                            BackupsViewModel.LoadFromBackups(projects, backups, disabledAuto);
                            BackupsViewModel.RefreshBackupDriveHealth();
                        }
                    });

                    if (onBackupsPage || force)
                    {
                        if (backups.Count > 0)
                        {
                            var scanAdded = ScanDestinationsForUntrackedBackups(projects, backups);
                            if (scanAdded > 0)
                            {
                                backups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();
                                useLightweight = false;
                                _backupsCacheBackups = backups;
                                _backupsCachePartial = useLightweight;
                                _backupsCacheUpdatedUtc = DateTime.UtcNow;

                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (onBackupsPage || force)
                                    {
                                        BackupsViewModel.LoadFromBackups(projects, backups, disabledAuto);
                                        BackupsViewModel.RefreshBackupDriveHealth();
                                    }
                                });
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reloadBackupsInFlight, 0);
                    if (Interlocked.Exchange(ref _reloadBackupsQueued, 0) == 1)
                    {
                        ReloadBackupsVmData();
                    }
                }
            });
        }

        private BackupProjectPreparation CreateManualBackupPreparation(int projectId)
        {
            var cfg = _config;
            var project = _repo.GetProjectById(projectId);
            var selection = project is null
                ? new ProjectDestinationSelection(GetActiveDestinations(cfg), null, null)
                : ResolveDestinationsForProject(project, cfg);
            return new BackupProjectPreparation(cfg, selection.Destinations, project, selection.WarningMessage, selection.WarningCode);
        }

        private int ScanDestinationsForUntrackedBackups(List<Project> projects, List<Backup> backups)
        {
            if (Interlocked.Exchange(ref _destinationScanInFlight, 1) == 1)
                return 0;

            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastDestinationScanUtc) < DestinationScanInterval)
                    return 0;

                _lastDestinationScanUtc = now;

                var cfg = _config;
                var destinations = GetActiveDestinations(cfg);
                if (destinations.Count == 0)
                    return 0;

                var projectBySlug = projects.ToDictionary(
                    p => BackupService.GetProjectBackupFolderName(p.Name),
                    p => p,
                    StringComparer.OrdinalIgnoreCase);

                var existingKeys = BuildExistingBackupKeys(backups);
                var added = 0;

                foreach (var dest in destinations)
                {
                    var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                        ? null
                        : cfg.Network.Credentials.FirstOrDefault(c =>
                            c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                    var resolution = _networkMountService.PrepareDestination(dest, profile);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    var destRoot = resolution.EffectivePath;

                    foreach (var projectEntry in projectBySlug)
                    {
                        var projectFolder = Path.Combine(destRoot, projectEntry.Key);
                        if (!Directory.Exists(projectFolder))
                            continue;

                        foreach (var backupFolder in SafeEnumerateDirectories(projectFolder))
                        {
                            var folderName = Path.GetFileName(backupFolder);
                            if (!TryParseBackupTimestamp(folderName, out var createdUtc))
                                continue;

                            if (!IsBackupFolderComplete(backupFolder))
                                continue;

                            var relativePath = Path.GetRelativePath(destRoot, backupFolder);
                            var key = BuildBackupKey(dest, relativePath);
                            if (existingKeys.Contains(key))
                                continue;

                            var sizeBytes = TryGetArchiveSize(backupFolder);
                            var snapshotId = _repo.CreateSnapshotFromMetadata(
                                string.Empty,
                                projectEntry.Value.Id,
                                createdUtc,
                                0,
                                sizeBytes);

                            var isProtected = IsBackupProtectedOnDisk(backupFolder);
                            var isEncrypted = false;
                            var cryptoDescriptorJson = BackupCryptoDescriptor.PlainMetadataJson;
                            if (BackupArchiveCryptoService.TryReadDescriptor(backupFolder, out var descriptor, out var encrypted))
                            {
                                isEncrypted = encrypted;
                                cryptoDescriptorJson = descriptor.ToMetadataJson(encrypted);
                            }
                            _repo.CreateBackupFromMetadata(
                                string.Empty,
                                projectEntry.Value.Id,
                                snapshotId,
                                createdUtc,
                                "manual",
                                sizeBytes,
                                relativePath,
                                dest.Path ?? destRoot,
                                dest.Alias ?? string.Empty,
                                isProtected,
                                isImported: true,
                                isEncrypted: isEncrypted,
                                cryptoDescriptorJson: cryptoDescriptorJson);

                            existingKeys.Add(key);
                            added++;
                        }
                    }

                    _networkMountService.Cleanup(resolution);
                }

                if (added > 0)
                {
                    Console.WriteLine($"[Backups] Imported {added} untracked backup(s) from destinations.");
                }

                return added;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Backups] Destination scan failed: {ex.Message}");
                return 0;
            }
            finally
            {
                Interlocked.Exchange(ref _destinationScanInFlight, 0);
            }
        }

        private static HashSet<string> BuildExistingBackupKeys(IEnumerable<Backup> backups)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var backup in backups)
            {
                if (string.IsNullOrWhiteSpace(backup.Path))
                    continue;

                if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
                {
                    keys.Add($"{backup.DestinationAlias}|{backup.Path}");
                }

                if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
                {
                    keys.Add($"{backup.DestinationPath}|{backup.Path}");
                }
            }

            return keys;
        }

        private static string BuildBackupKey(BackupDestination dest, string relativePath)
        {
            var key = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias;
            return $"{key}|{relativePath}";
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string root)
        {
            try
            {
                return Directory.EnumerateDirectories(root);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static bool IsBackupFolderComplete(string backupFolder)
        {
            var inProgress = Path.Combine(backupFolder, ".vaultsync_inprogress");
            if (File.Exists(inProgress))
                return false;

            var completed = Path.Combine(backupFolder, ".vaultsync_complete");
            var archive = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
            var encryptedArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (File.Exists(completed) || File.Exists(archive) || File.Exists(encryptedArchive))
                return true;

            try
            {
                return Directory.EnumerateFileSystemEntries(backupFolder)
                    .Any(entry =>
                    {
                        var name = Path.GetFileName(entry);
                        return !name.StartsWith(".vaultsync_", StringComparison.OrdinalIgnoreCase);
                    });
            }
            catch
            {
                return false;
            }
        }

        private static long TryGetArchiveSize(string backupFolder)
        {
            try
            {
                return BackupArchiveCryptoService.GetStoredArchiveSize(backupFolder);
            }
            catch
            {
                // ignore size probe failures
            }

            return 0;
        }

        private static bool IsBackupProtectedOnDisk(string backupFolder)
        {
            try
            {
                var marker = Path.Combine(backupFolder, BackupProtectionMarkerFileName);
                if (File.Exists(marker))
                    return true;
            }
            catch
            {
                return true;
            }

            return !TryWriteProbeFile(backupFolder);
        }

        private static bool TryParseBackupTimestamp(string? folderName, out DateTime createdUtc)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                createdUtc = DateTime.UtcNow;
                return false;
            }

            return DateTime.TryParseExact(
                folderName,
                "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out createdUtc);
        }

        public AppConfig GetConfigSnapshot() => _config;

        private sealed record BackupProjectPreparation(
            AppConfig Config,
            List<BackupDestination> Destinations,
            Project? Project,
            string? DestinationWarning,
            string? DestinationWarningCode);

        private void SetCurrentView(string viewKey, bool remember = true)
        {
            if (_currentView is not null &&
                string.Equals(viewKey, _currentViewKey, StringComparison.OrdinalIgnoreCase))
                return;

            switch (viewKey)
            {
                case "Projects":
                    BackupsViewModel.IsActiveView = false;
                    CurrentView  = _projectsViewModel;
                    HeaderTitle  = L("Nav.Projects", "Projects");
                    HeaderKicker = L("Main.HeaderProjects", "All repositories");
                    break;
                case "Backups":
                    BackupsViewModel.IsActiveView = true;
                    if (_backupsCacheProjects is not null && _backupsCacheBackups is not null)
                    {
                        BackupsViewModel.LoadFromBackups(
                            _backupsCacheProjects,
                            _backupsCacheBackups,
                            _backupsCacheDisabledAuto ?? new HashSet<int>());
                    }
                    var cacheFresh = (DateTime.UtcNow - _backupsCacheUpdatedUtc) < BackupsCacheTtl;
                    if (!cacheFresh || _backupsCachePartial)
                    {
                        _ = ReloadBackupsVmDataAsync(force: true);
                    }
                    else
                    {
                        QueueBackupsWarmLoadIfReady();
                    }
                    RefreshDestinationStatusOverview();
                    BackupsViewModel.RefreshActiveViewState();
                    CurrentView  = BackupsViewModel;
                    HeaderTitle  = L("Nav.Backups", "Backups");
                    HeaderKicker = L("Main.HeaderBackups", "Snapshots & history");
                    break;
                case "Settings":
                    BackupsViewModel.IsActiveView = false;
                    _settingsViewModel.RebindDestinationCredentials();
                    CurrentView  = _settingsViewModel;
                    HeaderTitle  = L("Nav.Settings", "Settings");
                    HeaderKicker = L("Main.HeaderSettings", "Preferences");
                    break;
                default:
                    BackupsViewModel.IsActiveView = false;
                    if (_lastDashboardRefreshUtc == DateTime.MinValue)
                    {
                        EnsureDashboardWarmLoad();
                    }
                    else if ((DateTime.UtcNow - _lastDashboardRefreshUtc) > DashboardRefreshTtl)
                    {
                        QueueDashboardWarmLoadIfReady();
                    }
                    CurrentView  = DashboardViewModel;
                    HeaderTitle  = L("Nav.Dashboard", "Dashboard");
                    HeaderKicker = L("Main.HeaderOverview", "Overview");
                    viewKey      = "Dashboard";
                    break;
            }

            CurrentViewKey = viewKey;

            if (remember)
            {
                var viewToSave = viewKey;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var cfg = AppConfigStore.Load();
                        cfg.LastView = viewToSave;
                        AppConfigStore.Save(cfg);
                        Dispatcher.UIThread.Post(() => _config.LastView = viewToSave);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Config] Failed to persist last view: {ex.Message}");
                    }
                });
            }
        }

        private void EnsureDashboardWarmLoad()
        {
            if (Interlocked.Exchange(ref _dashboardWarmLoadQueued, 1) == 1)
                return;

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (InitialDataLoadDelay > TimeSpan.Zero)
                        await Task.Delay(InitialDataLoadDelay).ConfigureAwait(false);
                    _lastDashboardRefreshUtc = DateTime.UtcNow;
                    await DashboardViewModel.RefreshAsync();
                }
                finally
                {
                    Interlocked.Exchange(ref _dashboardWarmLoadQueued, 0);
                }
            });
        }

        private void QueueDashboardWarmLoadIfReady()
        {
            if (Interlocked.Exchange(ref _dashboardWarmLoadScheduled, 1) == 1)
                return;

            var delay = WarmLoadStartupDelay - (DateTime.UtcNow - _appStartUtc);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay).ConfigureAwait(false);
                    if (CurrentViewKey == "Dashboard")
                    {
                        _lastDashboardRefreshUtc = DateTime.UtcNow;
                        EnsureDashboardWarmLoad();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _dashboardWarmLoadScheduled, 0);
                }
            });
        }

        private void EnsureBackupsWarmLoad()
        {
            if (Interlocked.Exchange(ref _backupsWarmLoadQueued, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                if (InitialDataLoadDelay > TimeSpan.Zero)
                    await Task.Delay(InitialDataLoadDelay).ConfigureAwait(false);
                _ = ReloadBackupsVmDataAsync(force: true);
                Interlocked.Exchange(ref _backupsWarmLoadQueued, 0);
            });
        }

        private void QueueBackupsWarmLoadIfReady()
        {
            if (Interlocked.Exchange(ref _backupsWarmLoadScheduled, 1) == 1)
                return;

            var delay = WarmLoadStartupDelay - (DateTime.UtcNow - _appStartUtc);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay).ConfigureAwait(false);
                    if (CurrentViewKey == "Backups")
                    {
                        EnsureBackupsWarmLoad();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _backupsWarmLoadScheduled, 0);
                }
            });
        }

        private void ApplyLastSessionView()
        {
            _ = Task.Run(() =>
            {
                var cfg = AppConfigStore.Load();
                var last = string.IsNullOrWhiteSpace(cfg.LastView)
                    ? "Dashboard"
                    : cfg.LastView;
                Dispatcher.UIThread.Post(() => SetCurrentView(last, remember: false));
            });
        }

        private void ConfigureAutoBackupTimer()
        {
            _autoBackupTimer?.Dispose();
            _autoBackupTimer = null;

            var intervalMinutes = _config.Backups.IntervalMinutes;
            if (!_config.Backups.EnableAutoBackups || intervalMinutes <= 0)
                return;

            var interval = TimeSpan.FromMinutes(intervalMinutes);

            // Use a wrapper to avoid unobserved exceptions from the async timer callback crashing the process.
            _autoBackupTimer = new Timer(
                _ => _ = SafeRunAutoBackupsAsync(),
                null,
                interval,
                interval);
        }

        private async Task SafeRunAutoBackupsAsync()
        {
            try
            {
                await RunAutoBackupsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppViewModel] Auto-backup timer failed: {ex}");
                Telemetry.Log("auto_backup_timer_failure", b => b.WithException(ex));
            }
        }

        private async Task RunAutoBackupsAsync()
        {
            if (Interlocked.Exchange(ref _autoBackupInFlight, 1) == 1)
                return;

            try
            {
                if (BackupsViewModel.IsBusy)
                {
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", "busy"));
                    return;
                }

                if (ShouldPauseBackupsForBattery(out var pauseReason))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = pauseReason;
                        BackupsViewModel.BusyMessage       = pauseReason;
                    });
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", "battery"));
                    return;
                }

                var preparation = await Task.Run(PrepareAutoBackupRun);
                if (!preparation.IsReady)
                {
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", preparation.FailureCode ?? "preflight_failed"));
                    return;
                }

                var cfg = preparation.Config;
                var disabled = preparation.DisabledProjects;
                var projects = preparation.Projects;

                var useArchiveMode = _settingsViewModel.UseBackupCompression;
                var backupAttempts = 0;
                var backupSucceeded = 0;
                var backupFailed = 0;
                var destinationUnreachable = 0;

                var maxParallel = Math.Max(1, Environment.ProcessorCount);
                using var throttler = new SemaphoreSlim(maxParallel);

                var tasks = projects
                        .Where(p => !disabled.Contains(p.Id))
                        .Select(async project =>
                        {
                            await throttler.WaitAsync();
                            try
                            {
                                var selection = ResolveDestinationsForProject(project, cfg);
                                if (!string.IsNullOrWhiteSpace(selection.WarningMessage))
                                {
                                    Telemetry.Log("auto_backup_destination_fallback", b => b
                                        .WithCode("reason", selection.WarningCode ?? "preferred_destination_fallback")
                                        .WithHashedString("project", project.Name));
                                }

                                if (selection.Destinations.Count == 0)
                                {
                                    Telemetry.Log("auto_backup_skipped", b => b
                                        .WithCode("reason", "no_destination")
                                        .WithHashedString("project", project.Name));
                                    return;
                                }

                                if (cfg.Backups.PromptRestoreAfterImport && project.NeedsRestore)
                                {
                                    MaybeNotifyRestoreRecommended(project);
                                    Telemetry.Log("auto_backup_advisory", b => b
                                        .WithCode("reason", "restore_recommended")
                                        .WithHashedString("project", project.Name));
                                }

                                if (!TryResolveProjectRoot(project, cfg, out var resolvedProject, out var rootError))
                                {
                                    MaybeNotifyProjectRootMissing(project, rootError);
                                    Telemetry.Log("auto_backup_skipped", b => b
                                        .WithCode("reason", "project_root_missing")
                                        .WithHashedString("project", project.Name)
                                        .WithHashedString("projectRoot", project.RootPath));
                                    return;
                                }

                                project = resolvedProject;
                                int? sharedSnapshotId = null;
                                bool metadataWritten = false;

                                var destinationResolutions = new List<(BackupDestination Dest, DestinationResolution Resolution)>();
                                foreach (var dest in selection.Destinations)
                                {
                                    var resolution = PrepareDestination(dest, cfg);
                                    if (!resolution.IsSuccess)
                                    {
                                        Interlocked.Increment(ref destinationUnreachable);
                                        continue;
                                    }

                                    destinationResolutions.Add((dest, resolution));
                                }

                                if (destinationResolutions.Count == 0)
                                {
                                    Telemetry.Log("auto_backup_skipped", b => b
                                        .WithCode("reason", "no_destination")
                                        .WithHashedString("project", project.Name));
                                    return;
                                }

                                try
                                {
                                    foreach (var (dest, resolution) in destinationResolutions)
                                    {
                                        var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                                        if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                                        {
                                            ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                                        }
                                        if (driveDecision.Block)
                                        {
                                            continue;
                                        }

                                        var destLabel = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;

                                        try
                                        {
                                            var archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                                                dest,
                                                cfg,
                                                resolution.EffectivePath,
                                                useArchiveMode,
                                                CancellationToken.None);
                                            Interlocked.Increment(ref backupAttempts);
                                            var isRemoteDestination = IsRemoteDestinationPath(resolution.EffectivePath)
                                                || IsRemoteDestinationPath(dest.Path);
                                            var allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                                            var preferParallelUpload = allowParallelUpload && isRemoteDestination;
                                            if (!allowParallelUpload)
                                            {
                                                Console.WriteLine($"[BackupService] Parallel archive upload disabled by user settings for '{destLabel}'.");
                                            }
                                            var sw = Stopwatch.StartNew();
                                            var backupResult = await _backupService.RunBackupAsync(
                                                project,
                                                resolution.EffectivePath,
                                                isAuto: true,
                                                progressCallback: null,
                                                CancellationToken.None,
                                                useArchiveMode: useArchiveMode,
                                                fullSnapshotHash: _settingsViewModel.UseFullSnapshotHash,
                                                maxSnapshotsToKeep: cfg.Backups.MaxSnapshotsPerProject,
                                                minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent,
                                                preferredFinalBackupRoot: null,
                                                reuseSnapshotId: metadataWritten ? sharedSnapshotId : null,
                                                writeMetadata: !metadataWritten,
                                                destinationPath: resolution.EffectivePath,
                                                destinationAlias: destLabel,
                                                skipIfNoChanges: true,
                                                useRsyncDelta: _settingsViewModel?.UseRsyncDelta ?? false,
                                                useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false,
                                                archiveUploadBufferBytes: archiveUploadBufferBytes,
                                                preferRunnerProgressOnly: isRemoteDestination,
                                                preferParallelArchiveUpload: preferParallelUpload,
                                                useScanCache: _settingsViewModel.EnableScanCache,
                                                aggressiveScanCache: _settingsViewModel.AggressiveScanCache);
                                            sw.Stop();

                                            if (backupResult.SkippedForNoChanges)
                                            {
                                                Telemetry.Log("auto_backup_skipped", b => b
                                                    .WithCode("reason", "no_changes")
                                                    .WithHashedString("project", project.Name)
                                                    .WithHashedString("destinationPath", dest.Path ?? string.Empty));
                                                // Skip the remaining destinations for this project to avoid redundant work.
                                                break;
                                            }

                                            if (!metadataWritten && backupResult.BackupId > 0)
                                            {
                                                metadataWritten = true;
                                                if (!sharedSnapshotId.HasValue)
                                                {
                                                    var created = _repo.GetBackupById(backupResult.BackupId);
                                                    sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                                }
                                            }

                                            if (backupResult.BackupId > 0)
                                            {
                                                Interlocked.Increment(ref backupSucceeded);
                                                RecordBackupThroughput(backupResult.BackupId, sw.Elapsed, useArchiveMode);
                                                TryExportMetadataForBackup(cfg, dest, resolution.EffectivePath, backupResult.BackupId);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Interlocked.Increment(ref backupFailed);
                                            Telemetry.Log("auto_backup_failure", b => b
                                                .WithHashedString("project", project.Name)
                                                .WithHashedString("projectRoot", project.RootPath)
                                                .WithHashedString("destinationPath", dest.Path)
                                                .WithHashedString("destinationAlias", dest.Alias ?? string.Empty)
                                                .WithFlag("useArchiveMode", useArchiveMode)
                                                .WithException(ex));
                                        }
                                    }
                                }
                                finally
                                {
                                    foreach (var (_, resolution) in destinationResolutions)
                                    {
                                        _networkMountService.Cleanup(resolution);
                                    }
                                }

                                if (metadataWritten && sharedSnapshotId.HasValue)
                                {
                                    StartPostBackupHashingAsync(project, sharedSnapshotId.Value);
                                }
                            }
                            finally
                            {
                                throttler.Release();
                            }
                        })
                        .ToList();

                await Task.WhenAll(tasks);

                // Marshal UI collection updates back to the UI thread to avoid cross-thread crashes.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ReloadBackupsVmData();
                    _ = DashboardViewModel.RefreshAsync();
                });

                Telemetry.Log("auto_backup_tick", b => b
                    .WithCount("projects", projects.Count)
                    .WithCount("destinations", GetAllDestinations(cfg).Count)
                    .WithCount("attempts", backupAttempts)
                    .WithCount("succeeded", backupSucceeded)
                    .WithCount("failed", backupFailed)
                    .WithCount("destinationsUnreachable", destinationUnreachable)
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("intervalMinutes", cfg.Backups.IntervalMinutes));
            }
            finally
            {
                Interlocked.Exchange(ref _autoBackupInFlight, 0);
            }
        }

        private void OnAutoBackupPreferenceChanged(int projectId, bool enabled)
        {
            Task.Run(() =>
            {
                try
                {
                    var cfg  = AppConfigStore.Load();
                    var list = cfg.Backups.AutoBackupDisabledProjects ?? new List<int>();
                    if (!enabled)
                    {
                        if (!list.Contains(projectId))
                            list.Add(projectId);
                    }
                    else
                    {
                        list.Remove(projectId);
                    }

                    cfg.Backups.AutoBackupDisabledProjects = list;
                    AppConfigStore.Save(cfg);

                    Dispatcher.UIThread.Post(() =>
                    {
                        _config.Backups.AutoBackupDisabledProjects = list;
                        ConfigureAutoBackupTimer();
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoBackup] Failed to update preference: {ex.Message}");
                }
            });
        }

        private async void OnPreferredDestinationChanged(int projectId, string preferredDestinationId)
        {
            try
            {
                _repo.UpdateProjectPreferredDestination(projectId, preferredDestinationId ?? string.Empty);
                await _projectsViewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Projects] Failed to update preferred destination for project {projectId}: {ex.Message}");
            }
        }

        private void OnDestinationActiveChanged(DestinationStatusItem item, bool isActive)
        {
            Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    if (!cfg.Backups.UseAdvancedDestinations ||
                        cfg.Backups.Destinations is null ||
                        cfg.Backups.Destinations.Count == 0)
                    {
                        return;
                    }

                    var target = new BackupDestination
                    {
                        Path = item.Path,
                        Alias = item.Alias
                    };
                    var destEntry = cfg.Backups.Destinations
                        .FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, target));
                    if (destEntry is null || destEntry.Active == isActive)
                        return;

                    destEntry.Active = isActive;
                    AppConfigStore.Save(cfg);

                    Dispatcher.UIThread.Post(() =>
                    {
                        _config = cfg;
                        if (_settingsViewModel is not null)
                        {
                            var vmDest = _settingsViewModel.Destinations
                                .FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, destEntry));
                            if (vmDest != null)
                            {
                                vmDest.Active = isActive;
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Destinations] Failed to update active flag: {ex.Message}");
                }
            });
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            var propertyName = e.PropertyName ?? string.Empty;
            QueueConfigReload(cfg =>
            {
                _config = cfg;

                if (propertyName is nameof(SettingsViewModel.EnableAutoBackups)
                    or nameof(SettingsViewModel.AutoBackupIntervalMinutes))
                {
                    ConfigureAutoBackupTimer();
                }

                if (propertyName == nameof(SettingsViewModel.CheckForUpdatesOnStartup))
                {
                    StartUpdateCheck();
                    ConfigureUpdateCheckTimer();
                }

                if (propertyName == nameof(SettingsViewModel.BetaChannelEnabled))
                {
                    StartUpdateCheck();
                }

                if (propertyName == nameof(SettingsViewModel.UpdateCheckIntervalMinutes))
                {
                    ConfigureUpdateCheckTimer();
                }

                if (propertyName is nameof(SettingsViewModel.EnableVerboseLogging)
                    or nameof(SettingsViewModel.SaveVerboseLogs))
                {
                    UpdateLogConsoleSettings();
                }

                if (propertyName is nameof(SettingsViewModel.UseAdvancedDestinations))
                {
                    RefreshDestinationOptionSources();
                }

                RefreshDestinationStatusOverview();
            }, "settings-change");
        }

        private void OnDestinationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (BackupDestinationViewModel dest in e.NewItems)
                {
                    TrackDestinationViewModel(dest);
                }
            }

            if (e.OldItems is not null)
            {
                foreach (BackupDestinationViewModel dest in e.OldItems)
                {
                    UntrackDestinationViewModel(dest);
                }
            }

            RefreshDestinationOptionSources();
            RefreshDestinationStatusOverview();
        }

        private void TrackDestinationViewModel(BackupDestinationViewModel dest)
        {
            if (_observedDestinations.Add(dest))
            {
                dest.PropertyChanged += OnDestinationViewModelPropertyChanged;
            }
        }

        private void UntrackDestinationViewModel(BackupDestinationViewModel dest)
        {
            if (_observedDestinations.Remove(dest))
            {
                dest.PropertyChanged -= OnDestinationViewModelPropertyChanged;
            }
        }

        private void OnDestinationViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not BackupDestinationViewModel)
                return;

            if (e.PropertyName is nameof(BackupDestinationViewModel.Alias)
                or nameof(BackupDestinationViewModel.Path)
                or nameof(BackupDestinationViewModel.Active))
            {
                RefreshDestinationOptionSources();
                RefreshDestinationStatusOverview();
            }
        }

        private void RefreshDestinationOptionSources()
        {
            QueueConfigReload(config =>
            {
                _projectsViewModel.RefreshDestinationOptions(config);
                BackupsViewModel.RefreshDestinationOptions(config);
            }, "destinations-options");
        }

        private void UpdateLogConsoleSettings()
        {
            _logConsoleService.Enabled = _settingsViewModel.EnableVerboseLogging;
            _logConsoleService.SaveToFile = _settingsViewModel.EnableVerboseLogging &&
                                            _settingsViewModel.SaveVerboseLogs;
        }

        private void OnOpenLogConsoleRequested()
        {
            DiagnosticsLogger.Record("Log console requested.");
            ShowLogConsole();
        }

        private void OnUpdateCheckRequested()
        {
            DiagnosticsLogger.Record("Manual update check requested.");
            Console.WriteLine("[Update] Manual update check requested.");
            StartUpdateCheck(ignoreSettings: true);
        }

        private void ShowLogConsole()
        {
            if (_logConsoleWindow is not null)
            {
                DiagnosticsLogger.Record("Log console already open; activating.");
                _logConsoleWindow.Activate();
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    DiagnosticsLogger.Record("Installing log capture for macOS.");
                    _logConsoleService.InstallCapture();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LogConsole] Capture install failed: {ex.Message}");
                    DiagnosticsLogger.Record($"Log capture install failed: {ex.GetType().Name} - {ex.Message}");
                }
            }

            DiagnosticsLogger.Record("Creating log console window.");
            var vm = new LogConsoleViewModel(_logConsoleService);
            var window = new LogConsoleWindow(vm);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                window.Show();
            }
            else
            {
                window.Show();
            }

            window.Closed += (_, _) =>
            {
                DiagnosticsLogger.Record("Log console closed.");
                _logConsoleWindow = null;
            };

            _logConsoleWindow = window;
        }

        private void OnLanguageChanged()
        {
            try
            {
                var culture = new CultureInfo(_localizationService.CurrentLanguage);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
                // Ignore culture switch failures to avoid breaking UI refresh.
            }

            Dispatcher.UIThread.Post(() =>
            {
                _projectsViewModel.RefreshLocalization();
                RefreshHeadersForCurrentView();
                RefreshCurrentViewLocalization();
                TrayMenuRefreshRequested?.Invoke();
            });
        }

        private void RefreshHeadersForCurrentView()
        {
            if (CurrentView == _projectsViewModel)
            {
                HeaderTitle  = L("Nav.Projects", "Projects");
                HeaderKicker = L("Main.HeaderProjects", "All repositories");
            }
            else if (CurrentView == _backupsViewModel)
            {
                HeaderTitle  = L("Nav.Backups", "Backups");
                HeaderKicker = L("Main.HeaderBackups", "Snapshots & history");
            }
            else if (CurrentView == _settingsViewModel)
            {
                HeaderTitle  = L("Nav.Settings", "Settings");
                HeaderKicker = L("Main.HeaderSettings", "Preferences");
            }
            else
            {
                HeaderTitle  = L("Nav.Dashboard", "Dashboard");
                HeaderKicker = L("Main.HeaderOverview", "Overview");
            }
        }

        private void RefreshCurrentViewLocalization()
        {
            if (CurrentView == _dashboardViewModel)
            {
                DashboardViewModel.ReapplyLocalization();
            }
            else if (CurrentView == _backupsViewModel)
            {
                ReloadBackupsVmData();
            }
            else if (CurrentView == _projectsViewModel)
            {
                _projectsViewModel.RefreshLocalization();
            }
        }

        private void StartUpdateCheck(bool ignoreSettings = false)
        {
            DiagnosticsLogger.Record($"Update check start (ignoreSettings={ignoreSettings}, channel={CurrentUpdateChannel}).");
            CancelUpdateCheck();
            CancelUpdateRetry();

            if (!ignoreSettings && !_settingsViewModel.CheckForUpdatesOnStartup)
            {
                ClearUpdateState();
                return;
            }

            var now = DateTime.UtcNow;
            if (!ignoreSettings && (now - _lastUpdateCheckUtc) < UpdateCheckMinInterval)
            {
                return;
            }
            _lastUpdateCheckUtc = now;

            _updateCheckCts = new CancellationTokenSource();
            Console.WriteLine($"[Update] Starting update check (channel={CurrentUpdateChannel}).");
            if (OperatingSystem.IsMacOS())
            {
                _logConsoleService.SetUiCaptureEnabled(false);
                _updateCheckLogCaptureSuppressed = 1;
                if (!_updateCheckLogServiceSuppressed)
                {
                    _updateCheckPrevLogEnabled = _logConsoleService.Enabled;
                    _updateCheckPrevSaveToFile = _logConsoleService.SaveToFile;
                    _logConsoleService.Enabled = false;
                    _logConsoleService.SaveToFile = false;
                    _updateCheckLogServiceSuppressed = true;
                }
            }
            _ = Task.Run(() => RunUpdateCheckAsync(_updateCheckCts.Token));
        }

        private void ConfigureUpdateCheckTimer()
        {
            _updateCheckTimer?.Dispose();
            _updateCheckTimer = null;
            CancelUpdateRetry();

            if (!_settingsViewModel.CheckForUpdatesOnStartup)
                return;

            var intervalMinutes = Math.Max(15, _settingsViewModel.UpdateCheckIntervalMinutes);
            var interval = TimeSpan.FromMinutes(intervalMinutes);

            _updateCheckTimer = new Timer(_ =>
            {
                if (!_settingsViewModel.CheckForUpdatesOnStartup)
                    return;

                if (Interlocked.Exchange(ref _updateCheckInFlight, 1) == 1)
                    return;

                try
                {
                    StartUpdateCheck(ignoreSettings: true);
                }
                finally
                {
                    Interlocked.Exchange(ref _updateCheckInFlight, 0);
                }
            }, null, interval, interval);
        }

        private void StartDeferredStartupTasks()
        {
            _ = Task.Run(async () =>
            {
                var delay = OperatingSystem.IsMacOS()
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(2);
                await Task.Delay(delay);

                var cfg = AppConfigStore.Load();
                EnsureDestinationProbeStarted();

                if (cfg.Backups.EnableMetadataSync)
                {
                    TryImportMetadataFromRoot(cfg.ProjectsRoot ?? string.Empty);
                }

                StartUpdateCheck();
                ConfigureUpdateCheckTimer();
            });
        }

        private void ScheduleLogCaptureInstall()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _logConsoleService.InstallCapture();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LogConsole] Capture install failed: {ex.Message}");
                    }
                });
            });
        }


        private async Task StartPatchInstallAsync()
        {
            if (!IsPatchAvailable || _pendingUpdateResult is null || IsPatchInstalling)
                return;

            if (OperatingSystem.IsMacOS() && !CanWriteInstallDir(AppContext.BaseDirectory))
            {
                PatchStatusMessage = L("Patch.Status.ManifestIncompatible", "Patch not available for this version. Use the installer instead.");
                _patchBlocked = true;
                _patchFailed = true;
                NotifyPatchAvailabilityChanged();
                OnPropertyChanged(nameof(ShowInstallerFallback));
                return;
            }

            IsPatchInstalling = true;
            PatchStatusMessage = L("Patch.Status.Downloading", "Downloading patch...");
            _patchFailed = false;
            OnPropertyChanged(nameof(ShowInstallerFallback));

            try
            {
                var plan = await _patchService.PreparePatchAsync(
                    _pendingUpdateResult,
                    _currentVersionString,
                    CancellationToken.None);

                if (plan is null)
                {
                    PatchStatusMessage = L("Patch.Status.ManifestIncompatible", "Patch manifest cannot be applied to this version.");
                    _patchBlocked = true;
                    _patchFailed = true;
                    NotifyPatchAvailabilityChanged();
                    OnPropertyChanged(nameof(ShowInstallerFallback));
                    return;
                }

                var archivePath = await _patchService.DownloadPatchArchiveAsync(
                    plan,
                    (downloaded, total, rate) =>
                    {
                        UpdateDownloadStatus(
                            L("Patch.Status.Downloading", "Downloading patch"),
                            downloaded,
                            total,
                            rate);
                    },
                    CancellationToken.None);
                if (archivePath is null)
                {
                    PatchStatusMessage = L("Patch.Status.DownloadFailed", "Failed to download or verify the patch.");
                    _patchFailed = true;
                    OnPropertyChanged(nameof(ShowInstallerFallback));
                    return;
                }

                PatchStatusMessage = L("Patch.Status.Installing", "Installing patch and restarting...");

                if (!PatchInstallService.TryLaunchPatchInstaller(plan, archivePath, out var error))
                {
                    PatchStatusMessage = L("Patch.Status.InstallFailed", "Failed to start the patch installer.");
                    Debug.WriteLine($"[Patch] Failed to launch helper: {error}");
                    _patchFailed = true;
                    OnPropertyChanged(nameof(ShowInstallerFallback));
                    return;
                }

                ShutdownForPatchInstall();
                return;
            }
            catch (TaskCanceledException)
            {
                PatchStatusMessage = L("Patch.Status.Timeout", "Patch download timed out. Check your connection or use the installer.");
                _patchFailed = true;
                OnPropertyChanged(nameof(ShowInstallerFallback));
            }
            catch (Exception ex)
            {
                PatchStatusMessage = L("Patch.Status.DownloadFailed", "Failed to download or verify the patch.");
                Debug.WriteLine($"[Patch] Install failed: {ex}");
                _patchFailed = true;
                OnPropertyChanged(nameof(ShowInstallerFallback));
            }
            finally
            {
                IsPatchInstalling = false;
            }
        }

        private void ShutdownForPatchInstall()
        {
            Dispatcher.UIThread.Post(() =>
            {
                DiagnosticsLogger.RecordWithStack("Shutdown for patch install requested.");
                App.MarkShuttingDown();
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                else
                {
                    Environment.Exit(0);
                }
            });
        }

        private static bool CanWriteInstallDir(string installDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installDir))
                    return false;

                Directory.CreateDirectory(installDir);
                var testPath = Path.Combine(installDir, $".vaultsync-write-test-{Guid.NewGuid():N}");
                using (new FileStream(testPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
                File.Delete(testPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void NotifyPatchAvailabilityChanged()
        {
            OnPropertyChanged(nameof(IsPatchAvailable));
            OnPropertyChanged(nameof(ShowPatchButton));
            OnPropertyChanged(nameof(ShowInstallerFallback));
            _installPatchCommand.RaiseCanExecuteChanged();
        }

        private async Task RunUpdateCheckAsync(CancellationToken cancellationToken)
        {
            try
            {
                DiagnosticsLogger.Record("Update check running.");
                var result = await _updateService
                    .CheckForUpdateAsync(_currentVersionString, CurrentUpdateChannel, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    Console.WriteLine("[Update] No update available.");
                    RecordUpdateCheckSuccess();
                    Dispatcher.UIThread.Post(ClearUpdateState);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_config.Advanced.SkippedUpdateTag)
                    && string.Equals(result.TagName, _config.Advanced.SkippedUpdateTag, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Update] Update skipped: tag={result.TagName}.");
                    RecordUpdateCheckSuccess();
                    Dispatcher.UIThread.Post(ClearUpdateState);
                    return;
                }

                if (result.HasPatch)
                {
                    var plan = await _patchService
                        .PreparePatchAsync(result, _currentVersionString, cancellationToken)
                        .ConfigureAwait(false);
                    if (plan is null)
                    {
                        _patchBlocked = true;
                        Console.WriteLine("[Update] Patch manifest is not compatible with the current version; hiding patch option.");
                    }
                    else
                    {
                        _patchBlocked = false;
                    }
                }
                else
                {
                    _patchBlocked = false;
                }

                Console.WriteLine($"[Update] Update available: tag={result.TagName}, name={result.ReleaseName}, patch={result.HasPatch}, installer={result.HasInstaller}.");
                RecordUpdateCheckSuccess();
                DiagnosticsLogger.Record($"Update available: tag={result.TagName}, patch={result.HasPatch}, installer={result.HasInstaller}.");
                Dispatcher.UIThread.Post(() => ApplyUpdateResult(result));
            }
            catch (OperationCanceledException)
            {
                DiagnosticsLogger.Record("Update check cancelled.");
            }
            catch (Exception ex)
            {
                // Silently ignore update failures; we don't want to disturb the user.
                DiagnosticsLogger.Record($"Update check failed: {ex.GetType().Name} - {ex.Message}");
                RecordUpdateCheckFailure(ex);
            }
            finally
            {
                _updateCheckCts?.Dispose();
                _updateCheckCts = null;
                if (_updateCheckLogCaptureSuppressed == 1)
                {
                    _updateCheckLogCaptureSuppressed = 0;
                    Dispatcher.UIThread.Post(() =>
                        _logConsoleService.SetUiCaptureEnabled(true, loadSnapshot: false));
                }
                if (_updateCheckLogServiceSuppressed)
                {
                    _updateCheckLogServiceSuppressed = false;
                    _logConsoleService.Enabled = _updateCheckPrevLogEnabled;
                    _logConsoleService.SaveToFile = _updateCheckPrevSaveToFile;
                }
            }
        }

        private void ApplyUpdateResult(UpdateCheckResult result)
        {
            if (App.IsCrashing)
                return;

            IsInstallerDownloading = false;
            _patchFailed = false;
            _isUpdateBannerDismissed = false;
            IsUpdateAvailable = true;
            UpdateBannerMessage = Lf("Update.Banner", "New update available: {0} ({1})", result.ReleaseName, result.TagName);
            SetUpdateReleaseNotes(TrimUpdateReleaseNotes(result.ReleaseNotes));
            _updateReleaseUrl = (result.InstallerUrl ?? result.ReleaseUrl).ToString();
            _pendingUpdateResult = result;
            NotifyPatchAvailabilityChanged();
            PatchStatusMessage = string.Empty;
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(ShowInstallerFallback));
            OnPropertyChanged(nameof(ReleaseActionText));
            OnPropertyChanged(nameof(IsReleaseActionEnabled));
            OnPropertyChanged(nameof(CanSkipUpdate));

            var title = L("Update.Available.Title", "Update available");
            var channelLabel = CurrentUpdateChannel == GitHubReleaseChannel.Beta
                ? L("Update.Channel.Beta", "Beta")
                : L("Update.Channel.Stable", "Stable");
            var message = Lf("Update.Available.MessageChannel", "VaultSync {0} is ready on the {1} channel.", result.TagName, channelLabel);

            GlobalNotificationCenter.Instance.Show(
                message,
                NotificationSeverity.Info,
                title);

            if (ShouldRaiseSystemNotification && !OperatingSystem.IsMacOS())
            {
                GlobalNotificationCenter.Instance.ShowSystem(
                    message,
                    NotificationSeverity.Info,
                    title);
            }
        }

        private void SetUpdateReleaseNotes(string? notes)
        {
            _updateReleaseNotes = notes ?? string.Empty;
            OnPropertyChanged(nameof(UpdateTooltip));
        }

        private static string TrimUpdateReleaseNotes(string? notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
                return string.Empty;

            var normalized = notes.Replace("\r", string.Empty).Trim();
            const int maxChars = 1200;
            if (normalized.Length <= maxChars)
                return normalized;

            return normalized[..maxChars] + "…";
        }

        private void ClearUpdateState()
        {
            IsUpdateAvailable = false;
            UpdateBannerMessage = string.Empty;
            _updateReleaseUrl = string.Empty;
            SetUpdateReleaseNotes(string.Empty);
            _pendingUpdateResult = null;
            _patchBlocked = false;
            _patchFailed = false;
            _isUpdateBannerDismissed = false;
            NotifyPatchAvailabilityChanged();
            PatchStatusMessage = string.Empty;
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(ShowInstallerFallback));
            OnPropertyChanged(nameof(ReleaseActionText));
            OnPropertyChanged(nameof(IsReleaseActionEnabled));
            OnPropertyChanged(nameof(CanSkipUpdate));
        }

        private void SkipUpdateVersion()
        {
            if (_pendingUpdateResult is null)
                return;

            var tag = _pendingUpdateResult.TagName ?? string.Empty;
            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    cfg.Advanced.SkippedUpdateTag = tag;
                    AppConfigStore.Save(cfg);
                    Dispatcher.UIThread.Post(() => _config.Advanced.SkippedUpdateTag = tag);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Update] Failed to persist skipped tag: {ex.Message}");
                }
            });

            ClearUpdateState();
            _isUpdateBannerDismissed = true;
            OnPropertyChanged(nameof(ShowUpdateBanner));
        }

        private void DismissUpdateBanner()
        {
            if (!IsUpdateAvailable)
                return;

            _isUpdateBannerDismissed = true;
            OnPropertyChanged(nameof(ShowUpdateBanner));
        }

        private void CancelUpdateCheck()
        {
            if (_updateCheckCts is null)
                return;

            _updateCheckCts.Cancel();
            _updateCheckCts.Dispose();
            _updateCheckCts = null;
        }

        private void CancelUpdateRetry()
        {
            _updateCheckRetryTimer?.Dispose();
            _updateCheckRetryTimer = null;
        }

        private void RecordUpdateCheckSuccess()
        {
            _lastUpdateCheckAt = DateTimeOffset.Now;
            _lastUpdateCheckError = null;
            CancelUpdateRetry();
            Dispatcher.UIThread.Post(() =>
                _settingsViewModel.UpdateUpdateCheckStatus(_lastUpdateCheckAt, _lastUpdateCheckError));
        }

        private void RecordUpdateCheckFailure(Exception ex)
        {
            _lastUpdateCheckAt = DateTimeOffset.Now;
            _lastUpdateCheckError = ex.Message;
            Console.WriteLine($"[Update] Update check failed: {ex.GetType().Name}: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
                _settingsViewModel.UpdateUpdateCheckStatus(_lastUpdateCheckAt, _lastUpdateCheckError));
            ScheduleUpdateRetry();
        }

        private void ScheduleUpdateRetry()
        {
            if (_updateCheckRetryTimer is not null)
                return;

            var delay = TimeSpan.FromMinutes(5);
            _updateCheckRetryTimer = new Timer(_ =>
            {
                _updateCheckRetryTimer?.Dispose();
                _updateCheckRetryTimer = null;

                if (!_settingsViewModel.CheckForUpdatesOnStartup)
                    return;

                StartUpdateCheck(ignoreSettings: true);
            }, null, delay, Timeout.InfiniteTimeSpan);
        }

        private async Task OpenUpdateReleaseAsync()
        {
            if (IsInstallerDownloading)
                return;

            if (_pendingUpdateResult?.HasInstaller == true && _pendingUpdateResult.InstallerUrl is not null)
            {
                await DownloadAndLaunchInstallerAsync(_pendingUpdateResult.InstallerUrl, _pendingUpdateResult.InstallerName);
                return;
            }

            if (string.IsNullOrWhiteSpace(_updateReleaseUrl))
                return;

            TryOpenUrl(_updateReleaseUrl);
        }

        private async Task DownloadAndLaunchInstallerAsync(Uri installerUrl, string? installerName)
        {
            IsInstallerDownloading = true;
            PatchStatusMessage = L("Update.Installer.Downloading", "Downloading installer...");

            try
            {
                var downloadDir = Path.Combine(Path.GetTempPath(), "VaultSync", "updates");
                Directory.CreateDirectory(downloadDir);

                var fileName = string.IsNullOrWhiteSpace(installerName)
                    ? Path.GetFileName(installerUrl.LocalPath)
                    : installerName;
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = "VaultSync-Installer";
                }

                var tempPath = Path.Combine(downloadDir, $"{fileName}.download");
                var finalPath = Path.Combine(downloadDir, fileName);

                using var response = await s_installerClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using (var contentStream = await response.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await CopyToWithProgressAsync(
                        contentStream,
                        fileStream,
                        totalBytes,
                        (downloaded, total, rate) =>
                        {
                            UpdateDownloadStatus(
                                L("Update.Installer.Downloading", "Downloading installer"),
                                downloaded,
                                total,
                                rate);
                        },
                        CancellationToken.None);
                }

                File.Copy(tempPath, finalPath, overwrite: true);
                File.Delete(tempPath);

                PatchStatusMessage = L("Update.Installer.Launching", "Launching installer...");

                if (!TryLaunchInstaller(finalPath))
                {
                    PatchStatusMessage = L("Update.Installer.LaunchFailed", "Installer downloaded but could not be started.");
                    ShowUpdateError(PatchStatusMessage);
                    return;
                }

                PatchStatusMessage = L("Update.Installer.Launched", "Installer launched. Close VaultSync if prompted.");
            }
            catch (TaskCanceledException)
            {
                PatchStatusMessage = L("Update.Installer.Timeout", "Installer download timed out. Check your connection or open the release page.");
                ShowUpdateError(PatchStatusMessage);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] Installer download failed: {ex}");
                PatchStatusMessage = L("Update.Installer.DownloadFailed", "Failed to download the installer. Open the release page instead.");
                ShowUpdateError(PatchStatusMessage);
            }
            finally
            {
                IsInstallerDownloading = false;
            }
        }

        private static bool TryLaunchInstaller(string installerPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void TryOpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                var message = L("Update.Failed.Message", "Unable to open the release page; visit the GitHub releases manually.");
                ShowUpdateError(message);
            }
        }

        private void ShowUpdateError(string message, string? titleOverride = null)
        {
            var title = titleOverride ?? L("Update.Failed.Title", "Update failed");
            GlobalNotificationCenter.Instance.Show(message, NotificationSeverity.Error, title);
            if (ShouldRaiseSystemNotification)
            {
                GlobalNotificationCenter.Instance.ShowSystem(message, NotificationSeverity.Error, title);
            }
        }

        private static HttpClient CreateInstallerHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VaultSync-Installer/1.0");
            return client;
        }

        private string BuildBackupProgressLabel(string? etaText, string? currentFile, double percent)
        {
            var isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                               etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);
            if (isFinalizing)
            {
                return L("Backups.Status.Finalizing", "Finalizing...");
            }

            if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Uploading", StringComparison.OrdinalIgnoreCase))
            {
                return L("Backups.Stage.Uploading", "Uploading archive");
            }

            if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Compressing", StringComparison.OrdinalIgnoreCase))
            {
                return L("Backups.Stage.Compressing", "Compressing archive");
            }

            if (!string.IsNullOrWhiteSpace(currentFile))
            {
                return currentFile;
            }

            if (percent <= 0.1)
            {
                return L("Backups.Status.Preparing", "Preparing backup...");
            }

            if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Copying", StringComparison.OrdinalIgnoreCase))
            {
                return L("Backups.Stage.Copying", "Copying files");
            }

            if (percent < 100)
            {
                return L("Backups.Status.Running", "Running backup...");
            }

            return L("Backups.Status.Finalizing", "Finalizing...");
        }

        private void UpdateAggregateBackupAllUi(
            ConcurrentDictionary<int, double> progressPerProject,
            ref DateTime lastAggregateUiUpdateUtc,
            string currentFile,
            string etaText)
        {
            if (progressPerProject.IsEmpty)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.BackupProgress    = 0;
                    BackupsViewModel.BackupCurrentFile = L("Backups.Status.Preparing", "Preparing backup...");
                    BackupsViewModel.BackupEtaText     = string.Empty;
                    BackupsViewModel.BusyMessage       = L("Backups.Busy.All", "Backing up all projects...");
                });
                return;
            }

            var avg = progressPerProject.Values.DefaultIfEmpty(0).Average();
            var now = DateTime.UtcNow;
            if (avg < 100 && (now - lastAggregateUiUpdateUtc) < TimeSpan.FromMilliseconds(200))
                return;
            lastAggregateUiUpdateUtc = now;

            string label;
            if (!string.IsNullOrWhiteSpace(currentFile))
            {
                label = currentFile;
            }
            else if (avg <= 0.1)
            {
                label = L("Backups.Status.Preparing", "Preparing backup...");
            }
            else if (avg < 100)
            {
                label = L("Backups.Status.RunningMultiple", "Running backups...");
            }
            else
            {
                label = L("Backups.Status.AllCompleted", "All backups completed");
            }

            Dispatcher.UIThread.Post(() =>
            {
                BackupsViewModel.BackupProgress    = avg;
                BackupsViewModel.BackupCurrentFile = label;
                BackupsViewModel.BackupEtaText     = etaText;
                BackupsViewModel.BusyMessage       = L("Backups.Busy.All", "Backing up all projects...");
            });
        }

        private void UpdateDownloadStatus(string prefix, long downloadedBytes, long? totalBytes, double? bytesPerSecond)
        {
            var totalMb = totalBytes.HasValue && totalBytes.Value > 0
                ? totalBytes.Value / (1024d * 1024d)
                : (double?)null;
            var downloadedMb = downloadedBytes / (1024d * 1024d);
            var rateMb = bytesPerSecond.HasValue && bytesPerSecond.Value > 0
                ? bytesPerSecond.Value / (1024d * 1024d)
                : (double?)null;

            var sizeText = totalMb.HasValue
                ? $"{downloadedMb:0.0}/{totalMb.Value:0.0} MB"
                : $"{downloadedMb:0.0} MB";

            var rateText = rateMb.HasValue
                ? $"{rateMb.Value:0.0} MB/s"
                : L("Update.Download.Waiting", "Waiting for network...");

            var status = $"{prefix} ({sizeText}) - {rateText}";

            if (Dispatcher.UIThread.CheckAccess())
            {
                PatchStatusMessage = status;
            }
            else
            {
                Dispatcher.UIThread.Post(() => PatchStatusMessage = status);
            }
        }

        private static async Task CopyToWithProgressAsync(
            Stream source,
            Stream destination,
            long? totalBytes,
            Action<long, long?, double?>? progress,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 128];
            long totalRead = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long lastBytes = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (progress is null)
                    continue;

                var elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport < TimeSpan.FromMilliseconds(250))
                    continue;

                var deltaBytes = totalRead - lastBytes;
                var deltaTime = (elapsed - lastReport).TotalSeconds;
                var bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;

                progress(totalRead, totalBytes, bytesPerSecond);
                lastReport = elapsed;
                lastBytes = totalRead;
            }

            if (progress is not null)
            {
                var elapsed = stopwatch.Elapsed;
                var deltaBytes = totalRead - lastBytes;
                var deltaTime = (elapsed - lastReport).TotalSeconds;
                var bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;
                progress(totalRead, totalBytes, bytesPerSecond);
            }
        }

        private static string GetCurrentVersionString()
        {
            var assembly = typeof(AppViewModel).Assembly;
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
                return informationalVersion.Trim();

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        private static string StripBuildMetadata(string version)
        {
            var plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }

        private async void OnBackupProjectRequested(ProjectBackupItem? item)
        {
            var trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;
            var start = DateTime.UtcNow;
            var inFlightAdded = false;
            var projectId = 0;

            if (ShouldPauseBackupsForBattery(out var pauseReason))
            {
                BackupsViewModel.BackupCurrentFile = pauseReason;
                BackupsViewModel.BusyMessage       = pauseReason;
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.BatteryPaused"],
                    NotificationSeverity.Warning);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "battery"));
                return;
            }

            if (item is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoProject"],
                    NotificationSeverity.Warning);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "no_project"));
                return;
            }

            if (!int.TryParse(item.Id, out projectId))
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.InvalidProjectId"],
                    NotificationSeverity.Error);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "invalid_project_id"));
                return;
            }

            if (Volatile.Read(ref _backupAllInProgress) == 1)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "backup_all_running")
                    .WithHashedString("project", item.Name));
                return;
            }

            if (BackupsViewModel.IsBusy && Volatile.Read(ref _manualBackupInFlightCount) == 0)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "busy"));
                return;
            }

            var preparation = await Task.Run(() => CreateManualBackupPreparation(projectId));

            if (preparation.Destinations.Count == 0)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoDestination"],
                    NotificationSeverity.Warning);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "no_destination"));
                return; // later: show error in UI
            }

            if (preparation.Project is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.ProjectNotFound"],
                    NotificationSeverity.Error);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "project_not_found"));
                return;
            }

            var cfg              = preparation.Config;
            var destinations     = preparation.Destinations;
            var project          = preparation.Project;
            if (!string.IsNullOrWhiteSpace(preparation.DestinationWarning))
            {
                BackupsViewModel.ShowNotification(preparation.DestinationWarning, "Warning");
                Telemetry.Log("backup_single_destination_fallback", b => b
                    .WithCode("reason", preparation.DestinationWarningCode ?? "preferred_destination_fallback")
                    .WithHashedString("project", project.Name));
            }
            if (!TryResolveProjectRoot(project, cfg, out var resolvedProject, out var rootError))
            {
                MaybeNotifyProjectRootMissing(project, rootError);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "project_root_missing")
                    .WithHashedString("project", project.Name)
                    .WithHashedString("projectRoot", project.RootPath));
                return;
            }
            project = resolvedProject;
            if (cfg.Backups.PromptRestoreAfterImport && project.NeedsRestore)
            {
                MaybeNotifyRestoreRecommended(project);
                Telemetry.Log("backup_single_advisory", b => b
                    .WithCode("reason", "restore_recommended")
                    .WithHashedString("project", project.Name));
            }

            if (!_manualBackupInFlight.TryAdd(projectId, 0))
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "duplicate")
                    .WithHashedString("project", item.Name));
                return;
            }
            inFlightAdded = true;
            var manualCount = Interlocked.Increment(ref _manualBackupInFlightCount);
            var isFirstManual = manualCount == 1;
            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
            var useArchiveMode   = _settingsViewModel.UseBackupCompression;
            Telemetry.Log("backup_single_start", b => b
                .WithHashedString("project", project.Name)
                .WithHashedString("projectRoot", project.RootPath)
                .WithCount("destinations", destinations.Count)
                .WithFlag("useArchiveMode", useArchiveMode));

            if (isFirstManual)
            {
                // Reset progress state
                BackupsViewModel.BackupProgress    = 0;
                BackupsViewModel.BackupCurrentFile = _localizationService["Backups.Notification.Preparing"];
                BackupsViewModel.BackupEtaText     = string.Empty;

                // Reset per-project cards and add this project
                BackupsViewModel.ClearActiveBackups();
            }
            BackupsViewModel.UpdateActiveBackup(
                project.Id.ToString(),
                project.Name,
                0,
                L("Backups.Status.Preparing", "Preparing backup..."),
                string.Empty);
            if (isFirstManual)
            {
                var allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
                var overviewDestinations = GetAllDestinations(cfg);
                BackupsViewModel.ResetDestinationStatuses(overviewDestinations, allowToggle);
                RefreshDestinationStatusOverview();
            }

            BackupsViewModel.IsBusy      = true;
            BackupsViewModel.BusyMessage = isFirstManual
                ? Lf("Backups.Busy.Single", "Backing up {0}...", project.Name)
                : L("Backups.Busy.All", "Backing up all projects...");
            if (trayRun && ShouldShowBackupWidget)
            {
                _backupWidgetService?.ShowForTrayBackup();
            }

            try
            {
                int? sharedSnapshotId  = null;
                bool metadataWritten   = false;
                bool cancelled         = false;
                string? metadataRoot   = null;
                int? metadataBackupId  = null;
                var attempts = 0;
                var succeeded = 0;
                var failed = 0;
                var unreachable = 0;
                var driveBlocked = 0;

                foreach (var dest in destinations)
                {
                    var destId     = DestinationStatusItem.GetId(dest);
                    var resolution = await PrepareDestinationAsync(dest, cfg);
                    if (!resolution.IsSuccess)
                    {
                        BackupsViewModel.UpdateDestinationStatus(destId, resolution.Message, "Error");
                    }

                    if (!resolution.IsSuccess)
                    {
                        unreachable++;
                        Telemetry.Log("backup_single_destination_unreachable", b => b
                            .WithHashedString("project", project.Name)
                            .WithHashedString("destinationPath", dest.Path)
                            .WithHashedString("destinationAlias", dest.Alias ?? string.Empty));
                        continue;
                    }

                    var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                    if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                    {
                        ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                    }
                    if (driveDecision.Block)
                    {
                        driveBlocked++;
                        BackupsViewModel.UpdateDestinationStatus(destId, driveDecision.Message, "Warning");
                        _networkMountService.Cleanup(resolution);
                        continue;
                    }

                    var labelPrefix = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;
                    var archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                        dest,
                        cfg,
                        resolution.EffectivePath,
                        useArchiveMode,
                        CancellationToken.None);

                    try
                    {
                        _ = TryShowPreflightEstimateAsync(project, resolution.EffectivePath, labelPrefix, useArchiveMode, cfg);

                        var backupResult = await Task.Run(async () =>
                        {
                            attempts++;
                            var isRemoteDestination = IsRemoteDestinationPath(resolution.EffectivePath)
                                || IsRemoteDestinationPath(dest.Path);
                            var allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                            var preferParallelUpload = allowParallelUpload && isRemoteDestination;
                            if (!allowParallelUpload)
                            {
                                Console.WriteLine($"[BackupService] Parallel archive upload disabled by user settings for '{labelPrefix}'.");
                            }
                            var sw = Stopwatch.StartNew();
                            var result = await _backupService.RunBackupAsync(
                                project,
                                resolution.EffectivePath,
                                isAuto: false,
                                progressCallback: (percent, currentFile, etaText) =>
                                {
                                    if (_backupCancelRequested.ContainsKey(project.Id))
                                        return;

                                    if (!ShouldUpdateBackupUi(project.Id, percent, etaText))
                                        return;

                                    var isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                                                       etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);
                                    var label = BuildBackupProgressLabel(etaText, currentFile, percent);
                                    if (!string.IsNullOrWhiteSpace(labelPrefix))
                                        label = $"[{labelPrefix}] {label}";

                                    // Update per-project card (used by BackupsView overlay)
                                    BackupsViewModel.UpdateActiveBackup(
                                        project.Id.ToString(),
                                        project.Name,
                                        percent,
                                        label,
                                        etaText,
                                        allowCancel: !isFinalizing,
                                        destinationLabel: labelPrefix);
                                    LogBackupProgress(project.Id, project.Name, percent, label, etaText);

                                    // Keep legacy aggregate fields in sync (if anything else binds to them)
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        BackupsViewModel.BackupProgress    = percent;
                                        BackupsViewModel.BackupCurrentFile = label;
                                        BackupsViewModel.BackupEtaText     = etaText;
                                    });
                                },
                                useArchiveMode: useArchiveMode,
                                fullSnapshotHash: _settingsViewModel.UseFullSnapshotHash,
                                maxSnapshotsToKeep: maxSnapshotsToKeep,
                                minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent,
                                reuseSnapshotId: metadataWritten ? sharedSnapshotId : null,
                                preferredFinalBackupRoot: null,
                                writeMetadata: !metadataWritten,
                                destinationPath: resolution.EffectivePath,
                                destinationAlias: labelPrefix,
                                useRsyncDelta: _settingsViewModel?.UseRsyncDelta ?? false,
                                useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false,
                                archiveUploadBufferBytes: archiveUploadBufferBytes,
                                preferRunnerProgressOnly: isRemoteDestination,
                                preferParallelArchiveUpload: preferParallelUpload,
                                useScanCache: _settingsViewModel.EnableScanCache,
                                aggressiveScanCache: _settingsViewModel.AggressiveScanCache
                            );
                            sw.Stop();

                            if (!metadataWritten && result.BackupId > 0)
                            {
                                metadataWritten  = true;
                                metadataRoot     = resolution.EffectivePath;
                                metadataBackupId = result.BackupId;

                                if (!sharedSnapshotId.HasValue && result.BackupId > 0)
                                {
                                    var created = _repo.GetBackupById(result.BackupId);
                                    sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                }
                            }

                            return (Result: result, Elapsed: sw.Elapsed);
                        });

                        if (backupResult.Result.SkippedForNoChanges)
                        {
                            Telemetry.Log("backup_single_skipped", b => b
                                .WithCode("reason", "no_changes")
                                .WithHashedString("project", project.Name)
                                .WithHashedString("destinationPath", dest.Path ?? string.Empty));
                            break;
                        }

                        if (backupResult.Result.Cancelled)
                        {
                            cancelled = true;
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                100,
                                L("Backups.Status.Cancelled", "Cancelled"),
                                string.Empty,
                                allowCancel: false);
                            Telemetry.Log("backup_single_cancelled", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("destinationPath", dest.Path ?? string.Empty)
                                .WithFlag("useArchiveMode", useArchiveMode));
                            break;
                        }

                        if (backupResult.Result.BackupId > 0)
                        {
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                100,
                                L("Backups.Status.Completed", "Completed"),
                                string.Empty);
                            succeeded++;
                            RecordBackupThroughput(backupResult.Result.BackupId, backupResult.Elapsed, useArchiveMode);
                            TryExportMetadataForBackup(cfg, dest, resolution.EffectivePath, backupResult.Result.BackupId);
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Telemetry.Log("backup_single_cancelled", b => b
                            .WithHashedString("project", project.Name)
                            .WithHashedString("destinationPath", dest.Path)
                            .WithFlag("useArchiveMode", useArchiveMode));
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Telemetry.Log("backup_single_failure", b => b
                            .WithHashedString("project", project.Name)
                            .WithHashedString("projectRoot", project.RootPath)
                            .WithHashedString("destinationPath", dest.Path)
                            .WithHashedString("destinationAlias", dest.Alias ?? string.Empty)
                            .WithFlag("useArchiveMode", useArchiveMode)
                            .WithException(ex));
                    }
                    finally
                    {
                        _networkMountService.Cleanup(resolution);
                    }
                }

                if (cancelled && !metadataWritten)
                {
                    return;
                }

                if (!metadataWritten)
                {
                    throw new InvalidOperationException("No destinations completed successfully.");
                }

                // --- After backup: optional verification / post-hash ---
                var cfgAfter = await Task.Run(AppConfigStore.Load);
                if (metadataRoot is not null)
                {
                    var latest = metadataBackupId.HasValue
                        ? _repo.GetBackupById(metadataBackupId.Value)
                        : _repo.GetLatestBackupForProject(project.Id);

                    if (latest != null)
                    {
                        if (cfgAfter.Backups.VerifyAfterCreate)
                        {
                            StartVerificationAsync(project, latest, metadataRoot, "backup_single_verify_failed");
                        }
                        else
                        {
                            StartPostBackupHashingAsync(project, latest.SnapshotId);
                        }
                    }
                }

                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();

                // Refresh Projects view so the newly created snapshot appears immediately.
                await _projectsViewModel.RefreshAsync();

                Telemetry.Log("backup_single_success", b => b
                    .WithHashedString("project", project.Name)
                    .WithHashedString("projectRoot", project.RootPath)
                    .WithCount("destinations", destinations.Count)
                    .WithCount("attempts", attempts)
                    .WithCount("succeeded", succeeded)
                    .WithCount("failed", failed)
                    .WithCount("destinationsUnreachable", unreachable)
                    .WithCount("driveBlocked", driveBlocked)
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                // Notify success if enabled in settings and globally
                if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                {
                    var msg   = Lf("Backups.Notification.Success", "Backup for '{0}' completed successfully.", project.Name);
                    var title = L("Backups.Notification.SuccessTitle", "Backup completed");

                    _notificationService.ShowInfo(
                        title,
                        msg,
                        NotificationKind.Backup);

                    BackupsViewModel.ShowNotification(
                        Lf("Backups.Notification.Success", "Backup for '{0}' completed successfully.", project.Name),
                        "Info");

                    // Toast only when not already on the Backups page.
                    if (!IsOnBackupsPage)
                    {
                        GlobalNotificationCenter.Instance.Show(
                            msg,
                            NotificationSeverity.Info,
                            title);
                    }

                    if (ShouldRaiseSystemNotification)
                    {
                        GlobalNotificationCenter.Instance.ShowSystem(
                            msg,
                            NotificationSeverity.Info,
                            title);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // User cancelled the backup; keep UI tidy without surfacing an error toast.
                BackupsViewModel.BackupCurrentFile = L("Backups.Notification.Cancelled", "Backup cancelled.");
                BackupsViewModel.BackupEtaText     = string.Empty;
                Telemetry.Log("backup_single_cancelled", b => b
                    .WithHashedString("project", project?.Name ?? string.Empty)
                    .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));
            }
            catch (Exception ex)
            {

                // Detect the low-disk-space condition thrown by BackupService
                var isLowDisk =
                    ex is InvalidOperationException ioe &&
                    ioe.Message.Contains("does not have enough free space", StringComparison.OrdinalIgnoreCase);

                if (isLowDisk)
                {
                    // Low disk space: treat as a skipped backup with a clear warning,
                    // honoring the notifications settings.
                    Telemetry.Log("backup_single_low_disk", b => b
                        .WithHashedString("project", project.Name)
                        .WithHashedString("projectRoot", project.RootPath)
                        .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnLowDiskSpace)
                    {
                        var msg   = Lf("Backups.Notification.LowDiskMessage", "Backup for '{0}' was skipped due to low disk space on the backup target.", project.Name);
                        var title = L("Backups.Notification.LowDiskTitle", "Low disk space");

                        // Always go through the central notification service so we get
                        // consistent logging and behavior.
                        _notificationService.ShowWarning(
                            title,
                            msg,
                            NotificationKind.Backup);

                        if (IsOnBackupsPage)
                        {
                            // When the user is on the Backups page, also show an in-page banner
                            // so the warning is clearly visible where the action happened.
                            BackupsViewModel.ShowNotification(
                                msg,
                                "Warning");
                        }
                        else
                        {
                            // When the user is elsewhere, show a global toast.
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Warning,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Warning,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = L("Backups.Status.LowDisk", "Backup skipped: low disk space.");
                        BackupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                                ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.LowDiskSuffix", "Low disk space");
                    });
                }
                else
                {
                    // Generic backup failure path
                    Telemetry.Log("backup_single_failure", b => b
                        .WithHashedString("project", project.Name)
                        .WithHashedString("projectRoot", project.RootPath)
                        .WithFlag("useArchiveMode", useArchiveMode)
                        .WithException(ex)
                        .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                    if (NotificationsEnabled)
                    {
                        var msg   = Lf("Backups.Notification.FailureMessage", "Backup failed for '{0}'. Check logs for details.", project.Name);
                        var title = L("Backups.Notification.FailureTitle", "Backup failed");
                        var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                        var actionCommand = CreateCopyLogSnippetCommand(
                            Lf("Logs.Snippet.BackupFailure", "Backup failure for '{0}'.", project.Name));

                        if (IsOnBackupsPage)
                        {
                            BackupsViewModel.ShowNotification(msg, "Error", actionLabel, actionCommand);
                        }
                        else
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Error,
                                title,
                                actionLabel: actionLabel,
                                actionCommand: actionCommand);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = L("Backups.Notification.FailureTitle", "Backup failed");
                        BackupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                                ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                    });
                }
            }
            finally
            {
                if (projectId != 0)
                    _backupCancelRequested.TryRemove(projectId, out _);

                if (inFlightAdded)
                {
                    _manualBackupInFlight.TryRemove(projectId, out _);
                    var remaining = Interlocked.Decrement(ref _manualBackupInFlightCount);
                    if (remaining <= 0)
                    {
                        BackupsViewModel.ClearActiveBackups();
                        BackupsViewModel.IsBusy      = false;
                        BackupsViewModel.BusyMessage = string.Empty;
                        TrayMenuRefreshRequested?.Invoke();
                    }
                    else
                    {
                        BackupsViewModel.BusyMessage = L("Backups.Busy.All", "Backing up all projects...");
                    }
                }
            }
        }

        private async void OnCreateBackupForAllProjectsRequested()
        {
            var trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;
            var start = DateTime.UtcNow;

            // Do not start "backup all" if a backup is already running.
            if (BackupsViewModel.IsBusy)
            {
                Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", "busy"));
                return;
            }

            if (ShouldPauseBackupsForBattery(out var pauseReason))
            {
                BackupsViewModel.BackupCurrentFile = pauseReason;
                BackupsViewModel.BusyMessage       = pauseReason;
                Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", "battery"));
                return;
            }


            var preparation = await Task.Run(() => PrepareBackupAll());

            if (!preparation.IsReady)
            {
                Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", preparation.FailureCode ?? "preflight_failed"));
                return;
            }

            if (Interlocked.CompareExchange(ref _backupAllInProgress, 1, 0) == 1)
                return;

            var cfg = preparation.Config!;
            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
            var useArchiveMode = _settingsViewModel.UseBackupCompression;
            Telemetry.Log("backup_all_start", b => b
                .WithCount("destinationsConfigured", GetAllDestinations(cfg).Count)
                .WithFlag("useArchiveMode", useArchiveMode));

            BackupsViewModel.BackupProgress    = 0;
            BackupsViewModel.BackupCurrentFile = L("Backups.Status.Preparing", "Preparing backup...");
            BackupsViewModel.BackupEtaText     = string.Empty;
            BackupsViewModel.IsBusy            = true;
            BackupsViewModel.BusyMessage       = L("Backups.Busy.All", "Backing up all projects...");
            if (trayRun && ShouldShowBackupWidget)
            {
                _backupWidgetService?.ShowForTrayBackup();
            }

            try
            {
                await Task.Run(async () =>
                {
                    var projects = _repo.GetAllProjects().ToList();
                    var results = new ConcurrentBag<(string name, string root, bool success)>();

                    if (projects.Count == 0)
                    {
                        Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", "no_projects"));
                        return;
                    }

                    var progressPerProject = new ConcurrentDictionary<int, double>();
                    var lastAggregateUiUpdateUtc = DateTime.MinValue;

                    // Reset per-project cards and add entry place-holders
                    BackupsViewModel.ClearActiveBackups();
                    foreach (var p in projects)
                    {
                        BackupsViewModel.UpdateActiveBackup(
                            p.Id.ToString(),
                            p.Name,
                            0,
                            L("Backups.Status.Preparing", "Preparing backup..."),
                            string.Empty);
                    }

                    void UpdateAggregateProgress(string currentFile, string etaText)
                        => UpdateAggregateBackupAllUi(progressPerProject, ref lastAggregateUiUpdateUtc, currentFile, etaText);

                    var tasks = projects.Select(project => Task.Run(async () =>
                    {
                        var projectId = project.Id;
                        var selection = ResolveDestinationsForProject(project, cfg);
                        if (!string.IsNullOrWhiteSpace(selection.WarningMessage))
                        {
                            BackupsViewModel.ShowNotification(selection.WarningMessage, "Warning");
                            Telemetry.Log("backup_all_destination_fallback", b => b
                                .WithCode("reason", selection.WarningCode ?? "preferred_destination_fallback")
                                .WithHashedString("project", project.Name));
                        }

                        if (selection.Destinations.Count == 0)
                        {
                            var message = L("Backups.Notification.NoDestination", "Backup could not start: no active destination configured.");
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "no_destination"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    message,
                                    string.Empty);
                            });
                            UpdateAggregateProgress(message, string.Empty);
                            return;
                        }

                        var primaryDest = selection.Destinations[0];
                        var preparedPrimary = PrepareDestination(primaryDest, cfg);
                        if (!preparedPrimary.IsSuccess || string.IsNullOrWhiteSpace(preparedPrimary.EffectivePath))
                        {
                            var message = preparedPrimary.Message;
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "destination_unreachable"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    message,
                                    string.Empty);
                            });
                            UpdateAggregateProgress(message, string.Empty);
                            return;
                        }

                        var backupRoot = preparedPrimary.EffectivePath;
                        var primaryAlias = string.IsNullOrWhiteSpace(primaryDest.Alias)
                            ? primaryDest.Path
                            : primaryDest.Alias ?? primaryDest.Path;
                        var effectiveBackupRoot = backupRoot;
                        if (!TryResolveProjectRoot(project, cfg, out var resolvedProject, out var rootError))
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "project_root_missing"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    rootError,
                                    string.Empty);
                            });
                            UpdateAggregateProgress(rootError, string.Empty);
                            return;
                        }

                        project = resolvedProject;

                        var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, effectiveBackupRoot);
                        if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                        {
                            ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                        }
                        if (driveDecision.Block)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "drive_health"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    driveDecision.Message,
                                    string.Empty);
                            });
                            UpdateAggregateProgress(driveDecision.Message, string.Empty);
                            return;
                        }

                        try
                        {
                            _ = TryShowPreflightEstimateAsync(project, effectiveBackupRoot, primaryAlias, useArchiveMode, cfg);

                            var archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                                primaryDest,
                                cfg,
                                effectiveBackupRoot,
                                useArchiveMode,
                                CancellationToken.None);
                            var isRemoteDestination = IsRemoteDestinationPath(effectiveBackupRoot)
                                || IsRemoteDestinationPath(primaryDest.Path);
                            var allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                            var preferParallelUpload = allowParallelUpload && isRemoteDestination;
                            if (!allowParallelUpload)
                            {
                                Console.WriteLine($"[BackupService] Parallel archive upload disabled by user settings for '{primaryAlias}'.");
                            }
                            var sw = Stopwatch.StartNew();
                            var backupResult = await _backupService.RunBackupAsync(
                                project,
                                effectiveBackupRoot,
                                isAuto: false,
                                progressCallback: (percent, currentFile, etaText) =>
                                {
                                    if (_backupCancelRequested.ContainsKey(project.Id))
                                        return;

                                    if (!ShouldUpdateBackupUi(project.Id, percent, etaText))
                                        return;

                                    // Per-project label for its own card
                                    var isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                                                       etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);

                                    string label;
                                    if (isFinalizing)
                                    {
                                        label = L("Backups.Status.Finalizing", "Finalizing...");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Uploading", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Uploading", "Uploading archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Compressing", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Compressing", "Compressing archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(currentFile))
                                    {
                                        label = currentFile;
                                    }
                                    else if (percent <= 0.1)
                                    {
                                        label = L("Backups.Status.Preparing", "Preparing backup...");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Copying", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Copying", "Copying files");
                                    }
                                    else if (percent < 100)
                                    {
                                        label = L("Backups.Status.Running", "Running backup...");
                                    }
                                    else
                                    {
                                        label = L("Backups.Status.Finalizing", "Finalizing...");
                                    }

                                    progressPerProject[project.Id] = percent;
                                    UpdateAggregateProgress(currentFile, etaText);

                                    // Update that project's card
                                    BackupsViewModel.UpdateActiveBackup(
                                        project.Id.ToString(),
                                        project.Name,
                                        percent,
                                        label,
                                        etaText,
                                        allowCancel: !isFinalizing);
                                    LogBackupProgress(project.Id, project.Name, percent, label, etaText);
                                },
                                useArchiveMode: useArchiveMode,
                                maxSnapshotsToKeep: maxSnapshotsToKeep,
                                minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent,
                                preferredFinalBackupRoot: null,
                                destinationPath: effectiveBackupRoot,
                                destinationAlias: primaryAlias,
                                skipIfNoChanges: true,
                                useRsyncDelta: _settingsViewModel?.UseRsyncDelta ?? false,
                                useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false,
                                archiveUploadBufferBytes: archiveUploadBufferBytes,
                                preferRunnerProgressOnly: isRemoteDestination,
                                preferParallelArchiveUpload: preferParallelUpload,
                                useScanCache: _settingsViewModel.EnableScanCache,
                                aggressiveScanCache: _settingsViewModel.AggressiveScanCache
                            );
                            sw.Stop();

                            if (backupResult.SkippedForNoChanges)
                            {
                                progressPerProject[project.Id] = 100;
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    L("Backups.Status.NoChanges", "No changes detected"),
                                    string.Empty);
                                UpdateAggregateProgress(string.Empty, string.Empty);
                                results.Add((project.Name, project.RootPath, true));
                                Telemetry.Log("backup_all_project_skipped", b => b
                                    .WithHashedString("project", project.Name)
                                    .WithHashedString("projectRoot", project.RootPath)
                                    .WithCode("reason", "no_changes"));
                                return;
                            }

                            if (backupResult.Cancelled)
                            {
                                results.Add((project.Name, project.RootPath, false));
                                Telemetry.Log("backup_all_project_cancelled", b => b
                                    .WithHashedString("project", project.Name)
                                    .WithHashedString("projectRoot", project.RootPath));
                                progressPerProject[project.Id] = 0;
                                UpdateAggregateProgress(string.Empty, string.Empty);
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    L("Backups.Status.Cancelled", "Cancelled"),
                                    string.Empty,
                                    allowCancel: false);
                                return;
                            }

                              progressPerProject[project.Id] = 100;
                              UpdateAggregateProgress(string.Empty, string.Empty);
                              BackupsViewModel.UpdateActiveBackup(
                                  project.Id.ToString(),
                                  project.Name,
                                  100,
                                  L("Backups.Status.Completed", "Completed"),
                                  string.Empty);
                              results.Add((project.Name, project.RootPath, backupResult.BackupId > 0));
                              if (backupResult.BackupId > 0)
                              {
                                  RecordBackupThroughput(backupResult.BackupId, sw.Elapsed, useArchiveMode);
                                  TryExportMetadataForBackup(cfg, primaryDest, effectiveBackupRoot, backupResult.BackupId);
                              }
                            Telemetry.Log("backup_all_project_success", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithFlag("useArchiveMode", useArchiveMode));
                        }
                        catch (OperationCanceledException)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_cancelled", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath));
                            progressPerProject[project.Id] = 0;
                            UpdateAggregateProgress(string.Empty, string.Empty);
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                0,
                                L("Backups.Status.Cancelled", "Cancelled"),
                                string.Empty,
                                allowCancel: false);
                            return;
                        }
                        catch (Exception ex)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_failure", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithFlag("useArchiveMode", useArchiveMode)
                                .WithException(ex));
                            throw;
                        }
                        finally
                        {
                            _backupCancelRequested.TryRemove(projectId, out _);
                        }
                    })).ToList();

                    await Task.WhenAll(tasks);

                    Telemetry.Log("backup_all_success", b => b
                        .WithCount("projects", projects.Count)
                        .WithCount("succeeded", results.Count(r => r.success))
                        .WithCount("failed", results.Count(r => !r.success))
                        .WithFlag("useArchiveMode", useArchiveMode)
                        .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));
                });

                // First reload history so the new backups appear.
                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();

                // --- After all backups: optional verification / post-hash ---
                var cfgAfterAll = await Task.Run(AppConfigStore.Load);
                var allDestinations = GetAllDestinations(cfgAfterAll);
                var allLatest = _repo.GetLatestBackupsPerProject();
                var projectsById = _repo.GetAllProjects()
                    .GroupBy(p => p.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var latest in allLatest)
                {
                    if (!projectsById.TryGetValue(latest.ProjectId, out var proj))
                        continue;
                    if (proj == null)
                        continue;

                    if (cfgAfterAll.Backups.VerifyAfterCreate)
                    {
                        var destinationRoot = ResolveDestinationRootForBackup(
                            latest,
                            allDestinations,
                            cfgAfterAll.Backups.BackupRoot);
                        StartVerificationAsync(proj, latest, destinationRoot ?? string.Empty, "backup_all_verify_failed");
                    }
                    else
                    {
                        StartPostBackupHashingAsync(proj, latest.SnapshotId);
                    }
                }

                // Then clear the active backup cards on the UI thread,
                // so the overlay collapses only after history is updated.
                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.ClearActiveBackups();

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                    {
                        var msg   = L("Backups.Notification.AllSuccess", "All project backups completed successfully.");
                        var title = L("Backups.Notification.AllSuccessTitle", "Backups completed");

                        _notificationService.ShowInfo(
                            title,
                            msg,
                            NotificationKind.Backup);

                        BackupsViewModel.ShowNotification(
                            msg,
                            "Info");

                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Info,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Info,
                                title);
                        }
                    }
                });
            }
            catch (Exception ex)
            {

                Telemetry.Log("backup_all_failure", b => b
                    .WithException(ex)
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                if (NotificationsEnabled)
                {
                    var msg   = L("Backups.Notification.AllFailureMessage", "Backup all projects failed. Check logs for details.");
                    var title = L("Backups.Notification.AllFailureTitle", "Backup-all failed");
                    var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                    var actionCommand = CreateCopyLogSnippetCommand(
                        L("Logs.Snippet.BackupAllFailure", "Backup-all failure."));

                    if (IsOnBackupsPage)
                    {
                        BackupsViewModel.ShowNotification(msg, "Error", actionLabel, actionCommand);
                    }
                    else
                    {
                        GlobalNotificationCenter.Instance.Show(
                            msg,
                            NotificationSeverity.Error,
                            title,
                            actionLabel: actionLabel,
                            actionCommand: actionCommand);
                    }

                    if (ShouldRaiseSystemNotification)
                    {
                        GlobalNotificationCenter.Instance.ShowSystem(
                            msg,
                            NotificationSeverity.Error,
                            title);
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.BackupCurrentFile = L("Backups.Notification.AllFailureTitle", "Backup-all failed");
                    BackupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                            ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                });

                // Clear cards on failure (ensure this runs on the UI thread)
                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.ClearActiveBackups();
                });
            }
            finally
            {
                BackupsViewModel.IsBusy      = false;
                BackupsViewModel.BusyMessage = string.Empty;

                TrayMenuRefreshRequested?.Invoke();

                Interlocked.Exchange(ref _backupAllInProgress, 0);
            }
        }

        private async Task TryShowPreflightEstimateAsync(Project project, string backupRoot, string? labelPrefix, bool useArchiveMode, AppConfig cfg)
        {
            try
            {
                var throughput = useArchiveMode
                    ? cfg.Backups.LastBackupThroughputArchiveMbSec
                    : cfg.Backups.LastBackupThroughputCopyMbSec;
                if (throughput <= 0)
                {
                    throughput = cfg.Backups.LastBackupThroughputMbSec;
                }
                var preflight = await Task.Run(
                        () => _backupService.PreflightBackupAsync(
                            project,
                            backupRoot,
                            CancellationToken.None,
                            throughputMbSec: throughput,
                            useArchiveMode: useArchiveMode,
                            cacheTtl: TimeSpan.FromSeconds(45)))
                    .ConfigureAwait(false);

                var sizeLabel = BackupSnapshotItem.FormatSize(preflight.TotalBytes);
                var estimateLabel = string.Empty;
                var etaText = FormatEta(preflight.EstimatedSeconds);
                if (!string.IsNullOrWhiteSpace(etaText))
                {
                    estimateLabel = Lf(
                        "Backups.Preflight.Message",
                        "Estimated {0} files, {1} total, ETA {2}.",
                        preflight.TotalFiles,
                        sizeLabel,
                        etaText);
                }
                else
                {
                    estimateLabel = Lf(
                        "Backups.Preflight.MessageNoEta",
                        "Estimated {0} files, {1} total.",
                        preflight.TotalFiles,
                        sizeLabel);
                }

                if (!string.IsNullOrWhiteSpace(labelPrefix))
                {
                    estimateLabel = $"[{labelPrefix}] {estimateLabel}";
                }

                var projectId = project.Id.ToString();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var active = BackupsViewModel.ActiveBackups.FirstOrDefault(p => p.ProjectId == projectId);
                    if (active is null || active.Progress <= 0.1d)
                    {
                        BackupsViewModel.UpdateActiveBackup(
                            projectId,
                            project.Name,
                            0,
                            L("Backups.Progress.Estimating", "Estimating..."),
                            estimateLabel,
                            allowCancel: true);
                    }

                    if (!preflight.HasEnoughSpace && preflight.VolumeFreeBytes.HasValue)
                    {
                        var freeLabel = BackupSnapshotItem.FormatSize(preflight.VolumeFreeBytes.Value);
                        var warning = Lf(
                            "Backups.Preflight.LowDisk",
                            "Backup may not fit on the destination. Free space: {0}.",
                            freeLabel);

                        BackupsViewModel.ShowNotification(warning, "Warning");
                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                warning,
                                NotificationSeverity.Warning,
                                L("Backups.Preflight.Title", "Backup estimate"));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Backup] Preflight estimate failed: {ex.Message}");
            }
        }

        private static string FormatEta(double? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0)
                return string.Empty;

            var eta = TimeSpan.FromSeconds(seconds.Value);
            if (eta.TotalHours >= 1)
                return $"{(int)eta.TotalHours}h {eta.Minutes}m";

            return eta.ToString(@"mm\:ss");
        }

        private void StartPostBackupHashingAsync(Project project, int snapshotId)
        {
            _ = Task.Run(async () =>
            {
                Console.WriteLine($"[Backup] Post-hash start: project='{project.Name}', snapshotId={snapshotId}");
                try
                {
                    var snapshotService = new SnapshotService(_repo, new HashService());
                    var hashed = await snapshotService.HashMissingFilesAsync(project, snapshotId, CancellationToken.None);
                    Telemetry.Log("backup_post_hash_complete", b => b
                        .WithHashedString("project", project.Name)
                        .WithCount("hashedFiles", hashed));
                    Console.WriteLine($"[Backup] Post-hash complete: project='{project.Name}', hashedFiles={hashed}");
                }
                catch (Exception ex)
                {
                    Telemetry.Log("backup_post_hash_failed", b => b
                        .WithHashedString("project", project.Name)
                        .WithException(ex));
                    Console.WriteLine($"[Backup] Post-hash failed: project='{project.Name}', error={ex.Message}");
                }
            });
        }

        private void RecordBackupThroughput(int backupId, TimeSpan elapsed, bool useArchiveMode)
        {
            try
            {
                if (backupId <= 0)
                    return;

                if (elapsed.TotalSeconds <= 1)
                    return;

                var backup = _repo.GetBackupById(backupId);
                if (backup is null || backup.TotalBytes <= 0)
                    return;

                var mbSec = backup.TotalBytes / (1024d * 1024d) / elapsed.TotalSeconds;
                if (double.IsNaN(mbSec) || double.IsInfinity(mbSec) || mbSec <= 0)
                    return;

                _ = Task.Run(() =>
                {
                    try
                    {
                        var cfg = AppConfigStore.Load();
                        var existing = useArchiveMode
                            ? cfg.Backups.LastBackupThroughputArchiveMbSec
                            : cfg.Backups.LastBackupThroughputCopyMbSec;
                        var blended = existing > 0 ? (existing * 0.7 + mbSec * 0.3) : mbSec;
                        var rounded = Math.Round(blended, 2);
                        if (useArchiveMode)
                        {
                            cfg.Backups.LastBackupThroughputArchiveMbSec = rounded;
                        }
                        else
                        {
                            cfg.Backups.LastBackupThroughputCopyMbSec = rounded;
                        }
                        cfg.Backups.LastBackupThroughputMbSec = rounded;
                        AppConfigStore.Save(cfg);

                        Dispatcher.UIThread.Post(() =>
                        {
                            _config.Backups.LastBackupThroughputArchiveMbSec = cfg.Backups.LastBackupThroughputArchiveMbSec;
                            _config.Backups.LastBackupThroughputCopyMbSec = cfg.Backups.LastBackupThroughputCopyMbSec;
                            _config.Backups.LastBackupThroughputMbSec = cfg.Backups.LastBackupThroughputMbSec;
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Backup] Failed to persist throughput: {ex.Message}");
                    }
                });
            }
            catch
            {
                // best-effort only; ignore throughput persistence errors
            }
        }

        private void StartVerificationAsync(Project project, Backup latest, string backupRoot, string telemetryEvent)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[Backup] Verification start: project='{project.Name}', backupId={latest.Id}, snapshotId={latest.SnapshotId}");
                    var snapshotService = new SnapshotService(_repo, new HashService());
                    await snapshotService.HashMissingFilesAsync(project, latest.SnapshotId, CancellationToken.None);

                    var verifyService = new VerifyService(_repo, new HashService());
                    var folder = Path.Combine(backupRoot, latest.Path ?? string.Empty);
                    await verifyService.VerifyAsync(project, folder, 100, full: true);
                    Console.WriteLine($"[Backup] Verification complete: project='{project.Name}', backupId={latest.Id}");
                }
                catch (Exception vex)
                {
                    Telemetry.Log(telemetryEvent, b => b
                        .WithHashedString("project", project.Name)
                        .WithHashedString("projectRoot", project.RootPath)
                        .WithHashedString("destinationPath", backupRoot)
                        .WithException(vex));
                    Console.WriteLine($"[Backup] Verification failed: project='{project.Name}', backupId={latest.Id}, error={vex.Message}");

                    if (NotificationsEnabled)
                    {
                        var title = L("Backups.Verification.Title", "Backup verification failed");
                        var msg = Lf("Backups.Verification.FailureMessage", "Verification failed for '{0}'. The backup may be corrupted or incomplete.", project.Name);

                        _notificationService.ShowError(title, msg, NotificationKind.Backup);

                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        var backupId = latest.Id.ToString();
                        BackupsViewModel.MarkSnapshotAsFailed(backupId);
                        BackupsViewModel.ShowVerificationFailure(backupId, project.Name);
                    });
                }
            });
        }

        private async void OnDeleteBackupRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;

            var preparation = await Task.Run(() => PrepareDeleteBackup(backupId));
            if (!preparation.IsReady)
                return;
            var backup      = preparation.Backup;
            var snapshotId  = preparation.SnapshotId;
            var projectId   = preparation.ProjectId;
            var backupRoot  = preparation.BackupRoot;
            var projectName = preparation.ProjectName;
            var cardId = $"delete-{backupId}";
            DestinationResolution? deleteResolution = null;

            var deleteContext = await Task.Run(() =>
            {
                var cfg = AppConfigStore.Load();
                var destinations = GetAllDestinations(cfg);
                var matchedDestination = FindDestinationForBackup(backup, destinations, backupRoot);
                var hasCredentialProfile = HasCredentialProfile(cfg, matchedDestination);
                return (cfg, matchedDestination, hasCredentialProfile);
            });
            var cfg = deleteContext.cfg;
            var matchedDestination = deleteContext.matchedDestination;
            var hasCredentialProfile = deleteContext.hasCredentialProfile;

            var confirm = await ConfirmDeleteBackupAsync(projectName, snapshot.Timestamp);
            if (!confirm)
            {
                return;
            }

            BackupsViewModel.PinExpandedProject(snapshot.ProjectId);

            BackupsViewModel.ShowTransientOperation(cardId, projectName, L("Backups.Status.Deleting", "Deleting backup files..."));

            BackupsViewModel.IsBusy      = true;
            BackupsViewModel.BusyMessage = L("Backups.Status.Deleting", L("Backups.Status.Deleting", "Deleting backup files..."));

            var deleteSucceeded = false;
            var deleteError = string.Empty;
            var permissionDenied = false;
            NetworkCredentialProfile? tempProfile = null;

            try
            {
                async Task TryDeleteAsync(bool forceCredentials, NetworkCredentialProfile? overrideProfile = null)
                {
                    if (matchedDestination is not null)
                    {
                        var destToUse = matchedDestination;
                        var rootSubPath = string.Empty;
                        if (forceCredentials)
                        {
                            var pathToUse = matchedDestination.Path;
                            if (OperatingSystem.IsWindows() && TryResolveUncPath(pathToUse, out var uncPath))
                            {
                                pathToUse = uncPath;
                            }
                            if (OperatingSystem.IsWindows() && TrySplitUncPath(pathToUse, out var uncRoot, out var uncSubPath))
                            {
                                pathToUse = uncRoot;
                                rootSubPath = uncSubPath;
                            }

                            destToUse = new BackupDestination
                            {
                                Path = pathToUse,
                                CredentialName = matchedDestination.CredentialName,
                                Active = true,
                                AutoMount = true,
                                AutoUnmount = true,
                                PreMounted = false,
                                Alias = matchedDestination.Alias,
                                EnableMetadataSync = matchedDestination.EnableMetadataSync,
                                AutoImportMetadata = matchedDestination.AutoImportMetadata,
                                ForceMetadataBackfill = matchedDestination.ForceMetadataBackfill,
                                ArchiveUploadBufferBytes = matchedDestination.ArchiveUploadBufferBytes
                            };
                        }

                        var profile = overrideProfile;
                        if (profile is null)
                        {
                            profile = string.IsNullOrWhiteSpace(destToUse.CredentialName)
                                ? null
                                : cfg.Network.Credentials.FirstOrDefault(c =>
                                    c.Name.Equals(destToUse.CredentialName, StringComparison.OrdinalIgnoreCase));
                        }

                        var resolution = _networkMountService.PrepareDestination(destToUse, profile);
                        if (!resolution.IsSuccess)
                        {
                            deleteError = resolution.Message;
                            deleteSucceeded = false;
                            permissionDenied = IsMountPermissionFailure(resolution.Message);
                            return;
                        }

                        if (resolution.IsSuccess && !string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        {
                            deleteResolution = resolution;
                            backupRoot = string.IsNullOrWhiteSpace(rootSubPath)
                                ? resolution.EffectivePath
                                : Path.Combine(resolution.EffectivePath, rootSubPath);
                        }
                    }

                    var relativePath = backup.Path ?? string.Empty;
                    var fullPath     = Path.GetFullPath(Path.Combine(backupRoot, relativePath));

                    await Task.Run(() =>
                    {
                        try
                        {
                            if (Directory.Exists(fullPath))
                            {
                                DeleteDirectoryRobust(fullPath);
                                deleteSucceeded = !Directory.Exists(fullPath);
                            }
                            else if (File.Exists(fullPath))
                            {
                                File.Delete(fullPath);
                                deleteSucceeded = !File.Exists(fullPath);
                            }
                            else
                            {
                                deleteSucceeded = true;
                            }
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            deleteError = ex.Message;
                            deleteSucceeded = false;
                            permissionDenied = true;
                        }
                        catch (IOException ex)
                        {
                            deleteError = ex.Message;
                            deleteSucceeded = false;
                            permissionDenied = IsAccessDenied(ex);
                        }
                        catch (Exception ex)
                        {
                            deleteError = ex.Message;
                            deleteSucceeded = false;
                        }
                        finally
                        {
                            if (deleteSucceeded)
                            {
                                _repo.DeleteBackupById(backupId);
                                TryDeleteSnapshotIfOrphan(projectId, snapshotId);
                            }
                        }
                    });
                }

                await TryDeleteAsync(forceCredentials: false);

                if (!deleteSucceeded && permissionDenied && matchedDestination is not null && hasCredentialProfile)
                {
                    var retry = await ConfirmDeleteWithCredentialsAsync();
                    if (retry)
                    {
                        permissionDenied = false;
                        deleteError = string.Empty;
                        await TryDeleteAsync(forceCredentials: true);
                    }
                }

                if (!deleteSucceeded && permissionDenied && matchedDestination is not null && !hasCredentialProfile)
                {
                    var retry = await ConfirmDeleteWithTemporaryCredentialsAsync();
                    if (retry.Confirmed)
                    {
                        tempProfile = new NetworkCredentialProfile
                        {
                            Name = "DeleteOnce",
                            Username = retry.Username,
                            Password = retry.Password,
                            UseKeychain = false,
                            KeyRef = string.Empty
                        };
                        permissionDenied = false;
                        deleteError = string.Empty;
                        await TryDeleteAsync(forceCredentials: true, overrideProfile: tempProfile);
                    }
                    else
                    {
                        var title = L("Backups.Delete.ForceCredentialsTitle", "Credentials required");
                        var msg = L("Backups.Delete.ForceCredentialsMissing",
                            "Assign a credential profile to this destination in Settings. If your usual user cannot delete backups, the NAS root/admin user may be required.");
                        BackupsViewModel.ShowNotification(msg, "Error");
                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(msg, NotificationSeverity.Error, title);
                        }
                    }
                }

                if (deleteSucceeded)
                {
                    ReloadBackupsVmData();
                    await DashboardViewModel.RefreshAsync();
                }
                else
                {
                    var title = L("Backups.Delete.FailedTitle", "Backup delete failed");
                    var msg = Lf("Backups.Delete.FailedMessage", "Could not delete backup '{0}'.", projectName);
                    if (!string.IsNullOrWhiteSpace(deleteError))
                    {
                        msg = $"{msg} {deleteError}";
                    }

                    BackupsViewModel.ShowNotification(msg, "Error");
                    if (!IsOnBackupsPage)
                    {
                        GlobalNotificationCenter.Instance.Show(msg, NotificationSeverity.Error, title);
                    }
                }
            }
            finally
            {
                var finalLabel = deleteSucceeded
                    ? L("Backups.Status.Deleted", "Deleted")
                    : L("Backups.Status.FailedSuffix", "Failed");
                BackupsViewModel.CompleteTransientOperation(cardId, finalLabel);
                BackupsViewModel.IsBusy      = false;
                BackupsViewModel.BusyMessage = string.Empty;

                if (deleteResolution is not null)
                {
                    _networkMountService.Cleanup(deleteResolution);
                }
            }
        }

        private void OnOpenBackupFolderRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;

            OpenBackupFolder(backupId);
        }

        private void OnOpenSettingsRequested()
        {
            NavigateSettings?.Execute(null);
        }

        private async Task<bool> ConfirmDeleteBackupAsync(string projectName, DateTime timestamp)
        {
            var cfg = await Task.Run(AppConfigStore.Load);
            if (!cfg.Behavior.ConfirmDeleteBackup)
                return true;

            var timeLabel = timestamp.ToString("g", CultureInfo.CurrentCulture);
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = L("Backups.Delete.Title", "Delete backup?"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = Lf("Backups.Delete.Message", "Delete the backup for '{0}' from {1}?", projectName, timeLabel),
                    TextWrapping = TextWrapping.Wrap
                };

                var warning = new TextBlock
                {
                    Text = L("Backups.Delete.Warning", "This removes data on the destination."),
                    TextWrapping = TextWrapping.Wrap
                };
                if (GetBrush("TextSecondary") is { } warningBrush)
                {
                    warning.Foreground = warningBrush;
                }

                var dontShowAgain = new CheckBox
                {
                    Content = L("Backups.Delete.DontShowAgain", "Don't show again"),
                    Margin = new Thickness(0, 2, 0, 0)
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var deleteButton = new Button
                {
                    Content = L("Backups.Delete.Confirm", "Delete backup"),
                    MinWidth = 140
                };
                deleteButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                deleteButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(deleteButton);

                var content = new StackPanel
                {
                    Spacing = 12
                };
                content.Children.Add(title);
                content.Children.Add(question);
                content.Children.Add(warning);
                content.Children.Add(dontShowAgain);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = L("Backups.Delete.Title", "Delete backup?"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                if (confirmed && dontShowAgain.IsChecked == true)
                {
                    cfg.Behavior.ConfirmDeleteBackup = false;
                    AppConfigStore.Save(cfg);
                    if (_settingsViewModel is not null)
                    {
                        _settingsViewModel.ConfirmDeleteBackups = false;
                    }
                }

                return confirmed;
            });
        }

        private static IBrush? GetBrush(string key)
        {
            if (Application.Current?.Resources.TryGetValue(key, out var value) == true)
            {
                return value as IBrush;
            }

            return null;
        }

        private async Task<bool> ConfirmDeleteWithCredentialsAsync()
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = L("Backups.Delete.ForceCredentialsPrompt",
                        "Use destination credentials to force delete this backup?"),
                    TextWrapping = TextWrapping.Wrap
                };

                var hint = new TextBlock
                {
                    Text = L("Backups.Delete.ForceCredentialsHint",
                        "Recommended for NAS shares when delete is denied."),
                    TextWrapping = TextWrapping.Wrap
                };
                if (GetBrush("TextSecondary") is { } hintBrush)
                {
                    hint.Foreground = hintBrush;
                }

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var forceButton = new Button
                {
                    Content = L("Backups.Delete.ForceCredentialsConfirm", "Use credentials"),
                    MinWidth = 160
                };
                forceButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                forceButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(forceButton);

                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(title);
                content.Children.Add(question);
                content.Children.Add(hint);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                return confirmed;
            });
        }

        private async Task<(bool Confirmed, string Username, string Password)> ConfirmDeleteWithTemporaryCredentialsAsync()
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = L("Backups.Delete.CredentialsPrompt",
                        "Enter destination credentials to force delete this backup. These credentials are used once and not saved."),
                    TextWrapping = TextWrapping.Wrap
                };

                var usernameLabel = new TextBlock
                {
                    Text = L("Backups.Delete.CredentialsUsername", "Username"),
                    FontWeight = FontWeight.SemiBold
                };
                var usernameBox = new TextBox
                {
                    Width = 320
                };

                var passwordLabel = new TextBlock
                {
                    Text = L("Backups.Delete.CredentialsPassword", "Password"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                var passwordBox = new TextBox
                {
                    Width = 320,
                    PasswordChar = '●'
                };

                var hint = new TextBlock
                {
                    Text = L("Backups.Delete.ForceCredentialsMissing",
                        "Assign a credential profile to this destination in Settings. If your usual user cannot delete backups, the NAS root/admin user may be required."),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                if (GetBrush("TextSecondary") is { } hintBrush)
                {
                    hint.Foreground = hintBrush;
                }

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var forceButton = new Button
                {
                    Content = L("Backups.Delete.ForceCredentialsConfirm", "Use credentials"),
                    MinWidth = 160
                };
                forceButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                forceButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(forceButton);

                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(title);
                content.Children.Add(question);
                content.Children.Add(usernameLabel);
                content.Children.Add(usernameBox);
                content.Children.Add(passwordLabel);
                content.Children.Add(passwordBox);
                content.Children.Add(hint);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                var username = usernameBox.Text?.Trim() ?? string.Empty;
                var password = passwordBox.Text ?? string.Empty;
                if (confirmed && (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)))
                    return (false, string.Empty, string.Empty);

                return (confirmed, username, password);
            });
        }

        /// <summary>
        /// Deletes a directory tree, clearing read-only attributes to avoid UnauthorizedAccess on Windows.
        /// </summary>
        private static void DeleteDirectoryRobust(string path)
        {
            // Clear read-only attributes on files and dirs before deletion.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // ignore individual failures; deletion will surface issues later
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Reverse())
            {
                try
                {
                    File.SetAttributes(dir, FileAttributes.Directory);
                }
                catch
                {
                    // ignore
                }
            }

            Directory.Delete(path, recursive: true);
        }

        private static bool IsAccessDenied(Exception ex)
        {
            const int accessDenied = unchecked((int)0x80070005);
            return ex.HResult == accessDenied ||
                   ex is UnauthorizedAccessException;
        }

        private static bool IsMountPermissionFailure(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("denied", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TrySplitUncPath(string? path, out string root, out string subPath)
        {
            root = string.Empty;
            subPath = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var trimmed = path.TrimStart('\\', '/');
            var parts = trimmed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;

            root = $@"\\{parts[0]}\{parts[1]}";
            if (parts.Length > 2)
            {
                subPath = Path.Combine(parts.Skip(2).ToArray());
            }

            return !string.IsNullOrWhiteSpace(subPath);
        }

        private const int UniversalNameInfoLevel = 0x00000001;
        private const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct UniversalNameInfo
        {
            public string? lpUniversalName;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetUniversalName(string localPath, int infoLevel, IntPtr buffer, ref int bufferSize);

        private static bool TryResolveUncPath(string? path, out string? uncPath)
        {
            uncPath = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase))
            {
                uncPath = path;
                return true;
            }

            if (!OperatingSystem.IsWindows())
                return false;

            if (path.Length < 2 || path[1] != ':')
                return false;

            var bufferSize = 0;
            var result = WNetGetUniversalName(path, UniversalNameInfoLevel, IntPtr.Zero, ref bufferSize);
            if (result != ErrorMoreData || bufferSize <= 0)
                return false;

            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                result = WNetGetUniversalName(path, UniversalNameInfoLevel, buffer, ref bufferSize);
                if (result != 0)
                    return false;

                var info = Marshal.PtrToStructure<UniversalNameInfo>(buffer);
                if (string.IsNullOrWhiteSpace(info.lpUniversalName))
                    return false;

                uncPath = info.lpUniversalName;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private DeleteBackupPreparation PrepareDeleteBackup(int backupId)
        {
            var backup = _repo.GetBackupById(backupId);
            if (backup is null)
                return DeleteBackupPreparation.Failure;

            var cfg = AppConfigStore.Load();
            var destinations = GetAllDestinations(cfg);
            var backupRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
                return DeleteBackupPreparation.Failure;

            var project = _repo.GetProjectById(backup.ProjectId);
            var projectName = project?.Name ?? "Backup";

            return new DeleteBackupPreparation(true, backup, backupRoot, projectName, project?.Id ?? 0, backup.SnapshotId);
        }

        private static BackupDestination? FindDestinationForBackup(
            Backup backup,
            IReadOnlyList<BackupDestination> destinations,
            string backupRoot)
        {
            if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
            {
                var aliasMatch = destinations.FirstOrDefault(d =>
                    string.Equals(d.Alias ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Path ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase));
                if (aliasMatch is not null)
                    return aliasMatch;
            }

            if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
            {
                var pathMatch = destinations.FirstOrDefault(d =>
                    string.Equals(d.Path ?? string.Empty, backup.DestinationPath, StringComparison.OrdinalIgnoreCase));
                if (pathMatch is not null)
                    return pathMatch;
            }

            var rootMatch = destinations.FirstOrDefault(d =>
                string.Equals(d.Path ?? string.Empty, backupRoot, StringComparison.OrdinalIgnoreCase));
            if (rootMatch is not null)
                return rootMatch;

            var prefixMatch = destinations.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.Path) &&
                !string.IsNullOrWhiteSpace(backupRoot) &&
                backupRoot.StartsWith(d.Path!, StringComparison.OrdinalIgnoreCase));
            if (prefixMatch is not null)
                return prefixMatch;

            return rootMatch;
        }

        private static bool HasCredentialProfile(AppConfig cfg, BackupDestination? dest)
        {
            if (dest is null || string.IsNullOrWhiteSpace(dest.CredentialName))
                return false;

            return cfg.Network.Credentials.Any(c =>
                c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));
        }

        private sealed record DeleteBackupPreparation(
            bool IsReady,
            Backup? Backup,
            string BackupRoot,
            string ProjectName,
            int ProjectId,
            int SnapshotId)
        {
            public static DeleteBackupPreparation Failure => new(false, null, string.Empty, string.Empty, 0, 0);
        }

        private RestoreBackupPreparation PrepareRestoreBackup(int backupId)
        {
            var backup = _repo.GetBackupById(backupId);
            if (backup is null)
            {
                Console.WriteLine($"[Restore] Backup id {backupId} not found.");
                return RestoreBackupPreparation.Failure;
            }

            var cfg = AppConfigStore.Load();
            var destinations = GetAllDestinations(cfg);
            var backupRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                Console.WriteLine($"[Restore] No backup root found for id={backupId}, path='{backup.Path}', dest='{backup.DestinationPath}', alias='{backup.DestinationAlias}'.");
                return RestoreBackupPreparation.Failure;
            }

            if (string.IsNullOrWhiteSpace(backup.Path))
            {
                Console.WriteLine($"[Restore] Backup path missing for id={backupId}. Root='{backupRoot}', rel='{backup.Path}'.");
                return RestoreBackupPreparation.Failure;
            }

            var backupFullPath = Path.Combine(backupRoot, backup.Path);
            if (!Directory.Exists(backupFullPath))
            {
                Console.WriteLine($"[Restore] Backup path missing for id={backupId}. Root='{backupRoot}', rel='{backup.Path}', full='{backupFullPath}'.");
                return RestoreBackupPreparation.Failure;
            }

            var project = _repo.GetProjectById(backup.ProjectId);
            if (project is null)
            {
                Console.WriteLine($"[Restore] Project id {backup.ProjectId} not found for backup id {backupId}.");
                return RestoreBackupPreparation.Failure;
            }

            var projectRoot = ResolveRestoreTarget(project);
            if (string.IsNullOrWhiteSpace(projectRoot))
                return RestoreBackupPreparation.Failure;

            var encryptedArchivePath = Path.Combine(backupFullPath, BackupArchiveCryptoService.EncryptedArchiveFileName);
            var isEncrypted = backup.IsEncrypted || File.Exists(encryptedArchivePath);

            return new RestoreBackupPreparation(true, backupFullPath, projectRoot, project.Name, isEncrypted);
        }

        private sealed record RestoreBackupPreparation(
            bool IsReady,
            string BackupFullPath,
            string ProjectRoot,
            string ProjectName,
            bool IsEncrypted)
        {
            public static RestoreBackupPreparation Failure => new(false, string.Empty, string.Empty, string.Empty, false);
        }

        private string ResolveRestoreTarget(Project project)
        {
            if (!string.IsNullOrWhiteSpace(project.RootPath) && Directory.Exists(project.RootPath))
                return project.RootPath;

            var cfg = AppConfigStore.Load();
            if (!string.IsNullOrWhiteSpace(cfg.ProjectsRoot))
            {
                var projectsRoot = Path.Combine(cfg.ProjectsRoot, project.Name);
                Directory.CreateDirectory(projectsRoot);
                _repo.UpdateProjectPath(project.Name, projectsRoot, out _);
                Console.WriteLine($"[Restore] Project root missing. Using ProjectsRoot '{projectsRoot}'.");
                return projectsRoot;
            }

            var fallbackRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VaultSync Restores",
                project.Name);

            Directory.CreateDirectory(fallbackRoot);
            _repo.UpdateProjectPath(project.Name, fallbackRoot, out _);

            Console.WriteLine($"[Restore] Project root missing. Using fallback restore path '{fallbackRoot}'.");
            return fallbackRoot;
        }

        private AutoBackupPreparation PrepareAutoBackupRun()
        {
            var cfg = AppConfigStore.Load();
            if (!cfg.Backups.EnableAutoBackups)
                return AutoBackupPreparation.Failure("disabled");

            var destinations = GetAllDestinations(cfg);
            if (destinations.Count == 0)
                return AutoBackupPreparation.Failure("no_destination");

            var projects = _repo.GetAllProjects().ToList();
            var disabled = cfg.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();

            return AutoBackupPreparation.Success(cfg, projects, disabled);
        }

        private sealed record AutoBackupPreparation(
            bool IsReady,
            string? FailureCode,
            AppConfig? Config,
            List<Project>? Projects,
            ISet<int>? DisabledProjects)
        {
            public static AutoBackupPreparation Failure(string reason) =>
                new(false, reason, null, null, null);

            public static AutoBackupPreparation Success(
                AppConfig cfg,
                List<Project> projects,
                ISet<int> disabled) =>
                new(true, null, cfg, projects, disabled);
        }

        private async Task<(bool Confirmed, string Password)> ConfirmEncryptedRestorePasswordAsync(string projectName)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = L("Backups.Restore.EncryptedPasswordTitle", "Encrypted backup password"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var prompt = new TextBlock
                {
                    Text = string.Format(
                        CultureInfo.CurrentCulture,
                        L("Backups.Restore.EncryptedPasswordPrompt", "Enter the encryption password to restore '{0}'."),
                        projectName),
                    TextWrapping = TextWrapping.Wrap
                };

                var passwordLabel = new TextBlock
                {
                    Text = L("Backups.Restore.EncryptedPasswordLabel", "Password"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var passwordBox = new TextBox
                {
                    Width = 320,
                    PasswordChar = '●'
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var restoreButton = new Button
                {
                    Content = L("Backups.Section.Restore", "Restore"),
                    MinWidth = 140
                };
                restoreButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                restoreButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(restoreButton);

                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(title);
                content.Children.Add(prompt);
                content.Children.Add(passwordLabel);
                content.Children.Add(passwordBox);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = L("Backups.Restore.EncryptedPasswordTitle", "Encrypted backup password"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                var password = passwordBox.Text ?? string.Empty;
                if (confirmed && string.IsNullOrWhiteSpace(password))
                    return (false, string.Empty);

                return (confirmed, password);
            });
        }

        private async void OnRestoreBackupRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;


            var preparation = await Task.Run(() => PrepareRestoreBackup(backupId));
            if (!preparation.IsReady)
            {
                BackupsViewModel.ShowNotification(
                    L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                Console.WriteLine($"[Restore] Restore preparation failed for backupId={backupId}.");
                return;
            }

            string? encryptedRestorePassword = null;
            if (preparation.IsEncrypted)
            {
                var passwordPrompt = await ConfirmEncryptedRestorePasswordAsync(preparation.ProjectName);
                if (!passwordPrompt.Confirmed)
                    return;

                encryptedRestorePassword = passwordPrompt.Password;
                if (string.IsNullOrWhiteSpace(encryptedRestorePassword))
                {
                    BackupsViewModel.ShowNotification(
                        L("Backups.Restore.EncryptedPasswordRequired", "A password is required to restore encrypted backups."),
                        "Error");
                    return;
                }
            }

            var projectRoot   = preparation.ProjectRoot;
            var backupFullPath = preparation.BackupFullPath;
            BackupsViewModel.IsBusy      = true;
            BackupsViewModel.BusyMessage = $"Restoring {preparation.ProjectName}...";
            var restoreCardId = $"restore-{backupId}";
            BackupsViewModel.UpdateActiveBackup(
                restoreCardId,
                preparation.ProjectName,
                0,
                L("Backups.Status.Restoring", "Restoring backup..."),
                string.Empty,
                allowCancel: false);

            var restoreSucceeded = false;
            try
            {
                await Task.Run(() =>
                {
                    Console.WriteLine($"[Restore] Starting restore for '{preparation.ProjectName}'.");
                    Console.WriteLine($"[Restore] Source='{backupFullPath}', Target='{projectRoot}'.");
                    RestoreDirectory(backupFullPath, projectRoot, encryptedRestorePassword, (percent, currentFile) =>
                    {
                        var label = string.IsNullOrWhiteSpace(currentFile)
                            ? L("Backups.Status.Restoring", "Restoring backup...")
                            : currentFile;
                        BackupsViewModel.UpdateActiveBackup(
                            restoreCardId,
                            preparation.ProjectName,
                            percent,
                            label,
                            string.Empty,
                            allowCancel: false);
                    });
                    Console.WriteLine($"[Restore] Completed restore for '{preparation.ProjectName}'.");
                });
                restoreSucceeded = true;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Restore] Restore failed for '{preparation.ProjectName}': {ex.Message}");

                var failureMessage = IsEncryptedRestorePasswordError(ex)
                    ? L("Backups.Status.RestoreWrongPassword", "Restore failed: invalid password or encrypted backup is corrupted.")
                    : ex.Message;

                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.BackupCurrentFile = L("Backups.Status.RestoreFailed", "Restore failed.");
                    BackupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                            ? failureMessage
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                });
            }
            finally
            {
                if (restoreSucceeded)
                {
                    var restoredProject = _repo.GetProjectByName(preparation.ProjectName);
                    if (restoredProject != null && restoredProject.NeedsRestore)
                    {
                        _repo.UpdateProjectNeedsRestore(restoredProject.Id, false);
                    }
                }
                BackupsViewModel.RemoveActiveBackup(restoreCardId);
                BackupsViewModel.IsBusy      = false;
                BackupsViewModel.BusyMessage = string.Empty;
            }
        }

        private static bool IsEncryptedRestorePasswordError(Exception ex)
        {
            Exception? current = ex;
            while (current is not null)
            {
                if (string.Equals(
                    current.Message,
                    BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage,
                    StringComparison.Ordinal))
                {
                    return true;
                }
                current = current.InnerException;
            }

            return false;
        }

        private static void RestoreDirectory(string sourceDir, string targetDir, string? encryptionPassword, Action<double, string>? progress)
        {
            if (string.IsNullOrWhiteSpace(sourceDir))
                throw new ArgumentException("Source directory is required.", nameof(sourceDir));

            if (string.IsNullOrWhiteSpace(targetDir))
                throw new ArgumentException("Target directory is required.", nameof(targetDir));

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist.");

            // Ensure target root exists
            Directory.CreateDirectory(targetDir);

            var archivePath = Path.Combine(sourceDir, BackupArchiveCryptoService.PlainArchiveFileName);
            if (File.Exists(archivePath))
            {
                ExtractArchiveWithProgress(archivePath, targetDir, progress);
                return;
            }

            var encryptedArchivePath = Path.Combine(sourceDir, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (File.Exists(encryptedArchivePath))
            {
                if (string.IsNullOrWhiteSpace(encryptionPassword))
                {
                    throw new InvalidOperationException(
                        "A password is required to restore encrypted backups.");
                }

                RestoreEncryptedArchiveWithProgress(sourceDir, targetDir, encryptionPassword, progress);
                return;
            }

            // Create all directories
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, dirPath);
                var target   = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(target);
            }

            // Copy all files, overwriting existing ones but not deleting extras.
            CopyDirectoryWithProgress(sourceDir, targetDir, 0, 100, progress);
        }

        private static void ExtractArchiveWithProgress(string archivePath, string targetDir, Action<double, string>? progress)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var totalEntries = archive.Entries.Count;
            var processed = 0;

            foreach (var entry in archive.Entries)
            {
                var destinationPath = Path.Combine(targetDir, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    var parent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    entry.ExtractToFile(destinationPath, overwrite: true);
                }

                processed++;
                progress?.Invoke(totalEntries == 0 ? 100 : processed * 100d / totalEntries, entry.FullName);
            }
        }

        private static void RestoreEncryptedArchiveWithProgress(
            string sourceDir,
            string targetDir,
            string password,
            Action<double, string>? progress)
        {
            var stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-restore-{Guid.NewGuid():N}");
            var stagingExtracted = Path.Combine(stagingRoot, "content");
            var stagingArchive = Path.Combine(stagingRoot, BackupArchiveCryptoService.PlainArchiveFileName);

            try
            {
                Directory.CreateDirectory(stagingExtracted);
                progress?.Invoke(5, "Decrypting backup...");

                var cryptoService = new BackupArchiveCryptoService();
                cryptoService.DecryptArchiveToPlainZip(sourceDir, password, stagingArchive);
                progress?.Invoke(30, "Decrypting backup...");

                ExtractArchiveWithProgress(stagingArchive, stagingExtracted, (percent, currentFile) =>
                {
                    var mapped = 30 + (percent * 0.5);
                    progress?.Invoke(Math.Clamp(mapped, 30, 80), currentFile);
                });

                progress?.Invoke(82, "Restoring backup...");
                CopyDirectoryWithProgress(stagingExtracted, targetDir, 82, 100, progress);
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    try
                    {
                        DeleteDirectoryRobust(stagingRoot);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }
            }
        }

        private static void CopyDirectoryWithProgress(
            string sourceDir,
            string targetDir,
            double startPercent,
            double endPercent,
            Action<double, string>? progress)
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var totalFiles = files.Length;
            var processed = 0;
            foreach (var filePath in files)
            {
                var relative = Path.GetRelativePath(sourceDir, filePath);
                var target = Path.Combine(targetDir, relative);

                var parentDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(filePath, target, overwrite: true);
                processed++;
                if (progress is not null)
                {
                    var ratio = totalFiles == 0 ? 1d : processed / (double)totalFiles;
                    var value = startPercent + ((endPercent - startPercent) * ratio);
                    progress(value, relative);
                }
            }

            if (totalFiles == 0)
                progress?.Invoke(endPercent, string.Empty);
        }

        private void OnCancelActiveBackupRequested(BackupProgressItem? item)
        {
            if (item is null)
                return;

            if (!int.TryParse(item.ProjectId, out var projectId))
            {
                return;
            }

            // Actually cancel the running backup for this project.
            _backupCancelRequested[projectId] = 1;
            _backupService.CancelBackup(projectId);
            BackupsViewModel.UpdateActiveBackup(
                item.ProjectId,
                item.ProjectName,
                item.Progress,
                L("Backups.Status.Cancelling", "Cancelling..."),
                string.Empty,
                allowCancel: false);
            Console.WriteLine($"[Backup] Cancel requested for projectId={projectId} ({item.ProjectName}).");
            Telemetry.Log("backup_cancel_requested", b => b
                .WithHashedString("projectId", item.ProjectId));

            // Do NOT remove the active backup card immediately.
            // Let the backup operation observe the cancellation token and finish,
            // then the existing completion logic (finally blocks / ReloadBackupsVmData)
            // will clear the cards and refresh the UI.
        }

        // ---------- Tray entry points ----------

        /// <summary>
        /// Triggered from the tray menu: backup all projects.
        /// Reuses the same logic as the Backups page \"backup all\" action.
        /// </summary>
        
        /// <summary>
        /// Returns the list of backup-capable projects for use in the tray menu.
        /// </summary>
        public IReadOnlyList<ProjectBackupItem> GetProjectsForBackupTray()
        {
            return BackupsViewModel.ProjectBackups.ToList();
        }

        /// <summary>
        /// Triggered from the tray menu: backup a specific project by its ProjectBackupItem.Id.
        /// This reuses the same pipeline as the Backups page per-project backup.
        /// </summary>
        public void RequestBackupProjectFromTray(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            // Don't start if something is already running
            if (BackupsViewModel.IsBusy)
            {
                return;
            }

            var projectItem = BackupsViewModel.ProjectBackups.FirstOrDefault(p => p.Id == projectId);
            if (projectItem == null)
            {
                return;
            }

            // When triggered from tray, navigate to the Backups page so the user
            // immediately sees the running backup card (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateBackups?.CanExecute(null) == true)
                {
                    NavigateBackups.Execute(null);
                }
            });

            _trayInitiatedBackup = true;

            OnBackupProjectRequested(projectItem);
        }

        public void RequestBackupAllFromTray()
        {
            // Do not start if something is already running.
            if (BackupsViewModel.IsBusy)
            {
                return;
            }

            // When triggered from tray, navigate to the Backups page so the user
            // immediately sees the running backup cards (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateBackups?.CanExecute(null) == true)
                {
                    NavigateBackups.Execute(null);
                }
            });

            _trayInitiatedBackup = true;

            OnCreateBackupForAllProjectsRequested();
        }

        /// <summary>
        /// Triggered from the tray menu: backup the selected project.
        /// For now we simply navigate to the Backups page so the user can pick a project
        /// and start the backup from there. Later we can wire this to the actual selection.
        /// </summary>
        public void RequestBackupSelectedProjectFromTray()
        {

            // For now, just bring the Backups page into view.
            if (NavigateBackups?.CanExecute(null) == true)
            {
                NavigateBackups.Execute(null);
            }

            // TODO (later): once BackupsViewModel exposes the currently selected project,
            // call OnBackupProjectRequested with that item to start the backup directly.
        }

        /// <summary>
        /// Returns the list of projects used for snapshots (Projects page),
        /// for use in the tray's Snapshot submenu.
        /// Only returns projects that are actually added/tracked in VaultSync.
        /// Untracked/discovered entries normally have ProjectId <= 0 and should not appear in the tray.
        /// </summary>
        public IReadOnlyList<ProjectItemViewModel> GetProjectsForSnapshotTray()
        {
            // Only expose projects that are actually registered in the backup DB.
            return _projectsViewModel.Projects
                .Where(p => p.IsRegistered)
                .ToList();
        }

        /// <summary>
        /// Triggered from the tray menu: create a snapshot for a specific project by name.
        /// This reuses the ProjectsViewModel.TakeSnapshotForProjectFromTrayAsync pipeline,
        /// which in turn calls the existing TakeSnapshot() logic.
        /// </summary>
        public async Task TakeSnapshotForProjectFromTrayAsync(string projectName)
        {
            // When triggered from tray, navigate to the Projects page so the user
            // immediately sees the snapshot activity (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateProjects?.CanExecute(null) == true)
                {
                    NavigateProjects.Execute(null);
                }
            });

            await _projectsViewModel.TakeSnapshotForProjectFromTrayAsync(projectName);
        }

        /// <summary>
        /// Triggered from the tray menu: create snapshots for all projects.
        /// This reuses the ProjectsViewModel.TakeSnapshotAllFromTrayAsync pipeline,
        /// which in turn calls the existing TakeSnapshot() logic for each project.
        /// </summary>
        public async Task TakeSnapshotAllFromTrayAsync()
        {
            // When triggered from tray, navigate to the Projects page so the user
            // immediately sees the snapshot activity (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateProjects?.CanExecute(null) == true)
                {
                    NavigateProjects.Execute(null);
                }
            });

            await _projectsViewModel.TakeSnapshotAllFromTrayAsync();
        }

        /// <summary>
        /// Resolve the effective backup root to use for a project, honoring preferences for external/NAS paths.
        /// If the preferred NAS path is unavailable, a temporary backup folder is used next to the project.
        /// </summary>
        private DestinationResolution PrepareDestination(BackupDestination dest, AppConfig cfg)
        {
            var profile = cfg.Network.Credentials?
                .FirstOrDefault(c =>
                    string.Equals(c.Name, dest.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase));

            var resolution = _networkMountService.PrepareDestination(dest, profile);
            Console.WriteLine($"[Backup] Destination resolved: alias='{dest.Alias ?? dest.Path}', path='{dest.Path}', effective='{resolution.EffectivePath}', success={resolution.IsSuccess}, mountedByUs={resolution.MountedByUs}");
            return resolution;
        }

        private Task<DestinationResolution> PrepareDestinationAsync(BackupDestination dest, AppConfig cfg)
        {
            return Task.Run(() => PrepareDestination(dest, cfg));
        }

        private BackupAllPreparationResult PrepareBackupAll()
        {
            var cfg = AppConfigStore.Load();
            var destinations = GetAllDestinations(cfg);
            if (destinations.Count == 0)
            {
                return BackupAllPreparationResult.Failure("no_destination");
            }

            return BackupAllPreparationResult.Success(cfg);
        }

        private sealed record BackupAllPreparationResult(
            bool IsReady,
            string? FailureCode,
            AppConfig? Config)
        {
            public static BackupAllPreparationResult Failure(string reason) =>
                new(false, reason, null);

            public static BackupAllPreparationResult Success(AppConfig cfg) =>
                new(true, null, cfg);
        }

        private void ResolveBackupRoots(
            Project project,
            string configuredBackupRoot,
            out string effectiveBackupRoot,
            out string? preferredFinalBackupRoot)
        {
            effectiveBackupRoot      = configuredBackupRoot;
            preferredFinalBackupRoot = null;

            if (_settingsViewModel?.PreferExternalDrives == true &&
                IsNetworkPath(configuredBackupRoot))
            {
                if (Directory.Exists(configuredBackupRoot))
                {
                    // If the NAS just came back, try to migrate any temp backups into it.
                    TryMigrateTempBackups(project, configuredBackupRoot);
                }
                else
                {
                    var tempRoot = Path.Combine(project.RootPath, ".vaultsync-temp-backups");
                    Directory.CreateDirectory(tempRoot);

                    effectiveBackupRoot      = tempRoot;
                    preferredFinalBackupRoot = configuredBackupRoot;
                    EnsureNasMonitorStarted();
                }
            }
        }

        private static void TryMigrateTempBackups(Project project, string targetRoot)
        {
            var tempRoot = Path.Combine(project.RootPath, ".vaultsync-temp-backups");
            if (!Directory.Exists(tempRoot))
                return;

            Directory.CreateDirectory(targetRoot);

            foreach (var dir in Directory.EnumerateDirectories(tempRoot))
            {
                var dest = Path.Combine(targetRoot, Path.GetFileName(dir));

                try
                {
                    if (Directory.Exists(dest))
                        continue; // already moved

                    Directory.Move(dir, dest);
                }
                catch
                {
                    // ignore and continue with other folders
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(tempRoot).Any())
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        private static string? TryResolveBackupPathForRead(string relativePath, IReadOnlyList<BackupDestination> destinations, string? legacyRoot)
        {
            foreach (var dest in destinations.OrderByDescending(d => d.Active))
            {
                if (string.IsNullOrWhiteSpace(dest.Path))
                    continue;

                var combined = Path.GetFullPath(Path.Combine(dest.Path, relativePath));
                if (Directory.Exists(combined) || File.Exists(combined))
                    return dest.Path;
            }

            if (!string.IsNullOrWhiteSpace(legacyRoot))
            {
                var combined = Path.GetFullPath(Path.Combine(legacyRoot, relativePath));
                if (Directory.Exists(combined) || File.Exists(combined))
                    return legacyRoot;
            }

            // fall back to first destination path even if not present, so caller can attempt/create
            var first = destinations.FirstOrDefault();
            return first?.Path ?? legacyRoot;
        }

        private static string? ResolveDestinationRootForBackup(Backup backup, IReadOnlyList<BackupDestination> destinations, string? legacyRoot)
        {
            if (!string.IsNullOrWhiteSpace(backup.Path))
            {
                foreach (var dest in destinations.Where(d => !string.IsNullOrWhiteSpace(d.Path)))
                {
                    var combined = Path.GetFullPath(Path.Combine(dest.Path!, backup.Path));
                    if (Directory.Exists(combined) || File.Exists(combined))
                        return dest.Path;
                }
            }

            if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
                return backup.DestinationPath;

            if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
            {
                var match = destinations.FirstOrDefault(d =>
                    string.Equals(d.Alias ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Path ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase));

                if (match is not null && !string.IsNullOrWhiteSpace(match.Path))
                {
                    var combined = Path.GetFullPath(Path.Combine(match.Path, backup.Path ?? string.Empty));
                    if (Directory.Exists(combined) || File.Exists(combined))
                        return match.Path;
                }
            }

            return TryResolveBackupPathForRead(backup.Path ?? string.Empty, destinations, legacyRoot);
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- Tray helpers: recent backups / keep / delete ----------

        public sealed record TrayBackupItem(int Id, int ProjectId, string Label, bool IsProtected);
        public sealed record TrayProjectBackups(int ProjectId, string ProjectName, IReadOnlyList<TrayBackupItem> Backups);
        public void OpenBackupFolderFromTray(int backupId)
        {
            OpenBackupFolder(backupId);
        }

        private async void OpenBackupFolder(int backupId)
        {
            try
            {
                var fullPath = await Task.Run(() => ResolveBackupFullPathForOpen(backupId));
                if (string.IsNullOrWhiteSpace(fullPath))
                    return;

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", fullPath);
                }
                else if (OperatingSystem.IsLinux())
                {
                    Process.Start("xdg-open", fullPath);
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
                }
            }
            catch
            {
                // Swallow tray errors
            }
        }

        private string? ResolveBackupFullPathForOpen(int backupId)
        {
            var backup = _repo.GetBackupById(backupId);
            if (backup is null)
                return null;

            var cfg = AppConfigStore.Load();
            var destinations = GetAllDestinations(cfg);
            var destinationRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(destinationRoot))
                return null;

            if (string.IsNullOrWhiteSpace(backup.Path))
                return null;

            var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, backup.Path));
            return Directory.Exists(fullPath) ? fullPath : null;
        }

        private void UpdateBackupProtectionMarker(int backupId, bool isProtected)
        {
            try
            {
                var fullPath = ResolveBackupFullPathForOpen(backupId);
                if (string.IsNullOrWhiteSpace(fullPath))
                    return;

                var markerPath = Path.Combine(fullPath, BackupProtectionMarkerFileName);
                if (isProtected)
                {
                    File.WriteAllText(markerPath, $"keep:{DateTime.UtcNow:O}");
                }
                else if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }
            }
            catch
            {
                // best-effort marker update
            }
        }

        public void ShowBackupInAppFromTray(int projectId)
        {
            try
            {
                ReloadBackupsVmData();
                var projectItem = BackupsViewModel.ProjectBackups
                    .FirstOrDefault(p => int.TryParse(p.Id, out var pid) && pid == projectId);
                if (projectItem is not null)
                {
                    BackupsViewModel.SelectedProject = projectItem;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    SetCurrentView("Backups");
                });
            }
            catch
            {
                // ignore tray errors
            }
        }

        public IReadOnlyList<TrayProjectBackups> GetRecentBackupsForTray(int maxPerProject = 5)
        {
            try
            {
                var projects = _repo.GetAllProjects().ToList();
                var result   = new List<TrayProjectBackups>();
                var projectsById = projects
                    .GroupBy(p => p.Id)
                    .ToDictionary(g => g.Key, g => g.First());
                var recent = _repo.GetRecentBackupsByProject(maxPerProject);
                var grouped = recent
                    .GroupBy(b => b.ProjectId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var project in projects)
                {
                    grouped.TryGetValue(project.Id, out var projectBackups);
                    var backups = (projectBackups ?? new List<Backup>())
                        .OrderByDescending(b => b.CreatedUtc)
                        .Select(b =>
                        {
                            var ts   = b.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                            var keep = b.IsProtected ? L("Tray.Recent.KeptSuffix", " * Keep") : string.Empty;
                            var label = $"{ts}{keep}";
                            return new TrayBackupItem(b.Id, project.Id, label, b.IsProtected);
                        })
                        .ToList();

                    result.Add(new TrayProjectBackups(project.Id, project.Name, backups));
                }

                return result;
            }
            catch
            {
                return Array.Empty<TrayProjectBackups>();
            }
        }

        public void ToggleBackupProtectionFromTray(int backupId)
        {
            try
            {
                var backup = _repo.GetBackupById(backupId);
                if (backup is null)
                    return;

                var newValue = !backup.IsProtected;
                _repo.SetBackupProtection(backupId, newValue);
                UpdateBackupProtectionMarker(backupId, newValue);
                BackupsViewModel.MarkBackupProtection(backupId, newValue);
                TrayMenuRefreshRequested?.Invoke();
            }
            catch
            {
                // Swallow for tray; avoid surfacing errors in the OS menu context.
            }
        }

        public void DeleteBackupFromTray(int backupId)
        {
            try
            {
                if (ShouldShowBackupWidget)
                {
                    _backupWidgetService?.ShowForTrayBackup();
                }

                var snapshot = new BackupSnapshotItem
                {
                    Id = backupId.ToString()
                };
                OnDeleteBackupRequested(snapshot);
            }
            catch
            {
                // Ignore tray errors to avoid blocking menu actions.
            }
            finally
            {
                TrayMenuRefreshRequested?.Invoke();
            }
        }

        private void EnsureNasMonitorStarted()
        {
            if (_nasMonitorTimer != null)
                return;

            // Check every 5 minutes; first check after 2 minutes.
            _nasMonitorTimer = new Timer(
                _ => _ = CheckNasAndMigrateAsync(),
                null,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(5));
        }

        private void StopNasMonitor()
        {
            _nasMonitorTimer?.Dispose();
            _nasMonitorTimer = null;
        }

        private void EnsureDestinationProbeStarted()
        {
            if (_destinationProbeTimer is not null)
                return;

            _destinationProbeTimer = new Timer(
                _ => _ = ProbeDestinationsAsync(),
                null,
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(10));

            var initialDelay = DateTime.UtcNow - _appStartUtc < TimeSpan.FromSeconds(10)
                ? TimeSpan.FromSeconds(10)
                : TimeSpan.Zero;
            _ = Task.Run(async () =>
            {
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay).ConfigureAwait(false);
                await ProbeDestinationsAsync().ConfigureAwait(false);
            });
        }

        public IReadOnlyList<DestinationProbeSummary> GetDestinationProbeSummaries()
        {
            return GetDestinationProbeSummaries(_config);
        }

        private IReadOnlyList<DestinationProbeSummary> GetDestinationProbeSummaries(AppConfig cfg)
        {
            var destinations = GetActiveDestinations(cfg);
            if (destinations.Count == 0)
            {
                _destinationProbeSummaries.Clear();
                return Array.Empty<DestinationProbeSummary>();
            }

            var activeIds = new HashSet<string>(
                destinations.Select(DestinationStatusItem.GetId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var id in _destinationProbeSummaries.Keys.ToList())
            {
                if (!activeIds.Contains(id))
                {
                    _destinationProbeSummaries.TryRemove(id, out _);
                }
            }

            var summaries = _destinationProbeSummaries.Values
                .Where(s => activeIds.Contains(s.Id))
                .OrderBy(s => s.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var signature = BuildDestinationProbeSignature(summaries);
            lock (_destinationProbeCacheGate)
            {
                if (signature == _cachedDestinationProbeSignature &&
                    _cachedDestinationProbeSummaries.Count == summaries.Count)
                {
                    return _cachedDestinationProbeSummaries;
                }

                _cachedDestinationProbeSignature = signature;
                _cachedDestinationProbeSummaries = summaries;
            }

            return summaries;
        }

        private static string BuildDestinationProbeSignature(IReadOnlyList<DestinationProbeSummary> summaries)
        {
            if (summaries.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var summary in summaries)
            {
                sb.Append(summary.Id).Append('|')
                  .Append(summary.Reachable).Append('|')
                  .Append(summary.Message).Append('|')
                  .Append(summary.LastChecked.ToString("O")).Append(';');
            }
            return sb.ToString();
        }

        private async Task ProbeDestinationsAsync()
        {
            if (Interlocked.Exchange(ref _destinationProbeInFlight, 1) == 1)
                return;

            try
            {
                var cfg = AppConfigStore.Load();
                var destinations = GetActiveDestinations(cfg);

                var now = DateTime.UtcNow;
                foreach (var dest in destinations)
                {
                    if (!dest.Active)
                        continue;

                    var id = DestinationStatusItem.GetId(dest);
                    _destinationProbeSummaries.TryGetValue(id, out var previous);
                    if (previous is not null &&
                        previous.Reachable &&
                        (now - previous.LastChecked) < DestinationProbeMinInterval)
                    {
                        continue;
                    }

                    var result = await Task.Run(() => TryTestDestination(dest, cfg));
                    UpdateDestinationProbeSummary(dest, result);

                    if (result.Reachable && (previous is null || !previous.Reachable))
                    {
                        TryImportMetadataForDestination(cfg, dest, result.EffectivePath);
                    }

                    if (!result.Reachable && (previous is null || previous.Reachable))
                    {
                        var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
                        var message = string.IsNullOrWhiteSpace(result.Message)
                            ? L("Destinations.Probe.DefaultHint", "Check mount/credentials.")
                            : result.Message;
                        GlobalNotificationCenter.Instance.Show(
                            Lf("Destinations.Probe.UnreachableMessage", "Destination '{0}' is unreachable. {1}", name, message),
                            NotificationSeverity.Warning,
                            L("Destinations.Probe.UnreachableTitle", "Destination unreachable"));
                    }
                }
            }
            catch
            {
                // swallow background probe errors
            }
            finally
            {
                Interlocked.Exchange(ref _destinationProbeInFlight, 0);
            }
        }

        private void UpdateDestinationProbeSummary(BackupDestination dest, DestinationTestResult result)
        {
            var id = DestinationStatusItem.GetId(dest);
            var alias = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias ?? dest.Path ?? string.Empty;
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? (result.Reachable
                    ? LStatic("Destinations.Test.Reachable", "Reachable")
                    : LStatic("Destinations.Test.Unavailable", "Unavailable"))
                : result.Message;

            var severity = result.Reachable
                ? (message.Contains(LStatic("Destinations.Test.ReadOnly", "Read-only"), StringComparison.OrdinalIgnoreCase)
                    ? "Warning"
                    : "Success")
                : "Error";

            _destinationProbeSummaries[id] = new DestinationProbeSummary(
                id,
                alias,
                dest.Path ?? string.Empty,
                result.Reachable,
                message,
                DateTime.UtcNow);

            BackupsViewModel.UpdateDestinationStatus(id, message, severity);
        }

        private async void OnRefreshHistoryRequested()
        {
            try
            {
                await RefreshMetadataNowAsync();
            }
            catch
            {
                // ignore manual refresh failures for now
            }
        }

        private async Task RefreshMetadataNowAsync()
        {
            var cfg = await Task.Run(AppConfigStore.Load);
            if (!cfg.Backups.EnableMetadataSync)
            {
                Console.WriteLine("[MetadataSync] Refresh skipped: metadata sync disabled.");
                return;
            }

            Console.WriteLine("[MetadataSync] Manual refresh started.");
            var refreshNeeded = false;

            if (!string.IsNullOrWhiteSpace(cfg.ProjectsRoot))
            {
                var options = new MetadataSyncOptions(
                    AllowCreateProjects: true,
                    MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
                var preview = await Task.Run(() => _metadataSyncService.PreviewImportFromStore(cfg.ProjectsRoot, options));
                var label = L("MetadataSync.Review.SourceProjectsRoot", "Projects root");
                    if (await ConfirmMetadataImportAsync(preview, label))
                    {
                        var result = await Task.Run(() => _metadataSyncService.ImportFromStore(cfg.ProjectsRoot, options));
                        Console.WriteLine($"[MetadataSync] Manual refresh (projects root) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                        _ = Task.Run(() => ApplyRetentionAfterMetadataImport(cfg.ProjectsRoot, result));
                        refreshNeeded |= result.Status == MetadataSyncStatus.Success &&
                                         (result.ImportedProjects > 0 ||
                                          result.ImportedSnapshots > 0 ||
                                          result.ImportedBackups > 0 ||
                                      result.AppliedTombstones > 0);
                }
            }

            var destinations = GetActiveDestinations(cfg);
            foreach (var dest in destinations)
            {
                if (!dest.EnableMetadataSync)
                    continue;

                var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                    ? null
                    : cfg.Network.Credentials.FirstOrDefault(c =>
                        c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                var resolution = await Task.Run(() => _networkMountService.PrepareDestination(dest, profile));
                if (!resolution.IsSuccess)
                    continue;

                try
                {
                    var options = new MetadataSyncOptions(
                        AllowCreateProjects: true,
                        MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
                    var preview = await Task.Run(() => _metadataSyncService.PreviewImportFromStore(resolution.EffectivePath, options));
                    var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
                    var label = Lf("MetadataSync.Review.SourceDestination", "Destination: {0}", name);
                    if (await ConfirmMetadataImportAsync(preview, label))
                    {
                        var result = await Task.Run(() => _metadataSyncService.ImportFromStore(resolution.EffectivePath, options));
                        Console.WriteLine($"[MetadataSync] Manual refresh ({name}) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                        _ = Task.Run(() => ApplyRetentionAfterMetadataImport(resolution.EffectivePath, result));
                        refreshNeeded |= result.Status == MetadataSyncStatus.Success &&
                                         (result.ImportedProjects > 0 ||
                                          result.ImportedSnapshots > 0 ||
                                          result.ImportedBackups > 0 ||
                                          result.AppliedTombstones > 0);
                    }
                }
                finally
                {
                    _networkMountService.Cleanup(resolution);
                }
            }

            if (refreshNeeded)
            {
                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();
                await _projectsViewModel.RefreshAsync();
            }
        }

        private async Task<bool> ConfirmMetadataImportAsync(MetadataSyncPreview preview, string sourceLabel)
        {
            if (preview.Status != MetadataSyncStatus.Success)
            {
                return false;
            }

            if (!preview.HasChanges)
            {
                Console.WriteLine($"[MetadataSync] Preview found no changes for '{sourceLabel}'.");
                return false;
            }

            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var window = new Views.MetadataSyncReviewWindow
                {
                    DataContext = new ViewModels.MetadataSyncReviewViewModel(_localizationService, preview, sourceLabel)
                };

                var owner = GetMainWindow();
                if (owner != null)
                {
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                return window.DataContext is ViewModels.MetadataSyncReviewViewModel vm && vm.Confirmed;
            });
        }

        private static Window? GetMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        private DestinationTestResult TryTestDestination(BackupDestination dest, AppConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(dest.Path))
                return new DestinationTestResult(false, false, string.Empty, LStatic("Destinations.Test.EmptyPath", "Destination path is empty."));

            DiagnosticsLogger.Record($"Destination test start: '{dest.Alias ?? dest.Path}'.");
            var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                ? null
                : cfg.Network.Credentials.FirstOrDefault(c =>
                    c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

            var resolution = _networkMountService.PrepareDestination(dest, profile);
            if (!resolution.IsSuccess)
            {
                DiagnosticsLogger.Record($"Destination test failed: '{dest.Alias ?? dest.Path}' - {resolution.Message}");
                return new DestinationTestResult(false, false, resolution.EffectivePath ?? string.Empty, resolution.Message);
            }

            var testTarget = resolution.EffectivePath;

            try
            {
                Directory.CreateDirectory(testTarget);

                var writable = true;
                var message = LStatic("Destinations.Test.Reachable", "Reachable");

                try
                {
                    if (!TryWriteProbeFile(testTarget))
                    {
                        writable = false;
                        message = LStatic("Destinations.Test.ReadOnly", "Read-only");
                    }
                }
                catch
                {
                    writable = false;
                    message = LStatic("Destinations.Test.ReadOnly", "Read-only");
                }

                return new DestinationTestResult(true, writable, testTarget, message);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Destination test exception: '{dest.Alias ?? dest.Path}' - {ex.GetType().Name} - {ex.Message}");
                return new DestinationTestResult(false, false, testTarget, ex.Message);
            }
            finally
            {
                DiagnosticsLogger.Record($"Destination test complete: '{dest.Alias ?? dest.Path}'.");
                if (resolution.MountedByUs)
                {
                    // Respect destination auto-unmount setting for reachability probes.
                    var cleanupDest = new BackupDestination
                    {
                        Path           = resolution.EffectivePath,
                        CredentialName = dest.CredentialName,
                        Active         = dest.Active,
                        AutoMount      = dest.AutoMount,
                        AutoUnmount    = dest.AutoUnmount,
                        PreMounted     = dest.PreMounted,
                        Alias          = dest.Alias
                    };

                    var cleanupResolution = resolution with { Destination = cleanupDest };
                    _networkMountService.Cleanup(cleanupResolution);
                }
            }
        }

        private sealed record DestinationTestResult(bool Reachable, bool Writable, string EffectivePath, string Message);

        private static bool TryWriteProbeFile(string effectivePath)
        {
            var testFile = Path.Combine(effectivePath, $".vaultsync_destination_test_{Guid.NewGuid():N}");
            try
            {
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(testFile))
                        File.Delete(testFile);
                }
                catch
                {
                    // best effort cleanup
                }
            }
        }

        private async Task<int?> EnsureArchiveUploadBufferAsync(
            BackupDestination dest,
            AppConfig cfg,
            string effectivePath,
            bool useArchiveMode,
            CancellationToken ct)
        {
            if (!useArchiveMode)
                return null;

            if (!cfg.Backups.EnableArchiveUploadAutoTune)
            {
                var configured = GetConfiguredArchiveUploadBufferBytes(cfg, dest);
                if (configured.HasValue && configured.Value > 0)
                    return configured.Value;

                if (IsSmbPath(dest.Path) || IsSmbPath(effectivePath))
                    return 1024 * 1024;

                return 1024 * 1024;
            }

            var existing = GetConfiguredArchiveUploadBufferBytes(cfg, dest);
            if (existing.HasValue && existing.Value > 0)
                return existing.Value;

            try
            {
                var display = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;
                Console.WriteLine($"[DestinationProbe] Auto-tuning archive upload buffer for '{display}'.");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeoutSeconds = IsSmbPath(dest.Path) || IsSmbPath(effectivePath) ? 8 : 3;
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var result = await Task.Run(() => ProbeArchiveUploadBufferBytes(effectivePath, timeoutCts.Token), timeoutCts.Token);
                SaveArchiveUploadBufferBytes(cfg, dest, result.BufferBytes);

                var bufferMb = result.BufferBytes / (1024d * 1024d);
                Console.WriteLine($"[DestinationProbe] Archive upload buffer for '{display}' set to {bufferMb:0.#} MB ({result.Mbps:0.0} MB/s).");

                return result.BufferBytes;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    throw;

                Console.WriteLine($"[DestinationProbe] Auto-tune timed out for '{dest.Path}'. Falling back to default buffer.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DestinationProbe] Auto-tune failed for '{dest.Path}': {ex.Message}");
                return null;
            }
        }

        private int? GetConfiguredArchiveUploadBufferBytes(AppConfig cfg, BackupDestination dest)
        {
            if (cfg.Backups.UseAdvancedDestinations)
            {
                var match = FindMatchingDestination(cfg, dest);
                return match?.ArchiveUploadBufferBytes;
            }

            return cfg.Backups.LegacyArchiveUploadBufferBytes;
        }

        private void SaveArchiveUploadBufferBytes(AppConfig cfg, BackupDestination dest, int bufferBytes)
        {
            if (bufferBytes <= 0)
                return;

            if (cfg.Backups.UseAdvancedDestinations)
            {
                var match = FindMatchingDestination(cfg, dest);
                if (match is null)
                    return;

                match.ArchiveUploadBufferBytes = bufferBytes;
            }
            else
            {
                cfg.Backups.LegacyArchiveUploadBufferBytes = bufferBytes;
            }

            AppConfigStore.Save(cfg);
        }

        private static (int BufferBytes, double Mbps) ProbeArchiveUploadBufferBytes(string effectivePath, CancellationToken ct)
        {
            const int probeSizeBytes = 64 * 1024 * 1024;
            const int chunkSizeBytes = 4 * 1024 * 1024;
            const int fallbackBytes  = 4 * 1024 * 1024;

            var probeDir  = Path.Combine(effectivePath, ".vaultsync");
            var probeFile = Path.Combine(probeDir, $".upload_probe_{Guid.NewGuid():N}.bin");
            var createdDir = false;

            try
            {
                if (!Directory.Exists(probeDir))
                {
                    Directory.CreateDirectory(probeDir);
                    createdDir = true;
                }

                var buffer = new byte[chunkSizeBytes];
                var remaining = probeSizeBytes;
                var sw = Stopwatch.StartNew();

                using (var fs = new FileStream(
                           probeFile,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None,
                           chunkSizeBytes,
                           FileOptions.SequentialScan))
                {
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var toWrite = Math.Min(chunkSizeBytes, remaining);
                        fs.Write(buffer, 0, toWrite);
                        remaining -= toWrite;
                    }

                    fs.Flush(true);
                }

                sw.Stop();
                var seconds = Math.Max(0.05, sw.Elapsed.TotalSeconds);
                var mbps = (probeSizeBytes / seconds) / (1024d * 1024d);
                var bufferBytes = SelectArchiveUploadBufferBytes(mbps);
                return (bufferBytes, mbps);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (fallbackBytes, 0);
            }
            finally
            {
                try
                {
                    if (File.Exists(probeFile))
                        File.Delete(probeFile);
                }
                catch
                {
                    // best-effort cleanup
                }

                if (createdDir)
                {
                    try
                    {
                        if (Directory.Exists(probeDir) && !Directory.EnumerateFileSystemEntries(probeDir).Any())
                            Directory.Delete(probeDir);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }
            }
        }

        private static int SelectArchiveUploadBufferBytes(double mbps)
        {
            if (mbps < 5)
                return 2 * 1024 * 1024;
            if (mbps < 15)
                return 4 * 1024 * 1024;
            if (mbps < 50)
                return 8 * 1024 * 1024;
            if (mbps < 150)
                return 16 * 1024 * 1024;
            if (mbps < 400)
                return 32 * 1024 * 1024;

            return 64 * 1024 * 1024;
        }

        private bool IsMetadataSyncEnabled(AppConfig cfg, BackupDestination dest)
        {
            if (!cfg.Backups.EnableMetadataSync)
                return false;

            if (!dest.EnableMetadataSync)
                return false;

            return true;
        }

        private bool IsMetadataImportEnabled(AppConfig cfg, BackupDestination dest)
        {
            if (!IsMetadataSyncEnabled(cfg, dest))
                return false;

            if (!cfg.Backups.AutoImportMetadata)
                return false;

            if (!dest.AutoImportMetadata)
                return false;

            return true;
        }

        private void TryImportMetadataForDestination(AppConfig cfg, BackupDestination dest, string effectivePath)
        {
            if (!IsMetadataImportEnabled(cfg, dest))
                return;

            if (string.IsNullOrWhiteSpace(effectivePath))
                return;

            var key = effectivePath.Trim();
            if (_metadataImportRetryAfter.TryGetValue(key, out var retryAfter) &&
                DateTime.UtcNow < retryAfter)
            {
                return;
            }
            if (_metadataImportAttempts.TryGetValue(key, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromMinutes(5))
            {
                return;
            }

            _metadataImportAttempts[key] = DateTime.UtcNow;
            var options = new MetadataSyncOptions(
                AllowCreateProjects: true,
                MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
            var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
            _ = Task.Run(() =>
            {
                try
                {
                    Console.WriteLine($"[MetadataSync] Auto import started for '{name}'.");
                    var result = _metadataSyncService.ImportFromStore(effectivePath, options);
                    Console.WriteLine($"[MetadataSync] Auto import ({name}) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                    if (result.Status == MetadataSyncStatus.Success &&
                        (result.ImportedProjects > 0 || result.ImportedSnapshots > 0 || result.ImportedBackups > 0 || result.AppliedTombstones > 0))
                    {
                        GlobalNotificationCenter.Instance.Show(
                            Lf("MetadataSync.Notification.Imported", "Imported updates from '{0}'.", name),
                            NotificationSeverity.Info,
                            L("MetadataSync.Notification.Title", "Metadata import"));
                    }
                    if (result.Status != MetadataSyncStatus.Success)
                    {
                        _metadataImportRetryAfter[key] = DateTime.UtcNow.AddMinutes(15);
                    }
                    ApplyRetentionAfterMetadataImport(effectivePath, result);
                    MaybeRefreshAfterImport(result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Auto import failed for '{name}': {ex.Message}");
                    _metadataImportRetryAfter[key] = DateTime.UtcNow.AddMinutes(15);
                    var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                    var actionCommand = CreateCopyLogSnippetCommand(
                        Lf("Logs.Snippet.MetadataImportFailure", "Metadata import failed for '{0}'.", name));
                    GlobalNotificationCenter.Instance.Show(
                        Lf("MetadataSync.Notification.ImportFailed", "Metadata import failed for '{0}'. Check logs for details.", name),
                        NotificationSeverity.Error,
                        L("MetadataSync.Notification.Title", "Metadata import"),
                        actionLabel: actionLabel,
                        actionCommand: actionCommand);
                }
            });
        }

        private void TryImportMetadataFromRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return;

            var cfg = _config;
            if (!cfg.Backups.EnableMetadataSync || !cfg.Backups.AutoImportMetadata)
                return;

            if (DateTime.UtcNow < _metadataRootImportRetryAfterUtc)
                return;

            var options = new MetadataSyncOptions(
                AllowCreateProjects: true,
                MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
            try
            {
                Console.WriteLine("[MetadataSync] Auto import started for projects root.");
                var result = _metadataSyncService.ImportFromStore(rootPath, options);
                Console.WriteLine($"[MetadataSync] Auto import (projects root) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                if (result.Status != MetadataSyncStatus.Success)
                {
                    _metadataRootImportRetryAfterUtc = DateTime.UtcNow.AddMinutes(15);
                }
                ApplyRetentionAfterMetadataImport(rootPath, result);
                MaybeRefreshAfterImport(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MetadataSync] Auto import failed for projects root: {ex.Message}");
                _metadataRootImportRetryAfterUtc = DateTime.UtcNow.AddMinutes(15);
                var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                var actionCommand = CreateCopyLogSnippetCommand(
                    L("Logs.Snippet.MetadataImportRootFailure", "Metadata import failed for projects root."));
                GlobalNotificationCenter.Instance.Show(
                    L("MetadataSync.Notification.ImportRootFailed", "Metadata import failed for projects root. Check logs for details."),
                    NotificationSeverity.Error,
                    L("MetadataSync.Notification.Title", "Metadata import"),
                    actionLabel: actionLabel,
                    actionCommand: actionCommand);
            }
        }

        private void MaybeRefreshAfterImport(MetadataSyncResult result)
        {
            if (result.Status != MetadataSyncStatus.Success)
                return;

            if (result.ImportedProjects <= 0 &&
                result.ImportedSnapshots <= 0 &&
                result.ImportedBackups <= 0 &&
                result.AppliedTombstones <= 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(RefreshUiAfterMetadataImport);
        }

        private void ApplyRetentionAfterMetadataImport(string rootPath, MetadataSyncResult result)
        {
            if (result.Status != MetadataSyncStatus.Success || result.ImportedBackups <= 0)
                return;

            if (string.IsNullOrWhiteSpace(rootPath))
                return;

            try
            {
                var cfg = AppConfigStore.Load();
                var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
                if (maxSnapshotsToKeep <= 0)
                    return;

                var projects = result.AffectedProjectIds.Count > 0
                    ? result.AffectedProjectIds
                    : _repo.GetAllProjects().Select(p => p.Id).ToArray();

                foreach (var projectId in projects)
                {
                    _backupService.EnforceRetentionForProject(projectId, rootPath, maxSnapshotsToKeep);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MetadataSync] Retention after import failed: {ex.Message}");
            }
        }

        private void RefreshUiAfterMetadataImport()
        {
            _ = RefreshUiAfterMetadataImportAsync();
        }

        private async Task RefreshUiAfterMetadataImportAsync()
        {
            if (Interlocked.Exchange(ref _metadataUiRefreshInFlight, 1) == 1)
            {
                Interlocked.Exchange(ref _metadataUiRefreshQueued, 1);
                return;
            }

            try
            {
                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();
                await _projectsViewModel.RefreshAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _metadataUiRefreshInFlight, 0);
                if (Interlocked.Exchange(ref _metadataUiRefreshQueued, 0) == 1)
                {
                    RefreshUiAfterMetadataImport();
                }
            }
        }

        private void TryExportMetadataForBackup(AppConfig cfg, BackupDestination dest, string effectivePath, int backupId)
        {
            if (!IsMetadataSyncEnabled(cfg, dest))
                return;

            if (string.IsNullOrWhiteSpace(effectivePath) || backupId <= 0)
                return;

            var machineId = Environment.MachineName;
            var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
            _ = Task.Run(() =>
            {
                try
                {
                    Console.WriteLine($"[MetadataSync] Export started for backup {backupId} -> '{name}'.");
                    var result = _metadataSyncService.ExportBackupToStore(
                        effectivePath,
                        backupId,
                        _currentVersionString,
                        machineId,
                        dest.ForceMetadataBackfill);
                    Console.WriteLine($"[MetadataSync] Export ({name}) result: {result.Status}.");
                    if (dest.ForceMetadataBackfill && result.Status == MetadataSyncStatus.Success)
                    {
                        ClearDestinationForceBackfill(dest);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Export failed for '{name}': {ex.Message}");
                }
            });
        }

        private void ClearDestinationForceBackfill(BackupDestination dest)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    var destEntry = FindMatchingDestination(cfg, dest);
                    if (destEntry != null && destEntry.ForceMetadataBackfill)
                    {
                        destEntry.ForceMetadataBackfill = false;
                        AppConfigStore.Save(cfg);
                    }

                    if (_settingsViewModel is null)
                        return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        var vmDest = _settingsViewModel.Destinations
                            .FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, dest));
                        if (vmDest != null)
                        {
                            vmDest.ForceMetadataBackfill = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Failed to clear force-backfill flag: {ex.Message}");
                }
            });
        }

        private static BackupDestination? FindMatchingDestination(AppConfig cfg, BackupDestination target)
        {
            if (cfg.Backups.Destinations is null || cfg.Backups.Destinations.Count == 0)
                return null;

            return cfg.Backups.Destinations.FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, target));
        }

        private static bool DestinationsMatch(string? path, string? alias, BackupDestination target)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !string.IsNullOrWhiteSpace(target.Path) &&
                string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(alias) &&
                !string.IsNullOrWhiteSpace(target.Alias) &&
                string.Equals(alias, target.Alias, StringComparison.OrdinalIgnoreCase);
        }

        private Task CheckNasAndMigrateAsync()
        {
            if (Interlocked.Exchange(ref _nasMonitorInFlight, 1) == 1)
                return Task.CompletedTask;

            try
            {
                if (BackupsViewModel.IsBusy)
                    return Task.CompletedTask;

                var cfg = AppConfigStore.Load();

                if (_settingsViewModel?.PreferExternalDrives != true)
                    return Task.CompletedTask;

                var backupRoot = cfg.Backups.BackupRoot;
                if (string.IsNullOrWhiteSpace(backupRoot) || !IsNetworkPath(backupRoot))
                    return Task.CompletedTask;

                if (!Directory.Exists(backupRoot))
                    return Task.CompletedTask;

                var projects = _repo.GetAllProjects().ToList();
                var hadTemp = false;

                foreach (var project in projects)
                {
                    var tempRoot = Path.Combine(project.RootPath, ".vaultsync-temp-backups");
                    if (Directory.Exists(tempRoot))
                    {
                        hadTemp = true;
                        TryMigrateTempBackups(project, backupRoot);
                    }
                }

                // If no temp backups remain anywhere, stop the monitor to avoid unnecessary pings.
                if (!hadTemp)
                {
                    StopNasMonitor();
                }
            }
            catch (Exception ex)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _nasMonitorInFlight, 0);
            }

            return Task.CompletedTask;
        }

        private void TryDeleteSnapshotIfOrphan(int projectId, int snapshotId)
        {
            try
            {
                // If any other backup references this snapshot, keep it.
                var remaining = _repo.HasBackupForSnapshot(projectId, snapshotId);
                if (remaining)
                    return;

                var project = _repo.GetProjectById(projectId);
                if (project is null)
                    return;

                _repo.DeleteSnapshotsById(project.Name, new[] { snapshotId });
            }
            catch
            {
                // Ignore snapshot cleanup failures for now.
            }
        }

        private void OnBackupProtectionChanged(int backupId, bool isProtected)
        {
            try
            {
                _repo.SetBackupProtection(backupId, isProtected);
                UpdateBackupProtectionMarker(backupId, isProtected);
            }
            catch
            {
                // swallow for now; could surface notification later
            }
        }

        private sealed record DriveHealthDecision(bool Block, string Message, NotificationSeverity Severity);

        private async Task<DriveHealthDecision> EvaluateDriveHealthAsync(string projectPath, string backupPath)
        {
            return await Task.Run(() =>
            {
                var block = ShouldBlockForDriveHealth(projectPath, backupPath, out var msg, out var sev);
                return new DriveHealthDecision(block, msg, sev);
            });
        }

        private bool ShouldBlockForDriveHealth(string projectPath, string backupPath, out string message, out NotificationSeverity severity)
        {
            message  = string.Empty;
            severity = NotificationSeverity.Warning;

            if (_settingsViewModel?.ShowDriveHealthWarnings != true)
                return false;

            var results = new List<DriveHealthResult>
            {
                _driveHealthService.CheckPath(projectPath),
                _driveHealthService.CheckPath(backupPath)
            };

            DriveHealthResult? issue = null;
            foreach (var r in results)
            {
                if (r.Status == DriveHealthStatus.Failing)
                {
                    issue = r;
                    break;
                }

                if (r.Status == DriveHealthStatus.Warning && issue is null)
                {
                    issue = r;
                }
            }

            if (issue is null || issue.Status == DriveHealthStatus.Unknown || issue.Status == DriveHealthStatus.Healthy)
                return false;

            var driveLabel = issue.DriveId ?? issue.Path ?? L("DriveHealth.UnknownDrive", "drive");
            severity = issue.Status == DriveHealthStatus.Failing
                ? NotificationSeverity.Error
                : NotificationSeverity.Warning;

            message = issue.Status == DriveHealthStatus.Failing
                ? Lf("DriveHealth.BlockedMessage", "Backup skipped: drive health failing on {0} ({1}).", driveLabel, issue.Message)
                : Lf("DriveHealth.WarningMessage", "Drive health warning on {0}: {1}.", driveLabel, issue.Message);

            return issue.Status == DriveHealthStatus.Failing;
        }

        private void ShowDriveHealthNotification(string message, NotificationSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!NotificationsEnabled)
                return;

            var title = severity == NotificationSeverity.Error
                ? L("Backups.Notification.DriveBlockedTitle", "Backup blocked: drive health")
                : L("Backups.Notification.DriveWarningTitle", "Drive health warning");

            GlobalNotificationCenter.Instance.Show(
                message,
                severity,
                title);

            if (ShouldRaiseSystemNotification)
            {
                GlobalNotificationCenter.Instance.ShowSystem(
                    message,
                    severity,
                    title);
            }
        }

        /// <summary>
        /// Returns true when backups should be paused because the device is on battery and the user enabled the setting.
        /// </summary>
        private bool ShouldPauseBackupsForBattery(out string reason)
        {
            reason = L("Backups.Notification.BatteryPaused", "Backups paused on battery power.");

            if (_settingsViewModel?.PauseBackupsOnBattery != true)
                return false;

            return _powerStatusProvider.GetPowerState() == PowerState.OnBattery;
        }

        private string L(string key, string fallback) => LStatic(key, fallback);
        private string Lf(string key, string fallback, params object[] args)
        {
            var text = L(key, fallback);
            return args is { Length: > 0 }
                ? string.Format(text, args)
                : text;
        }

        private static string LStatic(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private static string ResolveSystemLanguageCode(LocalizationService localizationService)
        {
            var uiLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (localizationService.SupportedLanguages.Any(l =>
                    string.Equals(l.Code, uiLang, StringComparison.OrdinalIgnoreCase)))
            {
                return uiLang;
            }

            return "en";
        }

        private void ShowBackupSkipNotification(string message, NotificationSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var title = severity switch
            {
                NotificationSeverity.Error   => L("Backups.Notification.ErrorTitle", "Backup error"),
                NotificationSeverity.Warning => L("Backups.Notification.WarningTitle", "Backup paused"),
                _                            => L("Backups.Notification.InfoTitle", "Backup info")
            };

            // In-page banner when user is on Backups; otherwise global toast.
            if (IsOnBackupsPage)
            {
                BackupsViewModel.ShowNotification(message, severity.ToString());
            }
            else
            {
                GlobalNotificationCenter.Instance.Show(
                    message,
                    severity,
                    title);
            }

            // OS notification when allowed.
            if (ShouldRaiseSystemNotification)
            {
                GlobalNotificationCenter.Instance.ShowSystem(
                    message,
                    severity,
                    title);
            }
        }

        private void MaybeNotifyRestoreRecommended(Project project)
        {
            if (project == null)
                return;

            if (!_restoreAdvisoryShown.TryAdd(project.Id, 0))
                return;

            var message = Lf(
                "Backups.Notification.RestoreRequiredForProject",
                "Imported history is newer for '{0}'. Consider restoring before creating new backups.",
                project.Name);
            ShowBackupSkipNotification(message, NotificationSeverity.Warning);
        }

        private bool TryResolveProjectRoot(Project project, AppConfig cfg, out Project resolvedProject, out string errorMessage)
        {
            resolvedProject = project;
            errorMessage = string.Empty;

            if (project is null)
            {
                errorMessage = L("Backups.Notification.ProjectRootMissing", "Project is not available on this machine. Update the project path or restore it.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(project.RootPath) && Directory.Exists(project.RootPath))
                return true;

            var projectsRoot = cfg.ProjectsRoot;
            if (!string.IsNullOrWhiteSpace(projectsRoot))
            {
                var fallback = Path.Combine(projectsRoot, project.Name);
                if (Directory.Exists(fallback))
                {
                    _repo.UpdateProjectPath(project.Name, fallback, out _);
                    resolvedProject = project with { RootPath = fallback };
                    return true;
                }
            }

            var expected = string.IsNullOrWhiteSpace(project.RootPath)
                ? (projectsRoot ?? string.Empty)
                : project.RootPath;
            errorMessage = Lf(
                "Backups.Notification.ProjectRootMissing",
                "Project '{0}' isn't available on this machine. Expected at '{1}'. Update the project path or restore it.",
                project.Name,
                expected);
            return false;
        }

        private void MaybeNotifyProjectRootMissing(Project project, string message)
        {
            if (project == null || string.IsNullOrWhiteSpace(message))
                return;

            if (!_projectRootMissingNotified.TryAdd(project.Id, 0))
                return;

            ShowBackupSkipNotification(message, NotificationSeverity.Error);
        }
    }
}
