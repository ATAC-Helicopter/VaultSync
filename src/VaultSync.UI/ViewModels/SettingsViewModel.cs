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
using VaultSync.UI.ViewModels;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI
{
    public sealed partial class SettingsViewModel : ViewModelBase
    {
        private const string BackupRepairTitleKey = "Settings.Advanced.BackupRepairTitle";
        private const string BackupRepairTitleFallback = "Backup index repair";
        private const string DefaultMaintenanceEnd = "05:00";
        private const string DefaultMaintenanceStart = "01:00";
        private const string DefaultQuietHoursEnd = "07:00";
        private const string DefaultQuietHoursStart = "23:00";
        private const string MetadataConflictsTitleKey = "Settings.Advanced.MetadataConflictsTitle";
        private const string MetadataConflictsTitleFallback = "Metadata conflicts";
        private const string MissingSecretStatusKey = "Settings.Encryption.SecretStatusMissing";
        private const string MissingSecretStatusFallback = "No encryption password enrolled yet.";
        private const string RsyncExecutableName = "rsync";
        private const string ToolsDirectoryName = "tools";
        private const string SupportBundleTitleKey = "Settings.Advanced.SupportBundle";
        private const string SupportBundleTitleFallback = "Support bundle";
        private const string ThemeCustom = "Custom";
        private const string ThemeDark = "Dark";
        private const string ThemeFollowSystem = "Follow system";
        private const string ThemeLight = "Light";
        private const string ThemeSystem = "System";

        // ---------------- Core backing fields ----------------

        private string _projectsRootPath = string.Empty;
        private bool _resumeLastSession = true;
        private bool _showWindowOnTrayActions = true;
        private bool _showTrayIcon = true;
        private bool _runInBackground = true;
        private bool _launchOnLogin = false;
        private List<int> _autoBackupDisabledProjects = [];

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
        private string _quietHoursStart = DefaultQuietHoursStart;
        private string _quietHoursEnd = DefaultQuietHoursEnd;
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

    private string _selectedTheme = ThemeSystem;
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
        private bool _crashReportAssistanceEnabled = true;
        private bool _checkForUpdatesOnStartup = true;
        private int _updateCheckIntervalMinutes = 120;
        private bool _betaChannelEnabled = false;
        private bool _enableMaintenanceWindow = false;
        private string _maintenanceWindowStart = DefaultMaintenanceStart;
        private string _maintenanceWindowEnd = DefaultMaintenanceEnd;
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
        private readonly IAppConfigStore _configStore;
        private readonly IRepositoryFactory _repositoryFactory;
        private readonly CredentialVault _credentialVault = CredentialVault.Instance;
        private readonly BackupEncryptionSecretService _backupEncryptionSecretService = new();
        private readonly NetworkMountService _networkMountService = new();
        private readonly RelayCommand? _addTagColorRuleCommand;
        private readonly RelayCommand? _removeTagColorRuleCommand;
        private readonly RelayCommand? _resetTagColorRuleCommand;
        private readonly RelayCommand? _applyThemePresetCommand;
        private readonly RelayCommand? _applyThemePaletteSwatchCommand;
        private readonly RelayCommand? _selectThemeColorSlotCommand;
        private readonly RelayCommand? _resetCustomThemeCommand;
        private readonly RelayCommand? _scanBackupIndexRepairPlanCommand;
        private readonly RelayCommand? _applyBackupIndexRepairPlanCommand;
        private readonly RelayCommand? _acceptProjectMetadataConflictCommand;
        private readonly RelayCommand? _keepLocalProjectMetadataConflictCommand;
        private readonly RelayCommand? _runRetentionSimulationCommand;
        private BackupIndexRepairPlan? _currentBackupIndexRepairPlan;
        private string _backupIndexRepairStatus = string.Empty;
        private string _backupIndexRepairSummary = string.Empty;
        private string _backupIndexRepairDetails = string.Empty;
        private string _projectMetadataConflictStatus = string.Empty;
        private bool _isBackupIndexRepairBusy;
        private bool _showLegacyBackupLocation = true;
        private string _customThemeName = "VaultSync Midnight";
        private string _customThemeBase = ThemeDark;
        private ThemeColorSlotViewModel? _selectedThemeColorSlot;
        private readonly bool _isInitialized;
        private bool _isSaving;
        private bool _savePending;
        private bool _isReloadingFromConfig;
        private bool _isRefreshingLocalizedOptions;
        private bool? _lastLaunchOnLoginApplied;

        public event Action? OpenLogConsoleRequested;
        public event Action? UpdateCheckRequested;
        public event Action? RefreshHistoryRequested;
        public event Action? RotateEncryptedBackupsRequested;
        public event Action? EnrollProjectEncryptionRequested;
        public event Action? LockEncryptedOpenWorkspacesRequested;
        public event Action? DestinationSettingsSaved;
        public event Action<BackupDestination, bool, bool, string>? DestinationTested;

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

        private sealed record CredentialSaveResult(
            NetworkCredentialProfile Profile,
            bool HadPlaintextFallback);

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

        public sealed class TagColorRuleViewModel : ViewModelBase
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
                    (string Background, string Foreground, string Border) defaults = ProjectTagChip.GetDefaultPalette(PreviewTag);
                    return ProjectTagAppearance.NormalizeHex(Background, defaults.Background);
                }
            }

            public string PreviewForeground
            {
                get
                {
                    (string Background, string Foreground, string Border) defaults = ProjectTagChip.GetDefaultPalette(PreviewTag);
                    return ProjectTagAppearance.NormalizeHex(Foreground, defaults.Foreground);
                }
            }

            public string PreviewBorder
            {
                get
                {
                    (string Background, string Foreground, string Border) defaults = ProjectTagChip.GetDefaultPalette(PreviewTag);
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
                return Color.TryParse(hex, out Color color) ? color : Colors.Transparent;
            }

            private void ApplyPaletteSwatch(ThemePaletteSwatchViewModel? swatch)
            {
                if (swatch is null)
                    return;

                ActiveColor = swatch.SwatchColor;
            }

            private void RaiseSelection()
            {
                OnPropertiesChanged(
                    nameof(IsEditingBackground),
                    nameof(IsEditingForeground),
                    nameof(IsEditingBorder),
                    nameof(ActiveColor),
                    nameof(ActiveColorHex));
            }

            private void RaiseAll()
            {
                OnPropertiesChanged(
                    nameof(Tag),
                    nameof(Background),
                    nameof(Foreground),
                    nameof(Border),
                    nameof(PreviewTag),
                    nameof(PreviewBackground),
                    nameof(PreviewForeground),
                    nameof(PreviewBorder),
                    nameof(ActiveColor),
                    nameof(ActiveColorHex));
                RaiseSelection();
            }
        }

        public ObservableCollection<ProjectMetadataConflictItemViewModel> ProjectMetadataConflicts { get; } = [];
        public ObservableCollection<TagColorRuleViewModel> TagColorRules { get; } = [];

        private void RefreshLegacyVisibility()
        {
            ShowLegacyBackupLocation = !UseAdvancedDestinations;
        }

        private void NotifyLocalizedSettingsTextChanged()
        {
            OnPropertiesChanged(
                nameof(EnrollProjectEncryptionPasswordLabel),
                nameof(EncryptionOpenTimeoutLabel),
                nameof(EncryptionOpenTimeoutDescription),
                nameof(LockEncryptedOpenNowLabel),
                nameof(BandwidthLimitLabel),
                nameof(BandwidthLimitDescription),
                nameof(BandwidthLimitValueLabel),
                nameof(BandwidthLimitValueDescription),
                nameof(QuietHoursLabel),
                nameof(QuietHoursDescription),
                nameof(QuietHoursStartLabel),
                nameof(QuietHoursEndLabel),
                nameof(QuietHoursWindowLabel),
                nameof(QuietHoursWindowPreview),
                nameof(MaintenanceWindowLabel),
                nameof(MaintenanceWindowDescription),
                nameof(MaintenanceWindowStartLabel),
                nameof(MaintenanceWindowEndLabel),
                nameof(MaintenanceWindowPreviewLabel),
                nameof(MaintenanceWindowPreview),
                nameof(MaintenanceWindowConsistencyLabel),
                nameof(MaintenanceWindowConsistencyDescription),
                nameof(MaintenanceWindowRepairLabel),
                nameof(MaintenanceWindowRepairDescription),
                nameof(MaintenanceWindowMetadataLabel),
                nameof(MaintenanceWindowMetadataDescription));
        }

        private void NotifyLoadedSettingsChanged()
        {
            OnPropertiesChanged(
                nameof(ProjectsRootPath),
                nameof(ResumeLastSession),
                nameof(ShowWindowOnTrayActions),
                nameof(ShowTrayIcon),
                nameof(RunInBackground),
                nameof(ShowTrayBackupWidget),
                nameof(LaunchOnLogin),
                nameof(ConfirmDeleteBackups),
                nameof(EnableAutoBackups),
                nameof(AutoBackupIntervalMinutes),
                nameof(MaxSnapshotsPerProject),
                nameof(BackupLocationPath),
                nameof(UseAdvancedDestinations),
                nameof(UseBackupCompression),
                nameof(UseRsyncDelta),
                nameof(UseIncrementalBackups),
                nameof(VerifyBackupsAfterCreate),
                nameof(PauseBackupsOnBattery),
                nameof(PreferExternalDrives),
                nameof(ShowDriveHealthWarnings),
                nameof(MinimumFreeSpacePercent),
                nameof(SelectedTheme),
                nameof(UseCompactLayout),
                nameof(ShowProjectAvatars),
                nameof(NotifyOnBackupSuccess),
                nameof(NotifyOnBackupFailure),
                nameof(NotifyOnLowDiskSpace),
                nameof(NotifyOnSnapshotSuccess),
                nameof(NotifyOnSnapshotFailure),
                nameof(UseOsNotifications),
                nameof(NotifyOnlyWhenInactive),
                nameof(EnableVerboseLogging),
                nameof(SaveVerboseLogs),
                nameof(CrashReportAssistanceEnabled),
                nameof(CheckForUpdatesOnStartup),
                nameof(UpdateCheckIntervalMinutes),
                nameof(BetaChannelEnabled),
                nameof(EnableMaintenanceWindow),
                nameof(MaintenanceWindowStart),
                nameof(MaintenanceWindowEnd),
                nameof(MaintenanceRunConsistencyScan),
                nameof(MaintenanceRunRepairDryRun),
                nameof(MaintenanceRunMetadataRefresh),
                nameof(SaveStatus));
        }

        public SettingsViewModel(LocalizationService localizationService, IAppConfigStore? configStore = null, IRepositoryFactory? repositoryFactory = null)
        {
            _localizationService = localizationService;
            _configStore = configStore ?? StaticAppConfigStore.Instance;
            _repositoryFactory = repositoryFactory ?? new SqliteRepositoryFactory(_configStore);
            _selectedLanguageCode = localizationService.CurrentLanguage;
            _localizationService.LanguageChanged += () =>
            {
                string normalizedTheme = NormalizeThemeOption(_selectedTheme);
                string normalizedBaseTheme = NormalizeThemeBaseOption(_customThemeBase);
                _isRefreshingLocalizedOptions = true;
                try
                {
                    RefreshThemeOptions();
                    RefreshCustomThemeBaseOptions();
                    _selectedTheme = DisplayThemeOption(normalizedTheme);
                    _customThemeBase = DisplayThemeBaseOption(normalizedBaseTheme);
                }
                finally
                {
                    _isRefreshingLocalizedOptions = false;
                }
                OnPropertyChanged(nameof(SelectedLanguage));
                OnPropertyChanged(nameof(SelectedTheme));
                OnPropertyChanged(nameof(CustomThemeBase));
                RefreshUpdateCheckStatus();
                RefreshRsyncStatusHint();
                NotifyLocalizedSettingsTextChanged();
                ProjectMetadataConflictStatus = ProjectMetadataConflicts.Count == 0
                    ? L("Settings.Advanced.MetadataConflictsNone", "No pending cross-machine metadata conflicts.")
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        L("Settings.Advanced.MetadataConflictsPending", "{0} pending cross-machine metadata conflict(s)."),
                        ProjectMetadataConflicts.Count);
                _backupEncryptionSecretStatus = _backupEncryptionHasSecret
                    ? L("Settings.Encryption.SecretStatusAvailable", "Password is enrolled in secure storage.")
                    : L(MissingSecretStatusKey, MissingSecretStatusFallback);
                OnPropertyChanged(nameof(BackupEncryptionSecretStatus));
                OnPropertyChanged(nameof(ProjectMetadataConflictStatus));
            };

            ThemeOptions = [];
            RefreshThemeOptions();

            _selectedTheme = ThemeOptions[0];
            InitializeThemeEditor();
            RefreshCustomThemeBaseOptions();

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
            TestCrashReportCommand       = new RelayCommand(_ => CrashHandler.ShowCrashReportTest());
            CheckUpdatesNowCommand       = new RelayCommand(_ => CheckUpdatesNow());
            OpenMicrosoftStoreCommand    = new RelayCommand(_ => OpenMicrosoftStoreListing());
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
            if (IsStoreDistribution)
            {
                SetStoreManagedUpdatesStatus();
            }
            _isInitialized = true;
        }

        // ---------------- Load + Save ----------------

        private void LoadFromConfig()
        {
            _isReloadingFromConfig = true;
            try
            {
                AppConfig cfg = _configStore.Load();
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
            _autoBackupDisabledProjects = cfg.Backups.AutoBackupDisabledProjects ?? [];
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
            _quietHoursStart           = NormalizeTimeOfDay(cfg.Backups.QuietHoursStart, DefaultQuietHoursStart);
            _quietHoursEnd             = NormalizeTimeOfDay(cfg.Backups.QuietHoursEnd, DefaultQuietHoursEnd);
            _backupEncryptionEnabled   = cfg.Backups.Encryption.Enabled;
            _backupEncryptionAllowSessionFallback = cfg.Backups.Encryption.AllowSessionFallback;
            _backupEncryptionOpenUnlockTimeoutMinutes = ClampInt(cfg.Backups.Encryption.OpenUnlockTimeoutMinutes, 1, 240, 10);
            _backupEncryptionKeyRef = cfg.Backups.Encryption.KeyRef ?? string.Empty;
            // Startup status is based on the credential index. Reading the secret
            // here would unlock Keychain before the user requests encrypted work.
            _backupEncryptionHasSecret = _credentialVault.HasStoredSecret(_backupEncryptionKeyRef);
            _backupEncryptionPasswordInput = string.Empty;
            _backupEncryptionSecretStatus = _backupEncryptionHasSecret
                ? L("Settings.Encryption.SecretStatusAvailable", "Password is enrolled in secure storage.")
                : L(MissingSecretStatusKey, MissingSecretStatusFallback);

            _preferExternalDrives    = cfg.Storage.PreferExternalDrives;
            _showDriveHealthWarnings = cfg.Storage.ShowDriveWarnings;
            _minimumFreeSpacePercent = ClampInt(cfg.Storage.MinFreeSpacePercent, 0, 95, 10);
            RefreshRsyncStatusHint();

            LoadCredentialProfiles(cfg);
            LoadDestinations(cfg);
            RefreshLegacyVisibility();

            // FIX: use Theme instead of ThemeName
            _selectedTheme      = DisplayThemeOption(cfg.Appearance.Theme ?? ThemeSystem);
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
            _crashReportAssistanceEnabled = cfg.Advanced.CrashReportAssistanceEnabled;
            _checkForUpdatesOnStartup  = cfg.Advanced.CheckUpdates;
            _updateCheckIntervalMinutes = ClampInt(cfg.Advanced.UpdateCheckIntervalMinutes, 15, 10080, 120);
            _betaChannelEnabled         = cfg.Advanced.BetaChannelEnabled;
            _enableMaintenanceWindow    = cfg.Advanced.Maintenance.Enabled;
            _maintenanceWindowStart     = NormalizeTimeOfDay(cfg.Advanced.Maintenance.WindowStart, DefaultMaintenanceStart);
            _maintenanceWindowEnd       = NormalizeTimeOfDay(cfg.Advanced.Maintenance.WindowEnd, DefaultMaintenanceEnd);
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

                SaveStatus = L("Settings.Status.Loaded", "Settings loaded");

                // Explicit notifications are more reliable here than a blanket null refresh,
                // especially when the cached Settings view is reloaded after startup.
                NotifyLoadedSettingsChanged();
            }
            finally
            {
                _isReloadingFromConfig = false;
            }
        }

        private void LoadCredentialProfiles(AppConfig cfg)
        {
            foreach (NetworkCredentialViewModel? cred in CredentialProfiles.ToList())
            {
                cred.PropertyChanged -= OnNestedPropertyChanged;
            }

            CredentialProfiles.Clear();
            foreach (NetworkCredentialProfile cred in cfg.Network.Credentials ?? [])
            {
                CredentialProfiles.Add(CreateCredentialViewModel(cred));
            }
        }

        private NetworkCredentialViewModel CreateCredentialViewModel(NetworkCredentialProfile cred)
        {
            string keyRef = CredentialVault.EnsureKeyRef(cred.KeyRef, cred.Name);
            return new NetworkCredentialViewModel
            {
                Name = cred.Name,
                Username = cred.Username,
                Domain = cred.Domain ?? string.Empty,
                KeyRef = keyRef,
                UseKeychain = cred.UseKeychain,
                // Loading Settings must not unlock every native credential. A blank
                // field means "keep the enrolled secret"; plaintext is present only
                // for legacy/fallback profiles and still needs to remain editable.
                Password = cred.Password ?? string.Empty
            };
        }

        private void LoadDestinations(AppConfig cfg)
        {
            foreach (BackupDestinationViewModel? dest in Destinations.ToList())
            {
                dest.PropertyChanged -= OnNestedPropertyChanged;
            }

            Destinations.Clear();
            if (cfg.Backups.Destinations?.Count > 0)
            {
                foreach (BackupDestination dest in cfg.Backups.Destinations)
                {
                    Destinations.Add(CreateDestinationViewModel(dest));
                }
                return;
            }

            AddLegacyPrimaryDestination();
        }

        private BackupDestinationViewModel CreateDestinationViewModel(BackupDestination dest)
        {
            var vm = new BackupDestinationViewModel
            {
                Alias = dest.Alias ?? string.Empty,
                Path = dest.Path,
                Active = dest.Active,
                AutoMount = dest.AutoMount,
                AutoUnmount = dest.AutoUnmount,
                PreMounted = dest.PreMounted,
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
            return vm;
        }

        private void AddLegacyPrimaryDestination()
        {
            if (string.IsNullOrWhiteSpace(_backupLocationPath))
                return;

            Destinations.Add(new BackupDestinationViewModel
            {
                Alias = "Primary",
                Path = _backupLocationPath,
                Active = true,
                PreMounted = true,
                AutoMount = false,
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

            SyncDestinationCredentialSelections();

            // Snapshot UI state to avoid cross-thread collection access during background work.
            var destinationSnapshot = CreateDestinationSnapshots();
            var credentialSnapshot = CreateCredentialSnapshots();

            AppConfig cfg = _configStore.Load();

            // Reload latest disabled list to avoid clobbering project-level auto-backup toggles.
            _autoBackupDisabledProjects = cfg.Backups.AutoBackupDisabledProjects ?? [];

            cfg.ProjectsRoot      = ResolveProjectsRootForSave(ProjectsRootPath, cfg.ProjectsRoot);
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
            bool preserveExistingDestinations =
                UseAdvancedDestinations &&
                destinationSnapshot.Count == 0 &&
                cfg.Backups.Destinations is { Count: > 0 };
            string? fallbackRoot = UseAdvancedDestinations
                ? (destinationSnapshot.FirstOrDefault(d => d.Active)?.Path ?? destinationSnapshot.FirstOrDefault()?.Path)
                : BackupLocationPath;
            string? nextBackupRoot = ResolveBackupRootForSave(fallbackRoot, cfg.Backups.BackupRoot ?? cfg.Backups.Location);
            List<BackupDestination> nextDestinations = preserveExistingDestinations
                ? [.. cfg.Backups.Destinations!]
                : [.. destinationSnapshot.Select(d => new BackupDestination
            {
                Alias          = d.Alias,
                Path           = d.Path ?? string.Empty,
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
            })];
            bool destinationSettingsChanged =
                cfg.Backups.UseAdvancedDestinations != UseAdvancedDestinations ||
                !string.Equals(cfg.Backups.BackupRoot ?? string.Empty, nextBackupRoot ?? string.Empty, StringComparison.Ordinal) ||
                !BackupDestinationsEqual(cfg.Backups.Destinations, nextDestinations);

            cfg.Backups.UseAdvancedDestinations = UseAdvancedDestinations;
            cfg.Backups.BackupRoot = nextBackupRoot;
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
            cfg.Backups.QuietHoursStart             = NormalizeTimeOfDay(QuietHoursStart, DefaultQuietHoursStart);
            cfg.Backups.QuietHoursEnd               = NormalizeTimeOfDay(QuietHoursEnd, DefaultQuietHoursEnd);
            cfg.Backups.Encryption.Enabled          = BackupEncryptionEnabled;
            cfg.Backups.Encryption.AllowSessionFallback = BackupEncryptionAllowSessionFallback;
            cfg.Backups.Encryption.OpenUnlockTimeoutMinutes = ClampInt(BackupEncryptionOpenUnlockTimeoutMinutes, 1, 240, 10);
            cfg.Backups.Encryption.KeyRef = string.IsNullOrWhiteSpace(_backupEncryptionKeyRef)
                ? string.Empty
                : _backupEncryptionKeyRef;
            cfg.Backups.Destinations                = nextDestinations;

            cfg.Storage.PreferExternalDrives = PreferExternalDrives;
            cfg.Storage.ShowDriveWarnings    = ShowDriveHealthWarnings;
            cfg.Storage.MinFreeSpacePercent  = MinimumFreeSpacePercent;

            var credentialSave = await SaveCredentialSnapshotsAsync(credentialSnapshot);

            cfg.Network.Credentials = credentialSave.savedCreds;

            cfg.Appearance.Theme              = NormalizeThemeOption(SelectedTheme);
            cfg.Appearance.CompactLayout      = UseCompactLayout;
            cfg.Appearance.ShowProjectAvatars = ShowProjectAvatars;
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
            cfg.Advanced.CrashReportAssistanceEnabled = CrashReportAssistanceEnabled;
            cfg.Advanced.CheckUpdates        = CheckForUpdatesOnStartup;
            cfg.Advanced.UpdateCheckIntervalMinutes = ClampInt(UpdateCheckIntervalMinutes, 15, 10080, 120);
            cfg.Advanced.BetaChannelEnabled  = BetaChannelEnabled;
            cfg.Advanced.Language            = SelectedLanguageCode;
            cfg.Advanced.Maintenance.Enabled = EnableMaintenanceWindow;
            cfg.Advanced.Maintenance.WindowStart = NormalizeTimeOfDay(MaintenanceWindowStart, DefaultMaintenanceStart);
            cfg.Advanced.Maintenance.WindowEnd = NormalizeTimeOfDay(MaintenanceWindowEnd, DefaultMaintenanceEnd);
            cfg.Advanced.Maintenance.RunConsistencyScan = MaintenanceRunConsistencyScan;
            cfg.Advanced.Maintenance.RunRepairDryRun = MaintenanceRunRepairDryRun;
            cfg.Advanced.Maintenance.RunMetadataRefresh = MaintenanceRunMetadataRefresh;

            if (_lastLaunchOnLoginApplied != _launchOnLogin)
            {
                _lastLaunchOnLoginApplied = _launchOnLogin;
                bool launchOnLogin = _launchOnLogin;
                _ = Task.Run(() => AutoStartService.SetLaunchOnLogin(launchOnLogin));
            }

            await _configStore.SaveAsync(cfg);
            if (destinationSettingsChanged)
            {
                DestinationSettingsSaved?.Invoke();
            }

            SaveStatus = string.Format(
                CultureInfo.CurrentCulture,
                credentialSave.hadPlaintextFallback
                    ? L("Settings.Status.SavedFallback", "Saved (with credential fallback) at {0}")
                    : L("Settings.Status.Saved", "Saved at {0}"),
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
        }

        private void SyncDestinationCredentialSelections()
        {
            foreach (BackupDestinationViewModel dest in Destinations)
            {
                SyncDestinationCredentialSelection(dest);
            }
        }

        private void SyncDestinationCredentialSelection(BackupDestinationViewModel dest)
        {
            if (dest.SelectedCredential is not null)
            {
                dest.CredentialName = dest.SelectedCredential.Name;
                return;
            }

            if (!string.IsNullOrWhiteSpace(dest.CredentialName))
            {
                dest.SelectedCredential = CredentialProfiles.FirstOrDefault(c =>
                    c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));
            }
        }

        private List<DestinationSnapshot> CreateDestinationSnapshots()
        {
            return [.. Destinations.Select(d => new DestinationSnapshot(
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
                QuotaWarningPercent: ClampInt(d.QuotaWarningPercent, 50, 99, 85)))];
        }

        private List<CredentialSnapshot> CreateCredentialSnapshots()
        {
            return [.. CredentialProfiles.Select(c => new CredentialSnapshot(
                Name: c.Name,
                Username: c.Username,
                Domain: c.Domain,
                KeyRef: c.KeyRef,
                UseKeychain: c.UseKeychain,
                Password: c.Password))];
        }

        private Task<(List<NetworkCredentialProfile> savedCreds, bool hadPlaintextFallback)> SaveCredentialSnapshotsAsync(IReadOnlyList<CredentialSnapshot> credentialSnapshot)
        {
            return Task.Run(() =>
            {
                var savedCreds = new List<NetworkCredentialProfile>();
                bool hadPlaintextFallback = false;
                foreach (CredentialSnapshot credential in credentialSnapshot)
                {
                    CredentialSaveResult saved = SaveCredentialSnapshot(credential);
                    savedCreds.Add(saved.Profile);
                    hadPlaintextFallback |= saved.HadPlaintextFallback;
                }

                return (savedCreds, hadPlaintextFallback);
            });
        }

        private CredentialSaveResult SaveCredentialSnapshot(CredentialSnapshot credential)
        {
            string keyRef = CredentialVault.EnsureKeyRef(credential.KeyRef, credential.Name);
            string? secret = string.IsNullOrWhiteSpace(credential.Password)
                ? null
                : credential.Password;
            bool persistPlaintext = TrySaveCredentialSecret(credential, keyRef, secret);

            var profile = new NetworkCredentialProfile
            {
                Name = credential.Name,
                Username = credential.Username,
                Domain = credential.Domain,
                KeyRef = keyRef,
                UseKeychain = credential.UseKeychain,
                Password = persistPlaintext ? secret : string.Empty
            };

            return new CredentialSaveResult(profile, persistPlaintext);
        }

        private bool TrySaveCredentialSecret(CredentialSnapshot credential, string keyRef, string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret))
                return false;

            try
            {
                _credentialVault.SaveSecret(keyRef, credential.Username, secret, credential.UseKeychain);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private bool ValidateDestinations(bool notifyOnError)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BackupDestinationViewModel dest in Destinations)
            {
                string? error = GetDestinationValidationError(dest, aliases);
                if (error is not null)
                {
                    if (notifyOnError)
                        ShowDestinationValidationError(error);
                    return false;
                }
            }

            return true;
        }

        private string? GetDestinationValidationError(BackupDestinationViewModel dest, HashSet<string> aliases)
        {
            if (string.IsNullOrWhiteSpace(dest.Path))
                return L("Settings.Destinations.ValidationPathRequired", "Destination path is required.");

            if (!string.IsNullOrWhiteSpace(dest.Alias) && !aliases.Add(dest.Alias))
                return string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Destinations.ValidationDuplicateAlias", "Duplicate destination alias '{0}'."),
                    dest.Alias);

            return dest.SoftQuotaGb < 0
                ? L("Settings.Destinations.ValidationQuotaNonNegative", "Destination quota must be 0 GB or higher.")
                : null;
        }

        private void ShowDestinationValidationError(string message)
        {
            SaveStatus = message;
            GlobalNotificationCenter.Instance.Show(
                SaveStatus,
                NotificationSeverity.Error,
                L("Settings.Destinations.ValidationTitle", "Destination validation"));
        }

        private static bool BackupDestinationsEqual(IReadOnlyList<BackupDestination>? current, IReadOnlyList<BackupDestination> next)
        {
            current ??= [];
            if (current.Count != next.Count)
                return false;

            for (int i = 0; i < current.Count; i++)
            {
                BackupDestination left = current[i];
                BackupDestination right = next[i];
                if (!string.Equals(left.Alias ?? string.Empty, right.Alias ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(left.Path ?? string.Empty, right.Path ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(left.CredentialName ?? string.Empty, right.CredentialName ?? string.Empty, StringComparison.Ordinal) ||
                    left.Active != right.Active ||
                    left.AutoMount != right.AutoMount ||
                    left.AutoUnmount != right.AutoUnmount ||
                    left.PreMounted != right.PreMounted ||
                    left.EnableMetadataSync != right.EnableMetadataSync ||
                    left.AutoImportMetadata != right.AutoImportMetadata ||
                    left.ForceMetadataBackfill != right.ForceMetadataBackfill ||
                    left.RetryMaxAttempts != right.RetryMaxAttempts ||
                    left.RetryBackoffSeconds != right.RetryBackoffSeconds ||
                    left.EnableCheckpointResume != right.EnableCheckpointResume ||
                    left.SoftQuotaBytes != right.SoftQuotaBytes ||
                    left.QuotaWarningPercent != right.QuotaWarningPercent)
                {
                    return false;
                }
            }

            return true;
        }

        internal static string ResolveProjectsRootForSave(string? requestedRoot, string? persistedRoot)
        {
            if (!string.IsNullOrWhiteSpace(requestedRoot))
                return requestedRoot.Trim();

            return string.IsNullOrWhiteSpace(persistedRoot)
                ? string.Empty
                : persistedRoot.Trim();
        }

        internal static string? ResolveBackupRootForSave(string? requestedRoot, string? persistedRoot)
        {
            if (!string.IsNullOrWhiteSpace(requestedRoot))
                return requestedRoot.Trim();

            return string.IsNullOrWhiteSpace(persistedRoot)
                ? null
                : persistedRoot.Trim();
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
                NetworkCredentialViewModel? match = CredentialProfiles.FirstOrDefault(c =>
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
            _ = DetachedTask.RunAsync(TriggerAutoSaveAsync, nameof(TriggerAutoSaveAsync));
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
                SaveStatus = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Status.SaveFailed", "Save failed: {0}"),
                    ex.Message);
                Debug.WriteLine($"[SettingsViewModel] Auto-save failed: {ex}");
            }
            finally
            {
                _isSaving = false;
                if (_savePending)
                {
                    _savePending = false;
                    _ = DetachedTask.RunAsync(TriggerAutoSaveAsync, nameof(TriggerAutoSaveAsync));
                }
            }
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isReloadingFromConfig || _isRefreshingLocalizedOptions || e.PropertyName is null || !ShouldAutoSaveProperty(e.PropertyName))
                return;

            TriggerAutoSave();
        }

        internal static bool ShouldAutoSaveProperty(string propertyName)
            => propertyName switch
            {
                nameof(BackupLocationStatus) => false,
                nameof(RsyncStatusHint) => false,
                nameof(ShowRsyncStatusHint) => false,
                nameof(BackupEncryptionSecretStatus) => false,
                nameof(SaveStatus) => false,
                nameof(BackupIndexRepairStatus) => false,
                nameof(ProjectMetadataConflictStatus) => false,
                nameof(RetentionSimulationStatus) => false,
                nameof(UpdateCheckStatusText) => false,
                nameof(UpdateCheckErrorText) => false,
                nameof(UpdateDiagnosticsText) => false,
                nameof(StartupDiagnosticsText) => false,
                nameof(CheckpointResumeDiagnosticsText) => false,
                _ => true
            };

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

        private static void ResetTagColorRule(TagColorRuleViewModel? rule)
        {
            if (rule is null)
                return;

            (string Background, string Foreground, string Border) defaults = ProjectTagChip.GetDefaultPalette(rule.PreviewTag);
            rule.Background = defaults.Background;
            rule.Foreground = defaults.Foreground;
            rule.Border = defaults.Border;
        }

        private void LoadTagColorRules(AppConfig cfg)
        {
            TagColorRules.Clear();

            IOrderedEnumerable<KeyValuePair<string, TagColorConfig>> rules = cfg.Appearance.TagColors
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, TagColorConfig> entry in rules)
            {
                (string Background, string Foreground, string Border) defaults = ProjectTagChip.GetDefaultPalette(entry.Key);
                TagColorRules.Add(new TagColorRuleViewModel
                {
                    Tag = entry.Key,
                    Background = ProjectTagAppearance.NormalizeHex(entry.Value?.Background, defaults.Background),
                    Foreground = ProjectTagAppearance.NormalizeHex(entry.Value?.Foreground, defaults.Foreground),
                    Border = ProjectTagAppearance.NormalizeHex(entry.Value?.Border, defaults.Border)
                });
            }
        }

        public void RebindDestinationCredentials()
        {
            foreach (BackupDestinationViewModel dest in Destinations)
            {
                if (!string.IsNullOrWhiteSpace(dest.CredentialName))
                {
                    NetworkCredentialViewModel? match = CredentialProfiles.FirstOrDefault(c =>
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

        private string NormalizeThemeOption(string theme)
        {
            int existingIndex = ThemeOptions.IndexOf(theme);
            if (existingIndex >= 0)
            {
                return existingIndex switch
                {
                    1 => ThemeDark,
                    2 => ThemeLight,
                    3 => ThemeCustom,
                    _ => ThemeSystem
                };
            }

            return theme switch
            {
                var value when string.Equals(value, ThemeOptionDarkLabel, StringComparison.OrdinalIgnoreCase) => ThemeDark,
                var value when string.Equals(value, ThemeOptionLightLabel, StringComparison.OrdinalIgnoreCase) => ThemeLight,
                var value when string.Equals(value, ThemeOptionCustomLabel, StringComparison.OrdinalIgnoreCase) => ThemeCustom,
                var value when string.Equals(value, ThemeOptionSystemLabel, StringComparison.OrdinalIgnoreCase) => ThemeSystem,
                ThemeDark          => ThemeDark,
                ThemeLight         => ThemeLight,
                ThemeCustom        => ThemeCustom,
                ThemeFollowSystem => ThemeSystem,
                ThemeSystem        => ThemeSystem,
                _               => ThemeSystem
            };
        }

        private string DisplayThemeOption(string storedTheme)
        {
            return storedTheme switch
            {
                ThemeDark => ThemeOptionDarkLabel,
                ThemeLight => ThemeOptionLightLabel,
                ThemeCustom => ThemeOptionCustomLabel,
                _ => ThemeOptionSystemLabel
            };
        }

        private string NormalizeThemeBaseOption(string value)
        {
            int existingIndex = CustomThemeBaseOptions.IndexOf(value);
            if (existingIndex >= 0)
            {
                return existingIndex == 1 ? ThemeLight : ThemeDark;
            }

            return value switch
            {
                var candidate when string.Equals(candidate, ThemeBaseLightLabel, StringComparison.OrdinalIgnoreCase) => ThemeLight,
                ThemeLight => ThemeLight,
                _ => ThemeDark
            };
        }

        private string DisplayThemeBaseOption(string value)
            => string.Equals(value, ThemeLight, StringComparison.OrdinalIgnoreCase)
                ? ThemeBaseLightLabel
                : ThemeBaseDarkLabel;

        private bool IsLightThemeBaseOption(string value)
            => string.Equals(NormalizeThemeBaseOption(value), ThemeLight, StringComparison.OrdinalIgnoreCase);

        private void RefreshThemeOptions()
        {
            ThemeOptions.Clear();
            ThemeOptions.Add(ThemeOptionSystemLabel);
            ThemeOptions.Add(ThemeOptionDarkLabel);
            ThemeOptions.Add(ThemeOptionLightLabel);
            ThemeOptions.Add(ThemeOptionCustomLabel);
        }

        private void RefreshCustomThemeBaseOptions()
        {
            CustomThemeBaseOptions.Clear();
            CustomThemeBaseOptions.Add(ThemeBaseDarkLabel);
            CustomThemeBaseOptions.Add(ThemeBaseLightLabel);
        }

        private string ThemeOptionSystemLabel => L("Settings.Appearance.ThemeOption.System", ThemeFollowSystem);
        private string ThemeOptionDarkLabel => L("Settings.Appearance.ThemeOption.Dark", ThemeDark);
        private string ThemeOptionLightLabel => L("Settings.Appearance.ThemeOption.Light", ThemeLight);
        private string ThemeOptionCustomLabel => L("Settings.Appearance.ThemeOption.Custom", ThemeCustom);
        private string ThemeBaseDarkLabel => L("Settings.Appearance.ThemeBase.Dark", ThemeDark);
        private string ThemeBaseLightLabel => L("Settings.Appearance.ThemeBase.Light", ThemeLight);

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
                    (bool reachable, bool writable) = ValidateBackupLocation(value, notifyOnSuccess: false);
                    if (_isInitialized)
                    {
                        DestinationTested?.Invoke(new BackupDestination { Alias = "Primary", Path = value, Active = true, PreMounted = true }, reachable, writable, BackupLocationStatus);
                    }
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

        public ObservableCollection<BackupDestinationViewModel> Destinations { get; } = [];
        public ObservableCollection<NetworkCredentialViewModel> CredentialProfiles { get; } = [];
        public IEnumerable<string> CredentialNames => CredentialProfiles.Select(c => c.Name);

        public string SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (_isRefreshingLocalizedOptions)
                    return;

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

        public bool IsCustomThemeSelected => string.Equals(NormalizeThemeOption(SelectedTheme), ThemeCustom, StringComparison.Ordinal);

        public string CustomThemeName
        {
            get => _customThemeName;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value)
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
                if (_isRefreshingLocalizedOptions)
                    return;

                string normalized = DisplayThemeBaseOption(NormalizeThemeBaseOption(value));
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

        public bool CrashReportAssistanceEnabled
        {
            get => _crashReportAssistanceEnabled;
            set => SetField(ref _crashReportAssistanceEnabled, value);
        }

        public bool CheckForUpdatesOnStartup
        {
            get => _checkForUpdatesOnStartup;
            set => SetField(ref _checkForUpdatesOnStartup, value);
        }

        public static bool IsStoreDistribution => DistributionChannelService.Current.IsStore;
        public static bool CanUseSelfUpdate => !IsStoreDistribution;

        public string DistributionChannelLabel => IsStoreDistribution
            ? L("Settings.Advanced.ChannelStore", "Microsoft Store")
            : L("Settings.Advanced.ChannelDirect", "Direct");

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
            DetachedTask.Run(() =>
            {
                AppConfig cfg = _configStore.Load();
                cfg.Advanced.Language = _selectedLanguageCode;
                _configStore.Save(cfg);
            }, nameof(PersistLanguage));
        }

        public void UpdateUpdateCheckStatus(DateTimeOffset? lastCheck, string? errorMessage)
        {
            _lastUpdateCheckAt = lastCheck;
            _lastUpdateCheckError = errorMessage;
            RefreshUpdateCheckStatus();
        }

        public void ReloadUpdateDiagnostics()
        {
            RefreshUpdateDiagnostics(_configStore.GetSnapshot().Advanced.UpdateDiagnostics);
        }

        public void ReloadStartupDiagnostics()
        {
            RefreshStartupDiagnostics(_configStore.GetSnapshot().Advanced.StartupDiagnostics);
        }

        public void ReloadCheckpointResumeDiagnostics()
        {
            RefreshCheckpointResumeDiagnostics(_configStore.GetSnapshot().Advanced.CheckpointResumeTelemetry);
        }

        private void RefreshUpdateCheckStatus()
        {
            if (IsStoreDistribution)
            {
                UpdateCheckStatusText = L("Settings.Advanced.UpdateManagedByStoreStatus", "Updates are managed by Microsoft Store for this build.");
                UpdateCheckErrorText = string.Empty;
                return;
            }

            string neverText = L("Settings.Advanced.UpdateStatusNever", "Never checked");
            string lastTemplate = L("Settings.Advanced.UpdateStatusLast", "Last check: {0}");
            string errorTemplate = L("Settings.Advanced.UpdateStatusError", "Last error: {0}");

            if (_lastUpdateCheckAt.HasValue)
            {
                string formatted = _lastUpdateCheckAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
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

        public void SetStoreManagedUpdatesStatus()
        {
            _lastUpdateCheckAt = null;
            _lastUpdateCheckError = null;
            UpdateDiagnosticsText = L("Settings.Advanced.UpdateManagedByStoreDiagnostics", "GitHub self-update is disabled because this build is managed by Microsoft Store.");
            RefreshUpdateCheckStatus();
        }

        private void RefreshUpdateDiagnostics(UpdateCheckDiagnostics? diagnostics)
        {
            if (IsStoreDistribution)
            {
                UpdateDiagnosticsText = L("Settings.Advanced.UpdateManagedByStoreDiagnostics", "GitHub self-update is disabled because this build is managed by Microsoft Store.");
                return;
            }

            diagnostics ??= new UpdateCheckDiagnostics();
            if (string.IsNullOrWhiteSpace(diagnostics.Decision))
            {
                UpdateDiagnosticsText = L("Settings.Advanced.UpdateDiagnosticsEmpty", "No release-target diagnostics captured yet.");
                return;
            }

            string selectedTag = string.IsNullOrWhiteSpace(diagnostics.SelectedCandidate?.Tag) ? "-" : diagnostics.SelectedCandidate.Tag;
            string selectedTarget = string.IsNullOrWhiteSpace(diagnostics.SelectedCandidate?.TargetCommitish) ? "-" : diagnostics.SelectedCandidate.TargetCommitish;
            string stableTag = string.IsNullOrWhiteSpace(diagnostics.StableCandidate?.Tag) ? "-" : diagnostics.StableCandidate.Tag;
            string betaTag = string.IsNullOrWhiteSpace(diagnostics.BetaCandidate?.Tag) ? "-" : diagnostics.BetaCandidate.Tag;

            string summary = string.Format(
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

            UpdateDiagnosticsText = summary.Replace(" | ", Environment.NewLine, StringComparison.Ordinal);
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
            if (DateTimeOffset.TryParse(diagnostics.LastCompletedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset completedUtc))
            {
                completedText = completedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            }
            else
            {
                completedText = diagnostics.LastCompletedUtc;
            }

            string phaseSummary = string.Join(
                Environment.NewLine,
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
                phaseSummary).Replace(" | ", Environment.NewLine, StringComparison.Ordinal);
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

            string updatedText = DateTimeOffset.TryParse(diagnostics.LastUpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset updatedUtc)
                ? updatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : diagnostics.LastUpdatedUtc;

            string projectText = string.IsNullOrWhiteSpace(diagnostics.LastProjectName)
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
                    : diagnostics.LastMessage).Replace(" | ", Environment.NewLine, StringComparison.Ordinal);
        }

        private void RefreshRsyncStatusHint()
        {
            if (!OperatingSystem.IsMacOS())
            {
                ShowRsyncStatusHint = false;
                RsyncStatusHint = string.Empty;
                return;
            }

            string? rsyncPath = TryGetBundledRsyncPath() ?? TryFindRsyncOnPath();
            if (string.IsNullOrWhiteSpace(rsyncPath))
            {
                ShowRsyncStatusHint = true;
                RsyncStatusHint = L(
                    "Settings.Backups.RsyncMissingHint",
                    "rsync not found. VaultSync will fall back to the built-in copy method. Reinstall the app or install rsync to restore delta sync."
                );
                return;
            }

            Version? version = TryGetRsyncVersion(rsyncPath);
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
            string value = _localizationService.GetString(key);
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
            if (TryParseTimeOfDay(value, out TimeSpan parsed))
            {
                return $"{parsed.Hours:00}:{parsed.Minutes:00}";
            }

            return fallback;
        }

        private static string? TryFindRsyncOnPath()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, RsyncExecutableName);
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
                string baseDir = AppContext.BaseDirectory;
                Architecture arch = RuntimeInformation.OSArchitecture;
                var candidates = new List<string>();
                if (arch == Architecture.Arm64)
                {
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "arm64", "bin", RsyncExecutableName));
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "arm64", RsyncExecutableName));
                }
                else if (arch == Architecture.X64)
                {
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "x64", "bin", RsyncExecutableName));
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "x64", RsyncExecutableName));
                }
                else
                {
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "arm64", "bin", RsyncExecutableName));
                    candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "x64", "bin", RsyncExecutableName));
                }

                candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, RsyncExecutableName));
                candidates.Add(Path.Combine(baseDir, ToolsDirectoryName, RsyncExecutableName, "bin", RsyncExecutableName));

                foreach (string candidate in candidates)
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

                string output = proc.StandardOutput.ReadToEnd();
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

            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                return null;

            string[] tokens = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? versionToken = tokens.FirstOrDefault(t => t.Any(char.IsDigit) && t.Contains('.'));
            return Version.TryParse(versionToken, out Version? parsed) ? parsed : null;
        }

        private static void ConfigureMacLibraryPath(ProcessStartInfo psi, string rsyncPath)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            string? directory = Path.GetDirectoryName(rsyncPath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            string libDir = Path.GetFullPath(Path.Combine(directory, "..", "lib"));
            if (!Directory.Exists(libDir))
                return;

            string existing = psi.Environment.TryGetValue("DYLD_LIBRARY_PATH", out string? current)
                ? current ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_LIBRARY_PATH"] = PrependPathEntry(existing, libDir);

            string fallback = psi.Environment.TryGetValue("DYLD_FALLBACK_LIBRARY_PATH", out string? fallbackCurrent)
                ? fallbackCurrent ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = PrependPathEntry(fallback, libDir);
        }

        private static string PrependPathEntry(string existing, string entry)
        {
            if (string.IsNullOrWhiteSpace(existing))
                return entry;

            string[] parts = existing.Split(':', StringSplitOptions.RemoveEmptyEntries);
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
        public ICommand TestCrashReportCommand { get; }
        public ICommand CheckUpdatesNowCommand { get; }
        public ICommand OpenMicrosoftStoreCommand { get; }
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
            $"{NormalizeTimeOfDay(QuietHoursStart, DefaultQuietHoursStart)} -> {NormalizeTimeOfDay(QuietHoursEnd, DefaultQuietHoursEnd)}";
        public string MaintenanceWindowLabel => L("Settings.Advanced.MaintenanceWindow", "Maintenance window");
        public string MaintenanceWindowDescription => L("Settings.Advanced.MaintenanceWindowDescription", "Run optional health and repair checks during this time window.");
        public string MaintenanceWindowStartLabel => L("Settings.Advanced.MaintenanceWindowStart", "Start (HH:mm)");
        public string MaintenanceWindowEndLabel => L("Settings.Advanced.MaintenanceWindowEnd", "End (HH:mm)");
        public string MaintenanceWindowPreviewLabel => L("Settings.Advanced.MaintenanceWindowPreview", "Active window");
        public string MaintenanceWindowPreview =>
            $"{NormalizeTimeOfDay(MaintenanceWindowStart, DefaultMaintenanceStart)} -> {NormalizeTimeOfDay(MaintenanceWindowEnd, DefaultMaintenanceEnd)}";
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
                    MissingSecretStatusKey,
                    MissingSecretStatusFallback);
                return;
            }

            try
            {
                _backupEncryptionKeyRef = _backupEncryptionSecretService.EnsureSecretRef(
                    _backupEncryptionKeyRef,
                    "backup-encryption-global");

                EncryptionSecretStorageMode storageMode = _backupEncryptionSecretService.SaveSecret(
                    _backupEncryptionKeyRef,
                    BackupEncryptionCredentialIdentity.AccountName,
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
                if (ex.InnerException?.Message == "LINUX_SECRET_TOOL_MISSING")
                {
                    BackupEncryptionSecretStatus = L("Projects.Encryption.LinuxSecretToolMissing",
                        "Linux secret storage is unavailable. Ensure 'libsecret' is installed and your keyring service is running.");
                }
                else
                {
                    BackupEncryptionSecretStatus = L("Settings.Encryption.SecretStatusSaveFailed",
                        "Failed to store encryption password.");
                }

                GlobalNotificationCenter.Instance.Show(
                    $"{BackupEncryptionSecretStatus} {ex.Message}",
                    NotificationSeverity.Error,
                    L("Settings.Encryption.Title", "Backup encryption"));
            }
        }

        private void ClearBackupEncryptionPassword()
        {
            _backupEncryptionSecretService.DeleteSecret(_backupEncryptionKeyRef, BackupEncryptionCredentialIdentity.AccountName);
            BackupEncryptionHasSecret = false;
            BackupEncryptionPasswordInput = string.Empty;
            BackupEncryptionSecretStatus = L(
                MissingSecretStatusKey,
                MissingSecretStatusFallback);
            TriggerAutoSave();
        }

        private void BrowseProjectsRoot()
        {
            _ = DetachedTask.RunAsync(BrowseProjectsRootAsync, nameof(BrowseProjectsRootAsync));
        }

        private async Task BrowseProjectsRootAsync()
        {
            IStorageProvider? storageProvider = GetStorageProvider();
            if (storageProvider is null || !storageProvider.CanPickFolder)
                return;

            IStorageFolder? startLocation = await ResolveFolderPickerStartLocationAsync(
                storageProvider,
                ProjectsRootPath);

            IReadOnlyList<IStorageFolder> folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose projects root",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation
            });

            IStorageFolder? folder = folders is { Count: > 0 } ? folders[0] : null;
            string? path = folder?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                AppConfig config = await Task.Run(_configStore.Load);
                config.ProjectsRoot = path;
                await _configStore.SaveAsync(config);
            }
            catch
            {
                // Best effort
            }

            ProjectsRootPath = path;
        }

        private void BrowseBackupLocation()
        {
            _ = DetachedTask.RunAsync(BrowseBackupLocationAsync, nameof(BrowseBackupLocationAsync));
        }

        private async Task BrowseBackupLocationAsync()
        {
            IStorageProvider? storageProvider = GetStorageProvider();
            if (storageProvider is null || !storageProvider.CanPickFolder)
                return;

            IStorageFolder? startLocation = await ResolveFolderPickerStartLocationAsync(
                storageProvider,
                BackupLocationPath);

            IReadOnlyList<IStorageFolder> folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose backup location",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation
            });

            IStorageFolder? folder = folders is { Count: > 0 } ? folders[0] : null;
            string? path = folder?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            BackupLocationPath = path;
            ValidateBackupLocation(path);
        }

        private void BrowseDestination(BackupDestinationViewModel? dest)
        {
            _ = DetachedTask.RunAsync(() => BrowseDestinationAsync(dest), nameof(BrowseDestinationAsync));
        }

        private async Task BrowseDestinationAsync(BackupDestinationViewModel? dest)
        {
            if (dest is null)
                return;

            IStorageProvider? storageProvider = GetStorageProvider();
            if (storageProvider is null || !storageProvider.CanPickFolder)
                return;

            IStorageFolder? startLocation = await ResolveFolderPickerStartLocationAsync(
                storageProvider,
                dest.Path);

            IReadOnlyList<IStorageFolder> folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose destination folder",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation
            });

            IStorageFolder? folder = folders is { Count: > 0 } ? folders[0] : null;
            string? path = folder?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            dest.Path = path;
            RefreshLegacyVisibility();
        }

        private void ResetToDefaults()
        {
            _configStore.Save(new AppConfig());
            LoadFromConfig();
        }

        private void ClearLocalCache()
        {
            int removed = 0;
            int failed = 0;

            CacheDeleteResult TryDeleteDir(string path)
            {
                if (!Directory.Exists(path))
                    return CacheDeleteResult.NotFound;

                try
                {
                    Directory.Delete(path, recursive: true);
                    return CacheDeleteResult.Removed;
                }
                catch
                {
                    return CacheDeleteResult.Failed;
                }
            }

            CacheDeleteResult TryDeleteFile(string path)
            {
                if (!File.Exists(path))
                    return CacheDeleteResult.NotFound;

                try
                {
                    File.Delete(path);
                    return CacheDeleteResult.Removed;
                }
                catch
                {
                    return CacheDeleteResult.Failed;
                }
            }

            void Count(CacheDeleteResult result)
            {
                switch (result)
                {
                    case CacheDeleteResult.Removed:
                        removed++;
                        break;
                    case CacheDeleteResult.Failed:
                        failed++;
                        break;
                    case CacheDeleteResult.NotFound:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(result), result, null);
                }
            }

            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync");

            Count(TryDeleteDir(Path.Combine(localRoot, "logs")));
            Count(TryDeleteDir(Path.Combine(localRoot, "crash")));
            Count(TryDeleteFile(Path.Combine(localRoot, "avatars.json")));
            Count(TryDeleteFile(Path.Combine(localRoot, "avatar-colors.json")));

            string tempRoot = Path.GetTempPath();
            Count(TryDeleteDir(Path.Combine(tempRoot, "vaultsync-meta-import")));
            Count(TryDeleteDir(Path.Combine(tempRoot, "vaultsync-telemetry-export")));
            Count(TryDeleteDir(Path.Combine(tempRoot, "VaultSync")));

            if (removed == 0 && failed == 0)
            {
                SaveStatus = L("Settings.Status.CacheNothingToClear", "No local cache data to clear.");
                return;
            }

            SaveStatus = string.Format(
                CultureInfo.CurrentCulture,
                failed == 0
                    ? L("Settings.Status.CacheCleared", "Local cache cleared ({0} item(s)).")
                    : L("Settings.Status.CacheClearedWithErrors", "Cache cleared with {0} error(s)."),
                failed == 0 ? removed : failed);
        }

        private enum CacheDeleteResult
        {
            NotFound,
            Removed,
            Failed
        }

        private void TestBackupLocation()
        {
            if (string.IsNullOrWhiteSpace(BackupLocationPath))
                return;

            ValidateBackupLocation(BackupLocationPath, notifyOnSuccess: false);
        }

        private void TestDestination(BackupDestinationViewModel? dest)
        {
            _ = DetachedTask.RunAsync(() => TestDestinationAsync(dest), nameof(TestDestinationAsync));
        }

        private async Task TestDestinationAsync(BackupDestinationViewModel? dest)
        {
            if (dest is null)
                return;

            string path = dest.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                string emptyText = LocalizationProvider.Service?.GetString("Destinations.Test.EmptyPath") ?? "Destination path is empty.";
                SaveStatus = emptyText;
                dest.LastTestStatus   = emptyText;
                dest.LastTestSeverity = "Error";
                return;
            }

            string display = dest.DisplayName;
            AppConfig cfg = await Task.Run(_configStore.Load);
            NetworkCredentialProfile? profile = string.IsNullOrWhiteSpace(dest.CredentialName)
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
                DestinationResolution resolution = _networkMountService.PrepareDestination(destModel, profile);
                if (!resolution.IsSuccess)
                {
                    return (resolution, success: false, readable: false, writable: false, message: resolution.Message);
                }

                try
                {
                    string effectivePath = resolution.EffectivePath;
                    Directory.CreateDirectory(effectivePath);

                    bool writable = TryWriteProbeFile(effectivePath);
                    string message = writable
                        ? (LocalizationProvider.Service?.GetString("Destinations.Test.Reachable") ?? "Reachable")
                        : (LocalizationProvider.Service?.GetString("Destinations.Test.ReadOnly") ?? "Read-only");

                    return (resolution, success: true, readable: true, writable, message);
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
            DestinationTested?.Invoke(destModel, result.success, result.writable, result.message);

            if (!result.success)
            {
                SaveStatus = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Settings.Destinations.TestFailed", "Destination '{0}' failed: {1}"),
                    display,
                    result.message);
                dest.LastTestStatus   = result.message;
                dest.LastTestSeverity = "Error";
                string actionLabel = LocalizationProvider.Service?.GetString("Logs.CopySnippet") ?? "Copy log snippet";
                ICommand actionCommand = CreateCopyLogSnippetCommand($"Destination test failed for '{display}'.");
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Error,
                    LocalizationProvider.Service?.GetString("Destinations.Test.Title") ?? "Destination test",
                    actionLabel: actionLabel,
                    actionCommand: actionCommand);
                return;
            }

            SaveStatus = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Destinations.TestReachable", "Destination '{0}' is reachable."),
                display);
            dest.LastTestStatus   = result.message;
            dest.LastTestSeverity = result.writable ? "Info" : "Warning";
            GlobalNotificationCenter.Instance.Show(
                SaveStatus,
                result.writable ? NotificationSeverity.Info : NotificationSeverity.Warning,
                LocalizationProvider.Service?.GetString("Destinations.Test.Title") ?? "Destination test");
        }

        private static bool TryWriteProbeFile(string effectivePath)
        {
            string testFile = Path.Combine(effectivePath, $".vaultsync_destination_test_{Guid.NewGuid():N}");
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

        private static ICommand CreateCopyLogSnippetCommand(string contextLabel)
        {
            return new RelayCommand(async _ =>
            {
                string? snippet = Services.LogConsoleProvider.Service?.GetRecentSnippet(30, contextLabel);
                if (string.IsNullOrWhiteSpace(snippet))
                    return;

                await ClipboardHelper.TryCopyAsync(snippet);
            });
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

        private (bool isReachable, bool isWritable) ValidateBackupLocation(string path, bool notifyOnSuccess = true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                BackupLocationStatus = string.Empty;
                return (false, false);
            }

            if (!TryEnsureBackupDirectory(path, notifyOnSuccess))
                return (false, false);

            if (!ValidateBackupLocationWritable(path, notifyOnSuccess))
                return (true, false);

            CheckBackupLocationFreeSpace(path, notifyOnSuccess);
            return (true, true);
        }

        private bool TryEnsureBackupDirectory(string path, bool notifyOnError)
        {
            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                BackupLocationStatus = "Not accessible";
                if (notifyOnError)
                    ShowBackupLocationNotification($"Backup location not accessible: {ex.Message}", NotificationSeverity.Error);
                return false;
            }
        }

        private bool ValidateBackupLocationWritable(string path, bool notifyOnError)
        {
            if (TryWriteProbeFile(path))
                return true;

            BackupLocationStatus = "Not writable";
            if (notifyOnError)
                ShowBackupLocationNotification("Backup location is not writable.", NotificationSeverity.Error);
            return false;
        }

        private void CheckBackupLocationFreeSpace(string path, bool notifyOnSuccess)
        {
            try
            {
                var drive = new DriveInfo(path);
                if (drive.IsReady && drive.TotalSize > 0)
                    ApplyBackupLocationFreeSpaceStatus(path, drive, notifyOnSuccess);
            }
            catch (Exception)
            {
                BackupLocationStatus = "OK";
                // Ignore disk space failures; path/write checks already passed.
            }
        }

        private void ApplyBackupLocationFreeSpaceStatus(string path, DriveInfo drive, bool notifyOnSuccess)
        {
            double freePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100d;
            if (freePercent < MinimumFreeSpacePercent)
            {
                BackupLocationStatus = $"Low space ({freePercent:0.#}% free)";
                ShowBackupLocationNotification(
                    $"Free space below threshold ({freePercent:0.#}% available, threshold {MinimumFreeSpacePercent}%).",
                    NotificationSeverity.Warning);
                return;
            }

            BackupLocationStatus = "OK";
            if (notifyOnSuccess)
                ShowBackupLocationNotification($"Backup location set: {path}", NotificationSeverity.Info);
        }

        private static void ShowBackupLocationNotification(string message, NotificationSeverity severity)
        {
            GlobalNotificationCenter.Instance.Show(message, severity, "Backup location");
        }

        private void ForgetAllProjects()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    // Dev helper: reset the VaultSync SQLite DB to a "fresh install" state
                    // without touching any real project files or backup folders on disk.
                    AppConfig cfg  = _configStore.Load();
                    var repo = _repositoryFactory.Create(cfg);

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
            Application? app = Application.Current;
            if (app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow?.StorageProvider;
            }

            return null;
        }

        private static async Task<IStorageFolder?> ResolveFolderPickerStartLocationAsync(
            IStorageProvider storageProvider,
            string? preferredPath)
        {
            foreach (Uri candidate in BuildFolderPickerStartCandidates(preferredPath))
            {
                try
                {
                    IStorageFolder? folder = await storageProvider.TryGetFolderFromPathAsync(candidate);
                    if (folder is not null)
                        return folder;
                }
                catch
                {
                    // A stale, disconnected, or permission-restricted path should not block the picker.
                }
            }

            foreach (WellKnownFolder fallback in new[] { WellKnownFolder.Documents, WellKnownFolder.Desktop })
            {
                try
                {
                    IStorageFolder? folder = await storageProvider.TryGetWellKnownFolderAsync(fallback);
                    if (folder is not null)
                        return folder;
                }
                catch
                {
                    // Continue to the next cross-platform fallback.
                }
            }

            return null;
        }

        internal static IReadOnlyList<Uri> BuildFolderPickerStartCandidates(
            string? preferredPath,
            string? homePath = null)
        {
            var candidates = new List<Uri>(capacity: 2);
            var seen = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

            AddCandidate(preferredPath);
            AddCandidate(homePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return candidates;

            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                try
                {
                    string fullPath = Path.GetFullPath(path.Trim());
                    if (!Directory.Exists(fullPath) || !seen.Add(fullPath))
                        return;

                    candidates.Add(new Uri(fullPath));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore invalid persisted paths and fall back to the user's home folder.
                }
            }
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

            double bytes = valueGb * 1024d * 1024d * 1024d;
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
                    if (File.Exists(target) || Directory.Exists(target))
                        SystemFileLauncher.OpenPath(target);
                    else
                        SystemFileLauncher.OpenUri(target);

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
                string root = AppContext.BaseDirectory;
                string[] candidatePaths = new[]
                {
                    Path.Combine(root, "docs", "HELP.md"),
                    Path.Combine(root, "docs", "wiki", "FAQ.md"),
                    Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "HELP.md")),
                    Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "wiki", "FAQ.md"))
                };

                foreach (string? path in candidatePaths)
                {
                    if (!File.Exists(path))
                        continue;

                    if (TryOpen(path))
                    {
                        SaveStatus = L("Settings.Destinations.OpenHelpSuccess", "Help guide opened.");
                        return;
                    }
                }

                string onlineFallback = "https://github.com/flaviorame/vaultsync/tree/main/docs/wiki";
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
            TelemetryExportResult result = Telemetry.ExportToZip();
            if (!result.Success || string.IsNullOrWhiteSpace(result.ZipPath))
            {
                SaveStatus = result.Message ?? L("Settings.Advanced.TelemetryExportFailed", "Telemetry export failed.");
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Warning,
                    L("Settings.Advanced.TelemetryExportTitle", "Telemetry export"));
                return;
            }

            SaveStatus = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.TelemetryExportedTo", "Telemetry exported to {0}"),
                result.ZipPath);
            GlobalNotificationCenter.Instance.Show(
                L("Settings.Advanced.TelemetryExportReady", "Telemetry export ready. You can share the zip file."),
                NotificationSeverity.Info,
                L("Settings.Advanced.TelemetryExportTitle", "Telemetry export"));

            TryOpenContainingFolder(result.ZipPath);
        }

        private void OpenLogConsole()
        {
            OpenLogConsoleRequested?.Invoke();
        }

        private void CheckUpdatesNow()
        {
            if (IsStoreDistribution)
            {
                SetStoreManagedUpdatesStatus();
                SaveStatus = L("Settings.Advanced.UpdateManagedByStoreStatus", "Updates are managed by Microsoft Store for this build.");
                return;
            }

            UpdateCheckRequested?.Invoke();
        }

        private void OpenMicrosoftStoreListing()
        {
            string target = OperatingSystem.IsWindows()
                ? "ms-windows-store://pdp/?productid=9N9HRX4JCLCP"
                : "https://apps.microsoft.com/detail/9N9HRX4JCLCP";

            try
            {
                SystemFileLauncher.OpenUri(target);
            }
            catch
            {
                try
                {
                    SystemFileLauncher.OpenUri("https://apps.microsoft.com/detail/9N9HRX4JCLCP");
                }
                catch
                {
                    SaveStatus = L("Settings.Advanced.UpdateManagedByStoreOpenFailed", "Could not open the Microsoft Store listing.");
                }
            }
        }

        private void ExportLogConsole()
        {
            LogConsoleService? service = Services.LogConsoleProvider.Service;
            string? path = service?.ExportBuffer();

            if (string.IsNullOrWhiteSpace(path))
            {
                SaveStatus = L("LogConsole.ExportFailed", "Log export failed.");
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Warning,
                    L("LogConsole.ExportTitle", "Log export"));
                return;
            }

            SaveStatus = string.Format(
                CultureInfo.CurrentCulture,
                L("LogConsole.ExportedTo", "Log exported to {0}"),
                path);
            GlobalNotificationCenter.Instance.Show(
                L("LogConsole.ExportReady", "Log export ready. You can share the file."),
                NotificationSeverity.Info,
                L("LogConsole.ExportTitle", "Log export"));
        }

        private void ExportSupportBundle()
        {
            SupportBundleExportResult result = SupportBundleService.Export();
            if (!result.Success || string.IsNullOrWhiteSpace(result.ZipPath))
            {
                SaveStatus = string.IsNullOrWhiteSpace(result.Message)
                    ? L("Settings.Advanced.SupportBundleFailed", "Support bundle export failed.")
                    : result.Message;
                GlobalNotificationCenter.Instance.Show(
                    SaveStatus,
                    NotificationSeverity.Warning,
                    L(SupportBundleTitleKey, SupportBundleTitleFallback));
                return;
            }

            SaveStatus = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.SupportBundleExportedTo", "Support bundle exported to {0}"),
                result.ZipPath);
            GlobalNotificationCenter.Instance.Show(
                L("Settings.Advanced.SupportBundleReady", "Support bundle ready. You can share the zip file."),
                NotificationSeverity.Info,
                L(SupportBundleTitleKey, SupportBundleTitleFallback));

            TryOpenContainingFolder(result.ZipPath);
        }

        private static void TryOpenContainingFolder(string? artifactPath)
        {
            try
            {
                string? folder = Path.GetDirectoryName(artifactPath);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    SystemFileLauncher.OpenPath(folder);
                }
            }
            catch
            {
                // best-effort
            }
        }

        private void ImportSupportBundle()
        {
            _ = DetachedTask.RunAsync(ImportSupportBundleAsync, nameof(ImportSupportBundleAsync));
        }

        private void RunRetentionSimulation()
        {
            _ = DetachedTask.RunAsync(RunRetentionSimulationAsync, nameof(RunRetentionSimulationAsync));
        }

        private async Task RunRetentionSimulationAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsRetentionSimulationBusy = true);
            DiagnosticsLogger.Record("Retention simulation started.");

            try
            {
                BackupRetentionSimulationResult result = await Task.Run(() =>
                {
                    AppConfig cfg = _configStore.Load();
                    var repo = _repositoryFactory.Create(cfg);
                    var service = new BackupRetentionSimulationService(repo);
                    return service.Simulate(ClampInt(cfg.Backups.MaxSnapshotsPerProject, 1, 999, 20));
                }).ConfigureAwait(false);

                string status = result.AffectedProjectCount == 0
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
                string status = string.Format(
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
            _ = DetachedTask.RunAsync(ScanBackupIndexRepairPlanAsync, nameof(ScanBackupIndexRepairPlanAsync));
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

            foreach (ProjectRetentionSimulationProjectResult? project in result.Projects
                         .OrderByDescending(item => item.SelectedDeleteBytes)
                         .ThenByDescending(item => item.DeleteQuota)
                         .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase))
            {
                string line = string.Format(
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

        private static string FormatByteSize(long bytes) =>
            UiFormat.FormatBytes(bytes);

        private async Task ScanBackupIndexRepairPlanAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record("Doctor action started: backup-index-repair scan.");

            try
            {
                BackupIndexRepairPlan plan = await Task.Run(() =>
                {
                    AppConfig cfg = _configStore.Load();
                    var repo = _repositoryFactory.Create(cfg);
                    var service = new BackupIndexRepairService(repo);
                    return service.BuildPlan();
                }).ConfigureAwait(false);

                string status = plan.HasActions
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
                        L(BackupRepairTitleKey, BackupRepairTitleFallback));
                });
                DiagnosticsLogger.Record(
                    $"Doctor action complete: backup-index-repair scan. Actions={plan.Actions.Count}; BlockedBuckets={plan.BlockedIssues.Count}.");
                PersistBackupRepairTelemetry(plan, appliedCount: null, status: status);
            }
            catch (Exception ex)
            {
                string status = string.Format(
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
                        L(BackupRepairTitleKey, BackupRepairTitleFallback));
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
            _ = DetachedTask.RunAsync(ApplyBackupIndexRepairPlanAsync, nameof(ApplyBackupIndexRepairPlanAsync));
        }

        private async Task ApplyBackupIndexRepairPlanAsync()
        {
            BackupIndexRepairPlan? plan = _currentBackupIndexRepairPlan;
            if (plan?.HasActions != true)
                return;

            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record(
                $"Doctor action started: backup-index-repair apply. PlannedActions={plan.Actions.Count}; BlockedBuckets={plan.BlockedIssues.Count}.");

            try
            {
                int applied = await Task.Run(() =>
                {
                    AppConfig cfg = _configStore.Load();
                    var repo = _repositoryFactory.Create(cfg);
                    var service = new BackupIndexRepairService(repo);
                    return service.ApplyPlan(plan);
                }).ConfigureAwait(false);

                string status = string.Format(
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
                        L(BackupRepairTitleKey, BackupRepairTitleFallback));
                });
                DiagnosticsLogger.Record($"Doctor action complete: backup-index-repair apply. Applied={applied}.");
                PersistBackupRepairTelemetry(plan, applied, status);

                await ScanBackupIndexRepairPlanAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string status = string.Format(
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
                        L(BackupRepairTitleKey, BackupRepairTitleFallback));
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
            string exactActions = string.Format(
                CultureInfo.CurrentCulture,
                L("Settings.Advanced.BackupRepairSummaryActions", "{0} exact remap action(s)"),
                plan.Actions.Count);
            string blockedIssues = string.Format(
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

            foreach (BackupIndexRepairBlockedIssue? issue in plan.BlockedIssues.OrderBy(static issue => issue.Code, StringComparer.Ordinal))
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

            foreach (ProjectMetadataConflictRecord? conflict in (conflicts ?? [])
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

            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? parsed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : value;
        }

        private void AcceptProjectMetadataConflict(ProjectMetadataConflictItemViewModel? item)
        {
            if (item is null)
                return;

            _ = DetachedTask.RunAsync(() => AcceptProjectMetadataConflictAsync(item), nameof(AcceptProjectMetadataConflictAsync));
        }

        private async Task AcceptProjectMetadataConflictAsync(ProjectMetadataConflictItemViewModel item)
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record($"Doctor action started: metadata-conflict accept. ProjectId={item.ProjectId}; ExternalId={item.ProjectExternalId}.");

            try
            {
                await Task.Run(() =>
                {
                    AppConfig cfg = _configStore.Load();
                    var repo = _repositoryFactory.Create(cfg);
                    repo.UpdateProjectPreferredDestination(item.ProjectId, EmptyToNull(item.ImportedPreferredDestinationId));
                    repo.UpdateProjectRestoreMode(item.ProjectId, EmptyToNull(item.ImportedRestoreMode));
                    repo.UpdateProjectVerificationPolicy(item.ProjectId, EmptyToNull(item.ImportedVerificationPolicy));
                    repo.UpdateProjectTags(item.ProjectId, EmptyToNull(item.ImportedTags));

                    RemoveProjectMetadataConflictRecord(cfg, item.ProjectId, item.ProjectExternalId);
                    UpdateMetadataConflictTelemetry(cfg, "accept-imported", item.ProjectName, Math.Max(0, cfg.Advanced.ProjectMetadataConflicts.Count));
                    _configStore.Save(cfg);
                }).ConfigureAwait(false);

                string status = string.Format(
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
                        L(MetadataConflictsTitleKey, MetadataConflictsTitleFallback));
                });
                DiagnosticsLogger.Record($"Doctor action complete: metadata-conflict accept. ProjectId={item.ProjectId}.");
            }
            catch (Exception ex)
            {
                string status = string.Format(
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
                        L(MetadataConflictsTitleKey, MetadataConflictsTitleFallback));
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

            _ = DetachedTask.RunAsync(() => KeepLocalProjectMetadataConflictAsync(item), nameof(KeepLocalProjectMetadataConflictAsync));
        }

        private async Task KeepLocalProjectMetadataConflictAsync(ProjectMetadataConflictItemViewModel item)
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBackupIndexRepairBusy = true);
            DiagnosticsLogger.Record($"Doctor action started: metadata-conflict keep-local. ProjectId={item.ProjectId}; ExternalId={item.ProjectExternalId}.");

            try
            {
                await Task.Run(() =>
                {
                    AppConfig cfg = _configStore.Load();
                    RemoveProjectMetadataConflictRecord(cfg, item.ProjectId, item.ProjectExternalId);
                    UpdateMetadataConflictTelemetry(cfg, "keep-local", item.ProjectName, Math.Max(0, cfg.Advanced.ProjectMetadataConflicts.Count));
                    _configStore.Save(cfg);
                }).ConfigureAwait(false);

                string status = string.Format(
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
                        L(MetadataConflictsTitleKey, MetadataConflictsTitleFallback));
                });
                DiagnosticsLogger.Record($"Doctor action complete: metadata-conflict keep-local. ProjectId={item.ProjectId}.");
            }
            catch (Exception ex)
            {
                string status = string.Format(
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
                        L(MetadataConflictsTitleKey, MetadataConflictsTitleFallback));
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
            cfg.Advanced.ProjectMetadataConflicts ??= [];
            ProjectMetadataConflictRecord? existing = cfg.Advanced.ProjectMetadataConflicts.FirstOrDefault(conflict =>
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
                AppConfig cfg = _configStore.Load();
                UpdateMetadataConflictTelemetry(cfg, lastAction, lastResolvedProject, pendingCount);
                _configStore.Save(cfg);
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

        private void PersistBackupRepairTelemetry(BackupIndexRepairPlan? plan, int? appliedCount, string status)
        {
            try
            {
                AppConfig cfg = _configStore.Load();
                cfg.Advanced.BackupRepairTelemetry ??= new BackupRepairTelemetry();
                BackupRepairTelemetry telemetry = cfg.Advanced.BackupRepairTelemetry;
                telemetry.LastScanUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                telemetry.LastStatus = status ?? string.Empty;
                telemetry.PlannedActionCount = plan?.Actions.Count ?? 0;
                telemetry.BlockedIssueBucketCount = plan?.BlockedIssues.Count ?? 0;
                telemetry.PlannedActionCodes = plan?.Actions
                    .Select(static action => action.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static code => code, StringComparer.Ordinal)
                    .ToList() ?? [];
                telemetry.BlockedIssueCodes = plan?.BlockedIssues
                    .Select(static issue => issue.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static code => code, StringComparer.Ordinal)
                    .ToList() ?? [];

                if (appliedCount.HasValue)
                {
                    telemetry.LastApplyUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    telemetry.LastAppliedCount = appliedCount.Value;
                }

                _configStore.Save(cfg);
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
            IStorageProvider? storageProvider = GetStorageProvider();
            if (storageProvider is null)
                return;

            IReadOnlyList<IStorageFile> files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Settings.Advanced.SupportBundleImport", "Import support bundle"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new("Zip archive") { Patterns = ["*.zip"] }
                ]
            });

            IStorageFile? file = files?.FirstOrDefault();
            string? zipPath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return;

            var (success, message) = await Task.Run(() => TryApplySupportBundleSettings(zipPath));
            if (!success)
            {
                SaveStatus = message;
                GlobalNotificationCenter.Instance.Show(
                    message,
                    NotificationSeverity.Warning,
                    L(SupportBundleTitleKey, SupportBundleTitleFallback));
                return;
            }

            SaveStatus = message;
            LoadFromConfig();
            GlobalNotificationCenter.Instance.Show(
                message,
                NotificationSeverity.Info,
                L(SupportBundleTitleKey, SupportBundleTitleFallback));
        }

        private (bool success, string message) TryApplySupportBundleSettings(string zipPath)
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                ZipArchiveEntry? reportEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, "support-report.json", StringComparison.OrdinalIgnoreCase));
                if (reportEntry is null)
                {
                    return (false, L("Settings.Advanced.SupportBundleImportMissingReport", "Support bundle is missing support-report.json."));
                }

                using Stream stream = reportEntry.Open();
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("redactedConfig", out JsonElement redactedConfig))
                {
                    return (false, L("Settings.Advanced.SupportBundleImportMissingConfig", "Support bundle does not contain importable settings."));
                }

                AppConfig cfg = _configStore.Load();
                ApplyImportableSettings(redactedConfig, cfg);
                _configStore.Save(cfg);
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
            if (redactedConfig.TryGetProperty("backups", out JsonElement backups))
                ApplyImportableBackupSettings(backups, cfg);

            if (redactedConfig.TryGetProperty("storage", out JsonElement storage))
                ApplyImportableStorageSettings(storage, cfg);

            if (redactedConfig.TryGetProperty("appearance", out JsonElement appearance))
                ApplyImportableAppearanceSettings(appearance, cfg);

            if (redactedConfig.TryGetProperty("notifications", out JsonElement notifications))
                ApplyImportableNotificationSettings(notifications, cfg);

            if (redactedConfig.TryGetProperty("advanced", out JsonElement advanced))
                ApplyImportableAdvancedSettings(advanced, cfg);

            if (redactedConfig.TryGetProperty("behavior", out JsonElement behavior))
                ApplyImportableBehaviorSettings(behavior, cfg);
        }

        private static void ApplyImportableBackupSettings(JsonElement backups, AppConfig cfg)
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
            cfg.Backups.QuietHoursStart = NormalizeTimeOfDay(ReadString(backups, nameof(cfg.Backups.QuietHoursStart), cfg.Backups.QuietHoursStart), cfg.Backups.QuietHoursStart);
            cfg.Backups.QuietHoursEnd = NormalizeTimeOfDay(ReadString(backups, nameof(cfg.Backups.QuietHoursEnd), cfg.Backups.QuietHoursEnd), cfg.Backups.QuietHoursEnd);
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

            if (backups.TryGetProperty("encryption", out JsonElement enc))
                ApplyImportableEncryptionSettings(enc, cfg);
        }

        private static void ApplyImportableEncryptionSettings(JsonElement enc, AppConfig cfg)
        {
            cfg.Backups.Encryption.Enabled = ReadBool(enc, nameof(cfg.Backups.Encryption.Enabled), cfg.Backups.Encryption.Enabled);
            cfg.Backups.Encryption.Algorithm = NormalizeImportedEncryptionAlgorithm(ReadString(enc, nameof(cfg.Backups.Encryption.Algorithm), cfg.Backups.Encryption.Algorithm), cfg.Backups.Encryption.Algorithm);
            cfg.Backups.Encryption.KdfProfile = NormalizeImportedKdfProfile(ReadString(enc, nameof(cfg.Backups.Encryption.KdfProfile), cfg.Backups.Encryption.KdfProfile), cfg.Backups.Encryption.KdfProfile);
            cfg.Backups.Encryption.KdfParamRef = NormalizeImportedKdfParamRef(ReadString(enc, nameof(cfg.Backups.Encryption.KdfParamRef), cfg.Backups.Encryption.KdfParamRef), cfg.Backups.Encryption.KdfParamRef);
            cfg.Backups.Encryption.AllowSessionFallback = ReadBool(enc, nameof(cfg.Backups.Encryption.AllowSessionFallback), cfg.Backups.Encryption.AllowSessionFallback);
            cfg.Backups.Encryption.OpenUnlockTimeoutMinutes = ClampInt(ReadInt(enc, nameof(cfg.Backups.Encryption.OpenUnlockTimeoutMinutes), cfg.Backups.Encryption.OpenUnlockTimeoutMinutes), 1, 1440, 10);
        }

        private static void ApplyImportableStorageSettings(JsonElement storage, AppConfig cfg)
        {
            cfg.Storage.PreferExternalDrives = ReadBool(storage, nameof(cfg.Storage.PreferExternalDrives), cfg.Storage.PreferExternalDrives);
            cfg.Storage.ShowDriveWarnings = ReadBool(storage, nameof(cfg.Storage.ShowDriveWarnings), cfg.Storage.ShowDriveWarnings);
            cfg.Storage.MinFreeSpacePercent = ClampInt(ReadInt(storage, nameof(cfg.Storage.MinFreeSpacePercent), cfg.Storage.MinFreeSpacePercent), 1, 99, 10);
        }

        private static void ApplyImportableAppearanceSettings(JsonElement appearance, AppConfig cfg)
        {
            cfg.Appearance.Theme = NormalizeImportedTheme(ReadString(appearance, nameof(cfg.Appearance.Theme), cfg.Appearance.Theme), cfg.Appearance.Theme);
            cfg.Appearance.CompactLayout = ReadBool(appearance, nameof(cfg.Appearance.CompactLayout), cfg.Appearance.CompactLayout);
            cfg.Appearance.ShowProjectAvatars = ReadBool(appearance, nameof(cfg.Appearance.ShowProjectAvatars), cfg.Appearance.ShowProjectAvatars);

            if (appearance.TryGetProperty(nameof(cfg.Appearance.TagColors), out JsonElement tagColors) && tagColors.ValueKind == JsonValueKind.Object)
                cfg.Appearance.TagColors = ReadImportableTagColors(tagColors);

            if (appearance.TryGetProperty(nameof(cfg.Appearance.CustomTheme), out JsonElement customTheme) && customTheme.ValueKind == JsonValueKind.Object)
                cfg.Appearance.CustomTheme = ReadImportableCustomTheme(customTheme, cfg.Appearance.CustomTheme);
        }

        private static Dictionary<string, TagColorConfig> ReadImportableTagColors(JsonElement tagColors)
        {
            var importedTagColors = new Dictionary<string, TagColorConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in tagColors.EnumerateObject())
                AddImportableTagColor(importedTagColors, property);
            return importedTagColors;
        }

        private static void AddImportableTagColor(Dictionary<string, TagColorConfig> importedTagColors, JsonProperty property)
        {
            string tag = property.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tag) || property.Value.ValueKind != JsonValueKind.Object)
                return;

            (string Background, string Foreground, string Border) defaults = ProjectTagChip.GetDefaultPalette(tag);
            importedTagColors[tag] = new TagColorConfig
            {
                Background = ProjectTagAppearance.NormalizeHex(ReadString(property.Value, nameof(TagColorConfig.Background), defaults.Background), defaults.Background),
                Foreground = ProjectTagAppearance.NormalizeHex(ReadString(property.Value, nameof(TagColorConfig.Foreground), defaults.Foreground), defaults.Foreground),
                Border = ProjectTagAppearance.NormalizeHex(ReadString(property.Value, nameof(TagColorConfig.Border), defaults.Border), defaults.Border)
            };
        }

        private static ThemePaletteConfig ReadImportableCustomTheme(JsonElement customTheme, ThemePaletteConfig fallback)
        {
            return new ThemePaletteConfig
            {
                Name = ReadString(customTheme, nameof(ThemePaletteConfig.Name), fallback.Name),
                BaseTheme = ReadString(customTheme, nameof(ThemePaletteConfig.BaseTheme), fallback.BaseTheme),
                Background = ReadString(customTheme, nameof(ThemePaletteConfig.Background), fallback.Background),
                Surface = ReadString(customTheme, nameof(ThemePaletteConfig.Surface), fallback.Surface),
                SurfaceAlt = ReadString(customTheme, nameof(ThemePaletteConfig.SurfaceAlt), fallback.SurfaceAlt),
                Accent = ReadString(customTheme, nameof(ThemePaletteConfig.Accent), fallback.Accent),
                TextPrimary = ReadString(customTheme, nameof(ThemePaletteConfig.TextPrimary), fallback.TextPrimary),
                TextSecondary = ReadString(customTheme, nameof(ThemePaletteConfig.TextSecondary), fallback.TextSecondary),
                Success = ReadString(customTheme, nameof(ThemePaletteConfig.Success), fallback.Success),
                Warning = ReadString(customTheme, nameof(ThemePaletteConfig.Warning), fallback.Warning),
                Danger = ReadString(customTheme, nameof(ThemePaletteConfig.Danger), fallback.Danger)
            };
        }

        private static void ApplyImportableNotificationSettings(JsonElement notifications, AppConfig cfg)
        {
            cfg.Notifications.OnBackupSuccess = ReadBool(notifications, nameof(cfg.Notifications.OnBackupSuccess), cfg.Notifications.OnBackupSuccess);
            cfg.Notifications.OnBackupFailure = ReadBool(notifications, nameof(cfg.Notifications.OnBackupFailure), cfg.Notifications.OnBackupFailure);
            cfg.Notifications.OnSnapshotSuccess = ReadBool(notifications, nameof(cfg.Notifications.OnSnapshotSuccess), cfg.Notifications.OnSnapshotSuccess);
            cfg.Notifications.OnSnapshotFailure = ReadBool(notifications, nameof(cfg.Notifications.OnSnapshotFailure), cfg.Notifications.OnSnapshotFailure);
            cfg.Notifications.OnLowDisk = ReadBool(notifications, nameof(cfg.Notifications.OnLowDisk), cfg.Notifications.OnLowDisk);
            cfg.Notifications.UseOsNotifications = ReadBool(notifications, nameof(cfg.Notifications.UseOsNotifications), cfg.Notifications.UseOsNotifications);
            cfg.Notifications.OnlyWhenInactive = ReadBool(notifications, nameof(cfg.Notifications.OnlyWhenInactive), cfg.Notifications.OnlyWhenInactive);
        }

        private static void ApplyImportableAdvancedSettings(JsonElement advanced, AppConfig cfg)
        {
            cfg.Advanced.VerboseLogging = ReadBool(advanced, nameof(cfg.Advanced.VerboseLogging), cfg.Advanced.VerboseLogging);
            cfg.Advanced.SaveVerboseLogs = ReadBool(advanced, nameof(cfg.Advanced.SaveVerboseLogs), cfg.Advanced.SaveVerboseLogs);
            cfg.Advanced.CrashReportAssistanceEnabled = ReadBool(
                advanced,
                nameof(cfg.Advanced.CrashReportAssistanceEnabled),
                cfg.Advanced.CrashReportAssistanceEnabled);
            cfg.Advanced.CheckUpdates = ReadBool(advanced, nameof(cfg.Advanced.CheckUpdates), cfg.Advanced.CheckUpdates);
            cfg.Advanced.UpdateCheckIntervalMinutes = ClampInt(ReadInt(advanced, nameof(cfg.Advanced.UpdateCheckIntervalMinutes), cfg.Advanced.UpdateCheckIntervalMinutes), 15, 1440, 120);
            cfg.Advanced.BetaChannelEnabled = ReadBool(advanced, nameof(cfg.Advanced.BetaChannelEnabled), cfg.Advanced.BetaChannelEnabled);
            cfg.Advanced.Language = NormalizeImportedLanguage(ReadString(advanced, nameof(cfg.Advanced.Language), cfg.Advanced.Language), cfg.Advanced.Language);
            cfg.Advanced.HasSeenOnboarding = ReadBool(advanced, nameof(cfg.Advanced.HasSeenOnboarding), cfg.Advanced.HasSeenOnboarding);

            if (advanced.TryGetProperty(nameof(cfg.Advanced.Maintenance), out JsonElement maintenance))
                ApplyImportableMaintenanceSettings(maintenance, cfg);
        }

        private static void ApplyImportableMaintenanceSettings(JsonElement maintenance, AppConfig cfg)
        {
            cfg.Advanced.Maintenance.Enabled = ReadBool(maintenance, nameof(cfg.Advanced.Maintenance.Enabled), cfg.Advanced.Maintenance.Enabled);
            cfg.Advanced.Maintenance.WindowStart = NormalizeTimeOfDay(ReadString(maintenance, nameof(cfg.Advanced.Maintenance.WindowStart), cfg.Advanced.Maintenance.WindowStart), DefaultMaintenanceStart);
            cfg.Advanced.Maintenance.WindowEnd = NormalizeTimeOfDay(ReadString(maintenance, nameof(cfg.Advanced.Maintenance.WindowEnd), cfg.Advanced.Maintenance.WindowEnd), DefaultMaintenanceEnd);
            cfg.Advanced.Maintenance.RunConsistencyScan = ReadBool(maintenance, nameof(cfg.Advanced.Maintenance.RunConsistencyScan), cfg.Advanced.Maintenance.RunConsistencyScan);
            cfg.Advanced.Maintenance.RunRepairDryRun = ReadBool(maintenance, nameof(cfg.Advanced.Maintenance.RunRepairDryRun), cfg.Advanced.Maintenance.RunRepairDryRun);
            cfg.Advanced.Maintenance.RunMetadataRefresh = ReadBool(maintenance, nameof(cfg.Advanced.Maintenance.RunMetadataRefresh), cfg.Advanced.Maintenance.RunMetadataRefresh);
        }

        private static void ApplyImportableBehaviorSettings(JsonElement behavior, AppConfig cfg)
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

        private static bool ReadBool(JsonElement parent, string propertyName, bool fallback)
        {
            if (!parent.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return fallback;
            return value.GetBoolean();
        }

        private static int ReadInt(JsonElement parent, string propertyName, int fallback)
        {
            if (!parent.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
                return fallback;
            return result;
        }

        private static string ReadString(JsonElement parent, string propertyName, string fallback)
        {
            if (!parent.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                return fallback;
            return value.GetString() ?? fallback;
        }

        private static string NormalizeImportedTheme(string value, string fallback)
        {
            return value switch
            {
                ThemeDark => ThemeDark,
                ThemeLight => ThemeLight,
                ThemeCustom => ThemeCustom,
                ThemeFollowSystem => ThemeFollowSystem,
                ThemeSystem => ThemeFollowSystem,
                _ => fallback
            };
        }

        private static string NormalizeImportedLanguage(string value, string fallback)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "en" or "it" or "es" or "fr" or "de" or "pt" or "zh" or "hi" or "ar" or "bn" or "ru" => normalized,
                _ => fallback
            };
        }

        private static string NormalizeImportedEncryptionAlgorithm(string value, string fallback)
        {
            string normalized = (value ?? string.Empty).Trim();
            return string.Equals(normalized, "aes-256-cbc-hmac-sha256-v1", StringComparison.OrdinalIgnoreCase)
                ? "aes-256-cbc-hmac-sha256-v1"
                : fallback;
        }

        private static string NormalizeImportedKdfProfile(string value, string fallback)
        {
            string normalized = (value ?? string.Empty).Trim();
            return string.Equals(normalized, "pbkdf2-sha256-v1", StringComparison.OrdinalIgnoreCase)
                ? "pbkdf2-sha256-v1"
                : fallback;
        }

        private static string NormalizeImportedKdfParamRef(string value, string fallback)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return fallback;

            const string prefix = "pbkdf2-iter-";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fallback;

            string valuePart = normalized[prefix.Length..].Trim();
            if (!int.TryParse(valuePart, out int iterations))
                return fallback;

            return $"pbkdf2-iter-{Math.Clamp(iterations, 10_000, 1_000_000)}";
        }

    }

}
