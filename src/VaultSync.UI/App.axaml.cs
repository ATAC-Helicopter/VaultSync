using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VaultSync.Core.Config;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels;
using VaultSync.UI.Views;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform;
using VaultSync.UI.Services;

namespace VaultSync.UI;

public partial class App : Application
{
    public static AppViewModel? AppViewModelInstance { get; private set; }

    // Keep a reference to the tray/menu-bar icon so it stays alive.
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppViewModelInstance = new AppViewModel();

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
            ToolTipText = "VaultSync - snapshots & backups"
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
        }
        else
        {
            DestroyTrayIcon();
        }
    }

    private NativeMenu BuildTrayMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Build a small context menu: header / Open / Backup / Snapshot / Recent backups / Quit.
        var menu = new NativeMenu();

        // Header (disabled) to give the menu a title and tighter OS alignment.
        menu.Items.Add(new NativeMenuItem("VaultSync") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());
        // Open main window
        var openItem = new NativeMenuItem("Open VaultSync");
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
        var backupRootItem = new NativeMenuItem("Backup");
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

            var backupAllItem = new NativeMenuItem("Backup all projects");
            backupAllItem.Click += (_, _) =>
            {
                BringWindowToFrontIfUserWants(desktop);
                AppViewModelInstance?.RequestBackupAllFromTray();
            };
            backupMenu.Items.Add(backupAllItem);
        }
        else
        {
            backupMenu.Items.Add(new NativeMenuItem("No projects available") { IsEnabled = false });
        }

        backupRootItem.Menu = backupMenu;

        // ---------- Snapshot submenu ----------
        var snapshotRootItem = new NativeMenuItem("Snapshot");
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

            var snapshotAllItem = new NativeMenuItem("Snapshot all projects");
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
            snapshotMenu.Items.Add(new NativeMenuItem("No projects available") { IsEnabled = false });
        }

        snapshotRootItem.Menu = snapshotMenu;

        // ---------- Recent backups (keep/delete) ----------
        var manageBackupsRoot = new NativeMenuItem("Recent backups");
        var manageBackupsMenu = new NativeMenu();

        var recentByProject = AppViewModelInstance?.GetRecentBackupsForTray(5)
                              ?? Array.Empty<AppViewModel.TrayProjectBackups>();

        var anyBackups = false;
        foreach (var project in recentByProject)
        {
            if (!project.Backups.Any())
                continue;

            anyBackups = true;

            // Project header (disabled) to avoid deep nesting.
            manageBackupsMenu.Items.Add(new NativeMenuItem(project.ProjectName) { IsEnabled = false });

            foreach (var backup in project.Backups)
            {
                var backupItem = new NativeMenuItem(backup.Label);
                var backupActions = new NativeMenu();

                var keepLabel = backup.IsProtected ? "Un-keep" : "Keep";
                var keepItem = new NativeMenuItem(keepLabel);
                keepItem.Click += (_, _) => AppViewModelInstance?.ToggleBackupProtectionFromTray(backup.Id);

                var deleteItem = new NativeMenuItem("Delete");
                deleteItem.Click += (_, _) => AppViewModelInstance?.DeleteBackupFromTray(backup.Id);

                var openFolderItem = new NativeMenuItem("Open folder");
                openFolderItem.Click += (_, _) => AppViewModelInstance?.OpenBackupFolderFromTray(backup.Id);

                var viewInAppItem = new NativeMenuItem("View in VaultSync");
                viewInAppItem.Click += (_, _) => AppViewModelInstance?.ShowBackupInAppFromTray(backup.ProjectId);

                backupActions.Items.Add(keepItem);
                backupActions.Items.Add(deleteItem);
                backupActions.Items.Add(new NativeMenuItemSeparator());
                backupActions.Items.Add(openFolderItem);
                backupActions.Items.Add(viewInAppItem);

                backupItem.Menu = backupActions;
                manageBackupsMenu.Items.Add(backupItem);
            }

            if (project.Backups.Any())
            {
                manageBackupsMenu.Items.Add(new NativeMenuItemSeparator());
            }
        }

        if (!anyBackups)
        {
            manageBackupsMenu.Items.Add(new NativeMenuItem("No backups yet") { IsEnabled = false });
        }
        else if (manageBackupsMenu.Items.LastOrDefault() is NativeMenuItemSeparator)
        {
            manageBackupsMenu.Items.RemoveAt(manageBackupsMenu.Items.Count - 1);
        }

        manageBackupsRoot.Menu = manageBackupsMenu;
        menu.Items.Add(manageBackupsRoot);

        var separator1 = new NativeMenuItemSeparator();
        var separator2 = new NativeMenuItemSeparator();

        var quitItem = new NativeMenuItem("Quit VaultSync");
        quitItem.Click += (_, _) =>
        {
            // Cleanly shut down the app.
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

    public void RefreshTrayMenu()
    {
        if (_trayIcon is null)
            return;

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        _trayIcon.Menu = BuildTrayMenu(desktop);
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
}

