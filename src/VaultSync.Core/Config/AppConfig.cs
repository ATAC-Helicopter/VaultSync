using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VaultSync.Core.Config
{
    public sealed class AppConfig
    {
        // -------- General --------

        public string? ProjectsRoot { get; set; } = string.Empty;
        public bool ResumeLastSession { get; set; } = true;

        [JsonPropertyName("AutoOpenLastProject")]
        public bool AutoOpenLastProject
        {
            get => ResumeLastSession;
            set => ResumeLastSession = value;
        }

        public string LastView { get; set; } = "Dashboard";

        /// <summary>
        /// Optional explicit path to the VaultSync metadata database (SQLite).
        /// If null or empty, a platform-specific default will be used.
        /// </summary>
        public string? DbPath { get; set; } = string.Empty;

        // Grouped sections - these are what SettingsViewModel expects
        public BackupsConfig Backups { get; set; } = new();
        public StorageConfig Storage { get; set; } = new();
        public NetworkConfig Network { get; set; } = new();
        public AppearanceConfig Appearance { get; set; } = new();
        public NotificationsConfig Notifications { get; set; } = new();
        public AdvancedConfig Advanced { get; set; } = new();
        public AppBehaviorConfig Behavior { get; set; } = new();
    }

    // -------- Backups --------

    public sealed class BackupsConfig
    {
        public bool   EnableAutoBackups     { get; set; } = true;
        public int    IntervalMinutes       { get; set; } = 30;
        public int    MaxSnapshotsPerProject{ get; set; } = 20;
        public List<int> AutoBackupDisabledProjects { get; set; } = [];
        public string? Location             { get; set; } = string.Empty;
        public bool   EnableMetadataSync    { get; set; } = true;
        public bool   AutoImportMetadata    { get; set; } = true;
        public bool   PromptRestoreAfterImport { get; set; } = true;
        /// <summary>
        /// When enabled, backup transfer speed is capped to reduce bandwidth impact.
        /// </summary>
        public bool EnableBandwidthLimit { get; set; } = false;
        /// <summary>
        /// Maximum transfer bandwidth in megabits per second (Mbps) when the limit is enabled.
        /// </summary>
        public int MaxBandwidthMbps { get; set; } = 100;
        /// <summary>
        /// When enabled, automatic backups follow the quiet-hours schedule.
        /// </summary>
        public bool EnableQuietHours { get; set; } = false;
        /// <summary>
        /// Quiet-hours start time in 24h HH:mm format.
        /// </summary>
        public string QuietHoursStart { get; set; } = "23:00";
        /// <summary>
        /// Quiet-hours end time in 24h HH:mm format.
        /// </summary>
        public string QuietHoursEnd { get; set; } = "07:00";
        // New canonical backup root path used by UI + snapshot service
        public string? BackupRoot { get; set; } = string.Empty;

        /// <summary>
        /// When true, VaultSync uses the per-destination list (Destinations).
        /// When false, VaultSync uses the simple single-path backup root (BackupRoot).
        /// </summary>
        public bool UseAdvancedDestinations { get; set; } = false;
        /// <summary>
        /// Backward-compatible alias for the backup root path.
        /// Prefer using BackupRoot in new code.
        /// </summary>
        public string? BackupLocation
        {
            get => string.IsNullOrWhiteSpace(BackupRoot) ? Location : BackupRoot;
            set
            {
                BackupRoot = value;
                Location   = value;
            }
        }
        public bool   UseCompression        { get; set; } = false;
        /// <summary>
        /// When true, rsync can use its delta-transfer algorithm on macOS/Linux.
        /// Windows backups (robocopy) are unaffected.
        /// </summary>
        public bool   UseRsyncDelta         { get; set; } = false;
        /// <summary>
        /// When true, backups use rsync --link-dest to hardlink unchanged files
        /// from the previous backup, keeping history while saving space/time.
        /// </summary>
        public bool   UseIncrementalBackups { get; set; } = false;
        /// <summary>
        /// Controls whether snapshots always compute full hashes for all files.
        /// When false, snapshots may reuse hashes for unchanged files to speed up runs.
        /// </summary>
        public bool   UseFullSnapshotHash   { get; set; } = true;
        /// <summary>
        /// When true, snapshot scans can reuse cached directory metadata to skip unchanged folders.
        /// </summary>
        public bool   EnableScanCache       { get; set; } = true;
        /// <summary>
        /// When true, scan cache is more aggressive about skipping unchanged folders.
        /// </summary>
        public bool   AggressiveScanCache   { get; set; } = false;
        /// <summary>
        /// Auto-tuned archive upload buffer size for the legacy single-destination flow.
        /// When null, VaultSync will probe before the first archive upload and cache the result.
        /// </summary>
        public int?   LegacyArchiveUploadBufferBytes { get; set; } = 1024 * 1024;
        /// <summary>
        /// When true, VaultSync probes the destination to auto-tune archive upload buffer sizes.
        /// </summary>
        public bool   EnableArchiveUploadAutoTune { get; set; } = false;
        /// <summary>
        /// When true, compressed archive uploads may use parallel writers on supported targets.
        /// </summary>
        public bool   EnableParallelArchiveUpload { get; set; } = true;
        /// <summary>
        /// Rolling estimate of recent backup throughput (MB/s) for ETA calculations.
        /// </summary>
        public double LastBackupThroughputMbSec { get; set; } = 0;
        /// <summary>
        /// Rolling estimate of recent archive backup throughput (MB/s).
        /// </summary>
        public double LastBackupThroughputArchiveMbSec { get; set; } = 0;
        /// <summary>
        /// Rolling estimate of recent file copy throughput (MB/s).
        /// </summary>
        public double LastBackupThroughputCopyMbSec { get; set; } = 0;
        public bool   VerifyAfterCreate     { get; set; } = true;
        public bool   PauseOnBattery        { get; set; } = true;

        /// <summary>
        /// Preferred backup destinations (local / external / network).
        /// When empty, BackupRoot is used for legacy compatibility.
        /// </summary>
        public List<BackupDestination> Destinations { get; set; } = [];

        /// <summary>
        /// Global backup encryption policy and non-secret key/material references.
        /// </summary>
        public BackupEncryptionConfig Encryption { get; set; } = new();
    }

    public sealed class BackupEncryptionConfig
    {
        public bool Enabled { get; set; } = false;
        /// <summary>
        /// Reference to secure-store entry; no plaintext password is persisted in config.
        /// </summary>
        public string KeyRef { get; set; } = string.Empty;
        public string Algorithm { get; set; } = "aes-256-cbc-hmac-sha256-v1";
        public string KdfProfile { get; set; } = "pbkdf2-sha256-v1";
        public string KdfParamRef { get; set; } = "pbkdf2-iter-210000";
        /// <summary>
        /// When true, in-memory session fallback can be offered if secure-store save fails.
        /// Explicit user confirmation is still required by runtime flows.
        /// </summary>
        public bool AllowSessionFallback { get; set; } = false;
        /// <summary>
        /// Minutes before encrypted "Open folder" session unlock expires and temp content is auto-locked.
        /// </summary>
        public int OpenUnlockTimeoutMinutes { get; set; } = 10;
    }

    // -------- Storage --------

    public sealed class StorageConfig
    {
        public bool PreferExternalDrives  { get; set; } = true;
        public bool ShowDriveWarnings     { get; set; } = true;
        public int  MinFreeSpacePercent   { get; set; } = 10;
    }

    // -------- Network / NAS --------

    public sealed class NetworkConfig
    {
        /// <summary>
        /// Saved credential profiles that can be assigned to NAS destinations.
        /// Secrets should be stored in platform keychain/credential manager; KeyRef points to that entry.
        /// </summary>
        public List<NetworkCredentialProfile> Credentials { get; set; } = [];
    }

    public sealed class NetworkCredentialProfile
    {
        public string Name { get; set; } = string.Empty;          // e.g., "Home NAS"
        public string Username { get; set; } = string.Empty;      // e.g., "media" or "DOMAIN\\user"
        public string? Domain { get; set; } = string.Empty;
        public string? KeyRef { get; set; } = string.Empty;       // reference to keychain/credman entry
        public bool UseKeychain { get; set; } = true;             // prefer platform store
        public string? Password { get; set; } = string.Empty;     // optional (fallback); prefer keychain in production
    }

    public sealed class BackupDestination
    {
        public string Path { get; set; } = string.Empty;          // UNC, smb://, local/external path
        public string? CredentialName { get; set; }               // links to NetworkCredentialProfile.Name
        public bool Active { get; set; } = true;                  // include by default
        public bool AutoMount { get; set; } = false;              // attempt to mount if unreachable
        public bool AutoUnmount { get; set; } = false;            // unmount after backup if we mounted it
        public bool PreMounted { get; set; } = false;             // treat as already mounted/guest; skip mount/creds
        /// <summary>
        /// User-confirmed offsite classification used by the transparent 3-2-1 advisor.
        /// VaultSync never guesses physical location from a path or network protocol.
        /// </summary>
        public bool IsOffsite { get; set; } = false;
        public string? Alias { get; set; } = string.Empty;        // optional display label
        public bool EnableMetadataSync { get; set; } = true;
        public bool AutoImportMetadata { get; set; } = true;
        public bool ForceMetadataBackfill { get; set; } = false;
        /// <summary>
        /// Total attempts for backup work on this destination (initial try included).
        /// </summary>
        public int RetryMaxAttempts { get; set; } = 1;
        /// <summary>
        /// Base backoff in seconds between retries for this destination.
        /// </summary>
        public int RetryBackoffSeconds { get; set; } = 10;
        /// <summary>
        /// When true, interrupted archive uploads keep resumable checkpoint state so later retries can continue
        /// from the last completed byte range instead of restarting the whole transfer.
        /// </summary>
        public bool EnableCheckpointResume { get; set; } = true;
        /// <summary>
        /// Auto-tuned archive upload buffer size for this destination.
        /// When null, VaultSync will probe before the first archive upload and cache the result.
        /// </summary>
        public int? ArchiveUploadBufferBytes { get; set; } = null;
        /// <summary>
        /// Optional soft quota target in bytes for backups written to this destination.
        /// When null or 0, VaultSync will not compute quota suggestions for the destination.
        /// </summary>
        public long? SoftQuotaBytes { get; set; } = null;
        /// <summary>
        /// Warning threshold percentage within the soft quota where VaultSync starts suggesting cleanup.
        /// </summary>
        public int QuotaWarningPercent { get; set; } = 85;
    }

    // -------- Appearance --------

    public sealed class AppearanceConfig
    {
        public string Theme { get; set; } = "System";
        public bool    CompactLayout      { get; set; } = false;
        public bool    ShowProjectAvatars { get; set; } = true;
        public Dictionary<string, TagColorConfig> TagColors { get; set; } = [];
        public ThemePaletteConfig CustomTheme { get; set; } = ThemePaletteConfig.CreateDefault();
    }

    public sealed class TagColorConfig
    {
        public string Background { get; set; } = string.Empty;
        public string Foreground { get; set; } = string.Empty;
        public string Border { get; set; } = string.Empty;
    }

    public sealed class ThemePaletteConfig
    {
        public string Name { get; set; } = "VaultSync Midnight";
        public string BaseTheme { get; set; } = "Dark";
        public string VisualStyle { get; set; } = "Solid";
        public string Background { get; set; } = "#101218";
        public string Surface { get; set; } = "#181B24";
        public string SurfaceAlt { get; set; } = "#222635";
        public string Accent { get; set; } = "#4F8DFF";
        public string TextPrimary { get; set; } = "#FFFFFF";
        public string TextSecondary { get; set; } = "#B3B8C7";
        public string Success { get; set; } = "#4FF2B6";
        public string Warning { get; set; } = "#FFC766";
        public string Danger { get; set; } = "#FF7676";

        public ThemePaletteConfig Clone()
        {
            return new ThemePaletteConfig
            {
                Name = Name,
                BaseTheme = BaseTheme,
                VisualStyle = VisualStyle,
                Background = Background,
                Surface = Surface,
                SurfaceAlt = SurfaceAlt,
                Accent = Accent,
                TextPrimary = TextPrimary,
                TextSecondary = TextSecondary,
                Success = Success,
                Warning = Warning,
                Danger = Danger
            };
        }

        public static ThemePaletteConfig CreateDefault() => new();
    }

    // -------- Notifications --------

    public sealed class NotificationsConfig
    {
        public bool OnBackupSuccess      { get; set; } = true;
        public bool OnBackupFailure      { get; set; } = true;

        public bool OnSnapshotSuccess    { get; set; } = false;
        public bool OnSnapshotFailure    { get; set; } = true;

        public bool OnLowDisk            { get; set; } = true;

        /// <summary>
        /// When true, VaultSync will attempt to use OS-level notifications
        /// (notification center / toasts) for important events.
        /// </summary>
        public bool UseOsNotifications   { get; set; } = true;

        /// <summary>
        /// When true, OS notifications are only shown if the main window is
        /// not active (app in background / not focused).
        /// </summary>
        public bool OnlyWhenInactive     { get; set; } = true;
    }

    // -------- Advanced / Misc --------

    public sealed class AdvancedConfig
    {
        public bool VerboseLogging   { get; set; } = false;
        public bool SaveVerboseLogs  { get; set; } = false;
        public bool CrashReportAssistanceEnabled { get; set; } = true;
        public bool CheckUpdates     { get; set; } = true;
        public int UpdateCheckIntervalMinutes { get; set; } = 120;
        public bool BetaChannelEnabled { get; set; } = false;
        public string Language       { get; set; } = "en";
        public string SkippedUpdateTag { get; set; } = string.Empty;
        public string LastWhatsNewVersion { get; set; } = string.Empty;
        public bool HasSeenOnboarding { get; set; } = false;
        public BackupIndexScanSummary BackupIndexLastScan { get; set; } = new();
        public List<ProjectMetadataConflictRecord> ProjectMetadataConflicts { get; set; } = [];
        public List<ProjectMetadataResolutionRecord> ProjectMetadataResolutions { get; set; } = [];
        public List<ProjectMetadataMergeBaseRecord> ProjectMetadataMergeBases { get; set; } = [];
        public UpdateCheckDiagnostics UpdateDiagnostics { get; set; } = new();
        public BackupRepairTelemetry BackupRepairTelemetry { get; set; } = new();
        public MetadataConflictTelemetry MetadataConflictTelemetry { get; set; } = new();
        public MaintenanceConfig Maintenance { get; set; } = new();
        public StartupDiagnosticsSummary StartupDiagnostics { get; set; } = new();
        public CheckpointResumeTelemetry CheckpointResumeTelemetry { get; set; } = new();
        public MetadataImportCacheConfig MetadataImportCache { get; set; } = new();
    }

    public sealed class MaintenanceConfig
    {
        public bool Enabled { get; set; } = false;
        public string WindowStart { get; set; } = "01:00";
        public string WindowEnd { get; set; } = "05:00";
        public bool RunConsistencyScan { get; set; } = true;
        public bool RunRepairDryRun { get; set; } = true;
        public bool RunMetadataRefresh { get; set; } = true;
        public string LastRunUtc { get; set; } = string.Empty;
        public string LastStatus { get; set; } = string.Empty;
    }

    public sealed class BackupIndexScanSummary
    {
        public string CheckedUtc { get; set; } = string.Empty;
        public int ProjectCount { get; set; }
        public int SnapshotCount { get; set; }
        public int BackupCount { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<string> TopFindingCodes { get; set; } = [];
    }

    public sealed class ProjectMetadataConflictRecord
    {
        public int ProjectId { get; set; }
        public string ProjectExternalId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string SourceMachineId { get; set; } = string.Empty;
        public string SourceUpdatedUtc { get; set; } = string.Empty;
        public string BaseMachineId { get; set; } = string.Empty;
        public string BaseUpdatedUtc { get; set; } = string.Empty;
        public string LocalMachineId { get; set; } = string.Empty;
        public string DetectedUtc { get; set; } = string.Empty;
        public string SourceKey { get; set; } = string.Empty;
        public long SourceRevision { get; set; }
        public long BaseRevision { get; set; }
        public List<string> ConflictingFields { get; set; } = [];
        public ProjectMetadataConflictValues Base { get; set; } = new();
        public ProjectMetadataConflictValues Local { get; set; } = new();
        public ProjectMetadataConflictValues Imported { get; set; } = new();
        public ProjectMetadataConflictValues KeepLocalResult { get; set; } = new();
        public ProjectMetadataConflictValues AcceptImportedResult { get; set; } = new();
    }

    public sealed class ProjectMetadataConflictValues
    {
        public string AvatarColor { get; set; } = string.Empty;
        public string EncryptionPolicy { get; set; } = string.Empty;
        public string PreferredDestinationId { get; set; } = string.Empty;
        public string RestoreMode { get; set; } = string.Empty;
        public string VerificationPolicy { get; set; } = string.Empty;
        public bool? AutoBackupEnabled { get; set; }
        public string Tags { get; set; } = string.Empty;
    }

    public sealed class ProjectMetadataResolutionRecord
    {
        public string SourceKey { get; set; } = string.Empty;
        public string ProjectExternalId { get; set; } = string.Empty;
        public string SourceMachineId { get; set; } = string.Empty;
        public string SourceUpdatedUtc { get; set; } = string.Empty;
        public long SourceRevision { get; set; }
        public long BaseRevision { get; set; }
        public string Decision { get; set; } = string.Empty;
        public string ResolvedUtc { get; set; } = string.Empty;
        public bool UndoAvailable { get; set; }
        public string UndoneUtc { get; set; } = string.Empty;
        public string SupersededUtc { get; set; } = string.Empty;
        public ProjectMetadataConflictValues Local { get; set; } = new();
        public ProjectMetadataConflictValues Imported { get; set; } = new();
        public ProjectMetadataConflictValues Result { get; set; } = new();
    }

    public sealed class ProjectMetadataMergeBaseRecord
    {
        public string SourceKey { get; set; } = string.Empty;
        public string ProjectExternalId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public string WriterMachineId { get; set; } = string.Empty;
        public string UpdatedUtc { get; set; } = string.Empty;
        public ProjectMetadataConflictValues Values { get; set; } = new();
        public Dictionary<string, ProjectMetadataFieldProvenance> FieldProvenance { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class ProjectMetadataFieldProvenance
    {
        public string WriterMachineId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public string UpdatedUtc { get; set; } = string.Empty;
    }

    public sealed class UpdateCheckDiagnostics
    {
        public string CheckedUtc { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string Decision { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public UpdateReleaseCandidateDiagnostics SelectedCandidate { get; set; } = new();
        public UpdateReleaseCandidateDiagnostics StableCandidate { get; set; } = new();
        public UpdateReleaseCandidateDiagnostics BetaCandidate { get; set; } = new();
        public PatchPreflightDiagnostics PatchPreflight { get; set; } = new();
    }

    public sealed class UpdateReleaseCandidateDiagnostics
    {
        public string Tag { get; set; } = string.Empty;
        public string TargetCommitish { get; set; } = string.Empty;
        public bool Prerelease { get; set; }
        public string PublishedUtc { get; set; } = string.Empty;
        public bool HasPatch { get; set; }
        public bool HasInstaller { get; set; }
    }

    public sealed class PatchPreflightDiagnostics
    {
        public string StatusCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string ManifestPreviousVersion { get; set; } = string.Empty;
        public List<string> ManifestAllowedBaseVersions { get; set; } = [];
        public string MatchedBaseVersion { get; set; } = string.Empty;
        public string ManifestTargetVersion { get; set; } = string.Empty;
        public bool Eligible { get; set; }
        public bool RequiresInstaller { get; set; }
        public bool HasManifest { get; set; }
        public bool HasArchive { get; set; }
        public bool HasInstaller { get; set; }
    }

    public sealed class BackupRepairTelemetry
    {
        public string LastScanUtc { get; set; } = string.Empty;
        public int PlannedActionCount { get; set; }
        public int BlockedIssueBucketCount { get; set; }
        public List<string> PlannedActionCodes { get; set; } = [];
        public List<string> BlockedIssueCodes { get; set; } = [];
        public string LastApplyUtc { get; set; } = string.Empty;
        public int LastAppliedCount { get; set; }
        public string LastStatus { get; set; } = string.Empty;
    }

    public sealed class MetadataConflictTelemetry
    {
        public string LastUpdatedUtc { get; set; } = string.Empty;
        public int PendingConflictCount { get; set; }
        public string LastResolutionAction { get; set; } = string.Empty;
        public string LastResolvedProject { get; set; } = string.Empty;
    }

    public sealed class StartupDiagnosticsSummary
    {
        public string LastCompletedUtc { get; set; } = string.Empty;
        public long TotalDurationMs { get; set; }
        public List<StartupDiagnosticsPhase> Phases { get; set; } = [];
    }

    public sealed class CheckpointResumeTelemetry
    {
        public string LastUpdatedUtc { get; set; } = string.Empty;
        public string LastStatus { get; set; } = string.Empty;
        public string LastProjectName { get; set; } = string.Empty;
        public string LastBackupFolder { get; set; } = string.Empty;
        public string LastArchivePath { get; set; } = string.Empty;
        public long LastResumeOffsetBytes { get; set; }
        public long LastArchiveSizeBytes { get; set; }
        public string LastSourceFingerprint { get; set; } = string.Empty;
        public string LastMessage { get; set; } = string.Empty;
    }

    public sealed class MetadataImportCacheConfig
    {
        public List<MetadataImportSourceStamp> Sources { get; set; } = [];
    }

    public sealed class MetadataImportSourceStamp
    {
        public string SourceKey { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string SourceMachineId { get; set; } = string.Empty;
        public string StoreUpdatedUtc { get; set; } = string.Empty;
        public int StoreSchemaVersion { get; set; }
        public long StoreFileLengthBytes { get; set; }
        public string StoreFileUpdatedUtc { get; set; } = string.Empty;
        public string StoreSidecarStamp { get; set; } = string.Empty;
        public string ImportedUtc { get; set; } = string.Empty;
        public int ProjectCount { get; set; }
        public int SnapshotCount { get; set; }
        public int BackupCount { get; set; }
        public int TombstoneCount { get; set; }
        public List<string> ProjectExternalIds { get; set; } = [];
        public List<string> SnapshotExternalIds { get; set; } = [];
        public List<string> BackupExternalIds { get; set; } = [];
    }

    public sealed class StartupDiagnosticsPhase
    {
        public string Name { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
    }

    // -------- App Behavior / Background Mode --------

public sealed class AppBehaviorConfig
{
        /// <summary>
        /// If true, closing the main window hides it and keeps VaultSync running
        /// in the background via tray/menu bar instead of quitting.
        /// </summary>
        public bool RunInBackground { get; set; } = true;

    /// <summary>
    /// When true, starting backups or snapshots from the tray/menu-bar will
    /// bring the main window to the foreground. When false, those actions
    /// will run in the background without showing the window.
    /// </summary>
    public bool ShowWindowOnTrayActions { get; set; } = true;

    /// <summary>
    /// If true, show a tray icon (Windows) or menu bar icon (macOS).
    /// </summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// If true, show the mini backup widget when starting backups from the tray/menu-bar.
    /// </summary>
    public bool ShowBackupWidget { get; set; } = true;

    /// <summary>
    /// If true, OS-level notifications (Notification Center / Windows Toasts)
    /// are enabled when the app is in background.
    /// </summary>
    public bool EnableSystemNotifications { get; set; } = true;

        /// <summary>
        /// If true, minimizing the window sends it to tray/menu bar.
        /// </summary>
        public bool MinimizeToTray { get; set; } = false;

    /// <summary>
    /// If true, the app will attempt to launch on login where supported.
    /// </summary>
    public bool LaunchOnLogin { get; set; } = true;

        /// <summary>
        /// If true, confirm before deleting backup data from destinations.
        /// </summary>
        public bool ConfirmDeleteBackup { get; set; } = true;

        /// <summary>
        /// Discovered project root paths hidden from the Projects page list.
        /// </summary>
        public List<string> HiddenProjectPaths { get; set; } = [];
    }
}
