using System;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        public AppViewModel()
            : this(StaticAppConfigStore.Instance)
        {
        }

        internal AppViewModel(IAppConfigStore configStore, IRepositoryFactory? repositoryFactory = null)
        {
            _configStore = configStore;
            _repositoryFactory = repositoryFactory ?? new SqliteRepositoryFactory(_configStore);
            _currentVersionString = GetCurrentVersionString();
            RecordStartupPhase("version-resolved");

            // 1) Config + DB + services
            _config = _configStore.Load();
            RecordStartupPhase("config-loaded");
            if (string.IsNullOrWhiteSpace(_config.Advanced.Language))
            {
                string systemLang = ResolveSystemLanguageCode(_localizationService);
                _config.Advanced.Language = systemLang;
                _ = PersistStartupConfigAsync("initial-language");
            }

            string targetLang = string.IsNullOrWhiteSpace(_config.Advanced.Language)
                ? _localizationService.CurrentLanguage
                : _config.Advanced.Language;
            _localizationService.SetLanguage(targetLang);
            LocalizationProvider.Initialize(_localizationService);
            _localizationService.LanguageChanged += OnLanguageChanged;
            RecordStartupPhase("localization-initialized");

            _repo = _repositoryFactory.Create(_config);

            _backupService = new BackupService(_repo, configStore: _configStore);
            _backupService.BackupRetentionDeleted += OnBackupRetentionDeleted;
            _metadataSyncService = new MetadataSyncService(_repo, _configStore);
            MetadataSyncService.ProjectColorResolver = project =>
                AvatarColorProvider.GetColor(project.Name, project.RootPath, project.ExternalId);
            MetadataSyncService.ProjectColorApplier = (externalId, color) =>
                AvatarColorProvider.SetColorForExternalId(externalId, color);
            _networkMountService = new NetworkMountService();
            _credentialVault = CredentialVault.Instance;
            _notificationService = new NotificationService();
            _powerStatusProvider = new PowerStatusProvider();
            _driveHealthService = new DriveHealthService();
            _backupIndexConsistencyService = new BackupIndexConsistencyService(_repo);
            RecordStartupPhase("core-services-ready");

            // 2) Section viewmodels
            _dashboardViewModel = null;
            _projectsViewModel = new ProjectsViewModel(_configStore, _repositoryFactory);
            _projectsViewModel.EditProjectEncryptionRequested += OnProjectEncryptionRequestedFromProjects;
            _projectsViewModel.ProjectEncryptionPolicyChanged += OnProjectEncryptionPolicyChanged;
            _projectsViewModel.ProjectSettingsMetadataChanged += OnProjectSettingsMetadataChanged;
            _projectsViewModel.BackupGroupRequested += OnBackupGroupRequested;
            _projectsViewModel.AutoBackupGroupPreferenceChanged += OnAutoBackupGroupPreferenceChanged;
            _projectsViewModel.ProjectRemovedFromDatabase += OnProjectRemovedFromDatabase;
            _backupsViewModel = null;
            _settingsViewModel = new SettingsViewModel(_localizationService, _configStore, _repositoryFactory);
            _settingsViewModel.PropertyChanged += OnSettingsChanged;
            _settingsViewModel.DestinationSettingsSaved += OnDestinationSettingsSaved;
            _settingsViewModel.OpenLogConsoleRequested += OnOpenLogConsoleRequested;
            _settingsViewModel.UpdateCheckRequested += OnUpdateCheckRequested;
            _settingsViewModel.RefreshHistoryRequested += OnRefreshHistoryRequested;
            _settingsViewModel.RotateEncryptedBackupsRequested += OnRotateEncryptedBackupsRequested;
            _settingsViewModel.EnrollProjectEncryptionRequested += OnEnrollProjectEncryptionRequested;
            _settingsViewModel.LockEncryptedOpenWorkspacesRequested += OnLockEncryptedOpenWorkspacesRequested;
            _settingsViewModel.UpdateUpdateCheckStatus(null, null);
            _settingsViewModel.Destinations.CollectionChanged += OnDestinationsCollectionChanged;
            _settingsViewModel.DestinationTested += (dest, success, writable, message) =>
            {
                var testResult = new DestinationTestResult(success, writable, dest.Path ?? string.Empty, message);
                UpdateDestinationProbeSummary(dest, testResult);
            };

            foreach (BackupDestinationViewModel dest in _settingsViewModel.Destinations)
            {
                TrackDestinationViewModel(dest);
            }
            RecordStartupPhase("section-viewmodels-ready");

            _projectEncryptionEnrollmentService = new ProjectEncryptionEnrollmentService(
                _repo,
                _credentialVault,
                GetMainWindow,
                ExportMetadataForProjectSettingsChangeAsync,
                () => _projectsViewModel.RefreshAsync(),
                () => ReloadBackupsVmDataAsync(force: true),
                ShowBackupSkipNotification,
                message => Console.WriteLine(message));

            _logConsoleService = new LogConsoleService();
            LogConsoleProvider.Initialize(_logConsoleService);
            UpdateLogConsoleSettings();
            ScheduleLogCaptureInstall();

            _ = Task.Run(RunStartupBackgroundWorkAsync);
            RecordStartupPhase("startup-cleanup-scheduled");

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
            RecordStartupPhase("initial-route-ready");

            ConfigureAutoBackupTimer();
            ConfigureMaintenanceTimer();
            LogBackupPolicyTransitionIfChanged(_config, "startup");
            RecordStartupPhase("timers-configured");

            // 6) Navigation commands (using cached VMs)
            NavigateDashboard = new RelayCommand(_ => SetCurrentView("Dashboard"));
            NavigateProjects = new RelayCommand(_ => SetCurrentView("Projects"));
            NavigateBackups = new RelayCommand(_ => SetCurrentView("Backups"));
            NavigateHistory = new RelayCommand(_ => SetCurrentView("History"));
            NavigateRecovery = new RelayCommand(_ => SetCurrentView("Recovery"));
            NavigateSettings = new RelayCommand(_ => SetCurrentView("Settings"));

            OnboardingTour = new OnboardingTourViewModel(this);
            _openReleaseCommand = new RelayCommand(_ => _ = OpenUpdateReleaseAsync(), _ => IsReleaseActionEnabled);
            _installPatchCommand = new RelayCommand(
                _ => _ = StartPatchInstallAsync(),
                _ => IsPatchAvailable && !IsPatchInstalling);
            _skipUpdateCommand = new RelayCommand(_ => SkipUpdateVersion());
            _dismissUpdateBannerCommand = new RelayCommand(_ => DismissUpdateBanner());
            _dismissSoftCrashBannerCommand = new RelayCommand(_ => DismissSoftCrashBanner());
            _copySoftCrashLogCommand = new RelayCommand(_ => _ = CopySoftCrashLogAsync(), _ => CanCopySoftCrashLog);
            RecordStartupPhase("commands-ready");

            StartDeferredStartupTasks();
            RecordStartupPhase("deferred-startup-scheduled");
        }

        private async Task PersistStartupConfigAsync(string reason)
        {
            try
            {
                await _configStore.SaveAsync(_config).ConfigureAwait(false);
                DiagnosticsLogger.Record($"Startup config persisted ({reason}).");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup config persist failed ({reason}): {ex.GetType().Name} - {ex.Message}");
            }
        }

        private async Task RunStartupBackgroundWorkAsync()
        {
            using var startupTiming = RuntimeTiming.Measure("Startup background work");
            DiagnosticsLogger.Record("Startup background work begin.");

            try
            {
                RecordStartupPhase("db-schema-begin");
                using var schemaTiming = RuntimeTiming.Measure("Startup background work db schema ensure");
                await Task.Run(() => _repo.EnsureSchema()).ConfigureAwait(false);
                RecordStartupPhase("db-schema-complete");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup schema ensure failed: {ex.GetType().Name} - {ex.Message}");
            }

            try
            {
                ReconcileBlankProjectRootsOnStartup();
                RecordStartupPhase("project-root-reconciliation-complete");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup project-root reconciliation failed: {ex.GetType().Name} - {ex.Message}");
            }

            try
            {
                CleanupIncompleteBackupsOnStartup();
                RecordStartupPhase("cleanup-incomplete-backups-complete");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup cleanup incomplete backups failed: {ex.GetType().Name} - {ex.Message}");
            }

            try
            {
                EnforceRetentionOnStartup();
                RecordStartupPhase("startup-retention-complete");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup retention enforcement failed: {ex.GetType().Name} - {ex.Message}");
            }

            try
            {
                CleanupUnusedCredentialSecretsOnStartup();
                RecordStartupPhase("cleanup-unused-secrets-complete");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup credential cleanup failed: {ex.GetType().Name} - {ex.Message}");
            }

            try
            {
                await Task.Run(() => AutoStartService.SetLaunchOnLogin(_config.Behavior.LaunchOnLogin)).ConfigureAwait(false);
                RecordStartupPhase("launch-on-login-synced");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup launch-on-login sync failed: {ex.GetType().Name} - {ex.Message}");
            }

            DiagnosticsLogger.Record("Startup background work complete.");
        }

        private BackupsViewModel CreateBackupsViewModel()
        {
            var vm = new BackupsViewModel(_configStore, _repositoryFactory);
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
            vm.ProjectEncryptionPolicyChanged += OnProjectEncryptionPolicyChanged;
            vm.ProjectRestoreModeChanged += OnProjectRestoreModeChanged;
            vm.ProjectVerificationPolicyChanged += OnProjectVerificationPolicyChanged;
            vm.ManageProjectEncryptionRequested += OnProjectEncryptionRequestedFromBackups;
            vm.OpenSettingsRequested += OnOpenSettingsRequested;
            InitializeDestinationStatusOverview(vm);
            return vm;
        }
    }
}
