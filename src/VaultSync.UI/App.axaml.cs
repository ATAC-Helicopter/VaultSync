using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VaultSync.Core.Config;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;
using System.Globalization;

namespace VaultSync.UI;

public partial class App : Application
{
    public static bool IsShuttingDown { get; private set; }
    public static bool IsCrashing { get; private set; }

    public static AppViewModel? AppViewModelInstance { get; private set; }

    // Keep a reference to the tray/menu-bar icon so it stays alive.
    private TrayIcon? _trayIcon;
    private static App? _instance;
    private static bool _trayRecentLatestOnly = true;
    private const string DefaultDriveHealthLabel = "Storage health: tap Recheck";
    private static string _cachedDriveHealthLabel = DefaultDriveHealthLabel;
    private static DriveHealthStatus _cachedDriveHealthStatus = DriveHealthStatus.Unknown;
    private const int MaxRecentBackupsPerProject = 3;

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    internal static void MarkCrashing()
    {
        if (IsCrashing)
            return;

        IsCrashing = true;
        IsShuttingDown = true;
        GlobalNotificationCenter.Instance.SuppressNotifications = true;
    }

    internal static void MarkShuttingDown()
    {
        IsShuttingDown = true;
    }

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _instance = this;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CrashHandler.RegisterAvalonia();
            WireGlobalExceptionHandlers();
            WireLifecycleBreadcrumbs(desktop);

            AppViewModelInstance = new AppViewModel();
            if (LocalizationProvider.Service is { } locService)
            {
                locService.LanguageChanged += () =>
                {
                    _cachedDriveHealthLabel = L("Tray.Health.DefaultLabel", DefaultDriveHealthLabel);
                    if (_trayIcon is not null)
                    {
                        _trayIcon.ToolTipText = L("Tray.Tooltip", "VaultSync - snapshots & backups");
                    }
                    RefreshTrayMenu();
                };
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = AppViewModelInstance
            };

            // Small always-on-top widget that lights up for tray-started backups.
            var backupWidgetService = new BackupWidgetService(
                desktop,
                AppViewModelInstance.BackupsViewModel,
                () => BringMainWindowToFront(desktop));
            AppViewModelInstance.AttachBackupWidgetService(backupWidgetService);
            AppViewModelInstance.TrayMenuRefreshRequested += RefreshTrayMenu;
            AppViewModelInstance.SettingsViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.ShowTrayIcon))
                {
                    UpdateTrayIconVisibility(desktop);
                }
                else if (e.PropertyName == nameof(SettingsViewModel.RunInBackground))
                {
                    // no-op here; MainWindow reads config on closing, so saving is enough.
                }
            };

            // Wire a platform-aware system notification service; fall back to stub if unavailable.
            GlobalNotificationCenter.Instance.SystemNotificationService =
                CreateSystemNotificationService() ?? new StubSystemNotificationService();
            GlobalNotificationCenter.Instance.ShouldShowSystemNotification = request =>
            {
                var cfg = AppConfigStore.Load();
                if (!cfg.Notifications.UseOsNotifications)
                    return false;
                if (!cfg.Notifications.OnBackupSuccess &&
                    !cfg.Notifications.OnBackupFailure &&
                    !cfg.Notifications.OnSnapshotSuccess &&
                    !cfg.Notifications.OnSnapshotFailure &&
                    !cfg.Notifications.OnLowDisk)
                    return false;

                if (cfg.Notifications.OnlyWhenInactive && MainWindow.IsForeground)
                    return false;

                return true;
            };

            // Read behavior config and, if enabled, create a tray/menu-bar icon.
            var config = AppConfigStore.Load();
            if (config.Behavior?.ShowTrayIcon == true)
            {
                CreateTrayIcon(desktop);
            }
        }

        // Apply theme from stored config on startup
        ApplyThemeFromConfig();

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Avoid creating multiple tray icons.
        if (_trayIcon != null)
            return;

        // Use a dedicated embedded tray icon resource.
        // Make sure Assets/vaultsync-tray.png exists and is marked as AvaloniaResource in the csproj.
        var uri = new Uri("avares://VaultSync.UI/Assets/vaultsync-tray.png");
        using var iconStream = AssetLoader.Open(uri);
        var trayIconSource = new WindowIcon(iconStream);

        _trayIcon = new TrayIcon
        {
            Icon = trayIconSource,
            ToolTipText = L("Tray.Tooltip", "VaultSync - snapshots & backups")
        };

        var menu = BuildTrayMenu(desktop);

        _trayIcon.Menu = menu;
        _trayIcon.IsVisible = true;
    }

    private void DestroyTrayIcon()
    {
        if (_trayIcon is null)
            return;

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void UpdateTrayIconVisibility(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cfg = AppConfigStore.Load();
        if (cfg.Behavior?.ShowTrayIcon == true)
        {
            if (_trayIcon is null)
            {
                CreateTrayIcon(desktop);
            }
            else
            {
                // Ensure the menu stays fresh when the icon is toggled on/off.
                RefreshTrayMenu();
            }
        }
        else
        {
            DestroyTrayIcon();
        }
    }

    private NativeMenu BuildTrayMenu(
        IClassicDesktopStyleApplicationLifetime desktop,
        IReadOnlyList<AppViewModel.TrayProjectBackups>? recentBackups = null)
    {
        // Build a small context menu: header / Open / Backup / Snapshot / Recent backups / Quit.
        var menu = new NativeMenu();

        // Header (disabled) to give the menu a title and tighter OS alignment.
        menu.Items.Add(new NativeMenuItem(L("Tray.Header", "VaultSync")) { IsEnabled = false });
        if (BuildDriveHealthItem(desktop) is { } healthItem)
        {
            menu.Items.Add(healthItem);
        }
        var destinationSummaries = AppViewModelInstance?.GetDestinationProbeSummaries()
            ?? Array.Empty<AppViewModel.DestinationProbeSummary>();
        var destinationRootItem = new NativeMenuItem(L("Tray.Destinations.Title", "Destinations"));
        var destinationMenu = new NativeMenu();

        if (destinationSummaries.Any())
        {
            foreach (var dest in destinationSummaries)
            {
                var status = dest.Reachable
                    ? L("Tray.Destinations.Ready", "Ready")
                    : L("Tray.Destinations.Unreachable", "Unreachable");
                var text = string.IsNullOrWhiteSpace(dest.Alias)
                    ? $"{dest.Path} -> {status}"
                    : $"{dest.Alias} -> {status}";

                var detail = new NativeMenuItem(text) { IsEnabled = false };
                destinationMenu.Items.Add(detail);
            }
        }
        else
        {
            destinationMenu.Items.Add(new NativeMenuItem(L("Tray.Destinations.None", "No destinations configured")) { IsEnabled = false });
        }

        destinationRootItem.Menu = destinationMenu;
        menu.Items.Add(destinationRootItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        // Open main window
        var openItem = new NativeMenuItem(L("Tray.Open", "Open VaultSync"));
        openItem.Click += (_, _) =>
        {
            var window = desktop.MainWindow;
            if (window is null)
                return;

            // If the window was hidden (RunInBackground + X pressed), show it again.
            if (!window.IsVisible)
                window.Show();

            // If minimized, restore.
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();
        };

        // ---------- Backup submenu ----------
        var backupRootItem = new NativeMenuItem(L("Tray.Backup.Title", "Backup"));
        var backupMenu = new NativeMenu();

        var backupProjects = AppViewModelInstance?.GetProjectsForBackupTray()
                             ?? Array.Empty<ProjectBackupItem>();

        if (backupProjects.Any())
        {
            foreach (var project in backupProjects)
            {
                var projectId = project.Id;
                var projectName = project.Name;

                var projectBackupItem = new NativeMenuItem(projectName);
                projectBackupItem.Click += (_, _) =>
                {
                    BringWindowToFrontIfUserWants(desktop);
                    AppViewModelInstance?.RequestBackupProjectFromTray(projectId);
                };

                backupMenu.Items.Add(projectBackupItem);
            }

            backupMenu.Items.Add(new NativeMenuItemSeparator());

            var backupAllItem = new NativeMenuItem(L("Tray.Backup.All", "Backup all projects"));
            backupAllItem.Click += (_, _) =>
            {
                BringWindowToFrontIfUserWants(desktop);
                AppViewModelInstance?.RequestBackupAllFromTray();
            };
            backupMenu.Items.Add(backupAllItem);
        }
        else
        {
            backupMenu.Items.Add(new NativeMenuItem(L("Tray.Common.NoProjects", "No projects available")) { IsEnabled = false });
        }

        backupRootItem.Menu = backupMenu;

        // ---------- Snapshot submenu ----------
        var snapshotRootItem = new NativeMenuItem(L("Tray.Snapshot.Title", "Snapshot"));
        var snapshotMenu = new NativeMenu();

        var snapshotProjects = AppViewModelInstance?.GetProjectsForSnapshotTray()
                               ?? Array.Empty<ProjectItemViewModel>();

        if (snapshotProjects.Any())
        {
            foreach (var project in snapshotProjects)
            {
                var projectName = project.Name;

                var projectSnapshotItem = new NativeMenuItem(projectName);
                projectSnapshotItem.Click += async (_, _) =>
                {
                    BringWindowToFrontIfUserWants(desktop);

                    if (AppViewModelInstance is not null)
                    {
                        await AppViewModelInstance.TakeSnapshotForProjectFromTrayAsync(projectName);
                    }
                };

                snapshotMenu.Items.Add(projectSnapshotItem);
            }

            snapshotMenu.Items.Add(new NativeMenuItemSeparator());

            var snapshotAllItem = new NativeMenuItem(L("Tray.Snapshot.All", "Snapshot all projects"));
            snapshotAllItem.Click += async (_, _) =>
            {
                BringWindowToFrontIfUserWants(desktop);

                if (AppViewModelInstance is not null)
                {
                    await AppViewModelInstance.TakeSnapshotAllFromTrayAsync();
                }
            };
            snapshotMenu.Items.Add(snapshotAllItem);
        }
        else
        {
            snapshotMenu.Items.Add(new NativeMenuItem(L("Tray.Common.NoProjects", "No projects available")) { IsEnabled = false });
        }

        snapshotRootItem.Menu = snapshotMenu;

        // ---------- Recent backups (keep/delete) ----------
        var manageBackupsRoot = new NativeMenuItem(L("Tray.Recent.Title", "Recent backups"));
        var manageBackupsMenu = new NativeMenu();

        var recentByProject = recentBackups
                              ?? AppViewModelInstance?.GetRecentBackupsForTray(MaxRecentBackupsPerProject)
                              ?? Array.Empty<AppViewModel.TrayProjectBackups>();

        var latestOnlyToggle = new NativeMenuItem(L("Tray.Recent.LatestOnly", "Show only latest per project"))
        {
            IsChecked = _trayRecentLatestOnly
        };
        latestOnlyToggle.Click += (_, _) =>
        {
            _trayRecentLatestOnly = !_trayRecentLatestOnly;
            RefreshTrayMenu();
        };
        manageBackupsMenu.Items.Add(latestOnlyToggle);
        manageBackupsMenu.Items.Add(new NativeMenuItemSeparator());

        var anyBackups = false;
        foreach (var project in recentByProject)
        {
            if (!project.Backups.Any())
                continue;

            anyBackups = true;

            // Project header (disabled) to avoid deep nesting.
            manageBackupsMenu.Items.Add(new NativeMenuItem(project.ProjectName) { IsEnabled = false });

            var backupsToShow = _trayRecentLatestOnly
                ? project.Backups.Take(1)
                : project.Backups;

            foreach (var backup in backupsToShow)
            {
                // Restore the older compact format: timestamp label + indented actions.
                manageBackupsMenu.Items.Add(new NativeMenuItem(backup.Label) { IsEnabled = false });

                var keepLabel = backup.IsProtected ? L("Tray.Recent.Unkeep", "Unkeep") : L("Tray.Recent.Keep", "Keep");
                var keepItem = new NativeMenuItem(Lf("Tray.Recent.ActionFormat", "   * {0}", keepLabel));
                keepItem.Click += (_, _) => AppViewModelInstance?.ToggleBackupProtectionFromTray(backup.Id);

                var deleteItem = new NativeMenuItem(Lf("Tray.Recent.ActionFormat", "   * {0}", L("Tray.Recent.Delete", "Delete")));
                deleteItem.Click += (_, _) => AppViewModelInstance?.DeleteBackupFromTray(backup.Id);

                var openFolderItem = new NativeMenuItem(Lf("Tray.Recent.ActionFormat", "   * {0}", L("Tray.Recent.OpenFolder", "Open folder")));
                openFolderItem.Click += (_, _) => AppViewModelInstance?.OpenBackupFolderFromTray(backup.Id);

                var viewInAppItem = new NativeMenuItem(Lf("Tray.Recent.ActionFormat", "   * {0}", L("Tray.Recent.ViewInApp", "View in VaultSync")));
                viewInAppItem.Click += (_, _) => AppViewModelInstance?.ShowBackupInAppFromTray(backup.ProjectId);

                manageBackupsMenu.Items.Add(keepItem);
                manageBackupsMenu.Items.Add(deleteItem);
                manageBackupsMenu.Items.Add(openFolderItem);
                manageBackupsMenu.Items.Add(viewInAppItem);
                manageBackupsMenu.Items.Add(new NativeMenuItemSeparator());
            }

            // Trim trailing separator after the last backup for this project.
            if (manageBackupsMenu.Items.LastOrDefault() is NativeMenuItemSeparator)
                manageBackupsMenu.Items.RemoveAt(manageBackupsMenu.Items.Count - 1);
        }

        if (anyBackups)
        {
            // Ensure we have exactly one separator before the footer link.
            if (manageBackupsMenu.Items.LastOrDefault() is not NativeMenuItemSeparator)
                manageBackupsMenu.Items.Add(new NativeMenuItemSeparator());

            var openBackups = new NativeMenuItem(L("Tray.Recent.OpenInApp", "Open in VaultSync"));
            openBackups.Click += (_, _) =>
            {
                BringMainWindowToFront(desktop);
                AppViewModelInstance?.NavigateBackups?.Execute(null);
            };
            manageBackupsMenu.Items.Add(openBackups);
        }
        else
        {
            manageBackupsMenu.Items.Add(new NativeMenuItem(L("Tray.Recent.None", "No backups yet")) { IsEnabled = false });
        }

        manageBackupsRoot.Menu = manageBackupsMenu;
        menu.Items.Add(manageBackupsRoot);

        var separator1 = new NativeMenuItemSeparator();
        var separator2 = new NativeMenuItemSeparator();

        var quitItem = new NativeMenuItem(L("Tray.Quit", "Quit VaultSync"));
        quitItem.Click += (_, _) =>
        {
            // Tell the window we're intentionally shutting down so it doesn't hijack the close.
            IsShuttingDown = true;
            desktop.Shutdown();
        };

        menu.Items.Add(openItem);
        menu.Items.Add(separator1);
        menu.Items.Add(backupRootItem);
        menu.Items.Add(snapshotRootItem);
        menu.Items.Add(separator2);
        menu.Items.Add(quitItem);

        return menu;
    }

    /// <summary>
    /// Global crash/exception hooks that log anonymised details before the process exits.
    /// </summary>
    private static void WireGlobalExceptionHandlers()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (ex is not null)
                {
                    Telemetry.Log("app_crash", b => b
                        .WithException(ex)
                        .WithCode("source", "unhandled"));
                }
                else
                {
                    Telemetry.Log("app_crash", b => b
                        .WithCode("source", "unhandled")
                        .WithCode("detail", "non_exception"));
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    Telemetry.Log("app_crash", b => b
                        .WithException(e.Exception)
                        .WithCode("source", "unobserved_task"));
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    e.SetObserved();
                }
            };
        }
        catch
        {
            // Swallow to avoid startup failures; telemetry must never break the app.
        }
    }

    /// <summary>
    /// Lifecycle breadcrumbs for start/exit to correlate crash sessions.
    /// </summary>
    private static void WireLifecycleBreadcrumbs(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var cfg = AppConfigStore.Load();
            Telemetry.SetEnabled(cfg.Advanced.SendUsageStats);
            Telemetry.SetSessionId(Guid.NewGuid());

            Telemetry.Log("app_start");

            desktop.Exit += (_, _) =>
            {
                Telemetry.Log("app_exit", b => b.WithCode("source", "desktop_exit"));
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Telemetry.Log("app_exit", b => b.WithCode("source", "process_exit"));
            };
        }
        catch
        {
            // Never throw from telemetry wiring.
        }
    }

    private NativeMenuItem BuildDriveHealthItem(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var cfg        = AppConfigStore.Load();
            var backupRoot = cfg.Backups.BackupRoot ?? string.Empty;
            var driveLabel = FormatDriveLabel(backupRoot);
            if (_cachedDriveHealthLabel == DefaultDriveHealthLabel)
            {
                _cachedDriveHealthLabel = L("Tray.Health.DefaultLabel", DefaultDriveHealthLabel);
            }

            var healthMenu = new NativeMenuItem(L("Tray.Health.Title", "Storage health"));
            var statusMenu = new NativeMenu();

            var statusLabel = string.IsNullOrWhiteSpace(backupRoot)
                ? L("Tray.Health.NoPath", "Backup path not set")
                : _cachedDriveHealthLabel;

            statusMenu.Items.Add(new NativeMenuItem(statusLabel) { IsEnabled = false });
            statusMenu.Items.Add(new NativeMenuItemSeparator());

            var recheck = new NativeMenuItem(L("Tray.Health.Recheck", "Recheck now"));
            recheck.Click += async (_, _) => await RecheckDriveHealthAsync(desktop);
            statusMenu.Items.Add(recheck);

            healthMenu.Menu = statusMenu;
            return healthMenu;
        }
        catch
        {
            return new NativeMenuItem(L("Tray.Health.Unavailable", "Storage health: unavailable")) { IsEnabled = false };
        }
    }

        public async void RefreshTrayMenu()
        {
            if (_trayIcon is null)
                return;

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var recent = await Task.Run(() =>
            AppViewModelInstance?.GetRecentBackupsForTray(MaxRecentBackupsPerProject)
            ?? Array.Empty<AppViewModel.TrayProjectBackups>());

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Reset first to avoid Avalonia native menu mismatch errors on macOS.
                _trayIcon.Menu = null;
                _trayIcon.Menu = BuildTrayMenu(desktop, recent);
            }
            catch (Exception ex)
            {
                // Best-effort: avoid crashing the app if tray menu rebuild fails.
                Console.WriteLine($"[Tray] Failed to refresh tray menu: {ex.Message}");
            }
        });
    }

    private static void BringWindowToFrontIfUserWants(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = desktop.MainWindow;
        if (window is null)
            return;

        var config = AppConfigStore.Load();
        if (config.Behavior?.ShowWindowOnTrayActions != true)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    public static void ActivateMainWindowFromSignal()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        Dispatcher.UIThread.Post(() => BringMainWindowToFront(desktop));
    }

    private static void BringMainWindowToFront(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = desktop.MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    private static ISystemNotificationService? CreateSystemNotificationService()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // macOS Notification Center implementation.
                return new MacSystemNotificationService();
            }

            if (OperatingSystem.IsWindows())
            {
                // Windows toast/notification implementation.
                return new WindowsSystemNotificationService();
            }
        }
        catch (Exception ex)
        {
        }

        // On unsupported platforms (or on failure), return null
        // so the caller can fall back to a stub implementation.
        return null;
    }

    private void ApplyThemeFromConfig()
    {
        var config = AppConfigStore.Load();
        ApplyTheme(config.Appearance.Theme);
        ThemeManager.ApplyCompactLayout(config.Appearance.CompactLayout);
    }

    public void ApplyTheme(string themeOption)
    {
        RequestedThemeVariant = themeOption switch
        {
            "Light" => ThemeVariant.Light,
            "Dark"  => ThemeVariant.Dark,
            _       => ThemeVariant.Default
        };

        var config = AppConfigStore.Load();
        config.Appearance.Theme = themeOption;
        AppConfigStore.Save(config);
    }

    private static string FormatDriveLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return L("DriveHealth.UnknownDrive", "drive");

        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            // ignore and fall back
        }

        // UNC paths: try to take \\server\share
        if (path.StartsWith("\\\\") || path.StartsWith("//"))
        {
            var parts = path.Trim('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"\\\\{parts[0]}\\{parts[1]}";
        }

        return path;
    }

    private static string DescribeHealth(DriveHealthResult health, string driveLabel)
    {
        return health.Status switch
        {
            DriveHealthStatus.Healthy => Lf("DriveHealth.OkMessage", "Storage health ({0}): OK ({1})", driveLabel, health.Message),
            DriveHealthStatus.Warning => Lf("DriveHealth.WarningMessage", "Drive health warning on {0}: {1}.", driveLabel, health.Message),
            DriveHealthStatus.Failing => Lf("DriveHealth.FailingMessage", "Storage health failing ({0}): {1}", driveLabel, health.Message),
            _ => Lf("DriveHealth.GenericMessage", "Storage health ({0}): {1}", driveLabel, health.Message)
        };
    }

    private static async Task RecheckDriveHealthAsync(IClassicDesktopStyleApplicationLifetime? desktop)
    {
        await Task.Run(() =>
        {
            try
            {
                var cfg        = AppConfigStore.Load();
                var backupRoot = cfg.Backups.BackupRoot ?? string.Empty;
                var driveLabel = FormatDriveLabel(backupRoot);

                if (string.IsNullOrWhiteSpace(backupRoot))
                {
                    GlobalNotificationCenter.Instance.Show(
                        L("Tray.Health.NoPathDetail", "Backup path not set. Set a backup location to check drive health."),
                        NotificationSeverity.Warning,
                        L("Tray.Health.Title", "Storage health"));
                    return;
                }

                var health = new DriveHealthService().CheckPath(backupRoot);
                _cachedDriveHealthLabel  = DescribeHealth(health, driveLabel);
                _cachedDriveHealthStatus = health.Status;

                var severity = health.Status switch
                {
                    DriveHealthStatus.Failing => NotificationSeverity.Error,
                    DriveHealthStatus.Warning => NotificationSeverity.Warning,
                    _ => NotificationSeverity.Info
                };

                GlobalNotificationCenter.Instance.Show(_cachedDriveHealthLabel, severity, L("Tray.Health.Title", "Storage health"));

                _instance?.RefreshTrayMenu();
            }
            catch
            {
                GlobalNotificationCenter.Instance.Show(
                    L("Tray.Health.Error", "Unable to check drive health."),
                    NotificationSeverity.Warning,
                    L("Tray.Health.Title", "Storage health"));
            }
        });
    }
}
