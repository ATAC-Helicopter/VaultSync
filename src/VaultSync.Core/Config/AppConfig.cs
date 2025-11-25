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

        // Grouped sections – these are what SettingsViewModel expects
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
        public List<int> AutoBackupDisabledProjects { get; set; } = new();
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
        /// <summary>
        /// Controls whether snapshots always compute full hashes for all files.
        /// When false, snapshots may reuse hashes for unchanged files to speed up runs.
        /// </summary>
        public bool   UseFullSnapshotHash   { get; set; } = true;
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
        public bool CheckUpdates     { get; set; } = true;
        public bool SendUsageStats   { get; set; } = false;
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
        public bool LaunchOnLogin { get; set; } = false;
    }
}
