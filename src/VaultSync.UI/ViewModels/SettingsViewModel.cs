using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using VaultSync.UI.Services;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI
{
    public sealed class SettingsViewModel : INotifyPropertyChanged
    {
        // ---------------- Core backing fields ----------------

        private string _projectsRootPath = string.Empty;
        private bool _resumeLastSession = true;
        private bool _showWindowOnTrayActions = true;
        private bool _showTrayIcon = true;
        private bool _runInBackground = true;
        private bool _launchOnLogin = false;
        private List<int> _autoBackupDisabledProjects = new();

        private bool _enableAutoBackups = true;
        private int _autoBackupIntervalMinutes = 30;
        private int _maxSnapshotsPerProject = 20;
        private string _backupLocationPath = string.Empty;
        private bool _useAdvancedDestinations = false;
        private bool _useBackupCompression = false;
        private bool _useRsyncDelta = false;
        private bool _useIncrementalBackups = false;
        private bool _verifyBackupsAfterCreate = true;
        private bool _pauseBackupsOnBattery = true;
        private bool _useFullSnapshotHash = true;
        private bool _enableScanCache = true;
        private bool _aggressiveScanCache = false;
        private bool _enableArchiveUploadAutoTune = true;
        private bool _enableParallelArchiveUpload = true;
        private bool _enableMetadataSync = true;
        private bool _autoImportMetadata = true;
        private bool _promptRestoreAfterImport = true;
        private string _backupLocationStatus = string.Empty;

        private bool _preferExternalDrives = true;
        private bool _showDriveHealthWarnings = true;
        private int _minimumFreeSpacePercent = 10;

    private string _selectedTheme;
    private bool _useCompactLayout = false;
    private bool _showProjectAvatars = true;
    private string _saveStatus = string.Empty;

        private bool _notificationsEnabled = true;
        private bool _notifyOnBackupSuccess = true;
        private bool _notifyOnBackupFailure = true;
        private bool _notifyOnLowDiskSpace = true;
        private bool _showTrayBackupWidget = true;
        private bool _confirmDeleteBackup = true;

        private bool _notifyOnSnapshotSuccess = false;
        private bool _notifyOnSnapshotFailure = true;

        private bool _useOsNotifications = true;
        private bool _notifyOnlyWhenInactive = true;

        private bool _enableVerboseLogging = false;
        private bool _saveVerboseLogs = false;
        private bool _checkForUpdatesOnStartup = true;
        private int _updateCheckIntervalMinutes = 120;
        private bool _betaChannelEnabled = false;
        private readonly LocalizationService _localizationService;
        private DateTimeOffset? _lastUpdateCheckAt;
        private string? _lastUpdateCheckError;
        private string _updateCheckStatusText = string.Empty;
        private string _updateCheckErrorText = string.Empty;
        private string _rsyncStatusHint = string.Empty;
        private bool _showRsyncStatusHint;
        private string _selectedLanguageCode = "en";
        private readonly CredentialVault _credentialVault = CredentialVault.Instance;
        private readonly NetworkMountService _networkMountService = new();
        private bool _showLegacyBackupLocation = true;

        private bool _isInitialized;
        private bool _isSaving;
        private bool _savePending;

        public event Action? OpenLogConsoleRequested;
        public event Action? UpdateCheckRequested;
        public event Action? RefreshHistoryRequested;

        private sealed record DestinationSnapshot(
            string Alias,
            string? Path,
            bool Active,
            bool AutoMount,
            bool AutoUnmount,
            bool PreMounted,
            string? CredentialName,
            bool EnableMetadataSync,
            bool AutoImportMetadata,
            bool ForceMetadataBackfill);

        private sealed record CredentialSnapshot(
            string Name,
            string Username,
            string Domain,
            string KeyRef,
            bool UseKeychain,
            string Password);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void RefreshLegacyVisibility()
        {
            ShowLegacyBackupLocation = !UseAdvancedDestinations;
        }

        public SettingsViewModel(LocalizationService localizationService)
        {
            _localizationService = localizationService;
            _selectedLanguageCode = localizationService.CurrentLanguage;
            _localizationService.LanguageChanged += () =>
            {
                OnPropertyChanged(nameof(SelectedLanguage));
                RefreshUpdateCheckStatus();
                RefreshRsyncStatusHint();
            };

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
            ApplySettingsCommand         = new RelayCommand(async _ => await SaveToConfigAsync(notifyOnValidationError: true));
            ClearLocalCacheCommand       = new RelayCommand(_ => ClearLocalCache());
            ForgetAllProjectsCommand     = new RelayCommand(_ => ForgetAllProjects());
            TestBackupLocationCommand    = new RelayCommand(_ => TestBackupLocation(), _ => !string.IsNullOrWhiteSpace(BackupLocationPath));
            AddDestinationCommand        = new RelayCommand(_ => AddDestination());
            RemoveDestinationCommand     = new RelayCommand(p => RemoveDestination(p as BackupDestinationViewModel));
            BrowseDestinationCommand     = new RelayCommand(p => BrowseDestination(p as BackupDestinationViewModel));
            TestDestinationCommand       = new RelayCommand(p => TestDestination(p as BackupDestinationViewModel));
            AddCredentialCommand         = new RelayCommand(_ => AddCredential());
            RemoveCredentialCommand      = new RelayCommand(p => RemoveCredential(p as NetworkCredentialViewModel));
            OpenHelpCommand              = new RelayCommand(_ => OpenHelp());
            ExportTelemetryCommand       = new RelayCommand(_ => ExportTelemetry());
            OpenLogConsoleCommand        = new RelayCommand(_ => OpenLogConsole());
            ExportLogConsoleCommand      = new RelayCommand(_ => ExportLogConsole());
            CheckUpdatesNowCommand       = new RelayCommand(_ => CheckUpdatesNow());
            RefreshHistoryCommand        = new RelayCommand(_ => RefreshHistoryRequested?.Invoke());

            CredentialProfiles.CollectionChanged += OnCredentialProfilesCollectionChanged;
            Destinations.CollectionChanged       += OnDestinationsCollectionChanged;

            PropertyChanged += OnSettingsPropertyChanged;

            // AUTO-LOAD CONFIG ON STARTUP
            LoadFromConfig();
            UpdateUpdateCheckStatus(null, null);
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
            _selectedLanguageCode = string.IsNullOrWhiteSpace(cfg.Advanced.Language)
                ? _localizationService.CurrentLanguage
                : cfg.Advanced.Language;
            _localizationService.SetLanguage(_selectedLanguageCode);

            _projectsRootPath      = cfg.ProjectsRoot ?? "";
            _resumeLastSession     = cfg.ResumeLastSession;
            _showWindowOnTrayActions = cfg.Behavior.ShowWindowOnTrayActions;
            _showTrayIcon            = cfg.Behavior.ShowTrayIcon;
            _runInBackground         = cfg.Behavior.RunInBackground;
            _showTrayBackupWidget    = cfg.Behavior.ShowBackupWidget;
            _launchOnLogin           = cfg.Behavior.LaunchOnLogin;
            _confirmDeleteBackup     = cfg.Behavior.ConfirmDeleteBackup;

            _enableAutoBackups         = cfg.Backups.EnableAutoBackups;
            _autoBackupIntervalMinutes = ClampInt(cfg.Backups.IntervalMinutes, 1, 10080, 30);
            _maxSnapshotsPerProject    = ClampInt(cfg.Backups.MaxSnapshotsPerProject, 1, 10000, 20);
            _autoBackupDisabledProjects = cfg.Backups.AutoBackupDisabledProjects ?? new List<int>();
            // Back-compat: older configs may have Destinations populated but no explicit toggle saved yet.
            _useAdvancedDestinations   = cfg.Backups.UseAdvancedDestinations || (cfg.Backups.Destinations?.Count > 0);
            _backupLocationPath        = string.IsNullOrWhiteSpace(cfg.Backups.BackupRoot)
                ? (cfg.Backups.Location ?? string.Empty)
                : cfg.Backups.BackupRoot;
            _useBackupCompression      = cfg.Backups.UseCompression;
            _useRsyncDelta             = cfg.Backups.UseRsyncDelta;
            _useIncrementalBackups     = cfg.Backups.UseIncrementalBackups;
            _verifyBackupsAfterCreate  = cfg.Backups.VerifyAfterCreate;
            _pauseBackupsOnBattery     = cfg.Backups.PauseOnBattery;
            _useFullSnapshotHash       = cfg.Backups.UseFullSnapshotHash;
            _enableScanCache           = cfg.Backups.EnableScanCache;
            _aggressiveScanCache       = cfg.Backups.AggressiveScanCache;
            _enableArchiveUploadAutoTune = cfg.Backups.EnableArchiveUploadAutoTune;
            _enableParallelArchiveUpload = cfg.Backups.EnableParallelArchiveUpload;
            _enableMetadataSync        = cfg.Backups.EnableMetadataSync;
            _autoImportMetadata        = cfg.Backups.AutoImportMetadata;
            _promptRestoreAfterImport  = cfg.Backups.PromptRestoreAfterImport;

            _preferExternalDrives    = cfg.Storage.PreferExternalDrives;
            _showDriveHealthWarnings = cfg.Storage.ShowDriveWarnings;
            _minimumFreeSpacePercent = ClampInt(cfg.Storage.MinFreeSpacePercent, 0, 95, 10);
            RefreshRsyncStatusHint();

            foreach (var cred in CredentialProfiles.ToList())
            {
                cred.PropertyChanged -= OnNestedPropertyChanged;
            }
            CredentialProfiles.Clear();
            foreach (var cred in cfg.Network.Credentials ?? new List<NetworkCredentialProfile>())
            {
                var keyRef  = _credentialVault.EnsureKeyRef(cred.KeyRef, cred.Name);
                var secret  = _credentialVault.GetSecret(keyRef, cred.Username, cred.UseKeychain, cred.Password);

                CredentialProfiles.Add(new NetworkCredentialViewModel
                {
                    Name        = cred.Name,
                    Username    = cred.Username,
                    Domain      = cred.Domain ?? string.Empty,
                    KeyRef      = keyRef,
                    UseKeychain = cred.UseKeychain,
                    Password    = secret ?? string.Empty
                });
            }

            foreach (var dest in Destinations.ToList())
            {
                dest.PropertyChanged -= OnNestedPropertyChanged;
            }
            Destinations.Clear();
            if (cfg.Backups.Destinations != null && cfg.Backups.Destinations.Count > 0)
            {
                foreach (var dest in cfg.Backups.Destinations)
                {
                    var vm = new BackupDestinationViewModel
                    {
                        Alias        = dest.Alias ?? string.Empty,
                        Path         = dest.Path,
                        Active       = dest.Active,
                        AutoMount    = dest.AutoMount,
                        AutoUnmount  = dest.AutoUnmount,
                        PreMounted   = dest.PreMounted,
                        EnableMetadataSync = dest.EnableMetadataSync,
                        AutoImportMetadata = dest.AutoImportMetadata,
                        ForceMetadataBackfill = dest.ForceMetadataBackfill
                    };

                    vm.SelectedCredential = CredentialProfiles.FirstOrDefault(c =>
                        c.Name.Equals(dest.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                    vm.CredentialName = vm.SelectedCredential?.Name ?? dest.CredentialName ?? string.Empty;

                    Destinations.Add(vm);
                }
            }
            else
            {
                // fallback: use BackupRoot as a single destination
                if (!string.IsNullOrWhiteSpace(_backupLocationPath))
                {
                Destinations.Add(new BackupDestinationViewModel
                {
                    Alias       = "Primary",
                    Path        = _backupLocationPath,
                    Active      = true,
                    PreMounted  = true,
                    AutoMount   = false,
                    AutoUnmount = false,
                    EnableMetadataSync = true,
                    AutoImportMetadata = true,
                    ForceMetadataBackfill = false
                });
            }
            }
            RefreshLegacyVisibility();

            // FIX: use Theme instead of ThemeName
            _selectedTheme      = DisplayThemeOption(cfg.Appearance.Theme ?? "System");
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
            _saveVerboseLogs           = cfg.Advanced.SaveVerboseLogs;
            _checkForUpdatesOnStartup  = cfg.Advanced.CheckUpdates;
            _updateCheckIntervalMinutes = ClampInt(cfg.Advanced.UpdateCheckIntervalMinutes, 15, 10080, 120);
            _betaChannelEnabled         = cfg.Advanced.BetaChannelEnabled;

            // Apply theme + layout when loading config (in case Settings view is opened first)
            ApplyThemeFromSelected();
            ThemeManager.ApplyCompactLayout(_useCompactLayout);

            SaveStatus = "Settings loaded";

            // Update UI
            OnPropertyChanged(null);
        }

        private async Task SaveToConfigAsync(bool notifyOnValidationError = true)
        {
            // Start from the latest persisted config so we don't clobber fields the Settings view doesn't edit
            // (e.g., LastView, DbPath, tray settings).
            if (!ValidateDestinations(notifyOnValidationError))
            {
                return;
            }

            // Keep name + object selection aligned before taking snapshots.
            foreach (var dest in Destinations)
            {
                if (dest.SelectedCredential is not null)
                {
                    dest.CredentialName = dest.SelectedCredential.Name;
                }
                else if (!string.IsNullOrWhiteSpace(dest.CredentialName))
                {
                    dest.SelectedCredential = CredentialProfiles.FirstOrDefault(c =>
                        c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));
                }
            }

            // Snapshot UI state to avoid cross-thread collection access during background work.
            var destinationSnapshot = Destinations
                .Select(d => new DestinationSnapshot(
                    Alias: d.Alias,
                    Path: d.Path,
                    Active: d.Active,
                    AutoMount: d.AutoMount,
                    AutoUnmount: d.AutoUnmount,
                    PreMounted: d.PreMounted,
                    CredentialName: d.SelectedCredential?.Name ?? d.CredentialName,
                    EnableMetadataSync: d.EnableMetadataSync,
                    AutoImportMetadata: d.AutoImportMetadata,
                    ForceMetadataBackfill: d.ForceMetadataBackfill))
                .ToList();

            var credentialSnapshot = CredentialProfiles
                .Select(c => new CredentialSnapshot(
                    Name: c.Name,
                    Username: c.Username,
                    Domain: c.Domain,
                    KeyRef: c.KeyRef,
                    UseKeychain: c.UseKeychain,
                    Password: c.Password))
                .ToList();

            var cfg = AppConfigStore.Load();

            // Reload latest disabled list to avoid clobbering project-level auto-backup toggles.
            _autoBackupDisabledProjects = cfg.Backups.AutoBackupDisabledProjects ?? new List<int>();

            cfg.ProjectsRoot      = ProjectsRootPath;
            cfg.ResumeLastSession = ResumeLastSession;

            cfg.Behavior.LaunchOnLogin           = _launchOnLogin;
            cfg.Behavior.ShowWindowOnTrayActions = _showWindowOnTrayActions;
            cfg.Behavior.ShowTrayIcon            = _showTrayIcon;
            cfg.Behavior.RunInBackground         = _runInBackground;
            cfg.Behavior.ShowBackupWidget        = _showTrayBackupWidget;
            cfg.Behavior.ConfirmDeleteBackup     = _confirmDeleteBackup;

            cfg.Backups.EnableAutoBackups           = EnableAutoBackups;
            cfg.Backups.IntervalMinutes             = ClampInt(AutoBackupIntervalMinutes, 1, 10080, 30);
            cfg.Backups.MaxSnapshotsPerProject      = ClampInt(MaxSnapshotsPerProject, 1, 10000, 20);
            cfg.Backups.AutoBackupDisabledProjects  = _autoBackupDisabledProjects;
            cfg.Backups.UseAdvancedDestinations     = UseAdvancedDestinations;
            // Sync legacy backup root so older code paths still work.
            // - Simple mode: BackupLocationPath is authoritative.
            // - Advanced mode: use the first active destination as the canonical root.
            var fallbackRoot = UseAdvancedDestinations
                ? (Destinations.FirstOrDefault(d => d.Active)?.Path ?? Destinations.FirstOrDefault()?.Path)
                : BackupLocationPath;
            cfg.Backups.BackupRoot = string.IsNullOrWhiteSpace(fallbackRoot) ? null : fallbackRoot;
            cfg.Backups.Location   = cfg.Backups.BackupRoot;
            cfg.Backups.UseCompression              = UseBackupCompression;
            cfg.Backups.UseRsyncDelta               = UseRsyncDelta;
            cfg.Backups.UseIncrementalBackups       = UseIncrementalBackups;
            cfg.Backups.VerifyAfterCreate           = VerifyBackupsAfterCreate;
            cfg.Backups.PauseOnBattery              = PauseBackupsOnBattery;
            cfg.Backups.UseFullSnapshotHash         = _useFullSnapshotHash;
            cfg.Backups.EnableScanCache             = _enableScanCache;
            cfg.Backups.AggressiveScanCache         = _aggressiveScanCache;
            cfg.Backups.EnableArchiveUploadAutoTune = _enableArchiveUploadAutoTune;
            cfg.Backups.EnableParallelArchiveUpload = _enableParallelArchiveUpload;
            cfg.Backups.EnableMetadataSync          = EnableMetadataSync;
            cfg.Backups.AutoImportMetadata          = AutoImportMetadata;
            cfg.Backups.PromptRestoreAfterImport    = PromptRestoreAfterImport;
            cfg.Backups.Destinations                = destinationSnapshot.Select(d => new BackupDestination
            {
                Alias          = d.Alias,
                Path           = d.Path,
                CredentialName = d.CredentialName,
                Active         = d.Active,
                AutoMount      = d.AutoMount,
                AutoUnmount    = d.AutoUnmount,
                PreMounted     = d.PreMounted,
                EnableMetadataSync = d.EnableMetadataSync,
                AutoImportMetadata = d.AutoImportMetadata,
                ForceMetadataBackfill = d.ForceMetadataBackfill
            }).ToList();

            cfg.Storage.PreferExternalDrives = PreferExternalDrives;
            cfg.Storage.ShowDriveWarnings    = ShowDriveHealthWarnings;
            cfg.Storage.MinFreeSpacePercent  = MinimumFreeSpacePercent;

            var credentialSave = await Task.Run(() =>
            {
                var savedCreds = new List<NetworkCredentialProfile>();
                var hadPlaintextFallback = false;

                foreach (var c in credentialSnapshot)
                {
                    var keyRef = _credentialVault.EnsureKeyRef(c.KeyRef, c.Name);

                    var secret = !string.IsNullOrWhiteSpace(c.Password)
                        ? c.Password
                        : _credentialVault.GetSecret(keyRef, c.Username, c.UseKeychain);

                    var persistPlaintext = false;

                    if (!string.IsNullOrWhiteSpace(secret))
                    {
                        try
                        {
                            _credentialVault.SaveSecret(keyRef, c.Username, secret, c.UseKeychain);
                        }
                        catch
                        {
                            persistPlaintext = true;
                        }
                    }

                    hadPlaintextFallback |= persistPlaintext;

                    savedCreds.Add(new NetworkCredentialProfile
                    {
                        Name        = c.Name,
                        Username    = c.Username,
                        Domain      = c.Domain,
                        KeyRef      = keyRef,
                        UseKeychain = c.UseKeychain,
                        Password    = persistPlaintext ? secret ?? string.Empty : string.Empty // keep out of config unless we must
                    });
                }

                return (savedCreds, hadPlaintextFallback);
            });

            cfg.Network.Credentials = credentialSave.savedCreds;

            cfg.Appearance.Theme              = NormalizeThemeOption(SelectedTheme);
            cfg.Appearance.CompactLayout      = UseCompactLayout;
            cfg.Appearance.ShowProjectAvatars = ShowProjectAvatars;

            cfg.Notifications.OnBackupSuccess    = NotifyOnBackupSuccess;
            cfg.Notifications.OnBackupFailure    = NotifyOnBackupFailure;
            cfg.Notifications.OnLowDisk          = NotifyOnLowDiskSpace;
            cfg.Notifications.OnSnapshotSuccess  = NotifyOnSnapshotSuccess;
            cfg.Notifications.OnSnapshotFailure  = NotifyOnSnapshotFailure;
            cfg.Notifications.UseOsNotifications = UseOsNotifications;
            cfg.Notifications.OnlyWhenInactive   = NotifyOnlyWhenInactive;

            cfg.Advanced.VerboseLogging      = EnableVerboseLogging;
            cfg.Advanced.SaveVerboseLogs     = SaveVerboseLogs;
            cfg.Advanced.CheckUpdates        = CheckForUpdatesOnStartup;
            cfg.Advanced.UpdateCheckIntervalMinutes = ClampInt(UpdateCheckIntervalMinutes, 15, 10080, 120);
            cfg.Advanced.BetaChannelEnabled  = BetaChannelEnabled;
            cfg.Advanced.Language            = SelectedLanguageCode;

            AutoStartService.SetLaunchOnLogin(_launchOnLogin);

            AppConfigStore.Save(cfg);

            SaveStatus = credentialSave.hadPlaintextFallback
                ? $"Saved (with credential fallback) at {DateTime.Now:HH:mm:ss}"
                : $"Saved at {DateTime.Now:HH:mm:ss}";
        }

        private bool ValidateDestinations(bool notifyOnError)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dest in Destinations)
            {
                if (string.IsNullOrWhiteSpace(dest.Path))
                {
                    if (notifyOnError)
                    {
                        SaveStatus = "Destination path is required.";
                        GlobalNotificationCenter.Instance.Show(
                            SaveStatus,
                            NotificationSeverity.Error,
                            "Destination validation");
                    }
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(dest.Alias) && !aliases.Add(dest.Alias))
                {
                    if (notifyOnError)
                    {
                        SaveStatus = $"Duplicate destination alias '{dest.Alias}'.";
                        GlobalNotificationCenter.Instance.Show(
                            SaveStatus,
                            NotificationSeverity.Error,
                            "Destination validation");
                    }
                    return false;
                }

            }

            return true;
        }

        private void OnCredentialProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (NetworkCredentialViewModel cred in e.NewItems)
                {
                    cred.PropertyChanged += OnNestedPropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (NetworkCredentialViewModel cred in e.OldItems)
                {
                    cred.PropertyChanged -= OnNestedPropertyChanged;
                }
            }

            OnPropertyChanged(nameof(CredentialNames));
            TriggerAutoSave();
        }

        private void OnDestinationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (BackupDestinationViewModel dest in e.NewItems)
                {
                    dest.PropertyChanged += OnNestedPropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (BackupDestinationViewModel dest in e.OldItems)
                {
                    dest.PropertyChanged -= OnNestedPropertyChanged;
                }
            }

            RefreshLegacyVisibility();
            TriggerAutoSave();
        }

        private void OnNestedPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is BackupDestinationViewModel dest &&
                string.Equals(e.PropertyName, nameof(BackupDestinationViewModel.CredentialName), StringComparison.Ordinal))
            {
                // Re-sync SelectedCredential when only the name is set (e.g., via binding)
                var match = CredentialProfiles.FirstOrDefault(c =>
                    c.Name.Equals(dest.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (!ReferenceEquals(dest.SelectedCredential, match))
                {
                    dest.SelectedCredential = match;
                }
            }

            TriggerAutoSave();
        }

        private async void TriggerAutoSave()
        {
            if (!_isInitialized)
                return;

            if (_isSaving)
            {
                _savePending = true;
                return;
            }

            try
            {
                _isSaving = true;
            await SaveToConfigAsync(notifyOnValidationError: false);
            }
            catch (Exception ex)
            {
                // Prevent background save exceptions from crashing the app; surface in status + debug output.
                SaveStatus = $"Save failed: {ex.Message}";
                Debug.WriteLine($"[SettingsViewModel] Auto-save failed: {ex}");
            }
            finally
            {
                _isSaving = false;
                if (_savePending)
                {
                    _savePending = false;
                    TriggerAutoSave();
                }
            }
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null)
                return;

            TriggerAutoSave();
        }

        public void RebindDestinationCredentials()
        {
            foreach (var dest in Destinations)
            {
                if (!string.IsNullOrWhiteSpace(dest.CredentialName))
                {
                    var match = CredentialProfiles.FirstOrDefault(c =>
                        c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));
                    dest.SelectedCredential = match;
                }
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

        private static string NormalizeThemeOption(string theme)
        {
            return theme switch
            {
                "Dark"          => "Dark",
                "Light"         => "Light",
                "Follow system" => "System",
                "System"        => "System",
                _               => "System"
            };
        }

        private static string DisplayThemeOption(string storedTheme)
        {
            return storedTheme switch
            {
                "Dark"  => "Dark",
                "Light" => "Light",
                _       => "Follow system"
            };
        }

        private static int ClampInt(int value, int min, int max, int fallback)
        {
            if (value < min || value > max)
                return fallback;
            return value;
        }

        // ---------------- Properties ----------------

        public ObservableCollection<string> ThemeOptions { get; }

        public string ProjectsRootPath
        {
            get => _projectsRootPath;
            set => SetField(ref _projectsRootPath, value);
        }

        public bool ResumeLastSession
        {
            get => _resumeLastSession;
            set => SetField(ref _resumeLastSession, value);
        }

        public bool EnableAutoBackups
        {
            get => _enableAutoBackups;
            set => SetField(ref _enableAutoBackups, value);
        }

        public int AutoBackupIntervalMinutes
        {
            get => _autoBackupIntervalMinutes;
            set => SetField(ref _autoBackupIntervalMinutes, ClampInt(value, 1, 10080, _autoBackupIntervalMinutes));
        }

        public int MaxSnapshotsPerProject
        {
            get => _maxSnapshotsPerProject;
            set => SetField(ref _maxSnapshotsPerProject, ClampInt(value, 1, 10000, _maxSnapshotsPerProject));
        }

        public string BackupLocationPath
        {
            get => _backupLocationPath;
            set
            {
                if (SetField(ref _backupLocationPath, value))
                {
                    ValidateBackupLocation(value);
                }
            }
        }

        public bool UseAdvancedDestinations
        {
            get => _useAdvancedDestinations;
            set
            {
                if (SetField(ref _useAdvancedDestinations, value))
                {
                    RefreshLegacyVisibility();
                }
            }
        }

        public bool ShowLegacyBackupLocation
        {
            get => _showLegacyBackupLocation;
            private set => SetField(ref _showLegacyBackupLocation, value);
        }

        public string BackupLocationStatus
        {
            get => _backupLocationStatus;
            private set => SetField(ref _backupLocationStatus, value);
        }

        public bool UseBackupCompression
        {
            get => _useBackupCompression;
            set => SetField(ref _useBackupCompression, value);
        }

        public bool UseRsyncDelta
        {
            get => _useRsyncDelta;
            set => SetField(ref _useRsyncDelta, value);
        }

        public bool UseIncrementalBackups
        {
            get => _useIncrementalBackups;
            set
            {
                if (SetField(ref _useIncrementalBackups, value))
                {
                    OnPropertyChanged(nameof(IsRsyncDeltaAvailable));
                    if (value && _useRsyncDelta)
                    {
                        _useRsyncDelta = false;
                        OnPropertyChanged(nameof(UseRsyncDelta));
                    }
                }
            }
        }

        public bool IsRsyncDeltaAvailable => !_useIncrementalBackups;

        public string RsyncStatusHint
        {
            get => _rsyncStatusHint;
            private set => SetField(ref _rsyncStatusHint, value);
        }

        public bool ShowRsyncStatusHint
        {
            get => _showRsyncStatusHint;
            private set => SetField(ref _showRsyncStatusHint, value);
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

        public bool EnableScanCache
        {
            get => _enableScanCache;
            set => SetField(ref _enableScanCache, value);
        }

        public bool AggressiveScanCache
        {
            get => _aggressiveScanCache;
            set => SetField(ref _aggressiveScanCache, value);
        }

        public bool EnableArchiveUploadAutoTune
        {
            get => _enableArchiveUploadAutoTune;
            set => SetField(ref _enableArchiveUploadAutoTune, value);
        }

        public bool EnableParallelArchiveUpload
        {
            get => _enableParallelArchiveUpload;
            set => SetField(ref _enableParallelArchiveUpload, value);
        }

        public bool EnableMetadataSync
        {
            get => _enableMetadataSync;
            set => SetField(ref _enableMetadataSync, value);
        }

        public bool AutoImportMetadata
        {
            get => _autoImportMetadata;
            set => SetField(ref _autoImportMetadata, value);
        }

        public bool PromptRestoreAfterImport
        {
            get => _promptRestoreAfterImport;
            set => SetField(ref _promptRestoreAfterImport, value);
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
            set => SetField(ref _minimumFreeSpacePercent, ClampInt(value, 0, 95, _minimumFreeSpacePercent));
        }

        public ObservableCollection<BackupDestinationViewModel> Destinations { get; } = new();
        public ObservableCollection<NetworkCredentialViewModel> CredentialProfiles { get; } = new();
        public IEnumerable<string> CredentialNames => CredentialProfiles.Select(c => c.Name);

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

        public bool ShowWindowOnTrayActions
        {
            get => _showWindowOnTrayActions;
            set => SetField(ref _showWindowOnTrayActions, value);
        }

        public bool ShowTrayIcon
        {
            get => _showTrayIcon;
            set => SetField(ref _showTrayIcon, value);
        }

        public bool RunInBackground
        {
            get => _runInBackground;
            set => SetField(ref _runInBackground, value);
        }

        public bool ShowTrayBackupWidget
        {
            get => _showTrayBackupWidget;
            set => SetField(ref _showTrayBackupWidget, value);
        }

        public bool ConfirmDeleteBackups
        {
            get => _confirmDeleteBackup;
            set => SetField(ref _confirmDeleteBackup, value);
        }

        public bool LaunchOnLogin
        {
            get => _launchOnLogin;
            set => SetField(ref _launchOnLogin, value);
        }

        public bool UseCompactLayout
        {
            get => _useCompactLayout;
            set
            {
                if (SetField(ref _useCompactLayout, value) && _isInitialized)
                {
                    ThemeManager.ApplyCompactLayout(value);
                }
            }
        }

        public bool ShowProjectAvatars
        {
            get => _showProjectAvatars;
            set => SetField(ref _showProjectAvatars, value);
        }

        public string SaveStatus
        {
            get => _saveStatus;
            private set => SetField(ref _saveStatus, value);
        }

        public bool NotificationsEnabled
        {
            get => _notificationsEnabled;
            set
            {
                if (!SetField(ref _notificationsEnabled, value))
                    return;

                if (!value)
                {
                    NotifyOnBackupSuccess   = false;
                    NotifyOnBackupFailure   = false;
                    NotifyOnSnapshotSuccess = false;
                    NotifyOnSnapshotFailure = false;
                    NotifyOnLowDiskSpace    = false;
                    UseOsNotifications      = false;
                }
                else
                {
                    NotifyOnBackupSuccess   = true;
                    NotifyOnBackupFailure   = true;
                    NotifyOnSnapshotSuccess = false;
                    NotifyOnSnapshotFailure = true;
                    NotifyOnLowDiskSpace    = true;
                    UseOsNotifications      = true;
                }
            }
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

        public bool SaveVerboseLogs
        {
            get => _saveVerboseLogs;
            set => SetField(ref _saveVerboseLogs, value);
        }

        public bool CheckForUpdatesOnStartup
        {
            get => _checkForUpdatesOnStartup;
            set => SetField(ref _checkForUpdatesOnStartup, value);
        }

        public int UpdateCheckIntervalMinutes
        {
            get => _updateCheckIntervalMinutes;
            set => SetField(ref _updateCheckIntervalMinutes, value);
        }

        public bool BetaChannelEnabled
        {
            get => _betaChannelEnabled;
            set => SetField(ref _betaChannelEnabled, value);
        }

        public string UpdateCheckStatusText
        {
            get => _updateCheckStatusText;
            private set => SetField(ref _updateCheckStatusText, value);
        }

        public string UpdateCheckErrorText
        {
            get => _updateCheckErrorText;
            private set
            {
                if (SetField(ref _updateCheckErrorText, value))
                {
                    OnPropertyChanged(nameof(HasUpdateCheckError));
                }
            }
        }

        public bool HasUpdateCheckError => !string.IsNullOrWhiteSpace(_updateCheckErrorText);

        public IReadOnlyList<LanguageOption> LanguageOptions => _localizationService.SupportedLanguages;

        public string SelectedLanguageCode
        {
            get => _selectedLanguageCode;
            set
            {
                if (_selectedLanguageCode == value)
                    return;

                if (!_localizationService.SetLanguage(value))
                    return;

                if (SetField(ref _selectedLanguageCode, value))
                {
                    OnPropertyChanged(nameof(SelectedLanguage));
                    PersistLanguage();
                }
            }
        }

        public LanguageOption? SelectedLanguage
        {
            get => LanguageOptions
                .FirstOrDefault(option => string.Equals(option.Code, _selectedLanguageCode, StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null)
                    return;

                SelectedLanguageCode = value.Code;
            }
        }

        private void PersistLanguage()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    cfg.Advanced.Language = _selectedLanguageCode;
                    AppConfigStore.Save(cfg);
                }
                catch
                {
                    // Best effort; avoid crashing when persisting language.
                }
            });
        }

        public void UpdateUpdateCheckStatus(DateTimeOffset? lastCheck, string? errorMessage)
        {
            _lastUpdateCheckAt = lastCheck;
            _lastUpdateCheckError = errorMessage;
            RefreshUpdateCheckStatus();
        }

        private void RefreshUpdateCheckStatus()
        {
            var neverText = L("Settings.Advanced.UpdateStatusNever", "Never checked");
            var lastTemplate = L("Settings.Advanced.UpdateStatusLast", "Last check: {0}");
            var errorTemplate = L("Settings.Advanced.UpdateStatusError", "Last error: {0}");

            if (_lastUpdateCheckAt.HasValue)
            {
                var formatted = _lastUpdateCheckAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                UpdateCheckStatusText = string.Format(CultureInfo.CurrentCulture, lastTemplate, formatted);
            }
            else
            {
                UpdateCheckStatusText = neverText;
            }

            UpdateCheckErrorText = string.IsNullOrWhiteSpace(_lastUpdateCheckError)
                ? string.Empty
                : string.Format(CultureInfo.CurrentCulture, errorTemplate, _lastUpdateCheckError);
        }

        private void RefreshRsyncStatusHint()
        {
            if (!OperatingSystem.IsMacOS())
            {
                ShowRsyncStatusHint = false;
                RsyncStatusHint = string.Empty;
                return;
            }

            var rsyncPath = TryGetBundledRsyncPath() ?? TryFindRsyncOnPath();
            if (string.IsNullOrWhiteSpace(rsyncPath))
            {
                ShowRsyncStatusHint = true;
                RsyncStatusHint = L(
                    "Settings.Backups.RsyncMissingHint",
                    "rsync not found. VaultSync will fall back to the built-in copy method. Reinstall the app or install rsync to restore delta sync."
                );
                return;
            }

            var version = TryGetRsyncVersion(rsyncPath);
            if (version is null || version < new Version(3, 1, 0))
            {
                ShowRsyncStatusHint = true;
                RsyncStatusHint = L(
                    "Settings.Backups.RsyncOldHint",
                    "Your rsync version is too old for progress reporting. Backups will still run, but progress may be limited."
                );
                return;
            }

            ShowRsyncStatusHint = false;
            RsyncStatusHint = string.Empty;
        }

        private string L(string key, string fallback)
        {
            var value = _localizationService.GetString(key);
            return string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
                ? fallback
                : value;
        }

        private static string? TryFindRsyncOnPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, "rsync");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // ignore invalid PATH entries
                }
            }

            return null;
        }

        private static string? TryGetBundledRsyncPath()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var arch = RuntimeInformation.OSArchitecture;
                var candidates = new List<string>();
                if (arch == Architecture.Arm64)
                {
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "arm64", "bin", "rsync"));
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "arm64", "rsync"));
                }
                else if (arch == Architecture.X64)
                {
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "x64", "bin", "rsync"));
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "x64", "rsync"));
                }
                else
                {
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "arm64", "bin", "rsync"));
                    candidates.Add(Path.Combine(baseDir, "tools", "rsync", "x64", "bin", "rsync"));
                }

                candidates.Add(Path.Combine(baseDir, "tools", "rsync", "rsync"));
                candidates.Add(Path.Combine(baseDir, "tools", "rsync", "bin", "rsync"));

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static Version? TryGetRsyncVersion(string rsyncPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = rsyncPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ConfigureMacLibraryPath(psi, rsyncPath);
                psi.ArgumentList.Add("--version");

                using var proc = Process.Start(psi);
                if (proc is null)
                    return null;

                if (!proc.WaitForExit(2000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return null;
                }

                var output = proc.StandardOutput.ReadToEnd();
                return ParseVersion(output);
            }
            catch
            {
                return null;
            }
        }

        private static Version? ParseVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                return null;

            var tokens = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var versionToken = tokens.FirstOrDefault(t => t.Any(char.IsDigit) && t.Contains('.'));
            return Version.TryParse(versionToken, out var parsed) ? parsed : null;
        }

        private static void ConfigureMacLibraryPath(ProcessStartInfo psi, string rsyncPath)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            var directory = Path.GetDirectoryName(rsyncPath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            var libDir = Path.GetFullPath(Path.Combine(directory, "..", "lib"));
            if (!Directory.Exists(libDir))
                return;

            var existing = psi.Environment.TryGetValue("DYLD_LIBRARY_PATH", out var current)
                ? current ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_LIBRARY_PATH"] = PrependPathEntry(existing, libDir);

            var fallback = psi.Environment.TryGetValue("DYLD_FALLBACK_LIBRARY_PATH", out var fallbackCurrent)
                ? fallbackCurrent ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = PrependPathEntry(fallback, libDir);
        }

        private static string PrependPathEntry(string existing, string entry)
        {
            if (string.IsNullOrWhiteSpace(existing))
                return entry;

            var parts = existing.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => string.Equals(p, entry, StringComparison.Ordinal)))
                return existing;

            return $"{entry}:{existing}";
        }

        // ---------------- Commands ----------------
        public ICommand BrowseProjectsRootCommand { get; }
    public ICommand BrowseBackupLocationCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }
        public ICommand ApplySettingsCommand { get; }
        public ICommand ClearLocalCacheCommand { get; }
        public ICommand ForgetAllProjectsCommand { get; }
        public ICommand TestBackupLocationCommand { get; }
        public ICommand AddDestinationCommand { get; }
    public ICommand RemoveDestinationCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand TestDestinationCommand { get; }
    public ICommand AddCredentialCommand { get; }
    public ICommand RemoveCredentialCommand { get; }
        public ICommand OpenHelpCommand { get; }
        public ICommand ExportTelemetryCommand { get; }
        public ICommand OpenLogConsoleCommand { get; }
        public ICommand ExportLogConsoleCommand { get; }
        public ICommand CheckUpdatesNowCommand { get; }
        public ICommand RefreshHistoryCommand { get; }

        private async void BrowseProjectsRoot()
        {
            var storageProvider = GetStorageProvider();
            if (storageProvider is null)
                return;

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose projects root",
                AllowMultiple = false
            });

            var folder = folders?.FirstOrDefault();
            var path = folder?.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    var config = AppConfigStore.Load();
                    config.ProjectsRoot = path;
                    AppConfigStore.Save(config);
                }
                catch
                {
                    // Best effort
                }
            });

            ProjectsRootPath = path;
        }

        private async void BrowseBackupLocation()
        {
            var storageProvider = GetStorageProvider();
            if (storageProvider is null)
                return;

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose backup location",
                AllowMultiple = false
            });

            var folder = folders?.FirstOrDefault();
            var path = folder?.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            BackupLocationPath = path;
            ValidateBackupLocation(path);
        }

        private async void BrowseDestination(BackupDestinationViewModel? dest)
        {
            if (dest is null)
                return;

            var storageProvider = GetStorageProvider();
            if (storageProvider is null)
                return;

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose destination folder",
                AllowMultiple = false
            });

            var folder = folders?.FirstOrDefault();
            var path = folder?.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            dest.Path = path;
            RefreshLegacyVisibility();
        }

        private void ResetToDefaults()
        {
            AppConfigStore.Save(new AppConfig());
            LoadFromConfig();
        }

        private void ClearLocalCache()
        {
            var removed = 0;
            var failed = 0;

            void TryDeleteDir(string path)
            {
                if (!Directory.Exists(path))
                    return;

                try
                {
                    Directory.Delete(path, recursive: true);
                    removed++;
                }
                catch
                {
                    failed++;
                }
            }

            void TryDeleteFile(string path)
            {
                if (!File.Exists(path))
                    return;

                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch
                {
                    failed++;
                }
            }

            var localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync");

            TryDeleteDir(Path.Combine(localRoot, "logs"));
            TryDeleteDir(Path.Combine(localRoot, "crash"));
            TryDeleteFile(Path.Combine(localRoot, "avatars.json"));
            TryDeleteFile(Path.Combine(localRoot, "avatar-colors.json"));

            var tempRoot = Path.GetTempPath();
            TryDeleteDir(Path.Combine(tempRoot, "vaultsync-meta-import"));
            TryDeleteDir(Path.Combine(tempRoot, "vaultsync-telemetry-export"));
            TryDeleteDir(Path.Combine(tempRoot, "VaultSync"));

            if (removed == 0 && failed == 0)
            {
                SaveStatus = "No local cache data to clear.";
                return;
            }

            SaveStatus = failed == 0
                ? $"Local cache cleared ({removed} item(s))."
                : $"Cache cleared with {failed} error(s).";
        }

        private void TestBackupLocation()
        {
            if (string.IsNullOrWhiteSpace(BackupLocationPath))
                return;

            ValidateBackupLocation(BackupLocationPath, notifyOnSuccess: false);
        }

        private async void TestDestination(BackupDestinationViewModel? dest)
        {
            if (dest is null)
                return;

            var path = dest.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                var emptyText = LocalizationProvider.Service?.GetString("Destinations.Test.EmptyPath") ?? "Destination path is empty.";
                SaveStatus = emptyText;
                dest.LastTestStatus   = emptyText;
                dest.LastTestSeverity = "Error";
                return;
            }

            var display = dest.DisplayName;
            var cfg = await Task.Run(AppConfigStore.Load);
            var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                ? null
                : cfg.Network.Credentials.FirstOrDefault(c =>
                    c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

            var destModel = new BackupDestination
            {
                Alias          = dest.Alias,
                Path           = dest.Path,
                Active         = dest.Active,
                PreMounted     = dest.PreMounted,
                AutoMount      = dest.AutoMount,
                AutoUnmount    = dest.AutoUnmount,
                CredentialName = dest.CredentialName
            };

            var result = await Task.Run(() =>
            {
                var resolution = _networkMountService.PrepareDestination(destModel, profile);
                if (!resolution.IsSuccess)
                {
                    return (resolution, success: false, readable: false, writable: false, message: resolution.Message);
                }

                try
                {
                    var effectivePath = resolution.EffectivePath;
                    Directory.CreateDirectory(effectivePath);

                    var writable = TryWriteProbeFile(effectivePath);
                    var message = writable
                        ? (LocalizationProvider.Service?.GetString("Destinations.Test.Reachable") ?? "Reachable")
                        : (LocalizationProvider.Service?.GetString("Destinations.Test.ReadOnly") ?? "Read-only");

                    return (resolution, success: true, readable: true, writable: writable, message: message);
                }
                catch (Exception ex)
                {
                    return (resolution, success: false, readable: false, writable: false, message: ex.Message);
                }
                finally
                {
                    _networkMountService.Cleanup(resolution);
                }
            });

            if (!result.success)
            {
                SaveStatus = $"Destination '{display}' failed: {result.message}";
                dest.LastTestStatus   = result.message;
                dest.LastTestSeverity = "Error";
                var actionLabel = LocalizationProvider.Service?.GetString("Logs.CopySnippet") ?? "Copy log snippet";
                var actionCommand = CreateCopyLogSnippetCommand($"Destination test failed for '{display}'.");
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Error,
                    LocalizationProvider.Service?.GetString("Destinations.Test.Title") ?? "Destination test",
                    actionLabel: actionLabel,
                    actionCommand: actionCommand);
                return;
            }

            SaveStatus = $"Destination '{display}' is reachable.";
            dest.LastTestStatus   = result.message;
            dest.LastTestSeverity = result.writable ? "Info" : "Warning";
            GlobalNotificationCenter.Instance.Show(
                SaveStatus,
                result.writable ? NotificationSeverity.Info : NotificationSeverity.Warning,
                LocalizationProvider.Service?.GetString("Destinations.Test.Title") ?? "Destination test");
        }

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

        private ICommand CreateCopyLogSnippetCommand(string contextLabel)
        {
            return new RelayCommand(async _ =>
            {
                var snippet = Services.LogConsoleProvider.Service?.GetRecentSnippet(30, contextLabel);
                if (string.IsNullOrWhiteSpace(snippet))
                    return;

                await ClipboardHelper.TryCopyAsync(snippet);
            });
        }

        private void ValidateBackupLocation(string path, bool notifyOnSuccess = true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                BackupLocationStatus = string.Empty;
                return;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                BackupLocationStatus = "Not accessible";
                GlobalNotificationCenter.Instance.Show(
                    $"Backup location not accessible: {ex.Message}",
                    NotificationSeverity.Error,
                    "Backup location");
                return;
            }

            // Write test file to ensure we can write to the target.
            var testFile = Path.Combine(path, ".vaultsync_write_test");
            try
            {
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                BackupLocationStatus = "Not writable";
                GlobalNotificationCenter.Instance.Show(
                    $"Backup location is not writable: {ex.Message}",
                    NotificationSeverity.Error,
                    "Backup location");
                return;
            }

            // Check free space against the configured minimum threshold.
            try
            {
                var drive = new DriveInfo(path);
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    var freePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100d;
                    if (freePercent < MinimumFreeSpacePercent)
                    {
                        BackupLocationStatus = $"Low space ({freePercent:0.#}% free)";
                        GlobalNotificationCenter.Instance.Show(
                            $"Free space below threshold ({freePercent:0.#}% available, threshold {MinimumFreeSpacePercent}%).",
                            NotificationSeverity.Warning,
                            "Backup location");
                    }
                    else
                    {
                        BackupLocationStatus = "OK";
                        if (notifyOnSuccess)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                $"Backup location set: {path}",
                                NotificationSeverity.Info,
                                "Backup location");
                        }
                    }
                }
            }
            catch
            {
                BackupLocationStatus = "OK";
                // Ignore disk space failures; path/write checks already passed.
            }
        }

        private void ForgetAllProjects()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    // Dev helper: reset the VaultSync SQLite DB to a "fresh install" state
                    // without touching any real project files or backup folders on disk.
                    var cfg  = AppConfigStore.Load();
                    var repo = new SqliteRepository(cfg.DbPath ?? string.Empty);

                    repo.EnsureSchema();
                    repo.ResetAllData();
                }
                catch
                {
                    // Best effort: ignore errors.
                }
            });
        }

        private static IStorageProvider? GetStorageProvider()
        {
            var app = Application.Current;
            if (app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow?.StorageProvider;
            }

            return null;
        }

        private void AddDestination()
        {
            Destinations.Add(new BackupDestinationViewModel
            {
                Alias = $"Destination {Destinations.Count + 1}",
                Active = true,
                PreMounted = true
            });
            RefreshLegacyVisibility();
        }

        private void RemoveDestination(BackupDestinationViewModel? dest)
        {
            if (dest is null) return;
            Destinations.Remove(dest);
            RefreshLegacyVisibility();
        }

        private void AddCredential()
        {
            CredentialProfiles.Add(new NetworkCredentialViewModel
            {
                Name = $"Profile {CredentialProfiles.Count + 1}",
                UseKeychain = true
            });
            OnPropertyChanged(nameof(CredentialNames));
        }

        private void RemoveCredential(NetworkCredentialViewModel? cred)
        {
            if (cred is null) return;
            _credentialVault.DeleteSecret(cred.KeyRef, cred.Username);
            CredentialProfiles.Remove(cred);
            OnPropertyChanged(nameof(CredentialNames));
        }

        public void OpenHelp()
        {
            try
            {
                var root = AppContext.BaseDirectory;
                var path = Path.Combine(root, "docs", "HELP.md");
                if (!File.Exists(path))
                {
                    // fallback to repo relative when running from source
                    var repoPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "HELP.md"));
                    if (File.Exists(repoPath))
                        path = repoPath;
                }

                if (File.Exists(path))
                {
                    if (OperatingSystem.IsMacOS())
                        Process.Start("open", path);
                    else if (OperatingSystem.IsWindows())
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    else
                        Process.Start("xdg-open", path);
                }
            }
            catch
            {
                // ignore failures
            }
        }

        private void ExportTelemetry()
        {
            var result = Telemetry.ExportToZip();
            if (!result.Success || string.IsNullOrWhiteSpace(result.ZipPath))
            {
                SaveStatus = result.Message ?? "Telemetry export failed.";
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Warning,
                    "Telemetry export");
                return;
            }

            SaveStatus = $"Telemetry exported to {result.ZipPath}";
            GlobalNotificationCenter.Instance.Show(
                "Telemetry export ready. You can share the zip file.",
                NotificationSeverity.Info,
                "Telemetry export");

            try
            {
                var folder = Path.GetDirectoryName(result.ZipPath);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // best-effort
            }
        }

        private void OpenLogConsole()
        {
            OpenLogConsoleRequested?.Invoke();
        }

        private void CheckUpdatesNow()
        {
            UpdateCheckRequested?.Invoke();
        }

        private void ExportLogConsole()
        {
            var service = Services.LogConsoleProvider.Service;
            var path = service?.ExportBuffer();

            if (string.IsNullOrWhiteSpace(path))
            {
                SaveStatus = "Log export failed.";
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Warning,
                    "Log export");
                return;
            }

            SaveStatus = $"Log exported to {path}";
            GlobalNotificationCenter.Instance.Show(
                "Log export ready. You can share the file.",
                NotificationSeverity.Info,
                "Log export");
        }

    }

    public class BackupDestinationViewModel : VaultSync.UI.ViewModels.ViewModelBase
    {
        private string _alias = string.Empty;
        public string Alias
        {
            get => _alias;
            set
            {
                if (SetField(ref _alias, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private string _path = string.Empty;
        public string Path
        {
            get => _path;
            set
            {
                if (SetField(ref _path, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private NetworkCredentialViewModel? _selectedCredential;
        public NetworkCredentialViewModel? SelectedCredential
        {
            get => _selectedCredential;
            set
            {
                if (SetField(ref _selectedCredential, value))
                {
                    CredentialName = value?.Name ?? string.Empty;
                    OnPropertyChanged(nameof(NeedsCredentialWarning));
                }
            }
        }

        private string _credentialName = string.Empty;
        public string CredentialName
        {
            get => _credentialName;
            set
            {
                if (SetField(ref _credentialName, value))
                {
                    // Keep SelectedCredential in sync when only the name changes
                    if (SelectedCredential is null || !string.Equals(SelectedCredential.Name, value, StringComparison.OrdinalIgnoreCase))
                    {
                        // Selection will be resolved via SettingsViewModel handler
                    }
                }
            }
        }

        // Used by the Settings UI ComboBox. When the Settings page is unloaded,
        // Avalonia may temporarily clear SelectedItem and push a null/empty value
        // back into the binding; ignore that so the destination keeps its selection.
        public string SelectedCredentialName
        {
            get => CredentialName ?? string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                CredentialName = value;
            }
        }

        private bool _active = true;
        public bool Active { get => _active; set => SetField(ref _active, value); }

        private bool _autoMount;
        public bool AutoMount
        {
            get => _autoMount;
            set
            {
                if (SetField(ref _autoMount, value))
                {
                    OnPropertyChanged(nameof(NeedsCredentialWarning));
                }
            }
        }

        private bool _autoUnmount;
        public bool AutoUnmount { get => _autoUnmount; set => SetField(ref _autoUnmount, value); }

        private bool _preMounted = true;
        public bool PreMounted
        {
            get => _preMounted;
            set
            {
                if (SetField(ref _preMounted, value))
                {
                    OnPropertyChanged(nameof(NeedsCredentialWarning));
                }
            }
        }

        public bool NeedsCredentialWarning =>
            AutoMount && !PreMounted && SelectedCredential is null;

        private bool _enableMetadataSync = true;
        public bool EnableMetadataSync { get => _enableMetadataSync; set => SetField(ref _enableMetadataSync, value); }

        private bool _autoImportMetadata = true;
        public bool AutoImportMetadata { get => _autoImportMetadata; set => SetField(ref _autoImportMetadata, value); }

        private bool _forceMetadataBackfill;
        public bool ForceMetadataBackfill { get => _forceMetadataBackfill; set => SetField(ref _forceMetadataBackfill, value); }

        private string _lastTestStatus = string.Empty;
        public string LastTestStatus
        {
            get => _lastTestStatus;
            set => SetField(ref _lastTestStatus, value);
        }

        private string _lastTestSeverity = "Info";
        public string LastTestSeverity
        {
            get => _lastTestSeverity;
            set => SetField(ref _lastTestSeverity, value);
        }

        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Path : Alias;
    }

    public class NetworkCredentialViewModel : VaultSync.UI.ViewModels.ViewModelBase
    {
        private string _name = string.Empty;
        public string Name { get => _name; set => SetField(ref _name, value); }

        private string _username = string.Empty;
        public string Username { get => _username; set => SetField(ref _username, value); }

        private string _domain = string.Empty;
        public string Domain { get => _domain; set => SetField(ref _domain, value); }

        private string _keyRef = string.Empty;
        public string KeyRef { get => _keyRef; set => SetField(ref _keyRef, value); }

        private bool _useKeychain = true;
        public bool UseKeychain { get => _useKeychain; set => SetField(ref _useKeychain, value); }

        private string _password = string.Empty;
        public string Password { get => _password; set => SetField(ref _password, value); }

        private bool _showPassword = false;
        public bool ShowPassword { get => _showPassword; set => SetField(ref _showPassword, value); }
    }
}
