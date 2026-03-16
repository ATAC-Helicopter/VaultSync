using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using VaultSync.UI.Services;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Models;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI
{
    public sealed partial class SettingsViewModel : INotifyPropertyChanged
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
        private bool _enableBandwidthLimit = false;
        private int _maxBandwidthMbps = 100;
        private bool _enableQuietHours = false;
        private string _quietHoursStart = "23:00";
        private string _quietHoursEnd = "07:00";
        private string _backupLocationStatus = string.Empty;
        private bool _backupEncryptionEnabled = false;
        private bool _backupEncryptionAllowSessionFallback = false;
        private int _backupEncryptionOpenUnlockTimeoutMinutes = 10;
        private string _backupEncryptionKeyRef = string.Empty;
        private string _backupEncryptionPasswordInput = string.Empty;
        private bool _backupEncryptionShowPassword = false;
        private string _backupEncryptionSecretStatus = string.Empty;
        private bool _backupEncryptionHasSecret = false;

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
        private bool _enableMaintenanceWindow = false;
        private string _maintenanceWindowStart = "01:00";
        private string _maintenanceWindowEnd = "05:00";
        private bool _maintenanceRunConsistencyScan = true;
        private bool _maintenanceRunRepairDryRun = true;
        private bool _maintenanceRunMetadataRefresh = true;
        private readonly LocalizationService _localizationService;
        private DateTimeOffset? _lastUpdateCheckAt;
        private string? _lastUpdateCheckError;
        private string _updateCheckStatusText = string.Empty;
        private string _updateCheckErrorText = string.Empty;
        private string _updateDiagnosticsText = string.Empty;
        private string _startupDiagnosticsText = string.Empty;
        private string _checkpointResumeDiagnosticsText = string.Empty;
        private string _retentionSimulationStatus = string.Empty;
        private string _retentionSimulationSummary = string.Empty;
        private string _retentionSimulationDetails = string.Empty;
        private bool _isRetentionSimulationBusy;
        private string _rsyncStatusHint = string.Empty;
        private bool _showRsyncStatusHint;
        private string _selectedLanguageCode = "en";
        private readonly CredentialVault _credentialVault = CredentialVault.Instance;
        private readonly BackupEncryptionSecretService _backupEncryptionSecretService = new();
        private readonly NetworkMountService _networkMountService = new();
        private readonly SupportBundleService _supportBundleService = new();
        private RelayCommand? _addTagColorRuleCommand;
        private RelayCommand? _removeTagColorRuleCommand;
        private RelayCommand? _resetTagColorRuleCommand;
        private RelayCommand? _applyThemePresetCommand;
        private RelayCommand? _applyThemePaletteSwatchCommand;
        private RelayCommand? _selectThemeColorSlotCommand;
        private RelayCommand? _resetCustomThemeCommand;
        private RelayCommand? _scanBackupIndexRepairPlanCommand;
        private RelayCommand? _applyBackupIndexRepairPlanCommand;
        private RelayCommand? _acceptProjectMetadataConflictCommand;
        private RelayCommand? _keepLocalProjectMetadataConflictCommand;
        private RelayCommand? _runRetentionSimulationCommand;
        private BackupIndexRepairPlan? _currentBackupIndexRepairPlan;
        private string _backupIndexRepairStatus = string.Empty;
        private string _backupIndexRepairSummary = string.Empty;
        private string _backupIndexRepairDetails = string.Empty;
        private string _projectMetadataConflictStatus = string.Empty;
        private bool _isBackupIndexRepairBusy;
        private bool _showLegacyBackupLocation = true;
        private string _customThemeName = "VaultSync Midnight";
        private string _customThemeBase = "Dark";
        private ThemeColorSlotViewModel? _selectedThemeColorSlot;
        private const string BackupEncryptionSecretUsername = "vaultsync-backup-encryption";

        private bool _isInitialized;
        private bool _isSaving;
        private bool _savePending;
        private bool? _lastLaunchOnLoginApplied;

        public event Action? OpenLogConsoleRequested;
        public event Action? UpdateCheckRequested;
        public event Action? RefreshHistoryRequested;
        public event Action? RotateEncryptedBackupsRequested;
        public event Action? EnrollProjectEncryptionRequested;
        public event Action? LockEncryptedOpenWorkspacesRequested;

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
            bool ForceMetadataBackfill,
            int RetryMaxAttempts,
            int RetryBackoffSeconds,
            bool EnableCheckpointResume,
            long? SoftQuotaBytes,
            int QuotaWarningPercent);

        private sealed record CredentialSnapshot(
            string Name,
            string Username,
            string Domain,
            string KeyRef,
            bool UseKeychain,
            string Password);

        public sealed class ProjectMetadataConflictItemViewModel
        {
            public required int ProjectId { get; init; }
            public required string ProjectName { get; init; }
            public required string ProjectExternalId { get; init; }
            public required string SourceMachineId { get; init; }
            public required string SourceUpdatedUtc { get; init; }
            public required string LocalPreferredDestinationId { get; init; }
            public required string ImportedPreferredDestinationId { get; init; }
            public required string LocalRestoreMode { get; init; }
            public required string ImportedRestoreMode { get; init; }
            public required string LocalVerificationPolicy { get; init; }
            public required string ImportedVerificationPolicy { get; init; }
            public required string LocalTags { get; init; }
            public required string ImportedTags { get; init; }
        }

        public sealed class TagColorRuleViewModel : INotifyPropertyChanged
        {
            private enum ColorSlot
            {
                Background,
                Foreground,
                Border
            }

            private string _tag = string.Empty;
            private string _background = string.Empty;
            private string _foreground = string.Empty;
            private string _border = string.Empty;
            private ColorSlot _selectedSlot = ColorSlot.Background;

            public TagColorRuleViewModel()
            {
                PaletteSwatches = new[]
                {
                    new ThemePaletteSwatchViewModel("#4F8DFF"),
                    new ThemePaletteSwatchViewModel("#2663FF"),
                    new ThemePaletteSwatchViewModel("#4CC9F0"),
                    new ThemePaletteSwatchViewModel("#5AC88F"),
                    new ThemePaletteSwatchViewModel("#B983FF"),
                    new ThemePaletteSwatchViewModel("#FF8B4D"),
                    new ThemePaletteSwatchViewModel("#F857A6"),
                    new ThemePaletteSwatchViewModel("#FFC766"),
                    new ThemePaletteSwatchViewModel("#FF7676"),
                    new ThemePaletteSwatchViewModel("#FFFFFF"),
                    new ThemePaletteSwatchViewModel("#B3B8C7"),
                    new ThemePaletteSwatchViewModel("#222635")
                };
                ApplyPaletteSwatchCommand = new RelayCommand(
                    p => ApplyPaletteSwatch(p as ThemePaletteSwatchViewModel),
                    p => p is ThemePaletteSwatchViewModel);
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public IReadOnlyList<ThemePaletteSwatchViewModel> PaletteSwatches { get; }
            public ICommand ApplyPaletteSwatchCommand { get; }

            public string Tag
            {
                get => _tag;
                set
                {
                    if (_tag == (value ?? string.Empty))
                        return;
                    _tag = value ?? string.Empty;
                    RaiseAll();
                }
            }

            public string Background
            {
                get => _background;
                set
                {
                    if (_background == (value ?? string.Empty))
                        return;
                    _background = value ?? string.Empty;
                    RaiseAll();
                }
            }

            public string Foreground
            {
                get => _foreground;
                set
                {
                    if (_foreground == (value ?? string.Empty))
                        return;
                    _foreground = value ?? string.Empty;
                    RaiseAll();
                }
            }

            public string Border
            {
                get => _border;
                set
                {
                    if (_border == (value ?? string.Empty))
                        return;
                    _border = value ?? string.Empty;
                    RaiseAll();
                }
            }

            public bool IsEditingBackground
            {
                get => _selectedSlot == ColorSlot.Background;
                set
                {
                    if (value)
                        SetSelectedSlot(ColorSlot.Background);
                }
            }

            public bool IsEditingForeground
            {
                get => _selectedSlot == ColorSlot.Foreground;
                set
                {
                    if (value)
                        SetSelectedSlot(ColorSlot.Foreground);
                }
            }

            public bool IsEditingBorder
            {
                get => _selectedSlot == ColorSlot.Border;
                set
                {
                    if (value)
                        SetSelectedSlot(ColorSlot.Border);
                }
            }

            public Color ActiveColor
            {
                get => ParseColor(GetSelectedColorHex());
                set => SetSelectedColor(ProjectTagAppearance.FormatHex(value.R, value.G, value.B));
            }

            public string ActiveColorHex => GetSelectedColorHex();

            public string PreviewTag => string.IsNullOrWhiteSpace(Tag) ? "Example" : Tag.Trim();

            public string PreviewBackground
            {
                get
                {
                    var defaults = ProjectTagChip.GetDefaultPalette(PreviewTag);
                    return ProjectTagAppearance.NormalizeHex(Background, defaults.Background);
                }
            }

            public string PreviewForeground
            {
                get
                {
                    var defaults = ProjectTagChip.GetDefaultPalette(PreviewTag);
                    return ProjectTagAppearance.NormalizeHex(Foreground, defaults.Foreground);
                }
            }

            public string PreviewBorder
            {
                get
                {
                    var defaults = ProjectTagChip.GetDefaultPalette(PreviewTag);
                    return ProjectTagAppearance.NormalizeHex(Border, defaults.Border);
                }
            }

            private void SetSelectedSlot(ColorSlot slot)
            {
                if (_selectedSlot == slot)
                    return;

                _selectedSlot = slot;
                RaiseSelection();
            }

            private void SetSelectedColor(string hex)
            {
                switch (_selectedSlot)
                {
                    case ColorSlot.Background:
                        Background = hex;
                        break;
                    case ColorSlot.Foreground:
                        Foreground = hex;
                        break;
                    default:
                        Border = hex;
                        break;
                }
            }

            private string GetSelectedColorHex() => _selectedSlot switch
            {
                ColorSlot.Background => PreviewBackground,
                ColorSlot.Foreground => PreviewForeground,
                _ => PreviewBorder
            };

            private static Color ParseColor(string hex)
            {
                return Color.TryParse(hex, out var color) ? color : Colors.Transparent;
            }

            private void ApplyPaletteSwatch(ThemePaletteSwatchViewModel? swatch)
            {
                if (swatch is null)
                    return;

                ActiveColor = swatch.SwatchColor;
            }

            private void RaiseSelection()
            {
                RaiseProperty(nameof(IsEditingBackground));
                RaiseProperty(nameof(IsEditingForeground));
                RaiseProperty(nameof(IsEditingBorder));
                RaiseProperty(nameof(ActiveColor));
                RaiseProperty(nameof(ActiveColorHex));
            }

            private void RaiseProperty(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            private void RaiseAll()
            {
                RaiseProperty(nameof(Tag));
                RaiseProperty(nameof(Background));
                RaiseProperty(nameof(Foreground));
                RaiseProperty(nameof(Border));
                RaiseProperty(nameof(PreviewTag));
                RaiseProperty(nameof(PreviewBackground));
                RaiseProperty(nameof(PreviewForeground));
                RaiseProperty(nameof(PreviewBorder));
                RaiseProperty(nameof(ActiveColor));
                RaiseProperty(nameof(ActiveColorHex));
                RaiseSelection();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<ProjectMetadataConflictItemViewModel> ProjectMetadataConflicts { get; } = new();
        public ObservableCollection<TagColorRuleViewModel> TagColorRules { get; } = new();

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
                OnPropertyChanged(nameof(EnrollProjectEncryptionPasswordLabel));
                OnPropertyChanged(nameof(EncryptionOpenTimeoutLabel));
                OnPropertyChanged(nameof(EncryptionOpenTimeoutDescription));
                OnPropertyChanged(nameof(LockEncryptedOpenNowLabel));
                OnPropertyChanged(nameof(BandwidthLimitLabel));
                OnPropertyChanged(nameof(BandwidthLimitDescription));
                OnPropertyChanged(nameof(BandwidthLimitValueLabel));
                OnPropertyChanged(nameof(BandwidthLimitValueDescription));
                OnPropertyChanged(nameof(QuietHoursLabel));
                OnPropertyChanged(nameof(QuietHoursDescription));
                OnPropertyChanged(nameof(QuietHoursStartLabel));
                OnPropertyChanged(nameof(QuietHoursEndLabel));
                OnPropertyChanged(nameof(QuietHoursWindowLabel));
                OnPropertyChanged(nameof(QuietHoursWindowPreview));
                OnPropertyChanged(nameof(MaintenanceWindowLabel));
                OnPropertyChanged(nameof(MaintenanceWindowDescription));
                OnPropertyChanged(nameof(MaintenanceWindowStartLabel));
                OnPropertyChanged(nameof(MaintenanceWindowEndLabel));
                OnPropertyChanged(nameof(MaintenanceWindowPreviewLabel));
                OnPropertyChanged(nameof(MaintenanceWindowPreview));
                OnPropertyChanged(nameof(MaintenanceWindowConsistencyLabel));
                OnPropertyChanged(nameof(MaintenanceWindowConsistencyDescription));
                OnPropertyChanged(nameof(MaintenanceWindowRepairLabel));
                OnPropertyChanged(nameof(MaintenanceWindowRepairDescription));
                OnPropertyChanged(nameof(MaintenanceWindowMetadataLabel));
                OnPropertyChanged(nameof(MaintenanceWindowMetadataDescription));
            };

            ThemeOptions = new ObservableCollection<string>
            {
                "Follow system",
                "Dark",
                "Light",
                "Custom"
            };

            _selectedTheme = ThemeOptions[0];
            InitializeThemeEditor();

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
            _addTagColorRuleCommand      = new RelayCommand(_ => AddTagColorRule());
            _removeTagColorRuleCommand   = new RelayCommand(p => RemoveTagColorRule(p as TagColorRuleViewModel), p => p is TagColorRuleViewModel);
            _resetTagColorRuleCommand    = new RelayCommand(p => ResetTagColorRule(p as TagColorRuleViewModel), p => p is TagColorRuleViewModel);
            _applyThemePresetCommand     = new RelayCommand(p => ApplyThemePreset(p as ThemePresetOptionViewModel), p => p is ThemePresetOptionViewModel);
            _applyThemePaletteSwatchCommand = new RelayCommand(p => ApplyThemePaletteSwatch(p as ThemePaletteSwatchViewModel), p => p is ThemePaletteSwatchViewModel && SelectedThemeColorSlot is not null);
            _selectThemeColorSlotCommand = new RelayCommand(p => SelectThemeColorSlot(p as ThemeColorSlotViewModel), p => p is ThemeColorSlotViewModel);
            _resetCustomThemeCommand     = new RelayCommand(_ => ResetCustomTheme());
            OpenHelpCommand              = new RelayCommand(_ => OpenHelp());
            ExportTelemetryCommand       = new RelayCommand(_ => ExportTelemetry());
            OpenLogConsoleCommand        = new RelayCommand(_ => OpenLogConsole());
            ExportLogConsoleCommand      = new RelayCommand(_ => ExportLogConsole());
            ExportSupportBundleCommand   = new RelayCommand(_ => ExportSupportBundle());
            ImportSupportBundleCommand   = new RelayCommand(_ => ImportSupportBundle());
            CheckUpdatesNowCommand       = new RelayCommand(_ => CheckUpdatesNow());
            _scanBackupIndexRepairPlanCommand = new RelayCommand(_ => ScanBackupIndexRepairPlan(), _ => !IsBackupIndexRepairBusy);
            _applyBackupIndexRepairPlanCommand = new RelayCommand(_ => ApplyBackupIndexRepairPlan(), _ => !IsBackupIndexRepairBusy && HasBackupIndexRepairActions);
            _acceptProjectMetadataConflictCommand = new RelayCommand(
                parameter => AcceptProjectMetadataConflict(parameter as ProjectMetadataConflictItemViewModel),
                parameter => parameter is ProjectMetadataConflictItemViewModel && !IsBackupIndexRepairBusy);
            _keepLocalProjectMetadataConflictCommand = new RelayCommand(
                parameter => KeepLocalProjectMetadataConflict(parameter as ProjectMetadataConflictItemViewModel),
                parameter => parameter is ProjectMetadataConflictItemViewModel && !IsBackupIndexRepairBusy);
            _runRetentionSimulationCommand = new RelayCommand(_ => RunRetentionSimulation(), _ => !IsRetentionSimulationBusy);
            RefreshHistoryCommand        = new RelayCommand(_ => RefreshHistoryRequested?.Invoke());
            SetBackupEncryptionPasswordCommand = new RelayCommand(_ => SetBackupEncryptionPassword());
            ClearBackupEncryptionPasswordCommand = new RelayCommand(_ => ClearBackupEncryptionPassword());
            RotateEncryptedBackupsCommand = new RelayCommand(_ => RotateEncryptedBackupsRequested?.Invoke());
            EnrollProjectEncryptionPasswordCommand = new RelayCommand(_ => EnrollProjectEncryptionRequested?.Invoke());
            LockEncryptedOpenWorkspacesCommand = new RelayCommand(_ => LockEncryptedOpenWorkspacesRequested?.Invoke());

            CredentialProfiles.CollectionChanged += OnCredentialProfilesCollectionChanged;
            Destinations.CollectionChanged       += OnDestinationsCollectionChanged;
            TagColorRules.CollectionChanged      += OnTagColorRulesCollectionChanged;
            ThemeColorSlots.CollectionChanged    += OnThemeColorSlotsCollectionChanged;

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
            _lastLaunchOnLoginApplied = _launchOnLogin;
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
            _enableBandwidthLimit      = cfg.Backups.EnableBandwidthLimit;
            _maxBandwidthMbps          = ClampInt(cfg.Backups.MaxBandwidthMbps, 1, 5000, 100);
            _enableQuietHours          = cfg.Backups.EnableQuietHours;
            _quietHoursStart           = NormalizeTimeOfDay(cfg.Backups.QuietHoursStart, "23:00");
            _quietHoursEnd             = NormalizeTimeOfDay(cfg.Backups.QuietHoursEnd, "07:00");
            _backupEncryptionEnabled   = cfg.Backups.Encryption.Enabled;
            _backupEncryptionAllowSessionFallback = cfg.Backups.Encryption.AllowSessionFallback;
            _backupEncryptionOpenUnlockTimeoutMinutes = ClampInt(cfg.Backups.Encryption.OpenUnlockTimeoutMinutes, 1, 240, 10);
            _backupEncryptionKeyRef = cfg.Backups.Encryption.KeyRef ?? string.Empty;
            _backupEncryptionHasSecret = !string.IsNullOrWhiteSpace(
                _backupEncryptionSecretService.GetSecret(_backupEncryptionKeyRef, BackupEncryptionSecretUsername));
            _backupEncryptionPasswordInput = string.Empty;
            _backupEncryptionSecretStatus = _backupEncryptionHasSecret
                ? L("Settings.Encryption.SecretStatusAvailable", "Password is enrolled in secure storage.")
                : L("Settings.Encryption.SecretStatusMissing", "No encryption password enrolled yet.");

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
                        ForceMetadataBackfill = dest.ForceMetadataBackfill,
                        RetryMaxAttempts = ClampInt(dest.RetryMaxAttempts, 1, 10, 1),
                        RetryBackoffSeconds = ClampInt(dest.RetryBackoffSeconds, 1, 300, 10),
                        EnableCheckpointResume = dest.EnableCheckpointResume,
                        SoftQuotaGb = dest.SoftQuotaBytes.HasValue && dest.SoftQuotaBytes.Value > 0
                            ? Math.Round(dest.SoftQuotaBytes.Value / 1024d / 1024d / 1024d, 2)
                            : 0d,
                        QuotaWarningPercent = ClampInt(dest.QuotaWarningPercent, 50, 99, 85)
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
                    ForceMetadataBackfill = false,
                    RetryMaxAttempts = 1,
                    RetryBackoffSeconds = 10,
                    EnableCheckpointResume = true,
                    SoftQuotaGb = 0d,
                    QuotaWarningPercent = 85
                });
            }
            }
            RefreshLegacyVisibility();

            // FIX: use Theme instead of ThemeName
            _selectedTheme      = DisplayThemeOption(cfg.Appearance.Theme ?? "System");
            _useCompactLayout   = cfg.Appearance.CompactLayout;
            _showProjectAvatars = cfg.Appearance.ShowProjectAvatars;
            LoadTagColorRules(cfg);
            LoadCustomTheme(cfg.Appearance.CustomTheme);

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
            _enableMaintenanceWindow    = cfg.Advanced.Maintenance.Enabled;
            _maintenanceWindowStart     = NormalizeTimeOfDay(cfg.Advanced.Maintenance.WindowStart, "01:00");
            _maintenanceWindowEnd       = NormalizeTimeOfDay(cfg.Advanced.Maintenance.WindowEnd, "05:00");
            _maintenanceRunConsistencyScan = cfg.Advanced.Maintenance.RunConsistencyScan;
            _maintenanceRunRepairDryRun = cfg.Advanced.Maintenance.RunRepairDryRun;
            _maintenanceRunMetadataRefresh = cfg.Advanced.Maintenance.RunMetadataRefresh;
            RefreshUpdateDiagnostics(cfg.Advanced.UpdateDiagnostics);
            RefreshStartupDiagnostics(cfg.Advanced.StartupDiagnostics);
            RefreshCheckpointResumeDiagnostics(cfg.Advanced.CheckpointResumeTelemetry);
            RefreshProjectMetadataConflicts(cfg.Advanced.ProjectMetadataConflicts);

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
            if (!ValidateTransferPolicy(notifyOnValidationError))
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
                    ForceMetadataBackfill: d.ForceMetadataBackfill,
                    RetryMaxAttempts: ClampInt(d.RetryMaxAttempts, 1, 10, 1),
                    RetryBackoffSeconds: ClampInt(d.RetryBackoffSeconds, 1, 300, 10),
                    EnableCheckpointResume: d.EnableCheckpointResume,
                    SoftQuotaBytes: ToQuotaBytes(d.SoftQuotaGb),
                    QuotaWarningPercent: ClampInt(d.QuotaWarningPercent, 50, 99, 85)))
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
            cfg.Backups.EnableBandwidthLimit        = EnableBandwidthLimit;
            cfg.Backups.MaxBandwidthMbps            = ClampInt(MaxBandwidthMbps, 1, 5000, 100);
            cfg.Backups.EnableQuietHours            = EnableQuietHours;
            cfg.Backups.QuietHoursStart             = NormalizeTimeOfDay(QuietHoursStart, "23:00");
            cfg.Backups.QuietHoursEnd               = NormalizeTimeOfDay(QuietHoursEnd, "07:00");
            cfg.Backups.Encryption.Enabled          = BackupEncryptionEnabled;
            cfg.Backups.Encryption.AllowSessionFallback = BackupEncryptionAllowSessionFallback;
            cfg.Backups.Encryption.OpenUnlockTimeoutMinutes = ClampInt(BackupEncryptionOpenUnlockTimeoutMinutes, 1, 240, 10);
            cfg.Backups.Encryption.KeyRef = string.IsNullOrWhiteSpace(_backupEncryptionKeyRef)
                ? string.Empty
                : _backupEncryptionKeyRef;
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
                ForceMetadataBackfill = d.ForceMetadataBackfill,
                RetryMaxAttempts = ClampInt(d.RetryMaxAttempts, 1, 10, 1),
                RetryBackoffSeconds = ClampInt(d.RetryBackoffSeconds, 1, 300, 10),
                EnableCheckpointResume = d.EnableCheckpointResume,
                SoftQuotaBytes = d.SoftQuotaBytes,
                QuotaWarningPercent = ClampInt(d.QuotaWarningPercent, 50, 99, 85)
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
            cfg.Appearance.TagColors          = BuildTagColorConfig();
            cfg.Appearance.CustomTheme        = BuildCustomThemeConfig();

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
            cfg.Advanced.Maintenance.Enabled = EnableMaintenanceWindow;
            cfg.Advanced.Maintenance.WindowStart = NormalizeTimeOfDay(MaintenanceWindowStart, "01:00");
            cfg.Advanced.Maintenance.WindowEnd = NormalizeTimeOfDay(MaintenanceWindowEnd, "05:00");
            cfg.Advanced.Maintenance.RunConsistencyScan = MaintenanceRunConsistencyScan;
            cfg.Advanced.Maintenance.RunRepairDryRun = MaintenanceRunRepairDryRun;
            cfg.Advanced.Maintenance.RunMetadataRefresh = MaintenanceRunMetadataRefresh;

            if (_lastLaunchOnLoginApplied != _launchOnLogin)
            {
                _lastLaunchOnLoginApplied = _launchOnLogin;
                var launchOnLogin = _launchOnLogin;
                _ = Task.Run(() => AutoStartService.SetLaunchOnLogin(launchOnLogin));
            }

            await AppConfigStore.SaveAsync(cfg);

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

                if (dest.SoftQuotaGb < 0)
                {
                    if (notifyOnError)
                    {
                        SaveStatus = "Destination quota must be 0 GB or higher.";
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

        private bool ValidateTransferPolicy(bool notifyOnError)
        {
            if (EnableBandwidthLimit && (MaxBandwidthMbps < 1 || MaxBandwidthMbps > 5000))
            {
                if (notifyOnError)
                {
                    SaveStatus = L(
                        "Settings.Validation.BandwidthLimitRange",
                        "Bandwidth limit must be between 1 and 5000 Mbps.");
                }

                return false;
            }

            if (EnableQuietHours &&
                (!TryParseTimeOfDay(QuietHoursStart, out _) || !TryParseTimeOfDay(QuietHoursEnd, out _)))
            {
                if (notifyOnError)
                {
                    SaveStatus = L(
                        "Settings.Validation.QuietHoursFormat",
                        "Quiet hours must use HH:mm format (24h).");
                }

                return false;
            }

            if (EnableMaintenanceWindow &&
                (!TryParseTimeOfDay(MaintenanceWindowStart, out _) || !TryParseTimeOfDay(MaintenanceWindowEnd, out _)))
            {
                if (notifyOnError)
                {
                    SaveStatus = L(
                        "Settings.Validation.MaintenanceWindowFormat",
                        "Maintenance window must use HH:mm format (24h).");
                }

                return false;
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

        private void OnTagColorRulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (TagColorRuleViewModel rule in e.NewItems)
                    rule.PropertyChanged += OnTagColorRulePropertyChanged;
            }

            if (e.OldItems is not null)
            {
                foreach (TagColorRuleViewModel rule in e.OldItems)
                    rule.PropertyChanged -= OnTagColorRulePropertyChanged;
            }

            TriggerAutoSave();
        }

        private void OnTagColorRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
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

        private void TriggerAutoSave()
        {
            _ = RunDetachedAsync(TriggerAutoSaveAsync, nameof(TriggerAutoSaveAsync));
        }

        private async Task TriggerAutoSaveAsync()
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
                    _ = RunDetachedAsync(TriggerAutoSaveAsync, nameof(TriggerAutoSaveAsync));
                }
            }
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null)
                return;

            TriggerAutoSave();
        }

        private void AddTagColorRule()
        {
            TagColorRules.Add(new TagColorRuleViewModel());
        }


        private void RemoveTagColorRule(TagColorRuleViewModel? rule)
        {
            if (rule is null)
                return;

            TagColorRules.Remove(rule);
        }

        private void ResetTagColorRule(TagColorRuleViewModel? rule)
        {
            if (rule is null)
                return;

            var defaults = ProjectTagChip.GetDefaultPalette(rule.PreviewTag);
            rule.Background = defaults.Background;
            rule.Foreground = defaults.Foreground;
            rule.Border = defaults.Border;
        }

        private void LoadTagColorRules(AppConfig cfg)
        {
            TagColorRules.Clear();

            var rules = cfg.Appearance.TagColors
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in rules)
            {
                var defaults = ProjectTagChip.GetDefaultPalette(entry.Key);
                TagColorRules.Add(new TagColorRuleViewModel
                {
                    Tag = entry.Key,
                    Background = ProjectTagAppearance.NormalizeHex(entry.Value?.Background, defaults.Background),
                    Foreground = ProjectTagAppearance.NormalizeHex(entry.Value?.Foreground, defaults.Foreground),
                    Border = ProjectTagAppearance.NormalizeHex(entry.Value?.Border, defaults.Border)
                });
            }
        }

        private Dictionary<string, TagColorConfig> BuildTagColorConfig()
        {
            var rules = new Dictionary<string, TagColorConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in TagColorRules)
            {
                var tag = (rule.Tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                var defaults = ProjectTagChip.GetDefaultPalette(tag);
                rules[tag] = new TagColorConfig
                {
                    Background = ProjectTagAppearance.NormalizeHex(rule.Background, defaults.Background),
                    Foreground = ProjectTagAppearance.NormalizeHex(rule.Foreground, defaults.Foreground),
                    Border = ProjectTagAppearance.NormalizeHex(rule.Border, defaults.Border)
                };
            }

            return rules;
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
            ThemeManager.ApplyAppearance(new AppearanceConfig
            {
                Theme = NormalizeThemeOption(_selectedTheme),
                CustomTheme = BuildCustomThemeConfig()
            });
        }

        private static string NormalizeThemeOption(string theme)
        {
            return theme switch
            {
                "Dark"          => "Dark",
                "Light"         => "Light",
                "Custom"        => "Custom",
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
                "Custom" => "Custom",
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

        public bool EnableBandwidthLimit
        {
            get => _enableBandwidthLimit;
            set => SetField(ref _enableBandwidthLimit, value);
        }

        public int MaxBandwidthMbps
        {
            get => _maxBandwidthMbps;
            set => SetField(ref _maxBandwidthMbps, ClampInt(value, 1, 5000, 100));
        }

        public bool EnableQuietHours
        {
            get => _enableQuietHours;
            set => SetField(ref _enableQuietHours, value);
        }

        public string QuietHoursStart
        {
            get => _quietHoursStart;
            set
            {
                if (SetField(ref _quietHoursStart, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(QuietHoursWindowPreview));
                }
            }
        }

        public string QuietHoursEnd
        {
            get => _quietHoursEnd;
            set
            {
                if (SetField(ref _quietHoursEnd, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(QuietHoursWindowPreview));
                }
            }
        }

        public bool BackupEncryptionEnabled
        {
            get => _backupEncryptionEnabled;
            set => SetField(ref _backupEncryptionEnabled, value);
        }

        public bool BackupEncryptionAllowSessionFallback
        {
            get => _backupEncryptionAllowSessionFallback;
            set => SetField(ref _backupEncryptionAllowSessionFallback, value);
        }

        public int BackupEncryptionOpenUnlockTimeoutMinutes
        {
            get => _backupEncryptionOpenUnlockTimeoutMinutes;
            set => SetField(ref _backupEncryptionOpenUnlockTimeoutMinutes, ClampInt(value, 1, 240, 10));
        }

        public string BackupEncryptionPasswordInput
        {
            get => _backupEncryptionPasswordInput;
            set => SetField(ref _backupEncryptionPasswordInput, value);
        }

        public bool BackupEncryptionShowPassword
        {
            get => _backupEncryptionShowPassword;
            set => SetField(ref _backupEncryptionShowPassword, value);
        }

        public string BackupEncryptionSecretStatus
        {
            get => _backupEncryptionSecretStatus;
            private set => SetField(ref _backupEncryptionSecretStatus, value);
        }

        public bool BackupEncryptionHasSecret
        {
            get => _backupEncryptionHasSecret;
            private set => SetField(ref _backupEncryptionHasSecret, value);
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
                    OnPropertyChanged(nameof(IsCustomThemeSelected));
                    if (_isInitialized)
                    {
                        ApplyThemeFromSelected();
                    }
                }
            }
        }

        public bool IsCustomThemeSelected => string.Equals(SelectedTheme, "Custom", StringComparison.Ordinal);

        public string CustomThemeName
        {
            get => _customThemeName;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value)
                    ? L("Settings.Appearance.ThemeNameDefault", "Custom theme")
                    : value.Trim();
                if (!SetField(ref _customThemeName, normalized))
                    return;

                if (_isInitialized && IsCustomThemeSelected)
                    ApplyThemePreview();
            }
        }

        public string CustomThemeBase
        {
            get => _customThemeBase;
            set
            {
                var normalized = string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
                if (!SetField(ref _customThemeBase, normalized))
                    return;

                if (_isInitialized && IsCustomThemeSelected)
                    ApplyThemePreview();
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

        public string BackupIndexRepairStatus
        {
            get => _backupIndexRepairStatus;
            private set => SetField(ref _backupIndexRepairStatus, value);
        }

        public string BackupIndexRepairSummary
        {
            get => _backupIndexRepairSummary;
            private set => SetField(ref _backupIndexRepairSummary, value);
        }

        public string BackupIndexRepairDetails
        {
            get => _backupIndexRepairDetails;
            private set => SetField(ref _backupIndexRepairDetails, value);
        }

        public string ProjectMetadataConflictStatus
        {
            get => _projectMetadataConflictStatus;
            private set => SetField(ref _projectMetadataConflictStatus, value);
        }

        public string RetentionSimulationStatus
        {
            get => _retentionSimulationStatus;
            private set => SetField(ref _retentionSimulationStatus, value);
        }

        public string RetentionSimulationSummary
        {
            get => _retentionSimulationSummary;
            private set => SetField(ref _retentionSimulationSummary, value);
        }

        public string RetentionSimulationDetails
        {
            get => _retentionSimulationDetails;
            private set => SetField(ref _retentionSimulationDetails, value);
        }

        public bool IsBackupIndexRepairBusy
        {
            get => _isBackupIndexRepairBusy;
            private set
            {
                if (!SetField(ref _isBackupIndexRepairBusy, value))
                    return;

                RaiseRepairCommandStateChanged();
            }
        }

        public bool IsRetentionSimulationBusy
        {
            get => _isRetentionSimulationBusy;
            private set
            {
                if (!SetField(ref _isRetentionSimulationBusy, value))
                    return;

                _runRetentionSimulationCommand?.RaiseCanExecuteChanged();
            }
        }

        public bool HasBackupIndexRepairPlan => _currentBackupIndexRepairPlan is not null;

        public bool HasBackupIndexRepairActions => _currentBackupIndexRepairPlan?.Actions.Count > 0;

        public bool HasBackupIndexRepairBlockedIssues => _currentBackupIndexRepairPlan?.BlockedIssues.Count > 0;

        public bool HasBackupIndexRepairFindings => HasBackupIndexRepairActions || HasBackupIndexRepairBlockedIssues;

        public bool HasProjectMetadataConflicts => ProjectMetadataConflicts.Count > 0;

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

        public bool EnableMaintenanceWindow
        {
            get => _enableMaintenanceWindow;
            set => SetField(ref _enableMaintenanceWindow, value);
        }

        public string MaintenanceWindowStart
        {
            get => _maintenanceWindowStart;
            set
            {
                if (SetField(ref _maintenanceWindowStart, value))
                    OnPropertyChanged(nameof(MaintenanceWindowPreview));
            }
        }

        public string MaintenanceWindowEnd
        {
            get => _maintenanceWindowEnd;
            set
            {
                if (SetField(ref _maintenanceWindowEnd, value))
                    OnPropertyChanged(nameof(MaintenanceWindowPreview));
            }
        }

        public bool MaintenanceRunConsistencyScan
        {
            get => _maintenanceRunConsistencyScan;
            set => SetField(ref _maintenanceRunConsistencyScan, value);
        }

        public bool MaintenanceRunRepairDryRun
        {
            get => _maintenanceRunRepairDryRun;
            set => SetField(ref _maintenanceRunRepairDryRun, value);
        }

        public bool MaintenanceRunMetadataRefresh
        {
            get => _maintenanceRunMetadataRefresh;
            set => SetField(ref _maintenanceRunMetadataRefresh, value);
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

        public string UpdateDiagnosticsText
        {
            get => _updateDiagnosticsText;
            private set => SetField(ref _updateDiagnosticsText, value);
        }

        public string StartupDiagnosticsText
        {
            get => _startupDiagnosticsText;
            private set => SetField(ref _startupDiagnosticsText, value);
        }

        public string CheckpointResumeDiagnosticsText
        {
            get => _checkpointResumeDiagnosticsText;
            private set => SetField(ref _checkpointResumeDiagnosticsText, value);
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

        public void ReloadUpdateDiagnostics()
        {
            RefreshUpdateDiagnostics(AppConfigStore.Load().Advanced.UpdateDiagnostics);
        }

        public void ReloadStartupDiagnostics()
        {
            RefreshStartupDiagnostics(AppConfigStore.Load().Advanced.StartupDiagnostics);
        }

        public void ReloadCheckpointResumeDiagnostics()
        {
            RefreshCheckpointResumeDiagnostics(AppConfigStore.Load().Advanced.CheckpointResumeTelemetry);
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

        private void RefreshUpdateDiagnostics(UpdateCheckDiagnostics? diagnostics)
        {
            diagnostics ??= new UpdateCheckDiagnostics();
            if (string.IsNullOrWhiteSpace(diagnostics.Decision))
            {
                UpdateDiagnosticsText = L("Settings.Advanced.UpdateDiagnosticsEmpty", "No release-target diagnostics captured yet.");
                return;
            }

            var selectedTag = string.IsNullOrWhiteSpace(diagnostics.SelectedCandidate?.Tag) ? "-" : diagnostics.SelectedCandidate.Tag;
            var selectedTarget = string.IsNullOrWhiteSpace(diagnostics.SelectedCandidate?.TargetCommitish) ? "-" : diagnostics.SelectedCandidate.TargetCommitish;
            var stableTag = string.IsNullOrWhiteSpace(diagnostics.StableCandidate?.Tag) ? "-" : diagnostics.StableCandidate.Tag;
            var betaTag = string.IsNullOrWhiteSpace(diagnostics.BetaCandidate?.Tag) ? "-" : diagnostics.BetaCandidate.Tag;

            var summary = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.UpdateDiagnosticsTemplate", "Decision: {0} | Channel: {1} | Selected: {2} ({3}) | Stable: {4} | Beta: {5}"),
                diagnostics.Decision,
                string.IsNullOrWhiteSpace(diagnostics.Channel) ? "-" : diagnostics.Channel,
                selectedTag,
                selectedTarget,
                stableTag,
                betaTag);

            if (!string.IsNullOrWhiteSpace(diagnostics.Error))
            {
                summary = string.Concat(
                    summary,
                    " | ",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        L("Settings.Advanced.UpdateDiagnosticsError", "Error: {0}"),
                        diagnostics.Error));
            }

            if (!string.IsNullOrWhiteSpace(diagnostics.PatchPreflight?.StatusCode))
            {
                summary = string.Concat(
                    summary,
                    " | ",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        L("Settings.Advanced.UpdateDiagnosticsPatch", "Patch preflight: {0} ({1})"),
                        diagnostics.PatchPreflight.StatusCode,
                        diagnostics.PatchPreflight.Eligible
                            ? L("Settings.Advanced.UpdateDiagnosticsPatchEligible", "eligible")
                            : L("Settings.Advanced.UpdateDiagnosticsPatchBlocked", "blocked")));
            }

            UpdateDiagnosticsText = summary;
        }

        private void RefreshStartupDiagnostics(StartupDiagnosticsSummary? diagnostics)
        {
            diagnostics ??= new StartupDiagnosticsSummary();
            if (string.IsNullOrWhiteSpace(diagnostics.LastCompletedUtc) || diagnostics.Phases.Count == 0)
            {
                StartupDiagnosticsText = L("Settings.Advanced.StartupDiagnosticsEmpty", "No startup diagnostics timeline captured yet.");
                return;
            }

            string completedText;
            if (DateTimeOffset.TryParse(diagnostics.LastCompletedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var completedUtc))
            {
                completedText = completedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            }
            else
            {
                completedText = diagnostics.LastCompletedUtc;
            }

            var phaseSummary = string.Join(
                " | ",
                diagnostics.Phases
                    .OrderBy(phase => phase.ElapsedMs)
                    .Select(phase => string.Format(
                        CultureInfo.CurrentCulture,
                        L("Settings.Advanced.StartupDiagnosticsPhase", "{0}: {1} ms"),
                        phase.Name,
                        phase.ElapsedMs)));

            StartupDiagnosticsText = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.StartupDiagnosticsTemplate", "Last startup: {0} | Total: {1} ms | Phases: {2}"),
                completedText,
                diagnostics.TotalDurationMs,
                phaseSummary);
        }

        private void RefreshCheckpointResumeDiagnostics(CheckpointResumeTelemetry? diagnostics)
        {
            diagnostics ??= new CheckpointResumeTelemetry();
            if (string.IsNullOrWhiteSpace(diagnostics.LastStatus))
            {
                CheckpointResumeDiagnosticsText = L(
                    "Settings.Advanced.CheckpointResumeDiagnosticsEmpty",
                    "No checkpointed retry diagnostics captured yet.");
                return;
            }

            var updatedText = DateTimeOffset.TryParse(diagnostics.LastUpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedUtc)
                ? updatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : diagnostics.LastUpdatedUtc;

            var projectText = string.IsNullOrWhiteSpace(diagnostics.LastProjectName)
                ? L("Settings.Advanced.CheckpointResumeUnknownProject", "unknown project")
                : diagnostics.LastProjectName;

            CheckpointResumeDiagnosticsText = string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "Settings.Advanced.CheckpointResumeDiagnosticsTemplate",
                    "Checkpoint retry: {0} | Project: {1} | Progress: {2:0.0}/{3:0.0} MB | Updated: {4} | Detail: {5}"),
                diagnostics.LastStatus,
                projectText,
                diagnostics.LastResumeOffsetBytes / (1024d * 1024d),
                diagnostics.LastArchiveSizeBytes / (1024d * 1024d),
                updatedText,
                string.IsNullOrWhiteSpace(diagnostics.LastMessage)
                    ? L("Settings.Advanced.CheckpointResumeDiagnosticsNoDetail", "No detail recorded.")
                    : diagnostics.LastMessage);
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

        private static bool TryParseTimeOfDay(string? value, out TimeSpan result)
        {
            return TimeSpan.TryParseExact(
                value ?? string.Empty,
                @"hh\:mm",
                CultureInfo.InvariantCulture,
                out result);
        }

        private static string NormalizeTimeOfDay(string? value, string fallback)
        {
            if (TryParseTimeOfDay(value, out var parsed))
            {
                return $"{parsed.Hours:00}:{parsed.Minutes:00}";
            }

            return fallback;
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
    public ICommand AddTagColorRuleCommand => _addTagColorRuleCommand!;
    public ICommand RemoveTagColorRuleCommand => _removeTagColorRuleCommand!;
    public ICommand ResetTagColorRuleCommand => _resetTagColorRuleCommand!;
        public ICommand OpenHelpCommand { get; }
        public ICommand ExportTelemetryCommand { get; }
        public ICommand OpenLogConsoleCommand { get; }
        public ICommand ExportLogConsoleCommand { get; }
        public ICommand ExportSupportBundleCommand { get; }
        public ICommand ImportSupportBundleCommand { get; }
        public ICommand CheckUpdatesNowCommand { get; }
        public ICommand ScanBackupIndexRepairPlanCommand => _scanBackupIndexRepairPlanCommand!;
        public ICommand ApplyBackupIndexRepairPlanCommand => _applyBackupIndexRepairPlanCommand!;
        public ICommand AcceptProjectMetadataConflictCommand => _acceptProjectMetadataConflictCommand!;
        public ICommand KeepLocalProjectMetadataConflictCommand => _keepLocalProjectMetadataConflictCommand!;
        public ICommand RunRetentionSimulationCommand => _runRetentionSimulationCommand!;
        public ICommand RefreshHistoryCommand { get; }
        public ICommand SetBackupEncryptionPasswordCommand { get; }
        public ICommand ClearBackupEncryptionPasswordCommand { get; }
        public ICommand RotateEncryptedBackupsCommand { get; }
        public ICommand EnrollProjectEncryptionPasswordCommand { get; }
        public ICommand LockEncryptedOpenWorkspacesCommand { get; }
        public string EnrollProjectEncryptionPasswordLabel =>
            $"{L("Settings.Encryption.SetPassword", "Set password")} ({L("Nav.Projects", "Projects")})";
        public string BandwidthLimitLabel => L("Settings.Backups.BandwidthLimit", "Bandwidth limit");
        public string BandwidthLimitDescription => L("Settings.Backups.BandwidthLimitDescription", "Cap backup transfer speed to reduce network impact.");
        public string BandwidthLimitValueLabel => L("Settings.Backups.BandwidthLimitValue", "Transfer cap (Mbps)");
        public string BandwidthLimitValueDescription => L(
            "Settings.Backups.BandwidthLimitValueDescription",
            "Maximum transfer speed in megabits per second. Leave disabled for no cap.");
        public string QuietHoursLabel => L("Settings.Backups.QuietHours", "Quiet hours");
        public string QuietHoursDescription => L("Settings.Backups.QuietHoursDescription", "Pause/defer automatic backups during this time window.");
        public string QuietHoursStartLabel => L("Settings.Backups.QuietHoursStart", "Start (HH:mm)");
        public string QuietHoursEndLabel => L("Settings.Backups.QuietHoursEnd", "End (HH:mm)");
        public string QuietHoursWindowLabel => L("Settings.Backups.QuietHoursWindow", "Active window");
        public string QuietHoursWindowPreview =>
            $"{NormalizeTimeOfDay(QuietHoursStart, "23:00")} -> {NormalizeTimeOfDay(QuietHoursEnd, "07:00")}";
        public string MaintenanceWindowLabel => L("Settings.Advanced.MaintenanceWindow", "Maintenance window");
        public string MaintenanceWindowDescription => L("Settings.Advanced.MaintenanceWindowDescription", "Run optional health and repair checks during this time window.");
        public string MaintenanceWindowStartLabel => L("Settings.Advanced.MaintenanceWindowStart", "Start (HH:mm)");
        public string MaintenanceWindowEndLabel => L("Settings.Advanced.MaintenanceWindowEnd", "End (HH:mm)");
        public string MaintenanceWindowPreviewLabel => L("Settings.Advanced.MaintenanceWindowPreview", "Active window");
        public string MaintenanceWindowPreview =>
            $"{NormalizeTimeOfDay(MaintenanceWindowStart, "01:00")} -> {NormalizeTimeOfDay(MaintenanceWindowEnd, "05:00")}";
        public string MaintenanceWindowConsistencyLabel => L("Settings.Advanced.MaintenanceConsistency", "Run consistency scan");
        public string MaintenanceWindowConsistencyDescription => L("Settings.Advanced.MaintenanceConsistencyDescription", "Scan backup, snapshot, and project links and record a health summary.");
        public string MaintenanceWindowRepairLabel => L("Settings.Advanced.MaintenanceRepairDryRun", "Run repair dry-run");
        public string MaintenanceWindowRepairDescription => L("Settings.Advanced.MaintenanceRepairDryRunDescription", "Generate an exact repair plan without applying changes.");
        public string MaintenanceWindowMetadataLabel => L("Settings.Advanced.MaintenanceMetadataRefresh", "Refresh metadata history");
        public string MaintenanceWindowMetadataDescription => L("Settings.Advanced.MaintenanceMetadataRefreshDescription", "Import latest destination metadata during the maintenance run.");
        public string EncryptionOpenTimeoutLabel =>
            L("Settings.Encryption.OpenTimeoutLabel", "Encrypted open timeout (minutes)");
        public string EncryptionOpenTimeoutDescription =>
            L("Settings.Encryption.OpenTimeoutDescription", "Auto-lock decrypted open-folder sessions and temp content after this many minutes.");
        public string LockEncryptedOpenNowLabel =>
            L("Settings.Encryption.LockNow", "Lock now (close decrypted open folders)");

        private void SetBackupEncryptionPassword()
        {
            if (string.IsNullOrWhiteSpace(BackupEncryptionPasswordInput))
            {
                BackupEncryptionSecretStatus = L(
                    "Settings.Encryption.SecretStatusMissing",
                    "No encryption password enrolled yet.");
                return;
            }

            try
            {
                _backupEncryptionKeyRef = _backupEncryptionSecretService.EnsureSecretRef(
                    _backupEncryptionKeyRef,
                    "backup-encryption-global");

                var storageMode = _backupEncryptionSecretService.SaveSecret(
                    _backupEncryptionKeyRef,
                    BackupEncryptionSecretUsername,
                    BackupEncryptionPasswordInput,
                    allowSessionFallback: BackupEncryptionAllowSessionFallback,
                    fallbackConfirmed: BackupEncryptionAllowSessionFallback);

                BackupEncryptionPasswordInput = string.Empty;
                BackupEncryptionHasSecret = true;
                BackupEncryptionSecretStatus = storageMode == EncryptionSecretStorageMode.SecureStore
                    ? L("Settings.Encryption.SecretStatusAvailable", "Password is enrolled in secure storage.")
                    : L("Settings.Encryption.SecretStatusSession", "Password is stored in this app session only.");

                TriggerAutoSave();
            }
            catch (Exception ex)
            {
                BackupEncryptionSecretStatus = L("Settings.Encryption.SecretStatusSaveFailed", "Failed to store encryption password.");
                GlobalNotificationCenter.Instance.Show(
                    $"{BackupEncryptionSecretStatus} {ex.Message}",
                    NotificationSeverity.Error,
                    L("Settings.Encryption.Title", "Backup encryption"));
            }
        }

        private void ClearBackupEncryptionPassword()
        {
            _backupEncryptionSecretService.DeleteSecret(_backupEncryptionKeyRef, BackupEncryptionSecretUsername);
            BackupEncryptionHasSecret = false;
            BackupEncryptionPasswordInput = string.Empty;
            BackupEncryptionSecretStatus = L(
                "Settings.Encryption.SecretStatusMissing",
                "No encryption password enrolled yet.");
            TriggerAutoSave();
        }

        private void BrowseProjectsRoot()
        {
            _ = RunDetachedAsync(BrowseProjectsRootAsync, nameof(BrowseProjectsRootAsync));
        }

        private async Task BrowseProjectsRootAsync()
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

            try
            {
                var config = await Task.Run(AppConfigStore.Load);
                config.ProjectsRoot = path;
                await AppConfigStore.SaveAsync(config);
            }
            catch
            {
                // Best effort
            }

            ProjectsRootPath = path;
        }

        private void BrowseBackupLocation()
        {
            _ = RunDetachedAsync(BrowseBackupLocationAsync, nameof(BrowseBackupLocationAsync));
        }

        private async Task BrowseBackupLocationAsync()
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

        private void BrowseDestination(BackupDestinationViewModel? dest)
        {
            _ = RunDetachedAsync(() => BrowseDestinationAsync(dest), nameof(BrowseDestinationAsync));
        }

        private async Task BrowseDestinationAsync(BackupDestinationViewModel? dest)
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

        private void TestDestination(BackupDestinationViewModel? dest)
        {
            _ = RunDetachedAsync(() => TestDestinationAsync(dest), nameof(TestDestinationAsync));
        }

        private async Task TestDestinationAsync(BackupDestinationViewModel? dest)
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
                CredentialName = dest.CredentialName,
                RetryMaxAttempts = ClampInt(dest.RetryMaxAttempts, 1, 10, 1),
                RetryBackoffSeconds = ClampInt(dest.RetryBackoffSeconds, 1, 300, 10),
                EnableCheckpointResume = dest.EnableCheckpointResume,
                SoftQuotaBytes = ToQuotaBytes(dest.SoftQuotaGb),
                QuotaWarningPercent = ClampInt(dest.QuotaWarningPercent, 50, 99, 85)
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

        private static async Task RunDetachedAsync(Func<Task> operation, string operationName)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsViewModel] Detached operation failed ({operationName}): {ex}");
            }
        }

        private void RaiseRepairCommandStateChanged()
        {
            void Raise()
            {
                _scanBackupIndexRepairPlanCommand?.RaiseCanExecuteChanged();
                _applyBackupIndexRepairPlanCommand?.RaiseCanExecuteChanged();
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Raise();
                return;
            }

            Dispatcher.UIThread.Post(Raise);
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
                PreMounted = true,
                RetryMaxAttempts = 1,
                RetryBackoffSeconds = 10,
                    EnableCheckpointResume = true,
                    SoftQuotaGb = 0d,
                QuotaWarningPercent = 85
            });
            RefreshLegacyVisibility();
        }

        private static long? ToQuotaBytes(double valueGb)
        {
            if (valueGb <= 0)
                return null;

            var bytes = valueGb * 1024d * 1024d * 1024d;
            if (bytes < 1d)
                return null;

            return (long)Math.Round(bytes, MidpointRounding.AwayFromZero);
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
            string? lastError = null;

            bool TryOpen(string target)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        Process.Start("open", target);
                    }
                    else
                    {
                        Process.Start("xdg-open", target);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    return false;
                }
            }

            try
            {
                var root = AppContext.BaseDirectory;
                var candidatePaths = new[]
                {
                    Path.Combine(root, "docs", "HELP.md"),
                    Path.Combine(root, "docs", "wiki", "FAQ.md"),
                    Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "HELP.md")),
                    Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "wiki", "FAQ.md"))
                };

                foreach (var path in candidatePaths)
                {
                    if (!File.Exists(path))
                        continue;

                    if (TryOpen(path))
                    {
                        SaveStatus = L("Settings.Destinations.OpenHelpSuccess", "Help guide opened.");
                        return;
                    }
                }

                var onlineFallback = "https://github.com/flaviorame/vaultsync/tree/main/docs/wiki";
                if (TryOpen(onlineFallback))
                {
                    SaveStatus = L("Settings.Destinations.OpenHelpSuccess", "Help guide opened.");
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            SaveStatus = L("Settings.Destinations.OpenHelpFailed", "Could not open help guide.");
            GlobalNotificationCenter.Instance.Show(
                string.IsNullOrWhiteSpace(lastError)
                    ? SaveStatus
                    : $"{SaveStatus} {lastError}",
                NotificationSeverity.Warning,
                L("Settings.Destinations.Title", "Backup destinations"));
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

        private void ExportSupportBundle()
        {
            var result = _supportBundleService.Export();
            if (!result.Success || string.IsNullOrWhiteSpace(result.ZipPath))
            {
                SaveStatus = string.IsNullOrWhiteSpace(result.Message)
                    ? L("Settings.Advanced.SupportBundleFailed", "Support bundle export failed.")
                    : result.Message;
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Warning,
                    L("Settings.Advanced.SupportBundle", "Support bundle"));
                return;
            }

            SaveStatus = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.SupportBundleExportedTo", "Support bundle exported to {0}"),
                result.ZipPath);
            GlobalNotificationCenter.Instance.Show(
                L("Settings.Advanced.SupportBundleReady", "Support bundle ready. You can share the zip file."),
                NotificationSeverity.Info,
                L("Settings.Advanced.SupportBundle", "Support bundle"));

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

        private void ImportSupportBundle()
        {
            _ = RunDetachedAsync(ImportSupportBundleAsync, nameof(ImportSupportBundleAsync));
        }

        private void RunRetentionSimulation()
        {
            _ = RunDetachedAsync(RunRetentionSimulationAsync, nameof(RunRetentionSimulationAsync));
        }

        private async Task RunRetentionSimulationAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsRetentionSimulationBusy = true);
            DiagnosticsLogger.Record("Retention simulation started.");

            try
            {
                var result = await Task.Run(() =>
                {
                    var cfg = AppConfigStore.Load();
                    var repo = new SqliteRepository(cfg.DbPath ?? string.Empty);
                    var service = new BackupRetentionSimulationService(repo);
                    return service.Simulate(ClampInt(cfg.Backups.MaxSnapshotsPerProject, 1, 999, 20));
                }).ConfigureAwait(false);

                var status = result.AffectedProjectCount == 0
                    ? L("Settings.Backups.RetentionSimulationClear", "No projects currently exceed the retention cap.")
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        L("Settings.Backups.RetentionSimulationReady", "Retention simulation ready for {0} project(s)."),
                        result.AffectedProjectCount);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RetentionSimulationSummary = BuildRetentionSimulationSummary(result);
                    RetentionSimulationDetails = BuildRetentionSimulationDetails(result);
                    RetentionSimulationStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Info,
                        L("Settings.Backups.RetentionSimulationTitle", "Retention simulation"));
                });
                DiagnosticsLogger.Record(
                    $"Retention simulation complete. Projects={result.AffectedProjectCount}; SuggestedDeletes={result.SuggestedDeleteCount}; BlockedProjects={result.BlockedProjectCount}.");
            }
            catch (Exception ex)
            {
                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Backups.RetentionSimulationFailed", "Retention simulation failed: {0}"),
                    ex.Message);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RetentionSimulationStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Error,
                        L("Settings.Backups.RetentionSimulationTitle", "Retention simulation"));
                });
                DiagnosticsLogger.Record($"Retention simulation failed. {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsRetentionSimulationBusy = false);
            }
        }

        private void ScanBackupIndexRepairPlan()
        {
            _ = RunDetachedAsync(ScanBackupIndexRepairPlanAsync, nameof(ScanBackupIndexRepairPlanAsync));
        }

        private string BuildRetentionSimulationSummary(BackupRetentionSimulationResult result)
        {
            if (result.AffectedProjectCount == 0)
            {
                return L("Settings.Backups.RetentionSimulationSummaryClear", "No projects currently exceed the retention cap.");
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                L(
                    "Settings.Backups.RetentionSimulationSummary",
                    "Affected projects: {0} · Suggested deletes: {1} · Reclaimable bytes: {2} · Blocked projects: {3}"),
                result.AffectedProjectCount,
                result.SuggestedDeleteCount,
                FormatByteSize(result.SuggestedDeleteBytes),
                result.BlockedProjectCount);
        }

        private string BuildRetentionSimulationDetails(BackupRetentionSimulationResult result)
        {
            if (result.Projects.Count == 0)
                return string.Empty;

            var lines = new List<string>
            {
                string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Backups.RetentionSimulationConfigLine", "Retention cap: keep {0} snapshots per project."),
                    result.MaxSnapshotsPerProject)
            };

            foreach (var project in result.Projects
                         .OrderByDescending(item => item.SelectedDeleteBytes)
                         .ThenByDescending(item => item.DeleteQuota)
                         .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase))
            {
                var line = string.Format(
                    CultureInfo.CurrentCulture,
                    L(
                        "Settings.Backups.RetentionSimulationProjectLine",
                        "{0}: backups {1}, delete quota {2}, suggested deletes {3}, reclaim {4}, valid restore points {5}, blocked {6}"),
                    project.ProjectName,
                    project.BackupCount,
                    project.DeleteQuota,
                    project.SelectedDeleteCount,
                    FormatByteSize(project.SelectedDeleteBytes),
                    project.ValidRestorePointCount,
                    project.CanPrune ? L("Common.No", "No") : L("Common.Yes", "Yes"));

                if (!project.CanPrune)
                {
                    line = $"{line} ({project.PreflightCode})";
                }

                lines.Add(line);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string FormatByteSize(long bytes)
        {
            const double Kb = 1024d;
            const double Mb = Kb * 1024d;
            const double Gb = Mb * 1024d;

            if (bytes >= Gb)
            {
                return $"{bytes / Gb:0.##} GB";
            }

            if (bytes >= Mb)
            {
                return $"{bytes / Mb:0.##} MB";
            }

            if (bytes >= Kb)
            {
                return $"{bytes / Kb:0.##} KB";
            }

            return $"{bytes} B";
        }

        private async Task ScanBackupIndexRepairPlanAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record("Doctor action started: backup-index-repair scan.");

            try
            {
                var plan = await Task.Run(() =>
                {
                    var cfg = AppConfigStore.Load();
                    var repo = new SqliteRepository(cfg.DbPath ?? string.Empty);
                    var service = new BackupIndexRepairService(repo);
                    return service.BuildPlan();
                }).ConfigureAwait(false);

                var status = plan.HasActions
                    ? L("Settings.Advanced.BackupRepairPlanReady", "Repair plan ready.")
                    : L("Settings.Advanced.BackupRepairPlanNoActions", "No exact repair actions are currently available.");

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _currentBackupIndexRepairPlan = plan;
                    BackupIndexRepairSummary = BuildBackupIndexRepairSummary(plan);
                    BackupIndexRepairDetails = BuildBackupIndexRepairDetails(plan);
                    BackupIndexRepairStatus = status;
                    SaveStatus = status;
                    OnPropertyChanged(nameof(HasBackupIndexRepairPlan));
                    OnPropertyChanged(nameof(HasBackupIndexRepairActions));
                    OnPropertyChanged(nameof(HasBackupIndexRepairBlockedIssues));
                    RaiseRepairCommandStateChanged();

                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Info,
                        L("Settings.Advanced.BackupRepairTitle", "Backup index repair"));
                });
                DiagnosticsLogger.Record(
                    $"Doctor action complete: backup-index-repair scan. Actions={plan.Actions.Count}; BlockedBuckets={plan.BlockedIssues.Count}.");
                PersistBackupRepairTelemetry(plan, appliedCount: null, status: status);
            }
            catch (Exception ex)
            {
                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.BackupRepairFailed", "Backup repair scan failed: {0}"),
                    ex.Message);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    BackupIndexRepairStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Error,
                        L("Settings.Advanced.BackupRepairTitle", "Backup index repair"));
                });
                DiagnosticsLogger.Record($"Doctor action failed: backup-index-repair scan. {ex.GetType().Name} - {ex.Message}");
                PersistBackupRepairTelemetry(null, appliedCount: null, status: status);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = false);
            }
        }

        private void ApplyBackupIndexRepairPlan()
        {
            _ = RunDetachedAsync(ApplyBackupIndexRepairPlanAsync, nameof(ApplyBackupIndexRepairPlanAsync));
        }

        private async Task ApplyBackupIndexRepairPlanAsync()
        {
            var plan = _currentBackupIndexRepairPlan;
            if (plan is null || !plan.HasActions)
                return;

            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record(
                $"Doctor action started: backup-index-repair apply. PlannedActions={plan.Actions.Count}; BlockedBuckets={plan.BlockedIssues.Count}.");

            try
            {
                var applied = await Task.Run(() =>
                {
                    var cfg = AppConfigStore.Load();
                    var repo = new SqliteRepository(cfg.DbPath ?? string.Empty);
                    var service = new BackupIndexRepairService(repo);
                    return service.ApplyPlan(plan);
                }).ConfigureAwait(false);

                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.BackupRepairApplied", "Applied {0} exact backup-link repair action(s)."),
                    applied);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    BackupIndexRepairStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Info,
                        L("Settings.Advanced.BackupRepairTitle", "Backup index repair"));
                });
                DiagnosticsLogger.Record($"Doctor action complete: backup-index-repair apply. Applied={applied}.");
                PersistBackupRepairTelemetry(plan, applied, status);

                await ScanBackupIndexRepairPlanAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.BackupRepairApplyFailed", "Backup repair apply failed: {0}"),
                    ex.Message);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    BackupIndexRepairStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Error,
                        L("Settings.Advanced.BackupRepairTitle", "Backup index repair"));
                });
                DiagnosticsLogger.Record($"Doctor action failed: backup-index-repair apply. {ex.GetType().Name} - {ex.Message}");
                PersistBackupRepairTelemetry(plan, appliedCount: null, status: status);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = false);
            }
        }

        private string BuildBackupIndexRepairSummary(BackupIndexRepairPlan plan)
        {
            var exactActions = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.BackupRepairSummaryActions", "{0} exact remap action(s)"),
                plan.Actions.Count);
            var blockedIssues = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.BackupRepairSummaryBlocked", "{0} blocked orphan bucket(s)"),
                plan.BlockedIssues.Count);
            return $"{exactActions} | {blockedIssues}";
        }

        private string BuildBackupIndexRepairDetails(BackupIndexRepairPlan plan)
        {
            var parts = new List<string>();

            if (plan.Actions.Count > 0)
            {
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.BackupRepairDetailsActions", "Exact remaps ready: {0}."),
                    plan.Actions.Count));
            }

            foreach (var issue in plan.BlockedIssues.OrderBy(static issue => issue.Code, StringComparer.Ordinal))
            {
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.BackupRepairDetailsBlocked", "{0}: {1} item(s)."),
                    issue.Code,
                    issue.Count));
            }

            if (parts.Count == 0)
            {
                parts.Add(L("Settings.Advanced.BackupRepairDetailsHealthy", "No deterministic repair issues were found."));
            }

            return string.Join(" ", parts);
        }

        private void RefreshProjectMetadataConflicts(IEnumerable<ProjectMetadataConflictRecord>? conflicts)
        {
            ProjectMetadataConflicts.Clear();

            foreach (var conflict in (conflicts ?? Enumerable.Empty<ProjectMetadataConflictRecord>())
                         .OrderBy(static item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static item => item.SourceUpdatedUtc, StringComparer.Ordinal))
            {
                ProjectMetadataConflicts.Add(new ProjectMetadataConflictItemViewModel
                {
                    ProjectId = conflict.ProjectId,
                    ProjectName = string.IsNullOrWhiteSpace(conflict.ProjectName) ? conflict.ProjectExternalId : conflict.ProjectName,
                    ProjectExternalId = conflict.ProjectExternalId,
                    SourceMachineId = string.IsNullOrWhiteSpace(conflict.SourceMachineId) ? "unknown" : conflict.SourceMachineId,
                    SourceUpdatedUtc = FormatConflictUtc(conflict.SourceUpdatedUtc),
                    LocalPreferredDestinationId = FormatConflictValue(conflict.Local?.PreferredDestinationId),
                    ImportedPreferredDestinationId = FormatConflictValue(conflict.Imported?.PreferredDestinationId),
                    LocalRestoreMode = FormatConflictValue(conflict.Local?.RestoreMode),
                    ImportedRestoreMode = FormatConflictValue(conflict.Imported?.RestoreMode),
                    LocalVerificationPolicy = FormatConflictValue(conflict.Local?.VerificationPolicy),
                    ImportedVerificationPolicy = FormatConflictValue(conflict.Imported?.VerificationPolicy),
                    LocalTags = FormatConflictValue(conflict.Local?.Tags),
                    ImportedTags = FormatConflictValue(conflict.Imported?.Tags)
                });
            }

            ProjectMetadataConflictStatus = ProjectMetadataConflicts.Count == 0
                ? L("Settings.Advanced.MetadataConflictsNone", "No pending cross-machine metadata conflicts.")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.MetadataConflictsPending", "{0} pending cross-machine metadata conflict(s)."),
                    ProjectMetadataConflicts.Count);
            PersistMetadataConflictTelemetry(lastAction: null, lastResolvedProject: null, pendingCount: ProjectMetadataConflicts.Count);
            OnPropertyChanged(nameof(HasProjectMetadataConflicts));
            RaiseRepairCommandStateChanged();
            _acceptProjectMetadataConflictCommand?.RaiseCanExecuteChanged();
            _keepLocalProjectMetadataConflictCommand?.RaiseCanExecuteChanged();
        }

        private static string FormatConflictValue(string? value)
            => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static string FormatConflictUtc(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";

            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : value;
        }

        private void AcceptProjectMetadataConflict(ProjectMetadataConflictItemViewModel? item)
        {
            if (item is null)
                return;

            _ = RunDetachedAsync(() => AcceptProjectMetadataConflictAsync(item), nameof(AcceptProjectMetadataConflictAsync));
        }

        private async Task AcceptProjectMetadataConflictAsync(ProjectMetadataConflictItemViewModel item)
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record($"Doctor action started: metadata-conflict accept. ProjectId={item.ProjectId}; ExternalId={item.ProjectExternalId}.");

            try
            {
                await Task.Run(() =>
                {
                    var cfg = AppConfigStore.Load();
                    var repo = new SqliteRepository(cfg.DbPath ?? string.Empty);
                    repo.UpdateProjectPreferredDestination(item.ProjectId, EmptyToNull(item.ImportedPreferredDestinationId));
                    repo.UpdateProjectRestoreMode(item.ProjectId, EmptyToNull(item.ImportedRestoreMode));
                    repo.UpdateProjectVerificationPolicy(item.ProjectId, EmptyToNull(item.ImportedVerificationPolicy));
                    repo.UpdateProjectTags(item.ProjectId, EmptyToNull(item.ImportedTags));

                    RemoveProjectMetadataConflictRecord(cfg, item.ProjectId, item.ProjectExternalId);
                    UpdateMetadataConflictTelemetry(cfg, "accept-imported", item.ProjectName, Math.Max(0, cfg.Advanced.ProjectMetadataConflicts.Count));
                    AppConfigStore.Save(cfg);
                }).ConfigureAwait(false);

                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.MetadataConflictAccepted", "Imported metadata applied for {0}."),
                    item.ProjectName);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoadFromConfig();
                    ProjectMetadataConflictStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Info,
                        L("Settings.Advanced.MetadataConflictsTitle", "Metadata conflicts"));
                });
                DiagnosticsLogger.Record($"Doctor action complete: metadata-conflict accept. ProjectId={item.ProjectId}.");
            }
            catch (Exception ex)
            {
                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.MetadataConflictAcceptFailed", "Applying imported metadata failed: {0}"),
                    ex.Message);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProjectMetadataConflictStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Error,
                        L("Settings.Advanced.MetadataConflictsTitle", "Metadata conflicts"));
                });
                DiagnosticsLogger.Record($"Doctor action failed: metadata-conflict accept. {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = false);
            }
        }

        private void KeepLocalProjectMetadataConflict(ProjectMetadataConflictItemViewModel? item)
        {
            if (item is null)
                return;

            _ = RunDetachedAsync(() => KeepLocalProjectMetadataConflictAsync(item), nameof(KeepLocalProjectMetadataConflictAsync));
        }

        private async Task KeepLocalProjectMetadataConflictAsync(ProjectMetadataConflictItemViewModel item)
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record($"Doctor action started: metadata-conflict keep-local. ProjectId={item.ProjectId}; ExternalId={item.ProjectExternalId}.");

            try
            {
                await Task.Run(() =>
                {
                    var cfg = AppConfigStore.Load();
                    RemoveProjectMetadataConflictRecord(cfg, item.ProjectId, item.ProjectExternalId);
                    UpdateMetadataConflictTelemetry(cfg, "keep-local", item.ProjectName, Math.Max(0, cfg.Advanced.ProjectMetadataConflicts.Count));
                    AppConfigStore.Save(cfg);
                }).ConfigureAwait(false);

                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.MetadataConflictKeptLocal", "Local metadata kept for {0}."),
                    item.ProjectName);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoadFromConfig();
                    ProjectMetadataConflictStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Info,
                        L("Settings.Advanced.MetadataConflictsTitle", "Metadata conflicts"));
                });
                DiagnosticsLogger.Record($"Doctor action complete: metadata-conflict keep-local. ProjectId={item.ProjectId}.");
            }
            catch (Exception ex)
            {
                var status = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.MetadataConflictKeepLocalFailed", "Keeping local metadata failed: {0}"),
                    ex.Message);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProjectMetadataConflictStatus = status;
                    SaveStatus = status;
                    GlobalNotificationCenter.Instance.Show(
                        status,
                        NotificationSeverity.Error,
                        L("Settings.Advanced.MetadataConflictsTitle", "Metadata conflicts"));
                });
                DiagnosticsLogger.Record($"Doctor action failed: metadata-conflict keep-local. {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = false);
            }
        }

        private static void RemoveProjectMetadataConflictRecord(AppConfig cfg, int projectId, string projectExternalId)
        {
            cfg.Advanced.ProjectMetadataConflicts ??= new List<ProjectMetadataConflictRecord>();
            var existing = cfg.Advanced.ProjectMetadataConflicts.FirstOrDefault(conflict =>
                conflict.ProjectId == projectId ||
                (!string.IsNullOrWhiteSpace(projectExternalId) &&
                 string.Equals(conflict.ProjectExternalId, projectExternalId, StringComparison.OrdinalIgnoreCase)));
            if (existing is not null)
            {
                cfg.Advanced.ProjectMetadataConflicts.Remove(existing);
            }
        }

        private void PersistMetadataConflictTelemetry(string? lastAction, string? lastResolvedProject, int pendingCount)
        {
            try
            {
                var cfg = AppConfigStore.Load();
                UpdateMetadataConflictTelemetry(cfg, lastAction, lastResolvedProject, pendingCount);
                AppConfigStore.Save(cfg);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Metadata conflict telemetry persist failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static void UpdateMetadataConflictTelemetry(AppConfig cfg, string? lastAction, string? lastResolvedProject, int pendingCount)
        {
            cfg.Advanced.MetadataConflictTelemetry ??= new MetadataConflictTelemetry();
            cfg.Advanced.MetadataConflictTelemetry.LastUpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            cfg.Advanced.MetadataConflictTelemetry.PendingConflictCount = Math.Max(0, pendingCount);

            if (!string.IsNullOrWhiteSpace(lastAction))
                cfg.Advanced.MetadataConflictTelemetry.LastResolutionAction = lastAction;

            if (!string.IsNullOrWhiteSpace(lastResolvedProject))
                cfg.Advanced.MetadataConflictTelemetry.LastResolvedProject = lastResolvedProject;
        }

        private static void PersistBackupRepairTelemetry(BackupIndexRepairPlan? plan, int? appliedCount, string status)
        {
            try
            {
                var cfg = AppConfigStore.Load();
                cfg.Advanced.BackupRepairTelemetry ??= new BackupRepairTelemetry();
                var telemetry = cfg.Advanced.BackupRepairTelemetry;
                telemetry.LastScanUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                telemetry.LastStatus = status ?? string.Empty;
                telemetry.PlannedActionCount = plan?.Actions.Count ?? 0;
                telemetry.BlockedIssueBucketCount = plan?.BlockedIssues.Count ?? 0;
                telemetry.PlannedActionCodes = plan?.Actions
                    .Select(static action => action.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static code => code, StringComparer.Ordinal)
                    .ToList() ?? new List<string>();
                telemetry.BlockedIssueCodes = plan?.BlockedIssues
                    .Select(static issue => issue.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static code => code, StringComparer.Ordinal)
                    .ToList() ?? new List<string>();

                if (appliedCount.HasValue)
                {
                    telemetry.LastApplyUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    telemetry.LastAppliedCount = appliedCount.Value;
                }

                AppConfigStore.Save(cfg);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Backup repair telemetry persist failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static string? EmptyToNull(string? value)
            => string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "-", StringComparison.Ordinal) ? null : value.Trim();

        private async Task ImportSupportBundleAsync()
        {
            var storageProvider = GetStorageProvider();
            if (storageProvider is null)
                return;

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Settings.Advanced.SupportBundleImport", "Import support bundle"),
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Zip archive") { Patterns = new[] { "*.zip" } }
                }
            });

            var file = files?.FirstOrDefault();
            var zipPath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return;

            var result = await Task.Run(() => TryApplySupportBundleSettings(zipPath));
            if (!result.success)
            {
                SaveStatus = result.message;
                GlobalNotificationCenter.Instance.Show(
                    result.message,
                    NotificationSeverity.Warning,
                    L("Settings.Advanced.SupportBundle", "Support bundle"));
                return;
            }

            SaveStatus = result.message;
            LoadFromConfig();
            GlobalNotificationCenter.Instance.Show(
                result.message,
                NotificationSeverity.Info,
                L("Settings.Advanced.SupportBundle", "Support bundle"));
        }

        private (bool success, string message) TryApplySupportBundleSettings(string zipPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var reportEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, "support-report.json", StringComparison.OrdinalIgnoreCase));
                if (reportEntry is null)
                {
                    return (false, L("Settings.Advanced.SupportBundleImportMissingReport", "Support bundle is missing support-report.json."));
                }

                using var stream = reportEntry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("redactedConfig", out var redactedConfig))
                {
                    return (false, L("Settings.Advanced.SupportBundleImportMissingConfig", "Support bundle does not contain importable settings."));
                }

                var cfg = AppConfigStore.Load();
                ApplyImportableSettings(redactedConfig, cfg);
                AppConfigStore.Save(cfg);
                return (true, L("Settings.Advanced.SupportBundleImportApplied", "Support bundle settings imported (diagnostics ignored)."));
            }
            catch (Exception ex)
            {
                return (false, string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Advanced.SupportBundleImportFailed", "Support bundle import failed: {0}"),
                    ex.Message));
            }
        }

        private static void ApplyImportableSettings(JsonElement redactedConfig, AppConfig cfg)
        {
            if (redactedConfig.TryGetProperty("backups", out var backups))
            {
                cfg.Backups.EnableAutoBackups = ReadBool(backups, nameof(cfg.Backups.EnableAutoBackups), cfg.Backups.EnableAutoBackups);
                cfg.Backups.IntervalMinutes = ClampInt(ReadInt(backups, nameof(cfg.Backups.IntervalMinutes), cfg.Backups.IntervalMinutes), 1, 10080, 30);
                cfg.Backups.MaxSnapshotsPerProject = ClampInt(ReadInt(backups, nameof(cfg.Backups.MaxSnapshotsPerProject), cfg.Backups.MaxSnapshotsPerProject), 1, 10000, 20);
                cfg.Backups.EnableMetadataSync = ReadBool(backups, nameof(cfg.Backups.EnableMetadataSync), cfg.Backups.EnableMetadataSync);
                cfg.Backups.AutoImportMetadata = ReadBool(backups, nameof(cfg.Backups.AutoImportMetadata), cfg.Backups.AutoImportMetadata);
                cfg.Backups.PromptRestoreAfterImport = ReadBool(backups, nameof(cfg.Backups.PromptRestoreAfterImport), cfg.Backups.PromptRestoreAfterImport);
                cfg.Backups.EnableBandwidthLimit = ReadBool(backups, nameof(cfg.Backups.EnableBandwidthLimit), cfg.Backups.EnableBandwidthLimit);
                cfg.Backups.MaxBandwidthMbps = ClampInt(ReadInt(backups, nameof(cfg.Backups.MaxBandwidthMbps), cfg.Backups.MaxBandwidthMbps), 1, 100000, 100);
                cfg.Backups.EnableQuietHours = ReadBool(backups, nameof(cfg.Backups.EnableQuietHours), cfg.Backups.EnableQuietHours);
                cfg.Backups.QuietHoursStart = NormalizeTimeOfDay(
                    ReadString(backups, nameof(cfg.Backups.QuietHoursStart), cfg.Backups.QuietHoursStart),
                    cfg.Backups.QuietHoursStart);
                cfg.Backups.QuietHoursEnd = NormalizeTimeOfDay(
                    ReadString(backups, nameof(cfg.Backups.QuietHoursEnd), cfg.Backups.QuietHoursEnd),
                    cfg.Backups.QuietHoursEnd);
                cfg.Backups.UseAdvancedDestinations = ReadBool(backups, nameof(cfg.Backups.UseAdvancedDestinations), cfg.Backups.UseAdvancedDestinations);
                cfg.Backups.UseCompression = ReadBool(backups, nameof(cfg.Backups.UseCompression), cfg.Backups.UseCompression);
                cfg.Backups.UseRsyncDelta = ReadBool(backups, nameof(cfg.Backups.UseRsyncDelta), cfg.Backups.UseRsyncDelta);
                cfg.Backups.UseIncrementalBackups = ReadBool(backups, nameof(cfg.Backups.UseIncrementalBackups), cfg.Backups.UseIncrementalBackups);
                cfg.Backups.UseFullSnapshotHash = ReadBool(backups, nameof(cfg.Backups.UseFullSnapshotHash), cfg.Backups.UseFullSnapshotHash);
                cfg.Backups.EnableScanCache = ReadBool(backups, nameof(cfg.Backups.EnableScanCache), cfg.Backups.EnableScanCache);
                cfg.Backups.AggressiveScanCache = ReadBool(backups, nameof(cfg.Backups.AggressiveScanCache), cfg.Backups.AggressiveScanCache);
                cfg.Backups.EnableArchiveUploadAutoTune = ReadBool(backups, nameof(cfg.Backups.EnableArchiveUploadAutoTune), cfg.Backups.EnableArchiveUploadAutoTune);
                cfg.Backups.EnableParallelArchiveUpload = ReadBool(backups, nameof(cfg.Backups.EnableParallelArchiveUpload), cfg.Backups.EnableParallelArchiveUpload);
                cfg.Backups.VerifyAfterCreate = ReadBool(backups, nameof(cfg.Backups.VerifyAfterCreate), cfg.Backups.VerifyAfterCreate);
                cfg.Backups.PauseOnBattery = ReadBool(backups, nameof(cfg.Backups.PauseOnBattery), cfg.Backups.PauseOnBattery);

                if (backups.TryGetProperty("encryption", out var enc))
                {
                    cfg.Backups.Encryption.Enabled = ReadBool(enc, nameof(cfg.Backups.Encryption.Enabled), cfg.Backups.Encryption.Enabled);
                    cfg.Backups.Encryption.Algorithm = NormalizeImportedEncryptionAlgorithm(
                        ReadString(enc, nameof(cfg.Backups.Encryption.Algorithm), cfg.Backups.Encryption.Algorithm),
                        cfg.Backups.Encryption.Algorithm);
                    cfg.Backups.Encryption.KdfProfile = NormalizeImportedKdfProfile(
                        ReadString(enc, nameof(cfg.Backups.Encryption.KdfProfile), cfg.Backups.Encryption.KdfProfile),
                        cfg.Backups.Encryption.KdfProfile);
                    cfg.Backups.Encryption.KdfParamRef = NormalizeImportedKdfParamRef(
                        ReadString(enc, nameof(cfg.Backups.Encryption.KdfParamRef), cfg.Backups.Encryption.KdfParamRef),
                        cfg.Backups.Encryption.KdfParamRef);
                    cfg.Backups.Encryption.AllowSessionFallback = ReadBool(enc, nameof(cfg.Backups.Encryption.AllowSessionFallback), cfg.Backups.Encryption.AllowSessionFallback);
                    cfg.Backups.Encryption.OpenUnlockTimeoutMinutes = ClampInt(ReadInt(enc, nameof(cfg.Backups.Encryption.OpenUnlockTimeoutMinutes), cfg.Backups.Encryption.OpenUnlockTimeoutMinutes), 1, 1440, 10);
                }
            }

            if (redactedConfig.TryGetProperty("storage", out var storage))
            {
                cfg.Storage.PreferExternalDrives = ReadBool(storage, nameof(cfg.Storage.PreferExternalDrives), cfg.Storage.PreferExternalDrives);
                cfg.Storage.ShowDriveWarnings = ReadBool(storage, nameof(cfg.Storage.ShowDriveWarnings), cfg.Storage.ShowDriveWarnings);
                cfg.Storage.MinFreeSpacePercent = ClampInt(ReadInt(storage, nameof(cfg.Storage.MinFreeSpacePercent), cfg.Storage.MinFreeSpacePercent), 1, 99, 10);
            }

            if (redactedConfig.TryGetProperty("appearance", out var appearance))
            {
                cfg.Appearance.Theme = NormalizeImportedTheme(
                    ReadString(appearance, nameof(cfg.Appearance.Theme), cfg.Appearance.Theme),
                    cfg.Appearance.Theme);
                cfg.Appearance.CompactLayout = ReadBool(appearance, nameof(cfg.Appearance.CompactLayout), cfg.Appearance.CompactLayout);
                cfg.Appearance.ShowProjectAvatars = ReadBool(appearance, nameof(cfg.Appearance.ShowProjectAvatars), cfg.Appearance.ShowProjectAvatars);

                if (appearance.TryGetProperty(nameof(cfg.Appearance.TagColors), out var tagColors) &&
                    tagColors.ValueKind == JsonValueKind.Object)
                {
                    var importedTagColors = new Dictionary<string, TagColorConfig>(StringComparer.OrdinalIgnoreCase);

                    foreach (var property in tagColors.EnumerateObject())
                    {
                        var tag = property.Name?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(tag) || property.Value.ValueKind != JsonValueKind.Object)
                            continue;

                        var defaults = ProjectTagChip.GetDefaultPalette(tag);
                        importedTagColors[tag] = new TagColorConfig
                        {
                            Background = ProjectTagAppearance.NormalizeHex(
                                ReadString(property.Value, nameof(TagColorConfig.Background), defaults.Background),
                                defaults.Background),
                            Foreground = ProjectTagAppearance.NormalizeHex(
                                ReadString(property.Value, nameof(TagColorConfig.Foreground), defaults.Foreground),
                                defaults.Foreground),
                            Border = ProjectTagAppearance.NormalizeHex(
                                ReadString(property.Value, nameof(TagColorConfig.Border), defaults.Border),
                                defaults.Border)
                        };
                    }

                    cfg.Appearance.TagColors = importedTagColors;
                }

                if (appearance.TryGetProperty(nameof(cfg.Appearance.CustomTheme), out var customTheme) &&
                    customTheme.ValueKind == JsonValueKind.Object)
                {
                    cfg.Appearance.CustomTheme = new ThemePaletteConfig
                    {
                        Name = ReadString(customTheme, nameof(ThemePaletteConfig.Name), cfg.Appearance.CustomTheme.Name),
                        BaseTheme = ReadString(customTheme, nameof(ThemePaletteConfig.BaseTheme), cfg.Appearance.CustomTheme.BaseTheme),
                        Background = ReadString(customTheme, nameof(ThemePaletteConfig.Background), cfg.Appearance.CustomTheme.Background),
                        Surface = ReadString(customTheme, nameof(ThemePaletteConfig.Surface), cfg.Appearance.CustomTheme.Surface),
                        SurfaceAlt = ReadString(customTheme, nameof(ThemePaletteConfig.SurfaceAlt), cfg.Appearance.CustomTheme.SurfaceAlt),
                        Accent = ReadString(customTheme, nameof(ThemePaletteConfig.Accent), cfg.Appearance.CustomTheme.Accent),
                        TextPrimary = ReadString(customTheme, nameof(ThemePaletteConfig.TextPrimary), cfg.Appearance.CustomTheme.TextPrimary),
                        TextSecondary = ReadString(customTheme, nameof(ThemePaletteConfig.TextSecondary), cfg.Appearance.CustomTheme.TextSecondary),
                        Success = ReadString(customTheme, nameof(ThemePaletteConfig.Success), cfg.Appearance.CustomTheme.Success),
                        Warning = ReadString(customTheme, nameof(ThemePaletteConfig.Warning), cfg.Appearance.CustomTheme.Warning),
                        Danger = ReadString(customTheme, nameof(ThemePaletteConfig.Danger), cfg.Appearance.CustomTheme.Danger)
                    };
                }
            }

            if (redactedConfig.TryGetProperty("notifications", out var notifications))
            {
                cfg.Notifications.OnBackupSuccess = ReadBool(notifications, nameof(cfg.Notifications.OnBackupSuccess), cfg.Notifications.OnBackupSuccess);
                cfg.Notifications.OnBackupFailure = ReadBool(notifications, nameof(cfg.Notifications.OnBackupFailure), cfg.Notifications.OnBackupFailure);
                cfg.Notifications.OnSnapshotSuccess = ReadBool(notifications, nameof(cfg.Notifications.OnSnapshotSuccess), cfg.Notifications.OnSnapshotSuccess);
                cfg.Notifications.OnSnapshotFailure = ReadBool(notifications, nameof(cfg.Notifications.OnSnapshotFailure), cfg.Notifications.OnSnapshotFailure);
                cfg.Notifications.OnLowDisk = ReadBool(notifications, nameof(cfg.Notifications.OnLowDisk), cfg.Notifications.OnLowDisk);
                cfg.Notifications.UseOsNotifications = ReadBool(notifications, nameof(cfg.Notifications.UseOsNotifications), cfg.Notifications.UseOsNotifications);
                cfg.Notifications.OnlyWhenInactive = ReadBool(notifications, nameof(cfg.Notifications.OnlyWhenInactive), cfg.Notifications.OnlyWhenInactive);
            }

            if (redactedConfig.TryGetProperty("advanced", out var advanced))
            {
                cfg.Advanced.VerboseLogging = ReadBool(advanced, nameof(cfg.Advanced.VerboseLogging), cfg.Advanced.VerboseLogging);
                cfg.Advanced.SaveVerboseLogs = ReadBool(advanced, nameof(cfg.Advanced.SaveVerboseLogs), cfg.Advanced.SaveVerboseLogs);
                cfg.Advanced.CheckUpdates = ReadBool(advanced, nameof(cfg.Advanced.CheckUpdates), cfg.Advanced.CheckUpdates);
                cfg.Advanced.UpdateCheckIntervalMinutes = ClampInt(ReadInt(advanced, nameof(cfg.Advanced.UpdateCheckIntervalMinutes), cfg.Advanced.UpdateCheckIntervalMinutes), 15, 1440, 120);
                cfg.Advanced.BetaChannelEnabled = ReadBool(advanced, nameof(cfg.Advanced.BetaChannelEnabled), cfg.Advanced.BetaChannelEnabled);
                cfg.Advanced.Language = NormalizeImportedLanguage(
                    ReadString(advanced, nameof(cfg.Advanced.Language), cfg.Advanced.Language),
                    cfg.Advanced.Language);
                cfg.Advanced.HasSeenOnboarding = ReadBool(advanced, nameof(cfg.Advanced.HasSeenOnboarding), cfg.Advanced.HasSeenOnboarding);

                if (advanced.TryGetProperty(nameof(cfg.Advanced.Maintenance), out var maintenance))
                {
                    cfg.Advanced.Maintenance.Enabled = ReadBool(
                        maintenance,
                        nameof(cfg.Advanced.Maintenance.Enabled),
                        cfg.Advanced.Maintenance.Enabled);
                    cfg.Advanced.Maintenance.WindowStart = NormalizeTimeOfDay(
                        ReadString(maintenance, nameof(cfg.Advanced.Maintenance.WindowStart), cfg.Advanced.Maintenance.WindowStart),
                        "01:00");
                    cfg.Advanced.Maintenance.WindowEnd = NormalizeTimeOfDay(
                        ReadString(maintenance, nameof(cfg.Advanced.Maintenance.WindowEnd), cfg.Advanced.Maintenance.WindowEnd),
                        "05:00");
                    cfg.Advanced.Maintenance.RunConsistencyScan = ReadBool(
                        maintenance,
                        nameof(cfg.Advanced.Maintenance.RunConsistencyScan),
                        cfg.Advanced.Maintenance.RunConsistencyScan);
                    cfg.Advanced.Maintenance.RunRepairDryRun = ReadBool(
                        maintenance,
                        nameof(cfg.Advanced.Maintenance.RunRepairDryRun),
                        cfg.Advanced.Maintenance.RunRepairDryRun);
                    cfg.Advanced.Maintenance.RunMetadataRefresh = ReadBool(
                        maintenance,
                        nameof(cfg.Advanced.Maintenance.RunMetadataRefresh),
                        cfg.Advanced.Maintenance.RunMetadataRefresh);
                }
            }

            if (redactedConfig.TryGetProperty("behavior", out var behavior))
            {
                cfg.Behavior.RunInBackground = ReadBool(behavior, nameof(cfg.Behavior.RunInBackground), cfg.Behavior.RunInBackground);
                cfg.Behavior.ShowWindowOnTrayActions = ReadBool(behavior, nameof(cfg.Behavior.ShowWindowOnTrayActions), cfg.Behavior.ShowWindowOnTrayActions);
                cfg.Behavior.ShowTrayIcon = ReadBool(behavior, nameof(cfg.Behavior.ShowTrayIcon), cfg.Behavior.ShowTrayIcon);
                cfg.Behavior.ShowBackupWidget = ReadBool(behavior, nameof(cfg.Behavior.ShowBackupWidget), cfg.Behavior.ShowBackupWidget);
                cfg.Behavior.EnableSystemNotifications = ReadBool(behavior, nameof(cfg.Behavior.EnableSystemNotifications), cfg.Behavior.EnableSystemNotifications);
                cfg.Behavior.MinimizeToTray = ReadBool(behavior, nameof(cfg.Behavior.MinimizeToTray), cfg.Behavior.MinimizeToTray);
                cfg.Behavior.LaunchOnLogin = ReadBool(behavior, nameof(cfg.Behavior.LaunchOnLogin), cfg.Behavior.LaunchOnLogin);
                cfg.Behavior.ConfirmDeleteBackup = ReadBool(behavior, nameof(cfg.Behavior.ConfirmDeleteBackup), cfg.Behavior.ConfirmDeleteBackup);
            }
        }

        private static bool ReadBool(JsonElement parent, string propertyName, bool fallback)
        {
            if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return fallback;
            return value.GetBoolean();
        }

        private static int ReadInt(JsonElement parent, string propertyName, int fallback)
        {
            if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
                return fallback;
            return result;
        }

        private static string ReadString(JsonElement parent, string propertyName, string fallback)
        {
            if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                return fallback;
            return value.GetString() ?? fallback;
        }

        private static string NormalizeImportedTheme(string value, string fallback)
        {
            return value switch
            {
                "Dark" => "Dark",
                "Light" => "Light",
                "Custom" => "Custom",
                "Follow system" => "Follow system",
                "System" => "Follow system",
                _ => fallback
            };
        }

        private static string NormalizeImportedLanguage(string value, string fallback)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "en" or "it" or "es" or "fr" or "de" or "pt" or "zh" or "hi" or "ar" or "bn" or "ru" => normalized,
                _ => fallback
            };
        }

        private static string NormalizeImportedEncryptionAlgorithm(string value, string fallback)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.Equals(normalized, "aes-256-cbc-hmac-sha256-v1", StringComparison.OrdinalIgnoreCase)
                ? "aes-256-cbc-hmac-sha256-v1"
                : fallback;
        }

        private static string NormalizeImportedKdfProfile(string value, string fallback)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.Equals(normalized, "pbkdf2-sha256-v1", StringComparison.OrdinalIgnoreCase)
                ? "pbkdf2-sha256-v1"
                : fallback;
        }

        private static string NormalizeImportedKdfParamRef(string value, string fallback)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return fallback;

            const string prefix = "pbkdf2-iter-";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fallback;

            var valuePart = normalized[prefix.Length..].Trim();
            if (!int.TryParse(valuePart, out var iterations))
                return fallback;

            return $"pbkdf2-iter-{Math.Clamp(iterations, 10_000, 1_000_000)}";
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

        private int _retryMaxAttempts = 1;
        public int RetryMaxAttempts
        {
            get => _retryMaxAttempts;
            set => SetField(ref _retryMaxAttempts, Math.Clamp(value, 1, 10));
        }

        private int _retryBackoffSeconds = 10;
        public int RetryBackoffSeconds
        {
            get => _retryBackoffSeconds;
            set => SetField(ref _retryBackoffSeconds, Math.Clamp(value, 1, 300));
        }

        private bool _enableCheckpointResume = true;
        public bool EnableCheckpointResume
        {
            get => _enableCheckpointResume;
            set => SetField(ref _enableCheckpointResume, value);
        }

        private double _softQuotaGb;
        public double SoftQuotaGb
        {
            get => _softQuotaGb;
            set => SetField(ref _softQuotaGb, Math.Clamp(value, 0d, 1024d * 1024d));
        }

        private int _quotaWarningPercent = 85;
        public int QuotaWarningPercent
        {
            get => _quotaWarningPercent;
            set => SetField(ref _quotaWarningPercent, Math.Clamp(value, 50, 99));
        }

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


