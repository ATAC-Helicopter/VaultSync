using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VaultSync.UI
{
    public sealed class SettingsViewModel : INotifyPropertyChanged
    {
        // ---------- General ----------
        private string _projectsRootPath = string.Empty;
        private bool _autoOpenLastProject = true;
        private bool _rememberWindowLayout = true;

        // ---------- Backups ----------
        private bool _enableAutoBackups = true;
        private int _autoBackupIntervalMinutes = 30;
        private int _maxSnapshotsPerProject = 20;
        private string _backupLocationPath = string.Empty;
        private bool _useBackupCompression = true;
        private bool _verifyBackupsAfterCreate = true;
        private bool _pauseBackupsOnBattery = true;

        // ---------- Storage ----------
        private bool _preferExternalDrives = true;
        private bool _showDriveHealthWarnings = true;
        private int _minimumFreeSpacePercent = 10;

        // Network share / NAS credentials
        private bool _useCustomNetworkCredentials = false;
        private string _networkShareUserName = string.Empty;
        private string _networkSharePassword = string.Empty;
        private bool _rememberNetworkCredentials = false;

        // ---------- Appearance ----------
        private string _selectedTheme;
        private bool _useCompactLayout = false;
        private bool _showProjectAvatars = true;

        // ---------- Notifications ----------
        private bool _notifyOnBackupSuccess = true;
        private bool _notifyOnBackupFailure = true;
        private bool _notifyOnLowDiskSpace = true;

        // ---------- Advanced ----------
        private bool _enableVerboseLogging = false;
        private bool _checkForUpdatesOnStartup = true;
        private bool _sendAnonymousUsageStats = false;

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
            ClearLocalCacheCommand       = new RelayCommand(_ => ClearLocalCache());
            ForgetAllProjectsCommand     = new RelayCommand(_ => ForgetAllProjects());
            TestNetworkConnectionCommand = new RelayCommand(_ => TestNetworkConnection(), _ => ShowNetworkShareOptions);
        }

        // ---------- INPC helpers ----------

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        // ---------- General ----------

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

        // ---------- Backups ----------

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
            set
            {
                if (SetField(ref _backupLocationPath, value))
                {
                    OnPropertyChanged(nameof(ShowNetworkShareOptions));
                    (TestNetworkConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
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

        // ---------- Storage ----------

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

        /// <summary>
        /// True when BackupLocationPath looks like a network share (UNC or smb:// / nfs://).
        /// Used to show/hide the network credentials section in the UI.
        /// </summary>
        public bool ShowNetworkShareOptions
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BackupLocationPath))
                    return false;

                var path = BackupLocationPath.Trim();

                // Windows UNC path: \\server\share
                if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                {
                    return uri.Scheme.Equals("smb", StringComparison.OrdinalIgnoreCase)
                           || uri.Scheme.Equals("nfs", StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }
        }

        public bool UseCustomNetworkCredentials
        {
            get => _useCustomNetworkCredentials;
            set => SetField(ref _useCustomNetworkCredentials, value);
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

        // ---------- Appearance ----------

        public ObservableCollection<string> ThemeOptions { get; }

        public string SelectedTheme
        {
            get => _selectedTheme;
            set => SetField(ref _selectedTheme, value);
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

        // ---------- Notifications ----------

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

        // ---------- Advanced ----------

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

        // ---------- Commands ----------

        public ICommand BrowseProjectsRootCommand { get; }
        public ICommand BrowseBackupLocationCommand { get; }
        public ICommand ResetToDefaultsCommand { get; }
        public ICommand ClearLocalCacheCommand { get; }
        public ICommand ForgetAllProjectsCommand { get; }
        public ICommand TestNetworkConnectionCommand { get; }

        private void BrowseProjectsRoot()
        {
            // TODO: integrate with folder picker / host service.
        }

        private void BrowseBackupLocation()
        {
            // TODO: integrate with folder picker / host service.
        }

        private void ResetToDefaults()
        {
            ProjectsRootPath          = string.Empty;
            AutoOpenLastProject       = true;
            RememberWindowLayout      = true;

            EnableAutoBackups         = true;
            AutoBackupIntervalMinutes = 30;
            MaxSnapshotsPerProject    = 20;
            BackupLocationPath        = string.Empty;
            UseBackupCompression      = true;
            VerifyBackupsAfterCreate  = true;
            PauseBackupsOnBattery     = true;

            PreferExternalDrives      = true;
            ShowDriveHealthWarnings   = true;
            MinimumFreeSpacePercent   = 10;

            UseCustomNetworkCredentials = false;
            NetworkShareUserName        = string.Empty;
            NetworkSharePassword        = string.Empty;
            RememberNetworkCredentials  = false;

            SelectedTheme             = ThemeOptions.Count > 0 ? ThemeOptions[0] : "Follow system";
            UseCompactLayout          = false;
            ShowProjectAvatars        = true;

            NotifyOnBackupSuccess     = true;
            NotifyOnBackupFailure     = true;
            NotifyOnLowDiskSpace      = true;

            EnableVerboseLogging      = false;
            CheckForUpdatesOnStartup  = true;
            SendAnonymousUsageStats   = false;
        }

        private void ClearLocalCache()
        {
            // TODO: hook into cache service.
        }

        private void ForgetAllProjects()
        {
            // TODO: hook into project registry to clear pinned/known projects.
        }

        private void TestNetworkConnection()
        {
            // TODO: real implementation later:
            //  - If UseCustomNetworkCredentials: try mounting/authenticating with provided credentials.
            //  - Else: try normal access to BackupLocationPath.
            //  - Then surface result via toast / dialog.
        }

        // ---------- RelayCommand ----------

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Func<object?, bool>? _canExecute;

            public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler? CanExecuteChanged;

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

            public void Execute(object? parameter) => _execute(parameter);

            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}