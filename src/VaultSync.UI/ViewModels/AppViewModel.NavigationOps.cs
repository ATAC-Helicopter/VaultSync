using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private const string DashboardViewKey = "Dashboard";
        private const string BackupsViewKey = "Backups";
        private readonly DesktopRouteCoordinator _desktopNavigation = new(
            DesktopRoute.ProtectOverview,
            (route, token) => Task.FromResult(LegacyDesktopRouteAdapter.TryGetLegacyKey(route, out _)));

        public bool CanNavigateBack => _desktopNavigation.CanGoBack;
        public bool CanNavigateForward => _desktopNavigation.CanGoForward;
        public DesktopRoute CurrentRoute => _desktopNavigation.Current;
        public ICommand NavigateBack { get; }
        public ICommand NavigateForward { get; }
        private PageLocationWriter? _pageLocationWriter;

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

            SetCurrentView(DashboardViewKey, remember: false);
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
        public ICommand NavigateDashboard
        {
            get;
        }

        public ICommand NavigateProjects
        {
            get;
        }

        public ICommand NavigateBackups
        {
            get;
        }

        public ICommand NavigateSchedule
        {
            get;
        }

        public ICommand NavigateHistory
        {
            get;
        }

        public ICommand NavigateRecovery
        {
            get;
        }

        public ICommand NavigateGuide
        {
            get;
        }

        public ICommand NavigateSettings
        {
            get;
        }

        private void SetCurrentView(string viewKey, bool remember = true)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => SetCurrentView(viewKey, remember));
                return;
            }
            // Keep the legacy shell's saved-location fallback during this first migration.
            DesktopRoute route = LegacyDesktopRouteAdapter.RestoreLegacyLocation(viewKey);
            _ = DetachedTask.RunAsync(async () =>
            {
                DesktopNavigationResult result = await _desktopNavigation.OpenAsync(route);
                if (result == DesktopNavigationResult.Opened ||
                    (result == DesktopNavigationResult.AlreadyCurrent && CurrentView is null))
                    ApplyResolvedPage(remember);
            }, nameof(SetCurrentView));
        }

        private void NavigatePageHistory(bool forward)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => NavigatePageHistory(forward));
                return;
            }
            _ = DetachedTask.RunAsync(async () =>
            {
                DesktopNavigationResult result = forward
                    ? await _desktopNavigation.ForwardAsync()
                    : await _desktopNavigation.BackAsync();
                if (result == DesktopNavigationResult.Opened)
                    ApplyResolvedPage(remember: true);
            }, nameof(NavigatePageHistory));
        }

        private void ApplyResolvedPage(bool remember)
        {
            if (!LegacyDesktopRouteAdapter.TryGetLegacyKey(CurrentRoute, out string? viewKey))
                throw new InvalidOperationException("Resolved desktop route has no legacy page.");
            RenderCurrentPage(viewKey!, remember);
            OnPropertyChanged(nameof(CurrentRoute));
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(CanNavigateForward));
            (NavigateBack as RelayCommand)?.RaiseCanExecuteChanged();
            (NavigateForward as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RenderCurrentPage(string viewKey, bool remember)
        {
            if (_currentView is not null &&
                string.Equals(viewKey, _currentViewKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            switch (viewKey)
            {
                case "Projects":
                    BackupsViewModel.IsActiveView = false;
                    CurrentViewName = "Projects";
                    CurrentView = _projectsViewModel;
                    HeaderTitle = AppViewModel.L("Nav.Projects", "Projects");
                    HeaderKicker = AppViewModel.L("Main.HeaderProjects", "All repositories");
                    Dispatcher.UIThread.Post(_projectsViewModel.EnsureLoaded, DispatcherPriority.Background);
                    break;
                case BackupsViewKey:
                    BackupsViewModel.IsActiveView = true;
                    CurrentViewName = BackupsViewKey;
                    CurrentView = BackupsViewModel;
                    HeaderTitle = AppViewModel.L("Nav.Backups", BackupsViewKey);
                    HeaderKicker = AppViewModel.L("Main.HeaderBackups", "Snapshots & restore");

                    if (_backupsCacheProjects is not null && _backupsCacheBackups is not null)
                    {
                        Dispatcher.UIThread.Post(
                            () => BackupsViewModel.LoadFromBackups(
                                _backupsCacheProjects,
                                _backupsCacheBackups,
                                _backupsCacheDisabledAuto ?? []),
                            DispatcherPriority.Background);
                    }

                    bool cacheFresh = (DateTime.UtcNow - _backupsCacheUpdatedUtc) < BackupsCacheTtl;
                    if (!cacheFresh || _backupsCachePartial)
                    {
                        _ = ReloadBackupsVmDataAsync(force: true);
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(QueueBackupsWarmLoadIfReady, DispatcherPriority.Background);
                    }

                    Dispatcher.UIThread.Post(RefreshDestinationStatusOverview, DispatcherPriority.Background);
                    Dispatcher.UIThread.Post(BackupsViewModel.RefreshActiveViewState, DispatcherPriority.Background);
                    break;
                case "Settings":
                    BackupsViewModel.IsActiveView = false;
                    _settingsViewModel.RebindDestinationCredentials();
                    CurrentViewName = "Settings";
                    CurrentView = _settingsViewModel;
                    HeaderTitle = AppViewModel.L("Nav.Settings", "Settings");
                    HeaderKicker = AppViewModel.L("Main.HeaderSettings", "Preferences");
                    break;
                case "Schedule":
                    BackupsViewModel.IsActiveView = false;
                    CurrentViewName = "Schedule";
                    _scheduleViewModel.Refresh();
                    CurrentView = _scheduleViewModel;
                    HeaderTitle = AppViewModel.L("Nav.Schedule", "Schedule");
                    HeaderKicker = AppViewModel.L("Schedule.Overview.Title", "Your protection schedule");
                    break;
                case "History":
                    BackupsViewModel.IsActiveView = false;
                    CurrentViewName = "History";
                    CurrentView = HistoryViewModel;
                    HeaderTitle = AppViewModel.L("Nav.History", "History");
                    HeaderKicker = AppViewModel.L("Main.HeaderHistory", "Project timeline");
                    _ = HistoryViewModel.RefreshAsync();
                    break;
                case "Recovery":
                    BackupsViewModel.IsActiveView = false;
                    CurrentViewName = "Recovery";
                    CurrentView = RecoveryViewModel;
                    HeaderTitle = AppViewModel.L("Nav.Recovery", "Recovery");
                    HeaderKicker = AppViewModel.L("Main.HeaderRecovery", "Readiness & coverage");
                    // Page refresh is independent of update-check cancellation.
                    _ = RecoveryViewModel.RefreshAsync(cancellationToken: CancellationToken.None);
                    break;
                case "Guide":
                    BackupsViewModel.IsActiveView = false;
                    CurrentViewName = "Guide";
                    CurrentView = GuideViewModel;
                    HeaderTitle = AppViewModel.L("Nav.Guide", "Guide");
                    HeaderKicker = AppViewModel.L("Main.HeaderGuide", "Setup, backup & recovery");
                    RunDetached(GuideViewModel.RefreshAsync, nameof(GuideViewModel.RefreshAsync));
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
                    CurrentViewName = DashboardViewKey;
                    CurrentView = DashboardViewModel;
                    HeaderTitle = AppViewModel.L("Nav.Dashboard", DashboardViewKey);
                    HeaderKicker = AppViewModel.L("Main.HeaderOverview", "Overview");
                    viewKey = DashboardViewKey;
                    break;
            }

            CurrentViewKey = viewKey;

            if (remember)
            {
                _pageLocationWriter ??= new PageLocationWriter(
                    viewToSave =>
                    {
                        AppConfig cfg = _configStore.Load();
                        cfg.LastView = viewToSave;
                        _configStore.Save(cfg);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (CurrentViewKey == viewToSave)
                                _config.LastView = viewToSave;
                        });
                        return Task.CompletedTask;
                    },
                    ex => Console.WriteLine($"[Config] Failed to persist last view: {ex.Message}"));
                _ = _pageLocationWriter.Enqueue(viewKey);
            }
        }

        private void EnsureDashboardWarmLoad()
        {
            if (Interlocked.Exchange(ref _dashboardWarmLoadQueued, 1) == 1)
                return;

            AppViewModel.RunDetached(async () =>
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

            TimeSpan delay = WarmLoadStartupDelay - (DateTime.UtcNow - _appStartUtc);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            AppViewModel.RunDetached(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay).ConfigureAwait(false);
                    if (CurrentViewKey == DashboardViewKey)
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

            TimeSpan delay = WarmLoadStartupDelay - (DateTime.UtcNow - _appStartUtc);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            AppViewModel.RunDetached(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay).ConfigureAwait(false);
                    if (CurrentViewKey == BackupsViewKey)
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
            // Config is already loaded. Do not let a delayed startup callback override
            // a page the user has opened in the meantime.
            SetCurrentView(_config.LastView ?? DashboardViewKey, remember: false);
        }
    }
}
