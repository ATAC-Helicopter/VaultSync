using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
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
        AppViewModel? appVm = _getViewModel();
        if (appVm is null)
            return;

        if (_window is null)
        {
            _window = new TrayPanelWindow();
            _window.Closed += (s, e) => _window = null;
            if (!IsLinux)
            {
                _window.Deactivated += (_, _) => Hide();
            }
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

        AppViewModel? appVm = _getViewModel();
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
        string title = LocalizationProvider.Service?.GetString("Shell.Title") ?? "VaultSync";
        string version = appVm.CurrentVersionDisplay;
        string header = string.IsNullOrWhiteSpace(version) ? title : $"{title} {version}";

        return new TrayPanelViewModel(
            header,
            LocalizationProvider.Service?.GetString("Tray.Tooltip") ?? "VaultSync - snapshots and backups",
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
                DiagnosticsLogger.RecordWithStack("TrayPanelService quit requested.");
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

    private static void LoadDestinations(TrayPanelViewModel viewModel, AppViewModel appVm)
    {
        IReadOnlyList<AppViewModel.DestinationProbeSummary> summaries = appVm.GetDestinationProbeSummaries();
        var items = new List<TrayPanelViewModel.TrayDestinationItem>();

        if (summaries.Count > 0)
        {
            foreach (AppViewModel.DestinationProbeSummary summary in summaries)
            {
                string name = string.IsNullOrWhiteSpace(summary.Alias) ? summary.Path : summary.Alias;
                items.Add(new TrayPanelViewModel.TrayDestinationItem(name, summary.Path, summary.Reachable));
            }
        }
        else
        {
            AppConfig cfg = appVm.GetConfigSnapshot();
            var configured = new List<BackupDestination>();
            if (cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 })
            {
                configured = [.. cfg.Backups.Destinations.Where(d => d.Active)];
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

            foreach (BackupDestination dest in configured)
            {
                string name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias;
                items.Add(new TrayPanelViewModel.TrayDestinationItem(name, dest.Path ?? string.Empty, true));
            }
        }

        string summaryText = BuildDestinationSummary(items, appVm.GetBackupPolicyTraySummary());
        viewModel.LoadDestinations(items, summaryText);
    }

    private static string BuildDestinationSummary(
        IReadOnlyCollection<TrayPanelViewModel.TrayDestinationItem> items,
        string policySummary)
    {
        string baseSummary = string.Empty;
        if (items.Count == 0)
        {
            baseSummary = LocalizationProvider.Service?.GetString("Tray.Destinations.None")
                   ?? "No destinations configured";
        }
        else
        {
            int reachable = items.Count(i => i.Reachable);
            if (reachable == items.Count)
            {
                baseSummary = LocalizationProvider.Service?.GetString("Tray.Destinations.Ready")
                       ?? "Ready";
            }
            else
            {
                string status = LocalizationProvider.Service?.GetString("Tray.Destinations.Unreachable")
                         ?? "Unreachable";
                baseSummary = $"{reachable}/{items.Count} {status}";
            }
        }

        if (string.IsNullOrWhiteSpace(policySummary))
            return baseSummary;

        return $"{baseSummary} - {policySummary}";
    }

    private static void LoadRecentBackups(TrayPanelViewModel viewModel, AppViewModel appVm)
    {
        IReadOnlyList<AppViewModel.TrayProjectBackups> recent = appVm.GetRecentBackupsForTray(4);
        var items = new List<TrayPanelViewModel.TrayRecentBackupItem>();

        foreach (AppViewModel.TrayProjectBackups project in recent)
        {
            foreach (AppViewModel.TrayBackupItem backup in project.Backups)
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

    private static void PositionWindow(Window window)
    {
        Avalonia.Platform.Screen? screen = window.Screens.Primary ?? window.Screens.ScreenFromVisual(window) ?? window.Screens.All.FirstOrDefault();
        if (screen == null) return;
        PixelRect working = screen.WorkingArea;
        int width = (int)Math.Ceiling(window.Bounds.Width > 0 ? window.Bounds.Width : window.Width);
        int height = (int)Math.Ceiling(window.Bounds.Height > 0 ? window.Bounds.Height : window.Height);
        int margin = 12;
        int inset = 80;
        int left = working.X;
        int top = working.Y;
        int right = working.X + working.Width;
        int bottom = working.Y + working.Height;

        int x;
        int y;

        int minX = left + margin;
        int maxX = right - width - margin;
        int minY = top + margin;
        int maxY = bottom - height - margin;

        if (IsLinux)
        {
            x = Clamp(right - width - margin, minX, maxX);
            y = Clamp(bottom - height - margin, minY, maxY);
        }
        else
        {
            // Prefer a centered-lower-right placement (not flush to the corner).
            x = Clamp(left + margin + inset, minX, maxX);
            y = Clamp(top + (int)Math.Round((bottom - top - height) * 0.60), minY, maxY);
        }

        window.Position = new PixelPoint(x, y);
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
        Window? window = _desktop.MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }
}
