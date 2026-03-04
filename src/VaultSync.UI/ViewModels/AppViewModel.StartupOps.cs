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

            _backupService = new BackupService(_repo);
            _backupService.BackupRetentionDeleted += OnBackupRetentionDeleted;
            _metadataSyncService = new MetadataSyncService(_repo);
            MetadataSyncService.ProjectColorResolver = project =>
                AvatarColorProvider.GetColor(project.Name, project.RootPath, project.ExternalId);
            MetadataSyncService.ProjectColorApplier = (externalId, color) =>
                AvatarColorProvider.SetColorForExternalId(externalId, color);
            _networkMountService = new NetworkMountService();
            _credentialVault = CredentialVault.Instance;
            _notificationService = new NotificationService();
            _powerStatusProvider = new PowerStatusProvider();
            _driveHealthService = new DriveHealthService();

            // 2) Section viewmodels
            _dashboardViewModel = null;
            _projectsViewModel = new ProjectsViewModel();
            _projectsViewModel.EditProjectEncryptionRequested += OnProjectEncryptionRequestedFromProjects;
            _projectsViewModel.ProjectEncryptionPolicyChanged += OnProjectEncryptionPolicyChanged;
            _backupsViewModel = null;
            _settingsViewModel = new SettingsViewModel(_localizationService);
            _settingsViewModel.PropertyChanged += OnSettingsChanged;
            _settingsViewModel.OpenLogConsoleRequested += OnOpenLogConsoleRequested;
            _settingsViewModel.UpdateCheckRequested += OnUpdateCheckRequested;
            _settingsViewModel.RefreshHistoryRequested += OnRefreshHistoryRequested;
            _settingsViewModel.RotateEncryptedBackupsRequested += OnRotateEncryptedBackupsRequested;
            _settingsViewModel.EnrollProjectEncryptionRequested += OnEnrollProjectEncryptionRequested;
            _settingsViewModel.LockEncryptedOpenWorkspacesRequested += OnLockEncryptedOpenWorkspacesRequested;
            _settingsViewModel.UpdateUpdateCheckStatus(null, null);
            _settingsViewModel.Destinations.CollectionChanged += OnDestinationsCollectionChanged;
            foreach (var dest in _settingsViewModel.Destinations)
            {
                TrackDestinationViewModel(dest);
            }

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

            _ = Task.Run(() => CleanupIncompleteBackupsOnStartup());
            _ = Task.Run(() => EnforceRetentionOnStartup());
            _ = Task.Run(() => CleanupUnusedCredentialSecretsOnStartup());

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
            LogBackupPolicyTransitionIfChanged(_config, "startup");

            // 6) Navigation commands (using cached VMs)
            NavigateDashboard = new RelayCommand(_ => SetCurrentView("Dashboard"));
            NavigateProjects = new RelayCommand(_ => SetCurrentView("Projects"));
            NavigateBackups = new RelayCommand(_ => SetCurrentView("Backups"));
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
            vm.ProjectEncryptionPolicyChanged += OnProjectEncryptionPolicyChanged;
            vm.ProjectRestoreModeChanged += OnProjectRestoreModeChanged;
            vm.ManageProjectEncryptionRequested += OnProjectEncryptionRequestedFromBackups;
            vm.OpenSettingsRequested += OnOpenSettingsRequested;
            InitializeDestinationStatusOverview(vm);
            return vm;
        }
    }
}
