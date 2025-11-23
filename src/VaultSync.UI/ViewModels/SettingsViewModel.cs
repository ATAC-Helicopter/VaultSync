using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Styling;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI
{
    public sealed class SettingsViewModel : INotifyPropertyChanged
    {
        // ---------------- Core backing fields ----------------

        private string _projectsRootPath = string.Empty;
        private bool _autoOpenLastProject = true;
        private bool _rememberWindowLayout = true;

        private bool _enableAutoBackups = true;
        private int _autoBackupIntervalMinutes = 30;
        private int _maxSnapshotsPerProject = 20;
        private string _backupLocationPath = string.Empty;
        private bool _useBackupCompression = true;
        private bool _verifyBackupsAfterCreate = true;
        private bool _pauseBackupsOnBattery = true;
        private bool _useFullSnapshotHash = true;

        private bool _preferExternalDrives = true;
        private bool _showDriveHealthWarnings = true;
        private int _minimumFreeSpacePercent = 10;

        private bool _useCustomNetworkCredentials = false;
        private string _networkShareUserName = string.Empty;
        private string _networkSharePassword = string.Empty;
        private bool _rememberNetworkCredentials = false;

        private string _selectedTheme;
        private bool _useCompactLayout = false;
        private bool _showProjectAvatars = true;

        private bool _notificationsEnabled = true;
        private bool _notifyOnBackupSuccess = true;
        private bool _notifyOnBackupFailure = true;
        private bool _notifyOnLowDiskSpace = true;

        private bool _notifyOnSnapshotSuccess = false;
        private bool _notifyOnSnapshotFailure = true;

        private bool _useOsNotifications = true;
        private bool _notifyOnlyWhenInactive = true;

        private bool _enableVerboseLogging = false;
        private bool _checkForUpdatesOnStartup = true;
        private bool _sendAnonymousUsageStats = false;

        private bool _isInitialized;
        private bool _isSaving;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SettingsViewModel()
        {
            ThemeOptions = new ObservableCollection<string>
            {
                "Follow system",
                "Dark",
                "Light"
            };

            _selectedTheme = ThemeOptions[0];

            BrowseProjectsRootCommand    = new RelayCommand(_ => BrowseProjectsRoot());
            BrowseBackupLocationCommand  = new RelayCommand(_ => BrowseBackupLocation());
            ResetToDefaultsCommand       = new RelayCommand(_ => ResetToDefaults());
            ApplySettingsCommand         = new RelayCommand(_ => SaveToConfig());
            ClearLocalCacheCommand       = new RelayCommand(_ => ClearLocalCache());
            ForgetAllProjectsCommand     = new RelayCommand(_ => ForgetAllProjects());
            TestNetworkConnectionCommand = new RelayCommand(_ => TestNetworkConnection(), _ => ShowNetworkShareOptions);

            PropertyChanged += OnSettingsPropertyChanged;

            // AUTO-LOAD CONFIG ON STARTUP
            LoadFromConfig();
            _isInitialized = true;
        }

        // ---------------- INPC helpers ----------------

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        // ---------------- Load + Save ----------------

        private void LoadFromConfig()
        {
            var cfg = AppConfigStore.Load();

            _projectsRootPath      = cfg.ProjectsRoot ?? "";
            _autoOpenLastProject   = cfg.AutoOpenLastProject;
            _rememberWindowLayout  = cfg.RememberWindowLayout;

            _enableAutoBackups         = cfg.Backups.EnableAutoBackups;
            _autoBackupIntervalMinutes = cfg.Backups.IntervalMinutes;
            _maxSnapshotsPerProject    = cfg.Backups.MaxSnapshotsPerProject;
            _backupLocationPath        = string.IsNullOrWhiteSpace(cfg.Backups.BackupRoot)
                ? (cfg.Backups.Location ?? string.Empty)
                : cfg.Backups.BackupRoot;
            _useBackupCompression      = cfg.Backups.UseCompression;
            _verifyBackupsAfterCreate  = cfg.Backups.VerifyAfterCreate;
            _pauseBackupsOnBattery     = cfg.Backups.PauseOnBattery;
            _useFullSnapshotHash       = cfg.Backups.UseFullSnapshotHash;

            _preferExternalDrives    = cfg.Storage.PreferExternalDrives;
            _showDriveHealthWarnings = cfg.Storage.ShowDriveWarnings;
            _minimumFreeSpacePercent = cfg.Storage.MinFreeSpacePercent;

            _useCustomNetworkCredentials = cfg.Network.UseCredentials;
            _networkShareUserName        = cfg.Network.Username ?? "";
            _networkSharePassword        = cfg.Network.Password ?? "";
            _rememberNetworkCredentials  = cfg.Network.RememberCredentials;

            // FIX: use Theme instead of ThemeName
            _selectedTheme      = cfg.Appearance.Theme ?? "Follow system";
            _useCompactLayout   = cfg.Appearance.CompactLayout;
            _showProjectAvatars = cfg.Appearance.ShowProjectAvatars;

            _notifyOnBackupSuccess   = cfg.Notifications.OnBackupSuccess;
            _notifyOnBackupFailure   = cfg.Notifications.OnBackupFailure;
            _notifyOnLowDiskSpace    = cfg.Notifications.OnLowDisk;
            _notifyOnSnapshotSuccess = cfg.Notifications.OnSnapshotSuccess;
            _notifyOnSnapshotFailure = cfg.Notifications.OnSnapshotFailure;
            _useOsNotifications      = cfg.Notifications.UseOsNotifications;
            _notifyOnlyWhenInactive  = cfg.Notifications.OnlyWhenInactive;

            // Derive master notifications toggle from individual flags for now
            _notificationsEnabled =
                _notifyOnBackupSuccess ||
                _notifyOnBackupFailure ||
                _notifyOnLowDiskSpace ||
                _notifyOnSnapshotSuccess ||
                _notifyOnSnapshotFailure ||
                _useOsNotifications;

            _enableVerboseLogging      = cfg.Advanced.VerboseLogging;
            _checkForUpdatesOnStartup  = cfg.Advanced.CheckUpdates;
            _sendAnonymousUsageStats   = cfg.Advanced.SendUsageStats;

            // Apply theme when loading config (in case Settings view is opened first)
            ApplyThemeFromSelected();

            // Update UI
            OnPropertyChanged(null);
        }

        private void SaveToConfig()
        {
            var cfg = new AppConfig
            {
                ProjectsRoot         = ProjectsRootPath,
                AutoOpenLastProject  = AutoOpenLastProject,
                RememberWindowLayout = RememberWindowLayout,

                Backups =
                {
                    EnableAutoBackups      = EnableAutoBackups,
                    IntervalMinutes        = AutoBackupIntervalMinutes,
                    MaxSnapshotsPerProject = MaxSnapshotsPerProject,
                    // Write to both for backwards compatibility
                    Location               = BackupLocationPath,
                    BackupRoot             = string.IsNullOrWhiteSpace(BackupLocationPath)
                        ? null
                        : BackupLocationPath,
                    UseCompression         = UseBackupCompression,
                    VerifyAfterCreate      = VerifyBackupsAfterCreate,
                    PauseOnBattery         = PauseBackupsOnBattery,
                    UseFullSnapshotHash    = _useFullSnapshotHash
                },

                Storage =
                {
                    PreferExternalDrives = PreferExternalDrives,
                    ShowDriveWarnings    = ShowDriveHealthWarnings,
                    MinFreeSpacePercent  = MinimumFreeSpacePercent
                },

                Network =
                {
                    UseCredentials      = UseCustomNetworkCredentials,
                    Username            = NetworkShareUserName,
                    Password            = RememberNetworkCredentials ? NetworkSharePassword : "",
                    RememberCredentials = RememberNetworkCredentials
                },

                Appearance =
                {
                    // FIX: use Theme instead of ThemeName
                    Theme              = SelectedTheme,
                    CompactLayout      = UseCompactLayout,
                    ShowProjectAvatars = ShowProjectAvatars
                },

                Notifications =
                {
                    OnBackupSuccess    = NotifyOnBackupSuccess,
                    OnBackupFailure    = NotifyOnBackupFailure,
                    OnLowDisk          = NotifyOnLowDiskSpace,
                    OnSnapshotSuccess  = NotifyOnSnapshotSuccess,
                    OnSnapshotFailure  = NotifyOnSnapshotFailure,
                    UseOsNotifications = UseOsNotifications,
                    OnlyWhenInactive   = NotifyOnlyWhenInactive
                },

                Advanced =
                {
                    VerboseLogging  = EnableVerboseLogging,
                    CheckUpdates    = CheckForUpdatesOnStartup,
                    SendUsageStats  = SendAnonymousUsageStats
                }
            };

            AppConfigStore.Save(cfg);
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_isInitialized)
                return;

            // Ignore non-setting notifications if needed
            if (e.PropertyName is null || e.PropertyName == nameof(ShowNetworkShareOptions))
                return;

            if (_isSaving)
                return;

            try
            {
                _isSaving = true;
                SaveToConfig();
            }
            finally
            {
                _isSaving = false;
            }
        }

        // ---------------- Theme helper ----------------

        private void ApplyThemeFromSelected()
        {
            var app = Application.Current;
            if (app is null) return;

            app.RequestedThemeVariant = _selectedTheme switch
            {
                "Dark"  => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _       => ThemeVariant.Default  // Follow system
            };
        }

        // ---------------- Properties ----------------

        public ObservableCollection<string> ThemeOptions { get; }

        public string ProjectsRootPath
        {
            get => _projectsRootPath;
            set => SetField(ref _projectsRootPath, value);
        }

        public bool AutoOpenLastProject
        {
            get => _autoOpenLastProject;
            set => SetField(ref _autoOpenLastProject, value);
        }

        public bool RememberWindowLayout
        {
            get => _rememberWindowLayout;
            set => SetField(ref _rememberWindowLayout, value);
        }

        public bool EnableAutoBackups
        {
            get => _enableAutoBackups;
            set => SetField(ref _enableAutoBackups, value);
        }

        public int AutoBackupIntervalMinutes
        {
            get => _autoBackupIntervalMinutes;
            set => SetField(ref _autoBackupIntervalMinutes, value);
        }

        public int MaxSnapshotsPerProject
        {
            get => _maxSnapshotsPerProject;
            set => SetField(ref _maxSnapshotsPerProject, value);
        }

        public string BackupLocationPath
        {
            get => _backupLocationPath;
            set => SetField(ref _backupLocationPath, value);
        }

        public bool UseBackupCompression
        {
            get => _useBackupCompression;
            set => SetField(ref _useBackupCompression, value);
        }

        public bool VerifyBackupsAfterCreate
        {
            get => _verifyBackupsAfterCreate;
            set => SetField(ref _verifyBackupsAfterCreate, value);
        }

        public bool PauseBackupsOnBattery
        {
            get => _pauseBackupsOnBattery;
            set => SetField(ref _pauseBackupsOnBattery, value);
        }

        public bool UseFullSnapshotHash
        {
            get => _useFullSnapshotHash;
            set => SetField(ref _useFullSnapshotHash, value);
        }

        public bool PreferExternalDrives
        {
            get => _preferExternalDrives;
            set => SetField(ref _preferExternalDrives, value);
        }

        public bool ShowDriveHealthWarnings
        {
            get => _showDriveHealthWarnings;
            set => SetField(ref _showDriveHealthWarnings, value);
        }

        public int MinimumFreeSpacePercent
        {
            get => _minimumFreeSpacePercent;
            set => SetField(ref _minimumFreeSpacePercent, value);
        }

        public bool UseCustomNetworkCredentials
        {
            get => _useCustomNetworkCredentials;
            set
            {
                if (SetField(ref _useCustomNetworkCredentials, value))
                {
                    OnPropertyChanged(nameof(ShowNetworkShareOptions));
                }
            }
        }

        public string NetworkShareUserName
        {
            get => _networkShareUserName;
            set => SetField(ref _networkShareUserName, value);
        }

        public string NetworkSharePassword
        {
            get => _networkSharePassword;
            set => SetField(ref _networkSharePassword, value);
        }

        public bool RememberNetworkCredentials
        {
            get => _rememberNetworkCredentials;
            set => SetField(ref _rememberNetworkCredentials, value);
        }

        /// <summary>
        /// Helper used by the UI to show/hide the NAS credentials section.
        /// </summary>
        public bool ShowNetworkShareOptions => UseCustomNetworkCredentials;

        public string SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (SetField(ref _selectedTheme, value))
                {
                    if (_isInitialized)
                    {
                        // change app theme live when dropdown changes
                        ApplyThemeFromSelected();
                    }
                }
            }
        }

        public bool UseCompactLayout
        {
            get => _useCompactLayout;
            set => SetField(ref _useCompactLayout, value);
        }

        public bool ShowProjectAvatars
        {
            get => _showProjectAvatars;
            set => SetField(ref _showProjectAvatars, value);
        }

        public bool NotificationsEnabled
        {
            get => _notificationsEnabled;
            set => SetField(ref _notificationsEnabled, value);
        }

        public bool NotifyOnBackupSuccess
        {
            get => _notifyOnBackupSuccess;
            set => SetField(ref _notifyOnBackupSuccess, value);
        }

        public bool NotifyOnBackupFailure
        {
            get => _notifyOnBackupFailure;
            set => SetField(ref _notifyOnBackupFailure, value);
        }

        public bool NotifyOnLowDiskSpace
        {
            get => _notifyOnLowDiskSpace;
            set => SetField(ref _notifyOnLowDiskSpace, value);
        }

        public bool NotifyOnSnapshotSuccess
        {
            get => _notifyOnSnapshotSuccess;
            set => SetField(ref _notifyOnSnapshotSuccess, value);
        }

        public bool NotifyOnSnapshotFailure
        {
            get => _notifyOnSnapshotFailure;
            set => SetField(ref _notifyOnSnapshotFailure, value);
        }

        public bool UseOsNotifications
        {
            get => _useOsNotifications;
            set => SetField(ref _useOsNotifications, value);
        }

        public bool NotifyOnlyWhenInactive
        {
            get => _notifyOnlyWhenInactive;
            set => SetField(ref _notifyOnlyWhenInactive, value);
        }

        public bool EnableVerboseLogging
        {
            get => _enableVerboseLogging;
            set => SetField(ref _enableVerboseLogging, value);
        }

        public bool CheckForUpdatesOnStartup
        {
            get => _checkForUpdatesOnStartup;
            set => SetField(ref _checkForUpdatesOnStartup, value);
        }

        public bool SendAnonymousUsageStats
        {
            get => _sendAnonymousUsageStats;
            set => SetField(ref _sendAnonymousUsageStats, value);
        }

        // ---------------- Commands ----------------
        public ICommand BrowseProjectsRootCommand { get; }
        public ICommand BrowseBackupLocationCommand { get; }
        public ICommand ResetToDefaultsCommand { get; }
        public ICommand ApplySettingsCommand { get; }
        public ICommand ClearLocalCacheCommand { get; }
        public ICommand ForgetAllProjectsCommand { get; }
        public ICommand TestNetworkConnectionCommand { get; }

        private void BrowseProjectsRoot()
        {
            // TODO: folder picker.
        }

        private void BrowseBackupLocation()
        {
            // TODO: folder picker.
        }

        private void ResetToDefaults()
        {
            AppConfigStore.Save(new AppConfig());
            LoadFromConfig();
        }

        private void ClearLocalCache()
        {
            // TODO
        }

        private void ForgetAllProjects()
        {
            try
            {
                // Dev helper: reset the VaultSync SQLite DB to a "fresh install" state
                // without touching any real project files or backup folders on disk.
                var cfg  = AppConfigStore.Load();
                var repo = new SqliteRepository(cfg.DbPath ?? string.Empty);

                repo.EnsureSchema();
                repo.ResetAllData();

                // Optionally, you could raise a notification/toast here in the future.
            }
            catch (Exception)
            {
                // For now, silently ignore errors. In the future we can surface a
                // message in the UI or log details when verbose logging is enabled.
            }
        }

        private void TestNetworkConnection()
        {
            // TODO
        }

    }
}