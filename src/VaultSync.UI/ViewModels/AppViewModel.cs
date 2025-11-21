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

namespace VaultSync.UI.ViewModels
{
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

            _repo = new SqliteRepository(cfg.DbPath);
            _repo.EnsureSchema();

            _backupService = new BackupService(_repo);

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

            // 5) Default route
            CurrentView  = _dashboardViewModel;
            HeaderTitle  = "Dashboard";
            HeaderKicker = "Overview";

            // 6) Navigation commands (using cached VMs)
            NavigateDashboard = new RelayCommand(_ =>
            {
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
                        useArchiveMode: useArchiveMode);
                });

                ReloadBackupsVmData();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppViewModel] Backup failed: {ex}");

                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.BackupCurrentFile = "Backup failed.";
                    _backupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(_backupsViewModel.BackupEtaText)
                            ? ex.Message
                            : _backupsViewModel.BackupEtaText + " · Failed";
                });
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
                            useArchiveMode: useArchiveMode
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

                // Then clear the active backup cards on the UI thread,
                // so the overlay collapses only after history is updated.
                Dispatcher.UIThread.Post(() =>
                {
                    _backupsViewModel.ClearActiveBackups();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppViewModel] Backup-all failed: {ex}");

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

    }
}