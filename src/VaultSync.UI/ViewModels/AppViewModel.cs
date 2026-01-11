using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
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
        private readonly DashboardViewModel _dashboardViewModel;
        private readonly ProjectsViewModel  _projectsViewModel;
        private readonly BackupsViewModel   _backupsViewModel;
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
        private readonly LogConsoleService _logConsoleService;
        private LogConsoleWindow? _logConsoleWindow;
        private readonly ConcurrentDictionary<string, DestinationProbeSummary> _destinationProbeSummaries = new();
        private readonly ConcurrentDictionary<string, DateTime> _metadataImportAttempts = new();
        private readonly ConcurrentDictionary<int, byte> _manualBackupInFlight = new();
        private int _manualBackupInFlightCount;
        private int _backupAllInProgress;
        private bool _trayInitiatedBackup;
        private readonly GitHubUpdateService _updateService = new();
        private readonly PatchUpdateService _patchService = new();
        private readonly LocalizationService _localizationService = new();
        private static readonly HttpClient s_installerClient = CreateInstallerHttpClient();
        private readonly string _currentVersionString;
        private CancellationTokenSource? _updateCheckCts;
        private Timer? _updateCheckTimer;
        private Timer? _updateCheckRetryTimer;
        private int _updateCheckInFlight;
        private DateTimeOffset? _lastUpdateCheckAt;
        private string? _lastUpdateCheckError;
        private UpdateCheckResult? _pendingUpdateResult;
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
        private bool _isPatchInstalling;
        private string _patchStatusMessage = string.Empty;

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

        private GitHubReleaseChannel CurrentUpdateChannel =>
            _settingsViewModel?.BetaChannelEnabled == true
                ? GitHubReleaseChannel.Beta
                : GitHubReleaseChannel.Stable;

        public SettingsViewModel SettingsViewModel => _settingsViewModel;
        public BackupsViewModel BackupsViewModel => _backupsViewModel;

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

        public bool ShowInstallerFallback => _patchFailed && (_pendingUpdateResult?.HasInstaller == true);

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
        public ICommand OpenReleasePageCommand => _openReleaseCommand;
        public ICommand InstallPatchCommand => _installPatchCommand;
        public ICommand SkipUpdateCommand => _skipUpdateCommand;
        public ICommand DismissUpdateBannerCommand => _dismissUpdateBannerCommand;
        public string CurrentVersionDisplay => $"v{StripBuildMetadata(_currentVersionString)}";
        public string FooterProductDisplay => $"VaultSync · {CurrentVersionDisplay}";
        public string FooterCopyrightDisplay => $"© {DateTime.UtcNow.Year} Flavio Giacchetti";

        public AppViewModel()
        {
            _currentVersionString = GetCurrentVersionString();

            // 1) Config + DB + services
            _config = AppConfigStore.Load();
            var targetLang = string.IsNullOrWhiteSpace(_config.Advanced.Language)
                ? _localizationService.CurrentLanguage
                : _config.Advanced.Language;
            _localizationService.SetLanguage(targetLang);
            LocalizationProvider.Initialize(_localizationService);
            _localizationService.LanguageChanged += OnLanguageChanged;

            _repo = new SqliteRepository(_config.DbPath ?? string.Empty);
            _repo.EnsureSchema();

            _backupService       = new BackupService(_repo);
            _metadataSyncService = new MetadataSyncService(_repo);
            _networkMountService = new NetworkMountService();
            _credentialVault     = CredentialVault.Instance;
            _notificationService = new NotificationService();
            _powerStatusProvider = new PowerStatusProvider();
            _driveHealthService  = new DriveHealthService();

            // 2) Section viewmodels
            _dashboardViewModel = new DashboardViewModel();
            _projectsViewModel  = new ProjectsViewModel();
            _backupsViewModel   = new BackupsViewModel();
            _settingsViewModel  = new SettingsViewModel(_localizationService);
            _settingsViewModel.PropertyChanged += OnSettingsChanged;
            _settingsViewModel.OpenLogConsoleRequested += OnOpenLogConsoleRequested;
            _settingsViewModel.UpdateCheckRequested += OnUpdateCheckRequested;
            _settingsViewModel.RefreshHistoryRequested += OnRefreshHistoryRequested;
            _settingsViewModel.UpdateUpdateCheckStatus(null, null);

            _logConsoleService = new LogConsoleService();
            _logConsoleService.InstallCapture();
            LogConsoleProvider.Initialize(_logConsoleService);
            UpdateLogConsoleSettings();

            // 3) Wire BackupsViewModel events to real logic
            _backupsViewModel.BackupProjectRequested += OnBackupProjectRequested;
            _backupsViewModel.CreateBackupForAllProjectsRequested += OnCreateBackupForAllProjectsRequested;
            _backupsViewModel.DeleteBackupRequested += OnDeleteBackupRequested;
            _backupsViewModel.RestoreBackupRequested += OnRestoreBackupRequested; // stub for later
            _backupsViewModel.CancelActiveBackupRequested += OnCancelActiveBackupRequested;
            _backupsViewModel.AutoBackupPreferenceChanged += OnAutoBackupPreferenceChanged;
            _backupsViewModel.BackupProtectionChanged += OnBackupProtectionChanged;

            // 4) Initial load of backup data
            ReloadBackupsVmData();
            _ = _dashboardViewModel.RefreshAsync();

            // 5) Default route
            // Default route (may be overridden by resume-last-session)
            CurrentView  = _dashboardViewModel;
            HeaderTitle  = L("Nav.Dashboard", "Dashboard");
            HeaderKicker = L("Main.HeaderOverview", "Overview");

            if (_config.ResumeLastSession)
            {
                ApplyLastSessionView();
            }

            // Ensure launch-on-login matches config
            AutoStartService.SetLaunchOnLogin(_config.Behavior.LaunchOnLogin);
            ConfigureAutoBackupTimer();

            // 6) Navigation commands (using cached VMs)
            NavigateDashboard = new RelayCommand(_ => SetCurrentView("Dashboard"));
            NavigateProjects  = new RelayCommand(_ => SetCurrentView("Projects"));
            NavigateBackups   = new RelayCommand(_ => SetCurrentView("Backups"));
            NavigateSettings  = new RelayCommand(_ => SetCurrentView("Settings"));
            _openReleaseCommand = new RelayCommand(_ => _ = OpenUpdateReleaseAsync(), _ => IsReleaseActionEnabled);
            _installPatchCommand = new RelayCommand(
                _ => _ = StartPatchInstallAsync(),
                _ => IsPatchAvailable && !IsPatchInstalling);
            _skipUpdateCommand = new RelayCommand(_ => SkipUpdateVersion());
            _dismissUpdateBannerCommand = new RelayCommand(_ => DismissUpdateBanner());

            EnsureDestinationProbeStarted();
            _ = Task.Run(() => TryImportMetadataFromRoot(_config.ProjectsRoot ?? string.Empty));
            StartUpdateCheck();
            ConfigureUpdateCheckTimer();
        }

        public void AttachBackupWidgetService(IBackupWidgetService? service)
        {
            _backupWidgetService = service;
        }

        // ---------- Backups wiring ----------

        private void ReloadBackupsVmData()
        {
            _ = ReloadBackupsVmDataAsync();
        }

        private Task ReloadBackupsVmDataAsync()
        {
            // Fetch and materialize data off the UI thread to reduce perceived hangs,
            // then marshal the lightweight ViewModel update back to the UI thread.
            return Task.Run(() =>
            {
                var projects = _repo.GetAllProjects().ToList();
                var backups  = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();
                var disabledAuto = _config.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();

                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.LoadFromBackups(projects, backups, disabledAuto);
                });
            });
        }

        private BackupProjectPreparation CreateManualBackupPreparation(int projectId)
        {
            var cfg = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
            return new BackupProjectPreparation(cfg, destinations, project);
        }

        private sealed record BackupProjectPreparation(
            AppConfig Config,
            List<BackupDestination> Destinations,
            Project? Project);

        private void SetCurrentView(string viewKey, bool remember = true)
        {
            switch (viewKey)
            {
                case "Projects":
                    CurrentView  = _projectsViewModel;
                    HeaderTitle  = L("Nav.Projects", "Projects");
                    HeaderKicker = L("Main.HeaderProjects", "All repositories");
                    break;
                case "Backups":
                    ReloadBackupsVmData();
                    _ = _dashboardViewModel.RefreshAsync();
                    CurrentView  = _backupsViewModel;
                    HeaderTitle  = L("Nav.Backups", "Backups");
                    HeaderKicker = L("Main.HeaderBackups", "Snapshots & history");
                    break;
                case "Settings":
                    _settingsViewModel.RebindDestinationCredentials();
                    CurrentView  = _settingsViewModel;
                    HeaderTitle  = L("Nav.Settings", "Settings");
                    HeaderKicker = L("Main.HeaderSettings", "Preferences");
                    break;
                default:
                    _ = _dashboardViewModel.RefreshAsync();
                    CurrentView  = _dashboardViewModel;
                    HeaderTitle  = L("Nav.Dashboard", "Dashboard");
                    HeaderKicker = L("Main.HeaderOverview", "Overview");
                    viewKey      = "Dashboard";
                    break;
            }

            if (remember)
            {
                var cfg = AppConfigStore.Load();
                cfg.LastView   = viewKey;
                _config.LastView = viewKey;
                AppConfigStore.Save(cfg);
            }
        }

        private void ApplyLastSessionView()
        {
            var last = string.IsNullOrWhiteSpace(_config.LastView)
                ? "Dashboard"
                : _config.LastView;

            SetCurrentView(last, remember: false);
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
                if (_backupsViewModel.IsBusy)
                {
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", "busy"));
                    return;
                }

                if (ShouldPauseBackupsForBattery(out var pauseReason))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _backupsViewModel.BackupCurrentFile = pauseReason;
                        _backupsViewModel.BusyMessage       = pauseReason;
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
                var destinations = preparation.Destinations;

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
                            if (cfg.Backups.PromptRestoreAfterImport && project.NeedsRestore)
                            {
                                Telemetry.Log("auto_backup_skipped", b => b
                                    .WithCode("reason", "restore_required")
                                    .WithHashedString("project", project.Name));
                                return;
                            }
                            int? sharedSnapshotId = null;
                            bool metadataWritten = false;

                            foreach (var dest in destinations)
                            {
                                var resolution = PrepareDestination(dest, cfg);
                                if (!resolution.IsSuccess)
                                {
                                    Interlocked.Increment(ref destinationUnreachable);
                                    continue;
                                }

                                var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                                if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                                {
                                    ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                                }
                                if (driveDecision.Block)
                                {
                                    _networkMountService.Cleanup(resolution);
                                    continue;
                                }

                                var destLabel = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;

                                try
                                {
                                    Interlocked.Increment(ref backupAttempts);
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
                                        useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false);

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
                                finally
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
                    _ = _dashboardViewModel.RefreshAsync();
                });

                Telemetry.Log("auto_backup_tick", b => b
                    .WithCount("projects", projects.Count)
                    .WithCount("destinations", destinations.Count)
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
            _config.Backups.AutoBackupDisabledProjects = list;
            ConfigureAutoBackupTimer();
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Keep the cached config in sync with persisted settings to avoid overwriting newer values.
            _config = AppConfigStore.Load();

            if (e.PropertyName is nameof(SettingsViewModel.EnableAutoBackups)
                or nameof(SettingsViewModel.AutoBackupIntervalMinutes))
            {
                ConfigureAutoBackupTimer();
            }

            if (e.PropertyName == nameof(SettingsViewModel.CheckForUpdatesOnStartup))
            {
                StartUpdateCheck();
                ConfigureUpdateCheckTimer();
            }

            if (e.PropertyName == nameof(SettingsViewModel.BetaChannelEnabled))
            {
                StartUpdateCheck();
            }

            if (e.PropertyName == nameof(SettingsViewModel.UpdateCheckIntervalMinutes))
            {
                ConfigureUpdateCheckTimer();
            }

            if (e.PropertyName is nameof(SettingsViewModel.EnableVerboseLogging)
                or nameof(SettingsViewModel.SaveVerboseLogs))
            {
                UpdateLogConsoleSettings();
            }
        }

        private void UpdateLogConsoleSettings()
        {
            _logConsoleService.Enabled = _settingsViewModel.EnableVerboseLogging;
            _logConsoleService.SaveToFile = _settingsViewModel.EnableVerboseLogging &&
                                            _settingsViewModel.SaveVerboseLogs;
        }

        private void OnOpenLogConsoleRequested()
        {
            ShowLogConsole();
        }

        private void OnUpdateCheckRequested()
        {
            Console.WriteLine("[Update] Manual update check requested.");
            StartUpdateCheck(ignoreSettings: true);
        }

        private void ShowLogConsole()
        {
            if (_logConsoleWindow is not null)
            {
                _logConsoleWindow.Activate();
                return;
            }

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
                _dashboardViewModel.ReapplyLocalization();
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
            CancelUpdateCheck();
            CancelUpdateRetry();

            if (!ignoreSettings && !_settingsViewModel.CheckForUpdatesOnStartup)
            {
                ClearUpdateState();
                return;
            }

            _updateCheckCts = new CancellationTokenSource();
            Console.WriteLine($"[Update] Starting update check (channel={CurrentUpdateChannel}).");
            _ = RunUpdateCheckAsync(_updateCheckCts.Token);
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

                var archivePath = await _patchService.DownloadPatchArchiveAsync(plan, CancellationToken.None);
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
            _installPatchCommand.RaiseCanExecuteChanged();
        }

        private async Task RunUpdateCheckAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await _updateService.CheckForUpdateAsync(_currentVersionString, CurrentUpdateChannel, cancellationToken);
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
                    var plan = await _patchService.PreparePatchAsync(result, _currentVersionString, cancellationToken);
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
                Dispatcher.UIThread.Post(() => ApplyUpdateResult(result));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Silently ignore update failures; we don't want to disturb the user.
                RecordUpdateCheckFailure(ex);
            }
            finally
            {
                _updateCheckCts?.Dispose();
                _updateCheckCts = null;
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
            SetUpdateReleaseNotes(result.ReleaseNotes);
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

            if (ShouldRaiseSystemNotification)
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

            var cfg = AppConfigStore.Load();
            cfg.Advanced.SkippedUpdateTag = _pendingUpdateResult.TagName ?? string.Empty;
            _config.Advanced.SkippedUpdateTag = cfg.Advanced.SkippedUpdateTag;
            AppConfigStore.Save(cfg);

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

                await using (var contentStream = await response.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream);
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
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VaultSync-Installer/1.0");
            return client;
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
                _backupsViewModel.BackupCurrentFile = pauseReason;
                _backupsViewModel.BusyMessage       = pauseReason;
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

            if (_backupsViewModel.IsBusy && Volatile.Read(ref _manualBackupInFlightCount) == 0)
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
            if (cfg.Backups.PromptRestoreAfterImport && project.NeedsRestore)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.RestoreRequired"],
                    NotificationSeverity.Warning);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "restore_required")
                    .WithHashedString("project", project.Name));
                return;
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
                _backupsViewModel.BackupProgress    = 0;
                _backupsViewModel.BackupCurrentFile = _localizationService["Backups.Notification.Preparing"];
                _backupsViewModel.BackupEtaText     = string.Empty;

                // Reset per-project cards and add this project
                _backupsViewModel.ClearActiveBackups();
            }
            _backupsViewModel.UpdateActiveBackup(
                project.Id.ToString(),
                project.Name,
                0,
                L("Backups.Status.Preparing", "Preparing backup..."),
                string.Empty);
            if (isFirstManual)
            {
                _backupsViewModel.ResetDestinationStatuses(destinations);
            }

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = isFirstManual
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
                    var resolution = PrepareDestination(dest, cfg);
                    _backupsViewModel.UpdateDestinationStatus(
                        destId,
                        resolution.Message,
                        resolution.IsSuccess ? "Info" : "Error");

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
                        _backupsViewModel.UpdateDestinationStatus(destId, driveDecision.Message, "Warning");
                        _networkMountService.Cleanup(resolution);
                        continue;
                    }

                    var labelPrefix = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;

                    try
                    {
                        var backupResult = await Task.Run(async () =>
                        {
                            attempts++;
                            var result = await _backupService.RunBackupAsync(
                                project,
                                resolution.EffectivePath,
                                isAuto: false,
                                progressCallback: (percent, currentFile, etaText) =>
                                {
                                    // Build a nice label for this project
                                    string label;
                                    if (!string.IsNullOrWhiteSpace(currentFile))
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
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Compressing", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Compressing", "Compressing archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Uploading", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Uploading", "Uploading archive");
                                    }
                                    else if (percent < 100)
                                    {
                                        label = L("Backups.Status.Running", "Running backup...");
                                    }
                                      else
                                      {
                                          label = L("Backups.Status.Finalizing", "Finalizing...");
                                      }

                                    if (!string.IsNullOrWhiteSpace(labelPrefix))
                                    {
                                        label = $"[{labelPrefix}] {label}";
                                    }

                                    // Update per-project card (used by BackupsView overlay)
                                    _backupsViewModel.UpdateActiveBackup(
                                        project.Id.ToString(),
                                        project.Name,
                                        percent,
                                        label,
                                        etaText);

                                    // Keep legacy aggregate fields in sync (if anything else binds to them)
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        _backupsViewModel.BackupProgress    = percent;
                                        _backupsViewModel.BackupCurrentFile = label;
                                        _backupsViewModel.BackupEtaText     = etaText;
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
                                useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false
                            );

                            if (!metadataWritten && result.BackupId > 0)
                            {
                                metadataWritten  = true;
                                metadataRoot     = resolution.EffectivePath;
                                metadataBackupId = result.BackupId;

                                if (!sharedSnapshotId.HasValue && result.BackupId > 0)
                                {
                                    var created = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                                        .FirstOrDefault(b => b.Id == result.BackupId);
                                    sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                }
                            }

                            return result;
                        });

                        if (backupResult.SkippedForNoChanges)
                        {
                            _backupsViewModel.MarkDestinationComplete(destId, true, L("Backups.Status.NoChanges", "No changes detected"));
                            Telemetry.Log("backup_single_skipped", b => b
                                .WithCode("reason", "no_changes")
                                .WithHashedString("project", project.Name)
                                .WithHashedString("destinationPath", dest.Path ?? string.Empty));
                            break;
                        }

                              if (backupResult.Cancelled)
                              {
                            _backupsViewModel.MarkDestinationComplete(destId, false, "Cancelled");
                            Telemetry.Log("backup_single_cancelled", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("destinationPath", dest.Path ?? string.Empty)
                                .WithFlag("useArchiveMode", useArchiveMode));
                            break;
                        }

                              if (backupResult.BackupId > 0)
                              {
                                  _backupsViewModel.UpdateActiveBackup(
                                      project.Id.ToString(),
                                      project.Name,
                                      100,
                                      L("Backups.Status.Completed", "Completed"),
                                      string.Empty);
                                  _backupsViewModel.MarkDestinationComplete(destId, true, "Completed");
                                  succeeded++;
                                  TryExportMetadataForBackup(cfg, dest, resolution.EffectivePath, backupResult.BackupId);
                              }
                        else
                        {
                            _backupsViewModel.MarkDestinationComplete(destId, false, "No backup created");
                            failed++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _backupsViewModel.MarkDestinationComplete(destId, false, "Cancelled");
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
                        _backupsViewModel.MarkDestinationComplete(destId, false, ex.Message);
                    }
                    finally
                    {
                        _networkMountService.Cleanup(resolution);
                    }
                }

                if (!metadataWritten)
                {
                    throw new InvalidOperationException("No destinations completed successfully.");
                }

                // --- After backup: optional verification / post-hash ---
                var cfgAfter = AppConfigStore.Load();
                if (metadataRoot is not null)
                {
                    var latest = metadataBackupId.HasValue
                        ? _repo.GetBackupById(metadataBackupId.Value)
                        : _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                            .OrderByDescending(b => b.CreatedUtc)
                            .FirstOrDefault(b => b.ProjectId == project.Id);

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
                await _dashboardViewModel.RefreshAsync();

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

                    _backupsViewModel.ShowNotification(
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
                _backupsViewModel.BackupCurrentFile = L("Backups.Notification.Cancelled", "Backup cancelled.");
                _backupsViewModel.BackupEtaText     = string.Empty;
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
                            _backupsViewModel.ShowNotification(
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
                        _backupsViewModel.BackupCurrentFile = L("Backups.Status.LowDisk", "Backup skipped: low disk space.");
                        _backupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                                ? ex.Message
                                : _backupsViewModel.BackupEtaText + " - " + L("Backups.Status.LowDiskSuffix", "Low disk space");
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
                        _backupsViewModel.BackupCurrentFile = L("Backups.Notification.FailureTitle", "Backup failed");
                        _backupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                                ? ex.Message
                                : _backupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                    });
                }
            }
            finally
            {
                if (inFlightAdded)
                {
                    _manualBackupInFlight.TryRemove(projectId, out _);
                    var remaining = Interlocked.Decrement(ref _manualBackupInFlightCount);
                    if (remaining <= 0)
                    {
                        _backupsViewModel.ClearActiveBackups();
                        _backupsViewModel.IsBusy      = false;
                        _backupsViewModel.BusyMessage = string.Empty;
                        TrayMenuRefreshRequested?.Invoke();
                    }
                    else
                    {
                        _backupsViewModel.BusyMessage = L("Backups.Busy.All", "Backing up all projects...");
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
            if (_backupsViewModel.IsBusy)
            {
                Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", "busy"));
                return;
            }

            if (ShouldPauseBackupsForBattery(out var pauseReason))
            {
                _backupsViewModel.BackupCurrentFile = pauseReason;
                _backupsViewModel.BusyMessage       = pauseReason;
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

            var cfg             = preparation.Config!;
            var destinations    = preparation.Destinations!;
            var primaryDest     = preparation.PrimaryDestination!;
            var preparedPrimary = preparation.PrimaryResolution!;

            var backupRoot = preparedPrimary.EffectivePath;
            var primaryAlias = string.IsNullOrWhiteSpace(primaryDest.Alias) ? primaryDest.Path : primaryDest.Alias ?? primaryDest.Path;

            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;

            var useArchiveMode = _settingsViewModel.UseBackupCompression;
            Telemetry.Log("backup_all_start", b => b
                .WithHashedString("destinationPath", primaryDest.Path)
                .WithHashedString("destinationAlias", primaryDest.Alias ?? string.Empty)
                .WithFlag("useArchiveMode", useArchiveMode));

            _backupsViewModel.BackupProgress    = 0;
            _backupsViewModel.BackupCurrentFile = L("Backups.Status.Preparing", "Preparing backup...");
            _backupsViewModel.BackupEtaText     = string.Empty;
            _backupsViewModel.IsBusy            = true;
            _backupsViewModel.BusyMessage       = L("Backups.Busy.All", "Backing up all projects...");
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

                    // Reset per-project cards and add entry place-holders
                    _backupsViewModel.ClearActiveBackups();
                    foreach (var p in projects)
                    {
                        _backupsViewModel.UpdateActiveBackup(
                            p.Id.ToString(),
                            p.Name,
                            0,
                            L("Backups.Status.Preparing", "Preparing backup..."),
                            string.Empty);
                    }

                    // Local helper to recompute aggregate progress and update the UI.
                    void UpdateAggregateProgress(string currentFile, string etaText)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (progressPerProject.IsEmpty)
                            {
                                _backupsViewModel.BackupProgress    = 0;
                                _backupsViewModel.BackupCurrentFile = L("Backups.Status.Preparing", "Preparing backup...");
                                _backupsViewModel.BackupEtaText     = string.Empty;
                                _backupsViewModel.BusyMessage       = L("Backups.Busy.All", "Backing up all projects...");
                                return;
                            }

                            var avg = progressPerProject.Values.DefaultIfEmpty(0).Average();
                            _backupsViewModel.BackupProgress = avg;

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

                            _backupsViewModel.BackupCurrentFile = label;
                            _backupsViewModel.BackupEtaText     = etaText;
                            _backupsViewModel.BusyMessage       = L("Backups.Busy.All", "Backing up all projects...");
                        });
                    }

                    var tasks = projects.Select(project => Task.Run(async () =>
                    {
                        var effectiveBackupRoot = backupRoot;

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
                                _backupsViewModel.UpdateActiveBackup(
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
                            var backupResult = await _backupService.RunBackupAsync(
                                project,
                                effectiveBackupRoot,
                                isAuto: false,
                                progressCallback: (percent, currentFile, etaText) =>
                                {
                                    // Per-project label for its own card
                                    string label;
                                    if (!string.IsNullOrWhiteSpace(currentFile))
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
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Compressing", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Compressing", "Compressing archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Uploading", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Uploading", "Uploading archive");
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
                                    _backupsViewModel.UpdateActiveBackup(
                                        project.Id.ToString(),
                                        project.Name,
                                        percent,
                                        label,
                                        etaText);
                                },
                                useArchiveMode: useArchiveMode,
                                maxSnapshotsToKeep: maxSnapshotsToKeep,
                                minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent,
                                preferredFinalBackupRoot: null,
                                destinationPath: effectiveBackupRoot,
                                destinationAlias: primaryAlias,
                                skipIfNoChanges: true,
                                useRsyncDelta: _settingsViewModel?.UseRsyncDelta ?? false,
                                useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false
                            );

                            if (backupResult.SkippedForNoChanges)
                            {
                                progressPerProject[project.Id] = 100;
                                _backupsViewModel.UpdateActiveBackup(
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
                                return;
                            }

                              progressPerProject[project.Id] = 100;
                              UpdateAggregateProgress(string.Empty, string.Empty);
                              _backupsViewModel.UpdateActiveBackup(
                                  project.Id.ToString(),
                                  project.Name,
                                  100,
                                  L("Backups.Status.Completed", "Completed"),
                                  string.Empty);
                              results.Add((project.Name, project.RootPath, backupResult.BackupId > 0));
                              if (backupResult.BackupId > 0)
                              {
                                  TryExportMetadataForBackup(cfg, primaryDest, effectiveBackupRoot, backupResult.BackupId);
                              }
                            Telemetry.Log("backup_all_project_success", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithFlag("useArchiveMode", useArchiveMode));
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
                    })).ToList();

                    await Task.WhenAll(tasks);

                    Telemetry.Log("backup_all_success", b => b
                        .WithCount("projects", projects.Count)
                        .WithCount("succeeded", results.Count(r => r.success))
                        .WithCount("failed", results.Count(r => !r.success))
                        .WithHashedString("destinationPath", backupRoot)
                        .WithHashedString("destinationAlias", primaryAlias)
                        .WithFlag("useArchiveMode", useArchiveMode)
                        .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));
                });

                // First reload history so the new backups appear.
                ReloadBackupsVmData();
                await _dashboardViewModel.RefreshAsync();

                // --- After all backups: optional verification / post-hash ---
                var cfgAfterAll = AppConfigStore.Load();
                var allLatest = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                                     .GroupBy(b => b.ProjectId)
                                     .Select(g => g.OrderByDescending(b => b.CreatedUtc).First());

                foreach (var latest in allLatest)
                {
                    var proj = _repo.GetAllProjects().FirstOrDefault(p => p.Id == latest.ProjectId);
                    if (proj == null)
                        continue;

                    if (cfgAfterAll.Backups.VerifyAfterCreate)
                    {
                        StartVerificationAsync(proj, latest, backupRoot, "backup_all_verify_failed");
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
                    _backupsViewModel.ClearActiveBackups();

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                    {
                        var msg   = L("Backups.Notification.AllSuccess", "All project backups completed successfully.");
                        var title = L("Backups.Notification.AllSuccessTitle", "Backups completed");

                        _notificationService.ShowInfo(
                            title,
                            msg,
                            NotificationKind.Backup);

                        _backupsViewModel.ShowNotification(
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
                    .WithHashedString("destinationPath", backupRoot)
                    .WithHashedString("destinationAlias", primaryAlias)
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                if (NotificationsEnabled)
                {
                    var msg   = L("Backups.Notification.AllFailureMessage", "Backup all projects failed. Check logs for details.");
                    var title = L("Backups.Notification.AllFailureTitle", "Backup-all failed");

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
                    _backupsViewModel.BackupCurrentFile = L("Backups.Notification.AllFailureTitle", "Backup-all failed");
                    _backupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                            ? ex.Message
                                : _backupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                });

                // Clear cards on failure (ensure this runs on the UI thread)
                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.ClearActiveBackups();
                });
            }
            finally
            {
                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;

                TrayMenuRefreshRequested?.Invoke();

                if (preparedPrimary.MountedByUs && primaryDest.AutoUnmount)
                {
                    _networkMountService.Cleanup(preparedPrimary);
                }
                Interlocked.Exchange(ref _backupAllInProgress, 0);
            }
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
                        _backupsViewModel.MarkSnapshotAsFailed(backupId);
                        _backupsViewModel.ShowVerificationFailure(backupId, project.Name);
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

            _backupsViewModel.ShowTransientOperation(cardId, projectName, L("Backups.Status.Deleting", "Deleting backup files..."));

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = L("Backups.Status.Deleting", L("Backups.Status.Deleting", "Deleting backup files..."));

            try
            {
                var relativePath = backup.Path ?? string.Empty;
                var fullPath     = Path.GetFullPath(Path.Combine(backupRoot, relativePath));

                await Task.Run(() =>
                {
                    try
                    {
                        if (Directory.Exists(fullPath))
                        {
                            DeleteDirectoryRobust(fullPath);
                        }
                        else if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                        }

                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        _repo.DeleteBackupById(backupId);
                        TryDeleteSnapshotIfOrphan(projectId, snapshotId);
                    }
                });

                ReloadBackupsVmData();
                await _dashboardViewModel.RefreshAsync();
            }
            finally
            {
                _backupsViewModel.CompleteTransientOperation(cardId, L("Backups.Status.Deleted", "Deleted"));
                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
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

        private DeleteBackupPreparation PrepareDeleteBackup(int backupId)
        {
            var allBackups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow);
            var backup = allBackups.FirstOrDefault(b => b.Id == backupId);
            if (backup is null)
                return DeleteBackupPreparation.Failure;

            var cfg = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            var backupRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath)
                ? backup.DestinationPath
                : TryResolveBackupPathForRead(backup.Path ?? string.Empty, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
                return DeleteBackupPreparation.Failure;

            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == backup.ProjectId);
            var projectName = project?.Name ?? "Backup";

            return new DeleteBackupPreparation(true, backup, backupRoot, projectName, project?.Id ?? 0, backup.SnapshotId);
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
            var allBackups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow);
            var backup = allBackups.FirstOrDefault(b => b.Id == backupId);
            if (backup is null)
                return RestoreBackupPreparation.Failure;

            var cfg = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            var backupRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath)
                ? backup.DestinationPath
                : TryResolveBackupPathForRead(backup.Path ?? string.Empty, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
                return RestoreBackupPreparation.Failure;

            var backupFullPath = Path.Combine(backupRoot, backup.Path ?? string.Empty);
            if (string.IsNullOrWhiteSpace(backup.Path) || !Directory.Exists(backupFullPath))
                return RestoreBackupPreparation.Failure;

            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == backup.ProjectId);
            if (project is null)
                return RestoreBackupPreparation.Failure;

            var projectRoot = project.RootPath;
            if (string.IsNullOrWhiteSpace(projectRoot))
                return RestoreBackupPreparation.Failure;

            return new RestoreBackupPreparation(true, backupFullPath, projectRoot, project.Name);
        }

        private sealed record RestoreBackupPreparation(
            bool IsReady,
            string BackupFullPath,
            string ProjectRoot,
            string ProjectName)
        {
            public static RestoreBackupPreparation Failure => new(false, string.Empty, string.Empty, string.Empty);
        }

        private AutoBackupPreparation PrepareAutoBackupRun()
        {
            var cfg = AppConfigStore.Load();
            if (!cfg.Backups.EnableAutoBackups)
                return AutoBackupPreparation.Failure("disabled");

            var destinations = GetActiveDestinations(cfg);
            if (destinations.Count == 0)
                return AutoBackupPreparation.Failure("no_destination");

            var projects = _repo.GetAllProjects().ToList();
            var disabled = cfg.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();

            return AutoBackupPreparation.Success(cfg, projects, destinations, disabled);
        }

        private sealed record AutoBackupPreparation(
            bool IsReady,
            string? FailureCode,
            AppConfig? Config,
            List<Project>? Projects,
            List<BackupDestination>? Destinations,
            ISet<int>? DisabledProjects)
        {
            public static AutoBackupPreparation Failure(string reason) =>
                new(false, reason, null, null, null, null);

            public static AutoBackupPreparation Success(
                AppConfig cfg,
                List<Project> projects,
                List<BackupDestination> destinations,
                ISet<int> disabled) =>
                new(true, null, cfg, projects, destinations, disabled);
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
                return;
            }

            var projectRoot   = preparation.ProjectRoot;
            var backupFullPath = preparation.BackupFullPath;
            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = $"Restoring {preparation.ProjectName}...";

            try
            {
                await Task.Run(() =>
                {
                    RestoreDirectory(backupFullPath, projectRoot);
                });

            }
            catch (Exception ex)
            {

                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.BackupCurrentFile = L("Backups.Status.RestoreFailed", "Restore failed.");
                    _backupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                            ? ex.Message
                                : _backupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                });
            }
            finally
            {
                var restoredProject = _repo.GetProjectByName(preparation.ProjectName);
                if (restoredProject != null && restoredProject.NeedsRestore)
                {
                    _repo.UpdateProjectNeedsRestore(restoredProject.Id, false);
                }
                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
        }

        private static void RestoreDirectory(string sourceDir, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(sourceDir))
                throw new ArgumentException("Source directory is required.", nameof(sourceDir));

            if (string.IsNullOrWhiteSpace(targetDir))
                throw new ArgumentException("Target directory is required.", nameof(targetDir));

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist.");

            // Ensure target root exists
            Directory.CreateDirectory(targetDir);

            // Create all directories
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, dirPath);
                var target   = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(target);
            }

            // Copy all files, overwriting existing ones but not deleting extras.
            foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, filePath);
                var target   = Path.Combine(targetDir, relative);

                var parentDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(filePath, target, overwrite: true);
            }
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
            _backupService.CancelBackup(projectId);
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
            return _backupsViewModel.ProjectBackups.ToList();
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
            if (_backupsViewModel.IsBusy)
            {
                return;
            }

            var projectItem = _backupsViewModel.ProjectBackups.FirstOrDefault(p => p.Id == projectId);
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
            if (_backupsViewModel.IsBusy)
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

        private BackupAllPreparationResult PrepareBackupAll()
        {
            var cfg = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            if (destinations.Count == 0)
            {
                return BackupAllPreparationResult.Failure("no_destination");
            }

            var primaryDest = destinations[0];
            var preparedPrimary = PrepareDestination(primaryDest, cfg);
            if (!preparedPrimary.IsSuccess)
            {
                return BackupAllPreparationResult.Failure("destination_unreachable");
            }

            return BackupAllPreparationResult.Success(cfg, destinations, primaryDest, preparedPrimary);
        }

        private sealed record BackupAllPreparationResult(
            bool IsReady,
            string? FailureCode,
            AppConfig? Config,
            List<BackupDestination>? Destinations,
            BackupDestination? PrimaryDestination,
            DestinationResolution? PrimaryResolution)
        {
            public static BackupAllPreparationResult Failure(string reason) =>
                new(false, reason, null, null, null, null);

            public static BackupAllPreparationResult Success(
                AppConfig cfg,
                List<BackupDestination> destinations,
                BackupDestination primaryDestination,
                DestinationResolution primaryResolution) =>
                new(true, null, cfg, destinations, primaryDestination, primaryResolution);
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
            foreach (var dest in destinations.Where(d => d.Active))
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
            try
            {
                var backup = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                    .FirstOrDefault(b => b.Id == backupId);
                if (backup is null)
                    return;

                var cfg = AppConfigStore.Load();
                var destinations = GetActiveDestinations(cfg);
                var destinationRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath)
                    ? backup.DestinationPath
                    : TryResolveBackupPathForRead(backup.Path ?? string.Empty, destinations, cfg.Backups.BackupRoot);
                if (string.IsNullOrWhiteSpace(destinationRoot))
                    return;

                var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, backup.Path ?? string.Empty));
                if (!Directory.Exists(fullPath))
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

        public void ShowBackupInAppFromTray(int projectId)
        {
            try
            {
                ReloadBackupsVmData();
                var projectItem = _backupsViewModel.ProjectBackups
                    .FirstOrDefault(p => int.TryParse(p.Id, out var pid) && pid == projectId);
                if (projectItem is not null)
                {
                    _backupsViewModel.SelectedProject = projectItem;
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

                foreach (var project in projects)
                {
                    var backups = _repo.GetBackupsForProject(project.Id)
                        .OrderByDescending(b => b.CreatedUtc)
                        .Take(maxPerProject)
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
                var backup = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                    .FirstOrDefault(b => b.Id == backupId);
                if (backup is null)
                    return;

                var newValue = !backup.IsProtected;
                _repo.SetBackupProtection(backupId, newValue);
                _backupsViewModel.MarkBackupProtection(backupId, newValue);
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

            _ = ProbeDestinationsAsync(); // initial probe at startup
        }

        public IReadOnlyList<DestinationProbeSummary> GetDestinationProbeSummaries()
        {
            var cfg = AppConfigStore.Load();
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

            return _destinationProbeSummaries.Values
                .Where(s => activeIds.Contains(s.Id))
                .OrderBy(s => s.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task ProbeDestinationsAsync()
        {
            if (Interlocked.Exchange(ref _destinationProbeInFlight, 1) == 1)
                return;

            try
            {
                var cfg = AppConfigStore.Load();
                var destinations = GetActiveDestinations(cfg);

                foreach (var dest in destinations)
                {
                    if (!dest.Active)
                        continue;

                    var id = DestinationStatusItem.GetId(dest);
                    _destinationProbeSummaries.TryGetValue(id, out var previous);
                    var result = await Task.Run(() => TryTestDestination(dest, cfg));
                    UpdateDestinationProbeSummary(dest, result);

                    if (result.Reachable && (previous is null || !previous.Reachable))
                    {
                        TryImportMetadataForDestination(cfg, dest, result.EffectivePath);
                    }

                    if (!result.Reachable)
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
            _destinationProbeSummaries[id] = new DestinationProbeSummary(
                id,
                alias,
                dest.Path ?? string.Empty,
                result.Reachable,
                string.IsNullOrWhiteSpace(result.Message)
                    ? (result.Reachable
                        ? LStatic("Destinations.Test.Reachable", "Reachable")
                        : LStatic("Destinations.Test.Unavailable", "Unavailable"))
                    : result.Message,
                DateTime.UtcNow);
        }

        private void OnRefreshHistoryRequested()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    RefreshMetadataNow();
                }
                catch
                {
                    // ignore manual refresh failures for now
                }
            });
        }

        private void RefreshMetadataNow()
        {
            var cfg = AppConfigStore.Load();
            if (!cfg.Backups.EnableMetadataSync)
            {
                Console.WriteLine("[MetadataSync] Refresh skipped: metadata sync disabled.");
                return;
            }

            Console.WriteLine("[MetadataSync] Manual refresh started.");

            if (!string.IsNullOrWhiteSpace(cfg.ProjectsRoot))
            {
                    var options = new MetadataSyncOptions(
                        AllowCreateProjects: true,
                        MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
                    var result = _metadataSyncService.ImportFromStore(cfg.ProjectsRoot, options);
                    Console.WriteLine($"[MetadataSync] Manual refresh (projects root) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
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

                var resolution = _networkMountService.PrepareDestination(dest, profile);
                if (!resolution.IsSuccess)
                    continue;

                try
                {
                    var options = new MetadataSyncOptions(
                        AllowCreateProjects: true,
                        MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
                    var result = _metadataSyncService.ImportFromStore(resolution.EffectivePath, options);
                    var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
                    Console.WriteLine($"[MetadataSync] Manual refresh ({name}) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                }
                finally
                {
                    _networkMountService.Cleanup(resolution);
                }
            }
        }

        private DestinationTestResult TryTestDestination(BackupDestination dest, AppConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(dest.Path))
                return new DestinationTestResult(false, false, string.Empty, LStatic("Destinations.Test.EmptyPath", "Destination path is empty."));

            var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                ? null
                : cfg.Network.Credentials.FirstOrDefault(c =>
                    c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

            var resolution = _networkMountService.PrepareDestination(dest, profile);
            if (!resolution.IsSuccess)
                return new DestinationTestResult(false, false, resolution.EffectivePath ?? string.Empty, resolution.Message);

            var testTarget = resolution.EffectivePath;

            try
            {
                Directory.CreateDirectory(testTarget);

                var writable = true;
                var message = LStatic("Destinations.Test.Reachable", "Reachable");

                try
                {
                    var testFile = Path.Combine(testTarget, ".vaultsync_destination_test");
                    File.WriteAllText(testFile, "ok");
                    File.Delete(testFile);
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
                return new DestinationTestResult(false, false, testTarget, ex.Message);
            }
            finally
            {
                if (resolution.MountedByUs)
                {
                    // Always disconnect temporary mounts used for reachability probes.
                    var cleanupDest = new BackupDestination
                    {
                        Path           = resolution.EffectivePath,
                        CredentialName = dest.CredentialName,
                        Active         = dest.Active,
                        AutoMount      = dest.AutoMount,
                        AutoUnmount    = true,
                        PreMounted     = dest.PreMounted,
                        Alias          = dest.Alias
                    };

                    var cleanupResolution = resolution with { Destination = cleanupDest };
                    _networkMountService.Cleanup(cleanupResolution);
                }
            }
        }

        private sealed record DestinationTestResult(bool Reachable, bool Writable, string EffectivePath, string Message);

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
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Auto import failed for '{name}': {ex.Message}");
                }
            });
        }

        private void TryImportMetadataFromRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return;

            var cfg = AppConfigStore.Load();
            if (!cfg.Backups.EnableMetadataSync || !cfg.Backups.AutoImportMetadata)
                return;

            var options = new MetadataSyncOptions(
                AllowCreateProjects: true,
                MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
            try
            {
                Console.WriteLine("[MetadataSync] Auto import started for projects root.");
                var result = _metadataSyncService.ImportFromStore(rootPath, options);
                Console.WriteLine($"[MetadataSync] Auto import (projects root) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MetadataSync] Auto import failed for projects root: {ex.Message}");
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

        private async Task CheckNasAndMigrateAsync()
        {
            if (Interlocked.Exchange(ref _nasMonitorInFlight, 1) == 1)
                return;

            try
            {
                if (_backupsViewModel.IsBusy)
                    return;

                var cfg = AppConfigStore.Load();

                if (_settingsViewModel?.PreferExternalDrives != true)
                    return;

                var backupRoot = cfg.Backups.BackupRoot;
                if (string.IsNullOrWhiteSpace(backupRoot) || !IsNetworkPath(backupRoot))
                    return;

                if (!Directory.Exists(backupRoot))
                    return;

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
        }

        private void TryDeleteSnapshotIfOrphan(int projectId, int snapshotId)
        {
            try
            {
                // If any other backup references this snapshot, keep it.
                var remaining = _repo.GetBackupsForProject(projectId)
                    .Any(b => b.SnapshotId == snapshotId);
                if (remaining)
                    return;

                var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
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
                _backupsViewModel.ShowNotification(message, severity.ToString());
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
    }
}
