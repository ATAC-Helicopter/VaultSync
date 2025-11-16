using System;

namespace VaultSync.Core.Config
{
    public sealed class AppConfig
    {
        // -------- General --------

        public string? ProjectsRoot { get; set; } = string.Empty;
        public bool AutoOpenLastProject { get; set; } = true;
        public bool RememberWindowLayout { get; set; } = true;

        /// <summary>
        /// Optional explicit path to the VaultSync metadata database (SQLite).
        /// If null or empty, a platform-specific default will be used.
        /// </summary>
        public string? DbPath { get; set; } = string.Empty;

        // Grouped sections – these are what SettingsViewModel expects
        public BackupsConfig Backups { get; set; } = new();
        public StorageConfig Storage { get; set; } = new();
        public NetworkConfig Network { get; set; } = new();
        public AppearanceConfig Appearance { get; set; } = new();
        public NotificationsConfig Notifications { get; set; } = new();
        public AdvancedConfig Advanced { get; set; } = new();
    }

    // -------- Backups --------

    public sealed class BackupsConfig
    {
        public bool   EnableAutoBackups     { get; set; } = true;
        public int    IntervalMinutes       { get; set; } = 30;
        public int    MaxSnapshotsPerProject{ get; set; } = 20;
        public string? Location             { get; set; } = string.Empty;
        // New canonical backup root path used by UI + snapshot service
        public string? BackupRoot { get; set; } = string.Empty;
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
        public bool   UseCompression        { get; set; } = true;
        public bool   VerifyAfterCreate     { get; set; } = true;
        public bool   PauseOnBattery        { get; set; } = true;
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
        public bool   UseCredentials        { get; set; } = false;
        public string? Username             { get; set; } = string.Empty;
        public string? Password             { get; set; } = string.Empty;
        public bool   RememberCredentials   { get; set; } = false;
    }

    // -------- Appearance --------

    public sealed class AppearanceConfig
    {
        public string Theme { get; set; } = "System";
        public bool    CompactLayout      { get; set; } = false;
        public bool    ShowProjectAvatars { get; set; } = true;
    }

    // -------- Notifications --------

    public sealed class NotificationsConfig
    {
        public bool OnBackupSuccess  { get; set; } = true;
        public bool OnBackupFailure  { get; set; } = true;
        public bool OnLowDisk        { get; set; } = true;
    }

    // -------- Advanced / Misc --------

    public sealed class AdvancedConfig
    {
        public bool VerboseLogging   { get; set; } = false;
        public bool CheckUpdates     { get; set; } = true;
        public bool SendUsageStats   { get; set; } = false;
    }
}