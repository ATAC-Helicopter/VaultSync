using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.Core.Config;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
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

        public string CurrentViewKey
        {
            get => _currentViewKey;
            private set
            {
                if (_currentViewKey != value)
                {
                    _currentViewKey = value;
                    OnPropertyChanged(nameof(CurrentViewKey));
                }
            }
        }

        public void EnsureInitialView()
        {
            if (CurrentView is not null)
                return;

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(EnsureInitialView);
                return;
            }

            SetCurrentView("Dashboard", remember: false);
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

        public ICommand NavigateProjects { get; }

        public ICommand NavigateBackups { get; }

        public ICommand NavigateSettings { get; }

        private void SetCurrentView(string viewKey, bool remember = true)
        {
            if (_currentView is not null &&
                string.Equals(viewKey, _currentViewKey, StringComparison.OrdinalIgnoreCase))
                return;

            switch (viewKey)
            {
                case "Projects":
                    BackupsViewModel.IsActiveView = false;
                    _projectsViewModel.EnsureLoaded();
                    CurrentView = _projectsViewModel;
                    HeaderTitle = L("Nav.Projects", "Projects");
                    HeaderKicker = L("Main.HeaderProjects", "All repositories");
                    break;
                case "Backups":
                    BackupsViewModel.IsActiveView = true;
                    if (_backupsCacheProjects is not null && _backupsCacheBackups is not null)
                    {
                        BackupsViewModel.LoadFromBackups(
                            _backupsCacheProjects,
                            _backupsCacheBackups,
                            _backupsCacheDisabledAuto ?? new HashSet<int>());
                    }
                    var cacheFresh = (DateTime.UtcNow - _backupsCacheUpdatedUtc) < BackupsCacheTtl;
                    if (!cacheFresh || _backupsCachePartial)
                    {
                        _ = ReloadBackupsVmDataAsync(force: true);
                    }
                    else
                    {
                        QueueBackupsWarmLoadIfReady();
                    }
                    RefreshDestinationStatusOverview();
                    BackupsViewModel.RefreshActiveViewState();
                    CurrentView = BackupsViewModel;
                    HeaderTitle = L("Nav.Backups", "Backups");
                    HeaderKicker = L("Main.HeaderBackups", "Snapshots & history");
                    break;
                case "Settings":
                    BackupsViewModel.IsActiveView = false;
                    _settingsViewModel.RebindDestinationCredentials();
                    CurrentView = _settingsViewModel;
                    HeaderTitle = L("Nav.Settings", "Settings");
                    HeaderKicker = L("Main.HeaderSettings", "Preferences");
                    break;
                default:
                    BackupsViewModel.IsActiveView = false;
                    if (_lastDashboardRefreshUtc == DateTime.MinValue)
                    {
                        EnsureDashboardWarmLoad();
                    }
                    else if ((DateTime.UtcNow - _lastDashboardRefreshUtc) > DashboardRefreshTtl)
                    {
                        QueueDashboardWarmLoadIfReady();
                    }
                    CurrentView = DashboardViewModel;
                    HeaderTitle = L("Nav.Dashboard", "Dashboard");
                    HeaderKicker = L("Main.HeaderOverview", "Overview");
                    viewKey = "Dashboard";
                    break;
            }

            CurrentViewKey = viewKey;

            if (remember)
            {
                var viewToSave = viewKey;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var cfg = AppConfigStore.Load();
                        cfg.LastView = viewToSave;
                        AppConfigStore.Save(cfg);
                        Dispatcher.UIThread.Post(() => _config.LastView = viewToSave);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Config] Failed to persist last view: {ex.Message}");
                    }
                });
            }
        }

        private void EnsureDashboardWarmLoad()
        {
            if (Interlocked.Exchange(ref _dashboardWarmLoadQueued, 1) == 1)
                return;

            RunDetached(async () =>
            {
                try
                {
                    if (InitialDataLoadDelay > TimeSpan.Zero)
                        await Task.Delay(InitialDataLoadDelay).ConfigureAwait(false);
                    _lastDashboardRefreshUtc = DateTime.UtcNow;
                    await DashboardViewModel.RefreshAsync().ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _dashboardWarmLoadQueued, 0);
                }
            }, nameof(EnsureDashboardWarmLoad));
        }

        private void QueueDashboardWarmLoadIfReady()
        {
            if (Interlocked.Exchange(ref _dashboardWarmLoadScheduled, 1) == 1)
                return;

            var delay = WarmLoadStartupDelay - (DateTime.UtcNow - _appStartUtc);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            RunDetached(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay).ConfigureAwait(false);
                    if (CurrentViewKey == "Dashboard")
                    {
                        _lastDashboardRefreshUtc = DateTime.UtcNow;
                        EnsureDashboardWarmLoad();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _dashboardWarmLoadScheduled, 0);
                }
            }, nameof(QueueDashboardWarmLoadIfReady));
        }

        private void EnsureBackupsWarmLoad()
        {
            if (Interlocked.Exchange(ref _backupsWarmLoadQueued, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                if (InitialDataLoadDelay > TimeSpan.Zero)
                    await Task.Delay(InitialDataLoadDelay).ConfigureAwait(false);
                _ = ReloadBackupsVmDataAsync(force: true);
                Interlocked.Exchange(ref _backupsWarmLoadQueued, 0);
            });
        }

        private void QueueBackupsWarmLoadIfReady()
        {
            if (Interlocked.Exchange(ref _backupsWarmLoadScheduled, 1) == 1)
                return;

            var delay = WarmLoadStartupDelay - (DateTime.UtcNow - _appStartUtc);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            RunDetached(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay).ConfigureAwait(false);
                    if (CurrentViewKey == "Backups")
                    {
                        EnsureBackupsWarmLoad();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _backupsWarmLoadScheduled, 0);
                }
            }, nameof(QueueBackupsWarmLoadIfReady));
        }

        private void ApplyLastSessionView()
        {
            _ = Task.Run(() =>
            {
                var cfg = AppConfigStore.GetSnapshot();
                var last = string.IsNullOrWhiteSpace(cfg.LastView)
                    ? "Dashboard"
                    : cfg.LastView;
                Dispatcher.UIThread.Post(() => SetCurrentView(last, remember: false));
            });
        }
    }
}
