using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;
using VaultSync.UI.Views;

namespace VaultSync.UI.Services;

public sealed class TrayPanelService : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Func<AppViewModel?> _getViewModel;
    private TrayPanelWindow? _window;
    private TrayPanelViewModel? _viewModel;

    public TrayPanelService(IClassicDesktopStyleApplicationLifetime desktop, Func<AppViewModel?> getViewModel)
    {
        _desktop = desktop;
        _getViewModel = getViewModel;
    }

    public void Toggle()
    {
        if (_window is { IsVisible: true })
        {
            Hide();
            return;
        }

        Show();
    }

    public void Show()
    {
        var appVm = _getViewModel();
        if (appVm is null)
            return;

        if (_window is null)
        {
            _window = new TrayPanelWindow();
            _window.Deactivated += (_, _) => Hide();
        }

        _viewModel ??= CreateViewModel(appVm);
        UpdateViewModel(appVm);
        _window.DataContext = _viewModel;
        _window.Show();
        _window.Activate();
        Dispatcher.UIThread.Post(() => PositionWindow(_window), DispatcherPriority.Background);
    }

    public void Hide()
    {
        _window?.Hide();
    }

    public void Refresh()
    {
        if (_window is not { IsVisible: true })
            return;

        var appVm = _getViewModel();
        if (appVm is null || _viewModel is null)
            return;

        UpdateViewModel(appVm);
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            _window.Close();
            _window = null;
        }
        _viewModel = null;
    }

    private TrayPanelViewModel CreateViewModel(AppViewModel appVm)
    {
        var title = "VaultSync";
        var version = appVm.CurrentVersionDisplay;
        var header = string.IsNullOrWhiteSpace(version) ? title : $"{title} {version}";

        return new TrayPanelViewModel(
            header,
            LocalizationProvider.Service?.GetString("Tray.Tooltip") ?? "VaultSync - snapshots & backups",
            openApp: () =>
            {
                BringMainWindowToFront();
                Hide();
            },
            backupAll: () =>
            {
                appVm.RequestBackupAllFromTray();
                Hide();
            },
            snapshotAll: () =>
            {
                _ = appVm.TakeSnapshotAllFromTrayAsync();
                Hide();
            },
            openBackups: () =>
            {
                BringMainWindowToFront();
                appVm.NavigateBackups?.Execute(null);
                Hide();
            },
            openSettings: () =>
            {
                BringMainWindowToFront();
                appVm.NavigateSettings?.Execute(null);
                Hide();
            },
            quit: () =>
            {
                App.MarkShuttingDown();
                _desktop.Shutdown();
            },
            close: Hide);
    }

    private void UpdateViewModel(AppViewModel appVm)
    {
        if (_viewModel is null)
            return;

        LoadDestinations(_viewModel, appVm);
        LoadRecentBackups(_viewModel, appVm);
    }

    private void LoadDestinations(TrayPanelViewModel viewModel, AppViewModel appVm)
    {
        var summaries = appVm.GetDestinationProbeSummaries();
        var items = new List<TrayPanelViewModel.TrayDestinationItem>();

        if (summaries.Count > 0)
        {
            foreach (var summary in summaries)
            {
                var name = string.IsNullOrWhiteSpace(summary.Alias) ? summary.Path : summary.Alias;
                items.Add(new TrayPanelViewModel.TrayDestinationItem(name, summary.Path, summary.Reachable));
            }
        }
        else
        {
            var cfg = appVm.GetConfigSnapshot();
            var configured = new List<BackupDestination>();
            if (cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 })
            {
                configured = cfg.Backups.Destinations.Where(d => d.Active).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(cfg.Backups.BackupLocation))
            {
                configured.Add(new BackupDestination
                {
                    Alias       = "Primary",
                    Path        = cfg.Backups.BackupLocation,
                    Active      = true,
                    PreMounted  = true,
                    AutoMount   = false,
                    AutoUnmount = false
                });
            }

            foreach (var dest in configured)
            {
                var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias;
                items.Add(new TrayPanelViewModel.TrayDestinationItem(name, dest.Path ?? string.Empty, true));
            }
        }

        var summaryText = BuildDestinationSummary(items);
        viewModel.LoadDestinations(items, summaryText);
    }

    private static string BuildDestinationSummary(IReadOnlyCollection<TrayPanelViewModel.TrayDestinationItem> items)
    {
        if (items.Count == 0)
        {
            return LocalizationProvider.Service?.GetString("Tray.Destinations.None")
                   ?? "No destinations configured";
        }

        var reachable = items.Count(i => i.Reachable);
        if (reachable == items.Count)
        {
            return LocalizationProvider.Service?.GetString("Tray.Destinations.Ready")
                   ?? "Ready";
        }

        var status = LocalizationProvider.Service?.GetString("Tray.Destinations.Unreachable")
                     ?? "Unreachable";
        return $"{reachable}/{items.Count} {status}";
    }

    private void LoadRecentBackups(TrayPanelViewModel viewModel, AppViewModel appVm)
    {
        var recent = appVm.GetRecentBackupsForTray(4);
        var items = new List<TrayPanelViewModel.TrayRecentBackupItem>();

        foreach (var project in recent)
        {
            foreach (var backup in project.Backups)
            {
                var item = new TrayPanelViewModel.TrayRecentBackupItem(
                    project.ProjectName,
                    backup.Label,
                    backup.IsProtected,
                    openFolder: () => appVm.OpenBackupFolderFromTray(backup.Id),
                    viewInApp: () => appVm.ShowBackupInAppFromTray(backup.ProjectId),
                    toggleKeep: () => appVm.ToggleBackupProtectionFromTray(backup.Id),
                    delete: () => appVm.DeleteBackupFromTray(backup.Id));
                items.Add(item);
            }
        }

        viewModel.LoadRecentBackups(items.Take(6));
    }

    private void PositionWindow(Window window)
    {
        var screen = window.Screens.Primary;
        var working = screen.WorkingArea;
        var width = (int)Math.Ceiling(window.Bounds.Width > 0 ? window.Bounds.Width : window.Width);
        var height = (int)Math.Ceiling(window.Bounds.Height > 0 ? window.Bounds.Height : window.Height);
        var margin = 12;
        var inset = 80;
        var left = working.X;
        var top = working.Y;
        var right = working.X + working.Width;
        var bottom = working.Y + working.Height;

        int x;
        int y;
        // Bottom-right, but clamp to working area to avoid off-screen placement.
        var minX = left + margin;
        var maxX = right - width - margin;
        var minY = top + margin;
        var maxY = bottom - height - margin;

        // Prefer a centered-lower-right placement (not flush to the corner).
        var desiredX = left + margin + inset;
        var desiredY = top + (int)Math.Round((bottom - top - height) * 0.60);

        window.Position = new PixelPoint(
            Clamp(desiredX, minX, maxX),
            Clamp(desiredY, minY, maxY));
    }

    private static int Clamp(int value, int min, int max)
    {
        if (min > max)
            return min;
        if (value < min)
            return min;
        return value > max ? max : value;
    }

    private void BringMainWindowToFront()
    {
        var window = _desktop.MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }
}
