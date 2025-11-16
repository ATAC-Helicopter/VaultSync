using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;

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
            var projects = _repo.GetAllProjects();
            var backups  = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow);

            _backupsViewModel.LoadFromBackups(projects, backups);
        }

        private async void OnBackupProjectRequested(ProjectBackupItem? item)
        {
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

            _backupsViewModel.BackupProgress = 0;
            _backupsViewModel.BackupCurrentFile = "Preparing backup…";
            _backupsViewModel.BackupEtaText = string.Empty;
            _backupsViewModel.IsBusy = true;
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
                            _backupsViewModel.BackupProgress = percent;
                            _backupsViewModel.BackupCurrentFile = string.IsNullOrWhiteSpace(currentFile)
                                ? "Preparing backup…"
                                : currentFile;
                            _backupsViewModel.BackupEtaText = etaText;
                        });
                });

                ReloadBackupsVmData();
            }
            finally
            {
                _backupsViewModel.BackupProgress = 100;
                _backupsViewModel.BackupCurrentFile = "Completed";
                _backupsViewModel.BackupEtaText = string.Empty;
                _backupsViewModel.IsBusy = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
        }

        private async void OnCreateBackupForAllProjectsRequested()
        {
            var cfg        = AppConfigStore.Load();
            var backupRoot = cfg.Backups.BackupRoot;
            if (string.IsNullOrWhiteSpace(backupRoot))
                return;

            _backupsViewModel.BackupProgress = 0;
            _backupsViewModel.BackupCurrentFile = "Preparing backup…";
            _backupsViewModel.BackupEtaText = string.Empty;
            _backupsViewModel.IsBusy = true;
            _backupsViewModel.BusyMessage = "Backing up all projects…";

            try
            {
                await Task.Run(async () =>
                {
                    foreach (var project in _repo.GetAllProjects())
                    {
                        await _backupService.RunBackupAsync(
                            project,
                            backupRoot,
                            isAuto: false,
                            progressCallback: (percent, currentFile, etaText) =>
                            {
                                _backupsViewModel.BackupProgress = percent;
                                _backupsViewModel.BackupCurrentFile = string.IsNullOrWhiteSpace(currentFile)
                                    ? "Preparing backup…"
                                    : currentFile;
                                _backupsViewModel.BackupEtaText = etaText;
                            });
                    }
                });

                ReloadBackupsVmData();
            }
            finally
            {
                _backupsViewModel.BackupProgress = 100;
                _backupsViewModel.BackupCurrentFile = "Completed";
                _backupsViewModel.BackupEtaText = string.Empty;
                _backupsViewModel.IsBusy = false;
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

            _backupsViewModel.IsBusy = true;
            _backupsViewModel.BusyMessage = "Deleting backup…";

            try
            {
                var fullPath = Path.Combine(backupRoot, backup.Path);

                await Task.Run(() =>
                {
                    if (Directory.Exists(fullPath))
                        Directory.Delete(fullPath, recursive: true);
                    else if (File.Exists(fullPath))
                        File.Delete(fullPath);

                    _repo.DeleteBackupById(backupId);
                });

                ReloadBackupsVmData();
            }
            finally
            {
                _backupsViewModel.IsBusy = false;
                _backupsViewModel.BusyMessage = string.Empty;
            }
        }

        private void OnRestoreBackupRequested(BackupSnapshotItem? snapshot)
        {
            // Intentionally left as a stub for now.
            // Later: copy files from backup path back into the project root.
        }

        // ---------- Minimal ICommand implementation ----------

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Func<object?, bool>? _canExecute;

            public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            {
                _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

            public void Execute(object? parameter) => _execute(parameter);

            public event EventHandler? CanExecuteChanged
            {
                add    { }
                remove { }
            }
        }
    }
}