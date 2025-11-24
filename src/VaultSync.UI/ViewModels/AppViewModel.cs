using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.Concurrent;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;
using System.Collections.Generic;

namespace VaultSync.UI.ViewModels
{
    public enum NotificationLevel
    {
        Info,
        Warning,
        Error
    }

    public enum NotificationKind
    {
        System,
        Backup,
        Snapshot
    }

    public interface INotificationService
    {
        void ShowInfo(string title, string message, NotificationKind kind = NotificationKind.System);
        void ShowWarning(string title, string message, NotificationKind kind = NotificationKind.System);
        void ShowError(string title, string message, NotificationKind kind = NotificationKind.System);
    }

    /// <summary>
    /// Basic in-app notification service. For now it only logs to the console;
    /// later we can extend this to drive UI banners/toasts and OS notifications.
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        public void ShowInfo(string title, string message, NotificationKind kind = NotificationKind.System)
        {
            Console.WriteLine($"[Notification][Info][{kind}] {title}: {message}");
        }

        public void ShowWarning(string title, string message, NotificationKind kind = NotificationKind.System)
        {
            Console.WriteLine($"[Notification][Warning][{kind}] {title}: {message}");
        }

        public void ShowError(string title, string message, NotificationKind kind = NotificationKind.System)
        {
            Console.WriteLine($"[Notification][Error][{kind}] {title}: {message}");
        }
    }

    public class AppViewModel : ViewModelBase
    {
        private object? _currentView;
        private string _headerTitle = "Dashboard";
        private string _headerKicker = "Overview";

        // Section view models (kept alive for entire app lifetime)
        private readonly DashboardViewModel _dashboardViewModel;
        private readonly ProjectsViewModel  _projectsViewModel;
        private readonly BackupsViewModel   _backupsViewModel;
        private readonly SettingsViewModel  _settingsViewModel;

        // Core services for live data
        private readonly SqliteRepository _repo;
        private readonly BackupService    _backupService;
        private readonly INotificationService _notificationService;

        // Helper property to detect when the Backups page is active
        private bool IsOnBackupsPage => CurrentView == _backupsViewModel;

        // Helper property to respect global notifications setting from SettingsViewModel
        private bool NotificationsEnabled => _settingsViewModel?.NotificationsEnabled ?? true;

        // New: helper to read system notification setting from AppConfig.Behavior
        private bool SystemNotificationsEnabled
        {
            get
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    return cfg.Behavior?.EnableSystemNotifications ?? true;
                }
                catch
                {
                    // Fail open: if config cannot be read, don't silently drop notifications.
                    return true;
                }
            }
        }

        // New: only raise system notifications when enabled AND not in foreground
        private bool ShouldRaiseSystemNotification =>
            SystemNotificationsEnabled && !VaultSync.UI.MainWindow.IsForeground;

        public object? CurrentView
        {
            get => _currentView;
            set
            {
                if (!Equals(_currentView, value))
                {
                    _currentView = value;
                    OnPropertyChanged(nameof(CurrentView));
                }
            }
        }

        public string HeaderTitle
        {
            get => _headerTitle;
            set
            {
                if (_headerTitle != value)
                {
                    _headerTitle = value;
                    OnPropertyChanged(nameof(HeaderTitle));
                }
            }
        }

        public string HeaderKicker
        {
            get => _headerKicker;
            set
            {
                if (_headerKicker != value)
                {
                    _headerKicker = value;
                    OnPropertyChanged(nameof(HeaderKicker));
                }
            }
        }

        // Commands used by the shell / main window
        public ICommand NavigateDashboard { get; }
        public ICommand NavigateProjects  { get; }
        public ICommand NavigateBackups   { get; }
        public ICommand NavigateSettings  { get; }

        public AppViewModel()
        {
            // 1) Config + DB + services
            var cfg = AppConfigStore.Load();

            _repo = new SqliteRepository(cfg.DbPath ?? string.Empty);
            _repo.EnsureSchema();

            _backupService       = new BackupService(_repo);
            _notificationService = new NotificationService();

            // 2) Section viewmodels
            _dashboardViewModel = new DashboardViewModel();
            _projectsViewModel  = new ProjectsViewModel();
            _backupsViewModel   = new BackupsViewModel();
            _settingsViewModel  = new SettingsViewModel();

            // 3) Wire BackupsViewModel events to real logic
            _backupsViewModel.BackupProjectRequested += OnBackupProjectRequested;
            _backupsViewModel.CreateBackupForAllProjectsRequested += OnCreateBackupForAllProjectsRequested;
            _backupsViewModel.DeleteBackupRequested += OnDeleteBackupRequested;
            _backupsViewModel.RestoreBackupRequested += OnRestoreBackupRequested; // stub for later
            _backupsViewModel.CancelActiveBackupRequested += OnCancelActiveBackupRequested;

            // 4) Initial load of backup data
            ReloadBackupsVmData();
            _ = _dashboardViewModel.RefreshAsync();

            // 5) Default route
            CurrentView  = _dashboardViewModel;
            HeaderTitle  = "Dashboard";
            HeaderKicker = "Overview";

            // 6) Navigation commands (using cached VMs)
            NavigateDashboard = new RelayCommand(_ =>
            {
                _ = _dashboardViewModel.RefreshAsync();

                CurrentView  = _dashboardViewModel;
                HeaderTitle  = "Dashboard";
                HeaderKicker = "Overview";
            });

            NavigateProjects = new RelayCommand(_ =>
            {
                CurrentView  = _projectsViewModel;
                HeaderTitle  = "Projects";
                HeaderKicker = "All repositories";
            });

            NavigateBackups = new RelayCommand(_ =>
            {
                // IMPORTANT: refresh data each time we navigate here,
                // so newly added projects/snapshots show up.
                ReloadBackupsVmData();
                _ = _dashboardViewModel.RefreshAsync();

                CurrentView  = _backupsViewModel;
                HeaderTitle  = "Backups";
                HeaderKicker = "Snapshots & history";
            });

            NavigateSettings = new RelayCommand(_ =>
            {
                CurrentView  = _settingsViewModel;
                HeaderTitle  = "Settings";
                HeaderKicker = "Preferences";
            });
        }

        // ---------- Backups wiring ----------

        private void ReloadBackupsVmData()
        {
            var projects = _repo.GetAllProjects().ToList();
            var backups  = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();

            Console.WriteLine($"[AppViewModel] ReloadBackupsVmData: projects={projects.Count}, backups={backups.Count}.");

            _backupsViewModel.LoadFromBackups(projects, backups);
        }

        private async void OnBackupProjectRequested(ProjectBackupItem? item)
        {
            // Prevent overlapping manual backups; if one is already running, ignore.
            if (_backupsViewModel.IsBusy)
            {
                Console.WriteLine("[AppViewModel] BackupProjectRequested ignored because a backup is already in progress.");
                return;
            }

            if (item is null)
                return;

            if (!int.TryParse(item.Id, out var projectId))
                return;

            var cfg        = AppConfigStore.Load();
            var backupRoot = cfg.Backups.BackupRoot;
            if (string.IsNullOrWhiteSpace(backupRoot))
                return; // later: show error in UI
            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;

            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == projectId);
            if (project is null)
                return;

            var useArchiveMode = _settingsViewModel.UseBackupCompression;

            Console.WriteLine($"[AppViewModel] BackupProjectRequested for project '{project.Name}' (Id={project.Id}).");

            // Reset progress state
            _backupsViewModel.BackupProgress    = 0;
            _backupsViewModel.BackupCurrentFile = "Preparing backup…";
            _backupsViewModel.BackupEtaText     = string.Empty;

            // Reset per-project cards and add this project
            _backupsViewModel.ClearActiveBackups();
            _backupsViewModel.UpdateActiveBackup(
                project.Id.ToString(),
                project.Name,
                0,
                "Preparing backup…",
                string.Empty);

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = $"Backing up {project.Name}…";

            try
            {
                await Task.Run(async () =>
                {
                    await _backupService.RunBackupAsync(
                        project,
                        backupRoot,
                        isAuto: false,
                        progressCallback: (percent, currentFile, etaText) =>
                        {
                            // Build a nice label for this project
                            string label;
                            if (!string.IsNullOrWhiteSpace(currentFile))
                            {
                                label = currentFile;
                            }
                            else if (percent <= 0.1)
                            {
                                label = "Preparing backup…";
                            }
                            else if (percent < 100)
                            {
                                label = "Running backup…";
                            }
                            else
                            {
                                label = "Completed";
                            }

                            // Update per-project card (used by BackupsView overlay)
                            _backupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                percent,
                                label,
                                etaText);

                            // Keep legacy aggregate fields in sync (if anything else binds to them)
                            Dispatcher.UIThread.Post(() =>
                            {
                                _backupsViewModel.BackupProgress    = percent;
                                _backupsViewModel.BackupCurrentFile = label;
                                _backupsViewModel.BackupEtaText     = etaText;
                            });
                        },
                        useArchiveMode: useArchiveMode,
                        maxSnapshotsToKeep: maxSnapshotsToKeep,
                        minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent
                    );
                });

                // --- After backup: optional verification ---
                var cfgAfter = AppConfigStore.Load();
                if (cfgAfter.Backups.VerifyAfterCreate)
                {
                    var verifyService = new VerifyService(_repo, new HashService());
                    var latest = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                                      .OrderByDescending(b => b.CreatedUtc)
                                      .FirstOrDefault(b => b.ProjectId == project.Id);

                    if (latest != null)
                    {
                        var folder = Path.Combine(backupRoot, latest.Path ?? "");
                        try
                        {
                            await verifyService.VerifyAsync(project, folder, 100, full: true);
                        }
                        catch (Exception vex)
                        {
                            Console.WriteLine($"[AppViewModel] Verification exception: {vex}");

                            if (NotificationsEnabled)
                            {
                                var msg   = $"Verification failed for '{project.Name}'. The backup may be corrupted or incomplete.";
                                var title = "Backup verification failed";

                                _notificationService.ShowError(
                                    title,
                                    msg,
                                    NotificationKind.Backup);

                                // Toast only when not already on the Backups page.
                                if (!IsOnBackupsPage)
                                {
                                    GlobalNotificationCenter.Instance.Show(
                                        msg,
                                        NotificationSeverity.Error,
                                        title);
                                }

                                // System notification depends only on window foreground + settings.
                                if (ShouldRaiseSystemNotification)
                                {
                                    GlobalNotificationCenter.Instance.ShowSystem(
                                        msg,
                                        NotificationSeverity.Error,
                                        title);
                                }
                            }

                            Dispatcher.UIThread.Post(() =>
                            {
                                var backupId = latest.Id.ToString();
                                _backupsViewModel.MarkSnapshotAsFailed(backupId);
                                _backupsViewModel.ShowVerificationFailure(backupId, project.Name);
                            });
                        }
                    }
                }

                ReloadBackupsVmData();
                await _dashboardViewModel.RefreshAsync();

                // Notify success if enabled in settings and globally
                if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                {
                    var msg   = $"Backup for '{project.Name}' completed successfully.";
                    var title = "Backup completed";

                    _notificationService.ShowInfo(
                        title,
                        msg,
                        NotificationKind.Backup);

                    _backupsViewModel.ShowNotification(
                        $"Backup completed for {project.Name}",
                        "Info");

                    // Toast only when not already on the Backups page.
                    if (!IsOnBackupsPage)
                    {
                        GlobalNotificationCenter.Instance.Show(
                            msg,
                            NotificationSeverity.Info,
                            title);
                    }

                    // System notification depends only on window foreground + settings.
                    if (ShouldRaiseSystemNotification)
                    {
                        GlobalNotificationCenter.Instance.ShowSystem(
                            msg,
                            NotificationSeverity.Info,
                            title);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppViewModel] Backup failed: {ex}");

                // Detect the low-disk-space condition thrown by BackupService
                var isLowDisk =
                    ex is InvalidOperationException ioe &&
                    ioe.Message.Contains("does not have enough free space", StringComparison.OrdinalIgnoreCase);

                if (isLowDisk)
                {
                    // Low disk space: treat as a skipped backup with a clear warning,
                    // honoring the notifications settings.
                    if (NotificationsEnabled && _settingsViewModel.NotifyOnLowDiskSpace)
                    {
                        var msg   = $"Backup for '{project.Name}' was skipped due to low disk space on the backup target.";
                        var title = "Low disk space";

                        // Always go through the central notification service so we get
                        // consistent logging and behavior.
                        _notificationService.ShowWarning(
                            title,
                            msg,
                            NotificationKind.Backup);

                        if (IsOnBackupsPage)
                        {
                            // When the user is on the Backups page, also show an in-page banner
                            // so the warning is clearly visible where the action happened.
                            _backupsViewModel.ShowNotification(
                                $"Backup for '{project.Name}' was skipped because the backup target is almost full.",
                                "Warning");
                        }
                        else
                        {
                            // When the user is elsewhere, show a global toast.
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Warning,
                                title);
                        }

                        // System notification depends only on window foreground + settings.
                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Warning,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        _backupsViewModel.BackupCurrentFile = "Backup skipped: low disk space.";
                        _backupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                                ? ex.Message
                                : _backupsViewModel.BackupEtaText + " · Low disk space";
                    });
                }
                else
                {
                    // Generic backup failure path
                    if (NotificationsEnabled)
                    {
                        var msg   = $"Backup failed for '{project.Name}'. Check logs for details.";
                        var title = "Backup failed";

                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        _backupsViewModel.BackupCurrentFile = "Backup failed.";
                        _backupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                                ? ex.Message
                                : _backupsViewModel.BackupEtaText + " · Failed";
                    });
                }
            }
            finally
            {
                // Clear per-project cards once done
                _backupsViewModel.ClearActiveBackups();

                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
        }

        private async void OnCreateBackupForAllProjectsRequested()
        {
            // Do not start "backup all" if a backup is already running.
            if (_backupsViewModel.IsBusy)
            {
                Console.WriteLine("[AppViewModel] CreateBackupForAllProjectsRequested ignored because a backup is already in progress.");
                return;
            }

            Console.WriteLine("[AppViewModel] CreateBackupForAllProjectsRequested starting…");

            var cfg        = AppConfigStore.Load();
            var backupRoot = cfg.Backups.BackupRoot;
            if (string.IsNullOrWhiteSpace(backupRoot))
                return;

            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;

            var useArchiveMode = _settingsViewModel.UseBackupCompression;

            _backupsViewModel.BackupProgress    = 0;
            _backupsViewModel.BackupCurrentFile = "Preparing backup…";
            _backupsViewModel.BackupEtaText     = string.Empty;
            _backupsViewModel.IsBusy            = true;
            _backupsViewModel.BusyMessage       = "Backing up all projects…";

            try
            {
                await Task.Run(async () =>
                {
                    var projects = _repo.GetAllProjects().ToList();
                    Console.WriteLine($"[AppViewModel] Backing up {projects.Count} projects in parallel…");

                    if (projects.Count == 0)
                    {
                        Console.WriteLine("[AppViewModel] No projects found for backup-all.");
                        return;
                    }

                    var progressPerProject = new ConcurrentDictionary<int, double>();

                    // Reset per-project cards and add entry place-holders
                    _backupsViewModel.ClearActiveBackups();
                    foreach (var p in projects)
                    {
                        _backupsViewModel.UpdateActiveBackup(
                            p.Id.ToString(),
                            p.Name,
                            0,
                            "Preparing backup…",
                            string.Empty);
                    }

                    // Local helper to recompute aggregate progress and update the UI.
                    void UpdateAggregateProgress(string currentFile, string etaText)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (progressPerProject.IsEmpty)
                            {
                                _backupsViewModel.BackupProgress    = 0;
                                _backupsViewModel.BackupCurrentFile = "Preparing backup…";
                                _backupsViewModel.BackupEtaText     = string.Empty;
                                _backupsViewModel.BusyMessage       = "Backing up all projects…";
                                return;
                            }

                            var avg = progressPerProject.Values.DefaultIfEmpty(0).Average();
                            _backupsViewModel.BackupProgress = avg;

                            string label;
                            if (!string.IsNullOrWhiteSpace(currentFile))
                            {
                                label = currentFile;
                            }
                            else if (avg <= 0.1)
                            {
                                label = "Preparing backup…";
                            }
                            else if (avg < 100)
                            {
                                label = "Running backups…";
                            }
                            else
                            {
                                label = "All backups completed";
                            }

                            _backupsViewModel.BackupCurrentFile = label;
                            _backupsViewModel.BackupEtaText     = etaText;
                            _backupsViewModel.BusyMessage       = "Backing up all projects…";
                        });
                    }

                    var tasks = projects.Select(project =>
                    {
                        return _backupService.RunBackupAsync(
                            project,
                            backupRoot,
                            isAuto: false,
                            progressCallback: (percent, currentFile, etaText) =>
                            {
                                // Per-project label for its own card
                                string label;
                                if (!string.IsNullOrWhiteSpace(currentFile))
                                {
                                    label = currentFile;
                                }
                                else if (percent <= 0.1)
                                {
                                    label = "Preparing backup…";
                                }
                                else if (percent < 100)
                                {
                                    label = "Running backup…";
                                }
                                else
                                {
                                    label = "Completed";
                                }

                                progressPerProject[project.Id] = percent;
                                UpdateAggregateProgress(currentFile, etaText);

                                // Update that project's card
                                _backupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    percent,
                                    label,
                                    etaText);
                            },
                            useArchiveMode: useArchiveMode,
                            maxSnapshotsToKeep: maxSnapshotsToKeep,
                            minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent
                        ).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                Console.WriteLine($"[AppViewModel] Parallel backup failed for '{project.Name}' (Id={project.Id}): {t.Exception?.GetBaseException().Message}");
                            }
                        });
                    }).ToList();

                    await Task.WhenAll(tasks);

                    Console.WriteLine("[AppViewModel] All parallel backups completed.");
                });

                // First reload history so the new backups appear.
                ReloadBackupsVmData();
                await _dashboardViewModel.RefreshAsync();

                // --- After all backups: optional verification ---
                var cfgAfterAll = AppConfigStore.Load();
                if (cfgAfterAll.Backups.VerifyAfterCreate)
                {
                    var verifyService = new VerifyService(_repo, new HashService());
                    var allLatest = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
                                         .GroupBy(b => b.ProjectId)
                                         .Select(g => g.OrderByDescending(b => b.CreatedUtc).First());

                    foreach (var latest in allLatest)
                    {
                        var proj = _repo.GetAllProjects().FirstOrDefault(p => p.Id == latest.ProjectId);
                        if (proj == null) continue;

                        var folder = Path.Combine(backupRoot, latest.Path ?? "");
                        try
                        {
                            await verifyService.VerifyAsync(proj, folder, 100, full: true);
                        }
                        catch (Exception vex)
                        {
                            Console.WriteLine($"[AppViewModel] Verification exception for {proj?.Name}: {vex}");

                            if (NotificationsEnabled)
                            {
                                _notificationService.ShowError(
                                    "Backup verification failed",
                                    $"Verification failed for '{proj?.Name ?? "Unknown project"}'. The backup may be corrupted or incomplete.",
                                    NotificationKind.Backup);

                                if (!IsOnBackupsPage)
                                {
                                    var name  = proj?.Name ?? "Unknown project";
                                    var msg   = $"Verification failed for '{name}'. The backup may be corrupted or incomplete.";
                                    var title = "Backup verification failed";

                                    GlobalNotificationCenter.Instance.Show(
                                        msg,
                                        NotificationSeverity.Error,
                                        title);

                                    if (ShouldRaiseSystemNotification)
                                    {
                                        GlobalNotificationCenter.Instance.ShowSystem(
                                            msg,
                                            NotificationSeverity.Error,
                                            title);
                                    }
                                }
                            }

                            Dispatcher.UIThread.Post(() =>
                            {
                                var backupId = latest.Id.ToString();
                                _backupsViewModel.MarkSnapshotAsFailed(backupId);
                                _backupsViewModel.ShowVerificationFailure(backupId, proj?.Name ?? "Unknown project");
                            });
                        }
                    }
                }

                // Then clear the active backup cards on the UI thread,
                // so the overlay collapses only after history is updated.
                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.ClearActiveBackups();

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                    {
                        var msg   = "All project backups completed successfully.";
                        var title = "Backups completed";

                        _notificationService.ShowInfo(
                            title,
                            msg,
                            NotificationKind.Backup);

                        _backupsViewModel.ShowNotification(
                            msg,
                            "Info");

                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Info,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Info,
                                title);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppViewModel] Backup-all failed: {ex}");

                if (NotificationsEnabled)
                {
                    var msg   = "Backup all projects failed. Check logs for details.";
                    var title = "Backup-all failed";

                    if (!IsOnBackupsPage)
                    {
                        GlobalNotificationCenter.Instance.Show(
                            msg,
                            NotificationSeverity.Error,
                            title);
                    }

                    if (ShouldRaiseSystemNotification)
                    {
                        GlobalNotificationCenter.Instance.ShowSystem(
                            msg,
                            NotificationSeverity.Error,
                            title);
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.BackupCurrentFile = "Backup all projects failed.";
                    _backupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                            ? ex.Message
                            : _backupsViewModel.BackupEtaText + " · Failed";
                });

                // Clear cards on failure (ensure this runs on the UI thread)
                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.ClearActiveBackups();
                });
            }
            finally
            {
                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
        }

        private async void OnDeleteBackupRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;

            // Find backup row to know its relative path
            var allBackups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow);
            var backup     = allBackups.FirstOrDefault(b => b.Id == backupId);
            if (backup is null)
                return;

            var cfg        = AppConfigStore.Load();
            var backupRoot = cfg.Backups.BackupRoot;
            if (string.IsNullOrWhiteSpace(backupRoot))
                return;

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = "Deleting backup…";

            try
            {
                var fullPath = Path.Combine(backupRoot, backup.Path);

                await Task.Run(() =>
                {
                    try
                    {
                        if (Directory.Exists(fullPath))
                        {
                            Directory.Delete(fullPath, recursive: true);
                        }
                        else if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                        }

                        _repo.DeleteBackupById(backupId);
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"[OnDeleteBackupRequested] IOException while deleting '{fullPath}': {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Console.WriteLine($"[OnDeleteBackupRequested] UnauthorizedAccessException while deleting '{fullPath}': {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OnDeleteBackupRequested] Unexpected exception while deleting '{fullPath}': {ex}");
                    }
                });

                ReloadBackupsVmData();
                await _dashboardViewModel.RefreshAsync();
            }
            finally
            {
                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
        }

        private async void OnRestoreBackupRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;

            Console.WriteLine($"[AppViewModel] RestoreBackupRequested for backupId={backupId}.");

            // Look up the backup row so we know which project and path this backup belongs to.
            var allBackups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow);
            var backup     = allBackups.FirstOrDefault(b => b.Id == backupId);
            if (backup is null)
            {
                Console.WriteLine($"[AppViewModel] RestoreBackupRequested: no backup found with Id={backupId}.");
                return;
            }

            var cfg        = AppConfigStore.Load();
            var backupRoot = cfg.Backups.BackupRoot;
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                Console.WriteLine("[AppViewModel] RestoreBackupRequested: backup root is not configured.");
                return;
            }

            var backupFullPath = Path.Combine(backupRoot, backup.Path ?? string.Empty);

            // Find the associated project so we know where to restore to.
            var project = _repo.GetAllProjects().FirstOrDefault(p => p.Id == backup.ProjectId);
            if (project is null)
            {
                Console.WriteLine($"[AppViewModel] RestoreBackupRequested: no project found with Id={backup.ProjectId}.");
                return;
            }

            var projectRoot = project.RootPath;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                Console.WriteLine($"[AppViewModel] RestoreBackupRequested: project '{project.Name}' has no root path.");
                return;
            }

            if (!Directory.Exists(backupFullPath))
            {
                Console.WriteLine($"[AppViewModel] RestoreBackupRequested: backup folder '{backupFullPath}' does not exist.");
                return;
            }

            _backupsViewModel.IsBusy      = true;
            _backupsViewModel.BusyMessage = $"Restoring {project.Name}…";

            try
            {
                await Task.Run(() =>
                {
                    Console.WriteLine($"[AppViewModel] Restoring backup '{backupFullPath}' to '{projectRoot}'.");
                    RestoreDirectory(backupFullPath, projectRoot);
                });

                Console.WriteLine($"[AppViewModel] Restore completed for project '{project.Name}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppViewModel] Restore failed for project '{project.Name}': {ex}");

                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.BackupCurrentFile = "Restore failed.";
                    _backupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                            ? ex.Message
                            : _backupsViewModel.BackupEtaText + " · Restore failed";
                });
            }
            finally
            {
                _backupsViewModel.IsBusy      = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
        }

        private static void RestoreDirectory(string sourceDir, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(sourceDir))
                throw new ArgumentException("Source directory is required.", nameof(sourceDir));

            if (string.IsNullOrWhiteSpace(targetDir))
                throw new ArgumentException("Target directory is required.", nameof(targetDir));

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist.");

            // Ensure target root exists
            Directory.CreateDirectory(targetDir);

            // Create all directories
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, dirPath);
                var target   = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(target);
            }

            // Copy all files, overwriting existing ones but not deleting extras.
            foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, filePath);
                var target   = Path.Combine(targetDir, relative);

                var parentDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(filePath, target, overwrite: true);
            }
        }

        private void OnCancelActiveBackupRequested(BackupProgressItem? item)
        {
            if (item is null)
                return;

            Console.WriteLine($"[AppViewModel] Cancel requested for project '{item.ProjectName}' (Id={item.ProjectId}).");

            if (!int.TryParse(item.ProjectId, out var projectId))
            {
                Console.WriteLine($"[AppViewModel] Unable to parse ProjectId '{item.ProjectId}' for cancellation.");
                return;
            }

            // Actually cancel the running backup for this project.
            _backupService.CancelBackup(projectId);

            // Do NOT remove the active backup card immediately.
            // Let the backup operation observe the cancellation token and finish,
            // then the existing completion logic (finally blocks / ReloadBackupsVmData)
            // will clear the cards and refresh the UI.
        }

        // ---------- Tray entry points ----------

        /// <summary>
        /// Triggered from the tray menu: backup all projects.
        /// Reuses the same logic as the Backups page \"backup all\" action.
        /// </summary>
        
        /// <summary>
        /// Returns the list of backup-capable projects for use in the tray menu.
        /// </summary>
        public IReadOnlyList<ProjectBackupItem> GetProjectsForBackupTray()
        {
            return _backupsViewModel.ProjectBackups.ToList();
        }

        /// <summary>
        /// Triggered from the tray menu: backup a specific project by its ProjectBackupItem.Id.
        /// This reuses the same pipeline as the Backups page per-project backup.
        /// </summary>
        public void RequestBackupProjectFromTray(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            // Don't start if something is already running
            if (_backupsViewModel.IsBusy)
            {
                Console.WriteLine("[AppViewModel] Tray project-backup ignored because a backup is already in progress.");
                return;
            }

            var projectItem = _backupsViewModel.ProjectBackups.FirstOrDefault(p => p.Id == projectId);
            if (projectItem == null)
            {
                Console.WriteLine($"[AppViewModel] Tray project-backup: no ProjectBackupItem found for Id={projectId}.");
                return;
            }

            Console.WriteLine($"[AppViewModel] Tray: backup requested for project '{projectItem.Name}' (Id={projectItem.Id}).");

            // When triggered from tray, navigate to the Backups page so the user
            // immediately sees the running backup card (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateBackups?.CanExecute(null) == true)
                {
                    NavigateBackups.Execute(null);
                }
            });

            OnBackupProjectRequested(projectItem);
        }

        public void RequestBackupAllFromTray()
        {
            // Do not start if something is already running.
            if (_backupsViewModel.IsBusy)
            {
                Console.WriteLine("[AppViewModel] Tray backup-all ignored because a backup is already in progress.");
                return;
            }

            Console.WriteLine("[AppViewModel] Tray: backup all projects requested.");

            // When triggered from tray, navigate to the Backups page so the user
            // immediately sees the running backup cards (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateBackups?.CanExecute(null) == true)
                {
                    NavigateBackups.Execute(null);
                }
            });

            OnCreateBackupForAllProjectsRequested();
        }

        /// <summary>
        /// Triggered from the tray menu: backup the selected project.
        /// For now we simply navigate to the Backups page so the user can pick a project
        /// and start the backup from there. Later we can wire this to the actual selection.
        /// </summary>
        public void RequestBackupSelectedProjectFromTray()
        {
            Console.WriteLine("[AppViewModel] Tray: backup selected project requested.");

            // For now, just bring the Backups page into view.
            if (NavigateBackups?.CanExecute(null) == true)
            {
                NavigateBackups.Execute(null);
            }

            // TODO (later): once BackupsViewModel exposes the currently selected project,
            // call OnBackupProjectRequested with that item to start the backup directly.
        }

        /// <summary>
        /// Returns the list of projects used for snapshots (Projects page),
        /// for use in the tray's Snapshot submenu.
        /// Only returns projects that are actually added/tracked in VaultSync.
        /// Untracked/discovered entries normally have ProjectId <= 0 and should not appear in the tray.
        /// </summary>
public IReadOnlyList<ProjectItemViewModel> GetProjectsForSnapshotTray()
{
    // Only expose projects that are actually registered in the backup DB.
    return _projectsViewModel.Projects
        .Where(p => p.IsRegistered)
        .ToList();
}

        /// <summary>
        /// Triggered from the tray menu: create a snapshot for a specific project by name.
        /// This reuses the ProjectsViewModel.TakeSnapshotForProjectFromTrayAsync pipeline,
        /// which in turn calls the existing TakeSnapshot() logic.
        /// </summary>
        public async Task TakeSnapshotForProjectFromTrayAsync(string projectName)
        {
            // When triggered from tray, navigate to the Projects page so the user
            // immediately sees the snapshot activity (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateProjects?.CanExecute(null) == true)
                {
                    NavigateProjects.Execute(null);
                }
            });

            await _projectsViewModel.TakeSnapshotForProjectFromTrayAsync(projectName);
        }

        /// <summary>
        /// Triggered from the tray menu: create snapshots for all projects.
        /// This reuses the ProjectsViewModel.TakeSnapshotAllFromTrayAsync pipeline,
        /// which in turn calls the existing TakeSnapshot() logic for each project.
        /// </summary>
        public async Task TakeSnapshotAllFromTrayAsync()
        {
            // When triggered from tray, navigate to the Projects page so the user
            // immediately sees the snapshot activity (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateProjects?.CanExecute(null) == true)
                {
                    NavigateProjects.Execute(null);
                }
            });

            await _projectsViewModel.TakeSnapshotAllFromTrayAsync();
        }
    }
}