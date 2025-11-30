using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels.Notifications;

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
        private string _headerTitle = "Dashboard";
        private string _headerKicker = "Overview";

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
        private readonly CredentialVault _credentialVault;
        private readonly INotificationService _notificationService;
        private readonly IPowerStatusProvider _powerStatusProvider;
        private readonly IDriveHealthService _driveHealthService;
        private readonly ConcurrentDictionary<string, DestinationProbeSummary> _destinationProbeSummaries = new();
        private bool _trayInitiatedBackup;
        private readonly GitHubUpdateService _updateService = new();
        private readonly PatchUpdateService _patchService = new();
        private readonly LocalizationService _localizationService = new();
        private readonly string _currentVersionString;
        private CancellationTokenSource? _updateCheckCts;
        private UpdateCheckResult? _pendingUpdateResult;
        private bool _isUpdateAvailable;
        private string _updateBannerMessage = string.Empty;
        private string _updateReleaseNotes = string.Empty;
        private string _updateReleaseUrl = string.Empty;
        private readonly RelayCommand _installPatchCommand;
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

        public SettingsViewModel SettingsViewModel => _settingsViewModel;
        public BackupsViewModel BackupsViewModel => _backupsViewModel;

        private List<BackupDestination> GetActiveDestinations(AppConfig cfg)
        {
            if (cfg.Backups.Destinations is { Count: > 0 })
            {
                return cfg.Backups.Destinations
                    .Where(d => d.Active)
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(cfg.Backups.BackupRoot))
            {
                return new List<BackupDestination>
                {
                    new BackupDestination
                    {
                        Alias       = "Primary",
                        Path        = cfg.Backups.BackupRoot,
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

        public string UpdateBannerMessage
        {
            get => _updateBannerMessage;
            private set => SetField(ref _updateBannerMessage, value);
        }

        public string UpdateTooltip => string.IsNullOrWhiteSpace(_updateReleaseNotes)
            ? "Open the latest release on GitHub"
            : _updateReleaseNotes;

        public bool IsPatchAvailable => _pendingUpdateResult?.HasPatch ?? false;

        public bool ShowPatchButton => IsPatchAvailable;

        public string InstallButtonText => IsPatchInstalling ? "Preparing patch…" : "Install patch";

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

        // Commands used by the shell / main window
        public ICommand NavigateDashboard { get; }
        public ICommand NavigateProjects  { get; }
        public ICommand NavigateBackups   { get; }
        public ICommand NavigateSettings  { get; }
        public ICommand OpenReleasePageCommand { get; }
        public ICommand InstallPatchCommand => _installPatchCommand;

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

            _repo = new SqliteRepository(_config.DbPath ?? string.Empty);
            _repo.EnsureSchema();

            _backupService       = new BackupService(_repo);
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
            HeaderTitle  = "Dashboard";
            HeaderKicker = "Overview";

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
            OpenReleasePageCommand = new RelayCommand(_ => OpenUpdateRelease());
            _installPatchCommand = new RelayCommand(
                _ => _ = StartPatchInstallAsync(),
                _ => IsPatchAvailable && !IsPatchInstalling);

            EnsureDestinationProbeStarted();
            StartUpdateCheck();
        }

        public void AttachBackupWidgetService(IBackupWidgetService? service)
        {
            _backupWidgetService = service;
        }

        // ---------- Backups wiring ----------

        private void ReloadBackupsVmData()
        {
            var projects = _repo.GetAllProjects().ToList();
            var backups  = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();
            var disabledAuto = _config.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();


            _backupsViewModel.LoadFromBackups(projects, backups, disabledAuto);
        }

        private void SetCurrentView(string viewKey, bool remember = true)
        {
            switch (viewKey)
            {
                case "Projects":
                    CurrentView  = _projectsViewModel;
                    HeaderTitle  = "Projects";
                    HeaderKicker = "All repositories";
                    break;
                case "Backups":
                    ReloadBackupsVmData();
                    _ = _dashboardViewModel.RefreshAsync();
                    CurrentView  = _backupsViewModel;
                    HeaderTitle  = "Backups";
                    HeaderKicker = "Snapshots & history";
                    break;
                case "Settings":
                    CurrentView  = _settingsViewModel;
                    HeaderTitle  = "Settings";
                    HeaderKicker = "Preferences";
                    break;
                default:
                    _ = _dashboardViewModel.RefreshAsync();
                    CurrentView  = _dashboardViewModel;
                    HeaderTitle  = "Dashboard";
                    HeaderKicker = "Overview";
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
            _autoBackupTimer = new Timer(async _ => await RunAutoBackupsAsync(), null, interval, interval);
        }

        private async Task RunAutoBackupsAsync()
        {
            if (Interlocked.Exchange(ref _autoBackupInFlight, 1) == 1)
                return;

            try
            {
                if (_backupsViewModel.IsBusy)
                    return;

                if (ShouldPauseBackupsForBattery(out var pauseReason))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _backupsViewModel.BackupCurrentFile = pauseReason;
                        _backupsViewModel.BusyMessage       = pauseReason;
                    });
                    return;
                }

                var cfg = AppConfigStore.Load();
                if (!cfg.Backups.EnableAutoBackups)
                    return;

                var disabled = cfg.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();
                var projects = _repo.GetAllProjects().ToList();

                var destinations = GetActiveDestinations(cfg);
                if (destinations.Count == 0)
                    return;

                var useArchiveMode = _settingsViewModel.UseBackupCompression;

                foreach (var project in projects)
                {
                    if (disabled.Contains(project.Id))
                        continue;

                    int? sharedSnapshotId = null;
                    bool metadataWritten = false;

                    foreach (var dest in destinations)
                    {
                        var resolution = PrepareDestination(dest, cfg);
                        if (!resolution.IsSuccess)
                            continue;

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
                            var backupId = await _backupService.RunBackupAsync(
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
                                destinationAlias: destLabel);

                            if (!metadataWritten)
                            {
                                metadataWritten = true;
                                if (!sharedSnapshotId.HasValue && backupId > 0)
                                {
                                    var created = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                                        .FirstOrDefault(b => b.Id == backupId);
                                    sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                }
                            }
                        }
                        catch (Exception)
                        {
                        }
                        finally
                        {
                            _networkMountService.Cleanup(resolution);
                        }
                    }

                }

                ReloadBackupsVmData();
                _ = _dashboardViewModel.RefreshAsync();
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
            }
        }

        private void StartUpdateCheck()
        {
            CancelUpdateCheck();

            if (!_settingsViewModel.CheckForUpdatesOnStartup)
            {
                ClearUpdateState();
                return;
            }

            _updateCheckCts = new CancellationTokenSource();
            _ = RunUpdateCheckAsync(_updateCheckCts.Token);
        }

        private async Task StartPatchInstallAsync()
        {
            if (!IsPatchAvailable || _pendingUpdateResult is null || IsPatchInstalling)
                return;

            IsPatchInstalling = true;
            PatchStatusMessage = "Downloading patch...";

            try
            {
                var plan = await _patchService.PreparePatchAsync(
                    _pendingUpdateResult,
                    _currentVersionString,
                    CancellationToken.None);

                if (plan is null)
                {
                    PatchStatusMessage = "Patch manifest cannot be applied to this version.";
                    return;
                }

                var archivePath = await _patchService.DownloadPatchArchiveAsync(plan, CancellationToken.None);
                if (archivePath is null)
                {
                    PatchStatusMessage = "Failed to download or verify the patch.";
                    return;
                }

                PatchStatusMessage = $"Patch ready at {archivePath}";
                const string title = "Patch downloaded";
                const string message = "The delta patch is staged; run the VaultSync patch helper to finish the update.";

                GlobalNotificationCenter.Instance.Show(message, NotificationSeverity.Info, title);
                if (ShouldRaiseSystemNotification)
                {
                    GlobalNotificationCenter.Instance.ShowSystem(message, NotificationSeverity.Info, title);
                }
            }
            finally
            {
                IsPatchInstalling = false;
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
                var result = await _updateService.CheckForUpdateAsync(_currentVersionString, cancellationToken);
                if (result is null)
                    return;

                Dispatcher.UIThread.Post(() => ApplyUpdateResult(result));
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Silently ignore update failures; we don't want to disturb the user.
            }
            finally
            {
                _updateCheckCts?.Dispose();
                _updateCheckCts = null;
            }
        }

        private void ApplyUpdateResult(UpdateCheckResult result)
        {
            IsUpdateAvailable = true;
            UpdateBannerMessage = $"New update available: {result.ReleaseName} ({result.TagName})";
            SetUpdateReleaseNotes(result.ReleaseNotes);
            _updateReleaseUrl = result.ReleaseUrl.ToString();
            _pendingUpdateResult = result;
            NotifyPatchAvailabilityChanged();
            PatchStatusMessage = string.Empty;

            const string title = "Update available";
            var message = $"VaultSync {result.TagName} is ready. Open the latest release to download it.";

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
            NotifyPatchAvailabilityChanged();
            PatchStatusMessage = string.Empty;
        }

        private void CancelUpdateCheck()
        {
            if (_updateCheckCts is null)
                return;

            _updateCheckCts.Cancel();
            _updateCheckCts.Dispose();
            _updateCheckCts = null;
        }

        private void OpenUpdateRelease()
        {
            if (string.IsNullOrWhiteSpace(_updateReleaseUrl))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _updateReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                const string title = "Update failed";
                const string message = "Unable to open the release page; visit the GitHub releases manually.";
                GlobalNotificationCenter.Instance.Show(message, NotificationSeverity.Error, title);
                if (ShouldRaiseSystemNotification)
                {
                    GlobalNotificationCenter.Instance.ShowSystem(message, NotificationSeverity.Error, title);
                }
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

        private async void OnBackupProjectRequested(ProjectBackupItem? item)
        {
            var trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;

            // Prevent overlapping manual backups; if one is already running, ignore.
            if (_backupsViewModel.IsBusy)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                return;
            }

            if (ShouldPauseBackupsForBattery(out var pauseReason))
            {
                _backupsViewModel.BackupCurrentFile = pauseReason;
                _backupsViewModel.BusyMessage       = pauseReason;
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.BatteryPaused"],
                    NotificationSeverity.Warning);
                return;
            }

            if (item is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoProject"],
                    NotificationSeverity.Warning);
                return;
            }

            if (!int.TryParse(item.Id, out var projectId))
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.InvalidProjectId"],
                    NotificationSeverity.Error);
                return;
            }

            var cfg        = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            if (destinations.Count == 0)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoDestination"],
                    NotificationSeverity.Warning);
                return; // later: show error in UI
            }
            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;

            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
            if (project is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.ProjectNotFound"],
                    NotificationSeverity.Error);
                return;
            }

            var useArchiveMode = _settingsViewModel.UseBackupCompression;

            // Reset progress state
            _backupsViewModel.BackupProgress    = 0;
            _backupsViewModel.BackupCurrentFile = _localizationService["Backups.Notification.Preparing"];
            _backupsViewModel.BackupEtaText     = string.Empty;

            // Reset per-project cards and add this project
            _backupsViewModel.ClearActiveBackups();
            _backupsViewModel.UpdateActiveBackup(
                project.Id.ToString(),
                project.Name,
                0,
                "Preparing backup...",
                string.Empty);
            _backupsViewModel.ResetDestinationStatuses(destinations);

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = $"Backing up {project.Name}...";
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

                foreach (var dest in destinations)
                {
                    var destId     = DestinationStatusItem.GetId(dest);
                    var resolution = PrepareDestination(dest, cfg);
                    _backupsViewModel.UpdateDestinationStatus(
                        destId,
                        resolution.Message,
                        resolution.IsSuccess ? "Info" : "Error");

                    if (!resolution.IsSuccess)
                        continue;

                    var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                    if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                    {
                        ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                    }
                    if (driveDecision.Block)
                    {
                        _backupsViewModel.UpdateDestinationStatus(destId, driveDecision.Message, "Warning");
                        _networkMountService.Cleanup(resolution);
                        continue;
                    }

                    var labelPrefix = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;

                    try
                    {
                        await Task.Run(async () =>
                        {
                            var backupId = await _backupService.RunBackupAsync(
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
                                        label = "Preparing backup...";
                                    }
                                    else if (percent < 100)
                                    {
                                        label = "Running backup...";
                                    }
                                    else
                                    {
                                        label = "Completed";
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
                                destinationAlias: labelPrefix
                            );

                            if (!metadataWritten)
                            {
                                metadataWritten  = true;
                                metadataRoot     = resolution.EffectivePath;
                                metadataBackupId = backupId > 0 ? backupId : metadataBackupId;

                                if (!sharedSnapshotId.HasValue && backupId > 0)
                                {
                                    var created = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                                        .FirstOrDefault(b => b.Id == backupId);
                                    sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                }
                            }
                        });

                        _backupsViewModel.MarkDestinationComplete(destId, true, "Completed");
                    }
                    catch (OperationCanceledException)
                    {
                        _backupsViewModel.MarkDestinationComplete(destId, false, "Cancelled");
                        throw;
                    }
                    catch (Exception ex)
                    {
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

                // --- After backup: optional verification ---
                var cfgAfter = AppConfigStore.Load();
                if (cfgAfter.Backups.VerifyAfterCreate && metadataRoot is not null)
                {
                    var verifyService = new VerifyService(_repo, new HashService());
                    var latest = metadataBackupId.HasValue
                        ? _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                            .FirstOrDefault(b => b.Id == metadataBackupId.Value)
                        : _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                            .OrderByDescending(b => b.CreatedUtc)
                            .FirstOrDefault(b => b.ProjectId == project.Id);

                    if (latest != null)
                    {
                        var folder = Path.Combine(metadataRoot, latest.Path ?? "");
                        try
                        {
                            await verifyService.VerifyAsync(project, folder, 100, full: true);
                        }
                        catch (Exception vex)
                        {

                            if (NotificationsEnabled)
                            {
                                var msg   = $"Verification failed for '{project.Name}'. The backup may be corrupted or incomplete.";
                                var title = "Backup verification failed";

                                _notificationService.ShowError(
                                    title,
                                    msg,
                                    NotificationKind.Backup);

                                // Toast only when not already on the Backups page.
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
                    }
                }

                ReloadBackupsVmData();
                await _dashboardViewModel.RefreshAsync();

                // Refresh Projects view so the newly created snapshot appears immediately.
                await _projectsViewModel.RefreshAsync();

                // Notify success if enabled in settings and globally
                if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                {
                    var msg   = $"Backup for '{project.Name}' completed successfully.";
                    var title = "Backup completed";

                    _notificationService.ShowInfo(
                        title,
                        msg,
                        NotificationKind.Backup);

                    _backupsViewModel.ShowNotification(
                        $"Backup completed for {project.Name}",
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
                _backupsViewModel.BackupCurrentFile = "Backup cancelled.";
                _backupsViewModel.BackupEtaText     = string.Empty;
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
                    if (NotificationsEnabled && _settingsViewModel.NotifyOnLowDiskSpace)
                    {
                        var msg   = $"Backup for '{project.Name}' was skipped due to low disk space on the backup target.";
                        var title = "Low disk space";

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
                                $"Backup for '{project.Name}' was skipped because the backup target is almost full.",
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
                        _backupsViewModel.BackupCurrentFile = "Backup skipped: low disk space.";
                        _backupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                                ? ex.Message
                                : _backupsViewModel.BackupEtaText + " Â· Low disk space";
                    });
                }
                else
                {
                    // Generic backup failure path
                    if (NotificationsEnabled)
                    {
                        var msg   = $"Backup failed for '{project.Name}'. Check logs for details.";
                        var title = "Backup failed";

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
                        _backupsViewModel.BackupCurrentFile = "Backup failed.";
                        _backupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                                ? ex.Message
                                : _backupsViewModel.BackupEtaText + " Â· Failed";
                    });
                }
            }
            finally
            {
                // Clear per-project cards once done
                _backupsViewModel.ClearActiveBackups();

                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;

                TrayMenuRefreshRequested?.Invoke();
            }
        }

        private async void OnCreateBackupForAllProjectsRequested()
        {
            var trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;

            // Do not start "backup all" if a backup is already running.
            if (_backupsViewModel.IsBusy)
            {
                return;
            }

            if (ShouldPauseBackupsForBattery(out var pauseReason))
            {
                _backupsViewModel.BackupCurrentFile = pauseReason;
                _backupsViewModel.BusyMessage       = pauseReason;
                return;
            }


            var cfg = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            if (destinations.Count == 0)
                return;

            var primaryDest = destinations[0];
            var preparedPrimary = PrepareDestination(primaryDest, cfg);
            if (!preparedPrimary.IsSuccess)
                return;

            var backupRoot = preparedPrimary.EffectivePath;
            var primaryAlias = string.IsNullOrWhiteSpace(primaryDest.Alias) ? primaryDest.Path : primaryDest.Alias ?? primaryDest.Path;

            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;

            var useArchiveMode = _settingsViewModel.UseBackupCompression;

            _backupsViewModel.BackupProgress    = 0;
            _backupsViewModel.BackupCurrentFile = "Preparing backup...";
            _backupsViewModel.BackupEtaText     = string.Empty;
            _backupsViewModel.IsBusy            = true;
            _backupsViewModel.BusyMessage       = "Backing up all projects...";
            if (trayRun && ShouldShowBackupWidget)
            {
                _backupWidgetService?.ShowForTrayBackup();
            }

            try
            {
                await Task.Run(async () =>
                {
                    var projects = _repo.GetAllProjects().ToList();

                    if (projects.Count == 0)
                    {
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
                            "Preparing backup...",
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
                                _backupsViewModel.BackupCurrentFile = "Preparing backup...";
                                _backupsViewModel.BackupEtaText     = string.Empty;
                                _backupsViewModel.BusyMessage       = "Backing up all projects...";
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
                                label = "Preparing backup...";
                            }
                            else if (avg < 100)
                            {
                                label = "Running backups...";
                            }
                            else
                            {
                                label = "All backups completed";
                            }

                            _backupsViewModel.BackupCurrentFile = label;
                            _backupsViewModel.BackupEtaText     = etaText;
                            _backupsViewModel.BusyMessage       = "Backing up all projects...";
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

                        await _backupService.RunBackupAsync(
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
                                    label = "Preparing backup...";
                                }
                                else if (percent < 100)
                                {
                                    label = "Running backup...";
                                }
                                else
                                {
                                    label = "Completed";
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
                            destinationAlias: primaryAlias
                        );

                        progressPerProject[project.Id] = 100;
                        UpdateAggregateProgress(string.Empty, string.Empty);
                    })).ToList();

                    await Task.WhenAll(tasks);

                });

                // First reload history so the new backups appear.
                ReloadBackupsVmData();
                await _dashboardViewModel.RefreshAsync();

                // --- After all backups: optional verification ---
                var cfgAfterAll = AppConfigStore.Load();
                if (cfgAfterAll.Backups.VerifyAfterCreate)
                {
                    var verifyService = new VerifyService(_repo, new HashService());
                    var allLatest = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                                         .GroupBy(b => b.ProjectId)
                                         .Select(g => g.OrderByDescending(b => b.CreatedUtc).First());

                    foreach (var latest in allLatest)
                    {
                        var proj = _repo.GetAllProjects().FirstOrDefault(p => p.Id == latest.ProjectId);
                        if (proj == null) continue;

                        var folder = Path.Combine(backupRoot, latest.Path ?? "");
                        try
                        {
                            await verifyService.VerifyAsync(proj, folder, 100, full: true);
                        }
                        catch (Exception vex)
                        {

                            if (NotificationsEnabled)
                            {
                                _notificationService.ShowError(
                                    "Backup verification failed",
                                    $"Verification failed for '{proj?.Name ?? "Unknown project"}'. The backup may be corrupted or incomplete.",
                                    NotificationKind.Backup);

                                if (!IsOnBackupsPage)
                                {
                                    var name  = proj?.Name ?? "Unknown project";
                                    var msg   = $"Verification failed for '{name}'. The backup may be corrupted or incomplete.";
                                    var title = "Backup verification failed";

                                    GlobalNotificationCenter.Instance.Show(
                                        msg,
                                        NotificationSeverity.Error,
                                        title);

                                    if (ShouldRaiseSystemNotification)
                                    {
                                        GlobalNotificationCenter.Instance.ShowSystem(
                                            msg,
                                            NotificationSeverity.Error,
                                            title);
                                    }
                                }
                            }

                            Dispatcher.UIThread.Post(() =>
                            {
                                var backupId = latest.Id.ToString();
                                _backupsViewModel.MarkSnapshotAsFailed(backupId);
                                _backupsViewModel.ShowVerificationFailure(backupId, proj?.Name ?? "Unknown project");
                            });
                        }
                    }
                }

                // Then clear the active backup cards on the UI thread,
                // so the overlay collapses only after history is updated.
                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.ClearActiveBackups();

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                    {
                        var msg   = "All project backups completed successfully.";
                        var title = "Backups completed";

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

                if (NotificationsEnabled)
                {
                    var msg   = "Backup all projects failed. Check logs for details.";
                    var title = "Backup-all failed";

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
                    _backupsViewModel.BackupCurrentFile = "Backup all projects failed.";
                    _backupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                            ? ex.Message
                            : _backupsViewModel.BackupEtaText + " Â· Failed";
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
            }
        }

        private async void OnDeleteBackupRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;

            // Find backup row to know its relative path
            var allBackups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow);
            var backup     = allBackups.FirstOrDefault(b => b.Id == backupId);
            if (backup is null)
                return;
            var snapshotId = backup.SnapshotId;
            var projectId  = backup.ProjectId;

            var cfg = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            var backupRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath)
                ? backup.DestinationPath
                : TryResolveBackupPathForRead(backup.Path ?? string.Empty, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
                return;

            var project    = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
            var projectName = project?.Name ?? "Backup";
            var cardId = $"delete-{backupId}";

            _backupsViewModel.ShowTransientOperation(cardId, projectName, "Deleting backup files...");

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = "Deleting backup...";

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
                _backupsViewModel.CompleteTransientOperation(cardId, "Deleted");
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

        private async void OnRestoreBackupRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;


            // Look up the backup row so we know which project and path this backup belongs to.
            var allBackups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow);
            var backup     = allBackups.FirstOrDefault(b => b.Id == backupId);
            if (backup is null)
            {
                return;
            }

            var cfg        = AppConfigStore.Load();
            var destinations = GetActiveDestinations(cfg);
            var backupRoot = !string.IsNullOrWhiteSpace(backup.DestinationPath)
                ? backup.DestinationPath
                : TryResolveBackupPathForRead(backup.Path ?? string.Empty, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                return;
            }

            var backupFullPath = Path.Combine(backupRoot, backup.Path ?? string.Empty);

            // Find the associated project so we know where to restore to.
            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == backup.ProjectId);
            if (project is null)
            {
                return;
            }

            var projectRoot = project.RootPath;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return;
            }

            if (!Directory.Exists(backupFullPath))
            {
                return;
            }

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = $"Restoring {project.Name}...";

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
                    _backupsViewModel.BackupCurrentFile = "Restore failed.";
                    _backupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                            ? ex.Message
                            : _backupsViewModel.BackupEtaText + " Â· Restore failed";
                });
            }
            finally
            {
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

            return _networkMountService.PrepareDestination(dest, profile);
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
                            var ts   = b.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                            var keep = b.IsProtected ? " * Keep" : string.Empty;
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
            => _destinationProbeSummaries.Values
                .OrderBy(s => s.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();

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

                    var result = await Task.Run(() => TryTestDestination(dest));
                    UpdateDestinationProbeSummary(dest, result);

                    if (!result.Reachable)
                    {
                        var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
                        var message = string.IsNullOrWhiteSpace(result.Message)
                            ? "Check mount/credentials."
                            : result.Message;
                        GlobalNotificationCenter.Instance.Show(
                            $"Destination '{name}' is unreachable. {message}",
                            NotificationSeverity.Warning,
                            "Destination unreachable");
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
                string.IsNullOrWhiteSpace(result.Message) ? (result.Reachable ? "Reachable" : "Unavailable") : result.Message,
                DateTime.UtcNow);
        }

        private static DestinationTestResult TryTestDestination(BackupDestination dest)
        {
            if (string.IsNullOrWhiteSpace(dest.Path))
                return new DestinationTestResult(false, "Destination path is empty.");

            try
            {
                Directory.CreateDirectory(dest.Path);
                var testFile = Path.Combine(dest.Path, ".vaultsync_destination_test");
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
                return new DestinationTestResult(true, "Reachable");
            }
            catch (Exception ex)
            {
                return new DestinationTestResult(false, ex.Message);
            }
        }

        private sealed record DestinationTestResult(bool Reachable, string Message);

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

            var driveLabel = issue.DriveId ?? issue.Path ?? "drive";
            severity = issue.Status == DriveHealthStatus.Failing
                ? NotificationSeverity.Error
                : NotificationSeverity.Warning;

            message = issue.Status == DriveHealthStatus.Failing
                ? $"Backup skipped: drive health failing on {driveLabel} ({issue.Message})."
                : $"Drive health warning on {driveLabel}: {issue.Message}.";

            return issue.Status == DriveHealthStatus.Failing;
        }

        private void ShowDriveHealthNotification(string message, NotificationSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!NotificationsEnabled)
                return;

            var title = severity == NotificationSeverity.Error
                ? "Backup blocked: drive health"
                : "Drive health warning";

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
            reason = "Backups paused on battery power.";

            if (_settingsViewModel?.PauseBackupsOnBattery != true)
                return false;

            return _powerStatusProvider.GetPowerState() == PowerState.OnBattery;
        }

        private void ShowBackupSkipNotification(string message, NotificationSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var title = severity switch
            {
                NotificationSeverity.Error   => "Backup error",
                NotificationSeverity.Warning => "Backup paused",
                _                            => "Backup info"
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
