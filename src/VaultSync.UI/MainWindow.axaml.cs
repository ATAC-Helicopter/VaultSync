using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.Core.Config;

namespace VaultSync.UI;

public partial class MainWindow : Window
{
    private readonly AppViewModel _appVm;
    private bool _fullscreenSuppressed;
    private bool _macFullscreenDisabled;
    private bool _ignoreNextPointerPress;
    private bool _isSidebarCollapsed;
    private bool _sidebarAutoCollapsed;
    private const double SidebarAutoCollapseWidth = 1450;

    /// <summary>
    /// Indicates whether the main window is currently active (in the foreground).
    /// This will be used to decide when to show OS-level notifications.
    /// </summary>
    public static bool IsForeground { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        // Use the shared AppViewModel created in App.axaml.cs when available;
        // fall back to a new instance (e.g. for design-time preview).
        _appVm = App.AppViewModelInstance ?? new AppViewModel();
        DataContext = _appVm;

        // --------- FOREGROUND / BACKGROUND TRACKING ----------
        // Window opened = definitely foreground.
        Opened += (_, _) =>
        {
            IsForeground = true;
            if (!_macFullscreenDisabled)
            {
                _macFullscreenDisabled = TryDisableMacFullscreen();
            }
            _appVm.EnsureInitialView();
        };

        // Activated = user focused the window again.
        Activated += (_, _) =>
        {
            IsForeground = true;
            _ignoreNextPointerPress = true;
        };

        // Deactivated = window lost focus (background).
        Deactivated += (_, _) => IsForeground = false;

        // Closed = not foreground.
        Closed += (_, _) => IsForeground = false;

        // Intercept closing to optionally run in background instead of quitting.
        Closing += OnMainWindowClosing;
        // ------------------------------------------------------
        SizeChanged += (_, _) => ApplyResponsiveSidebar();
        Dispatcher.UIThread.Post(() =>
        {
            ApplyResponsiveSidebar(force: true);
        }, DispatcherPriority.Background);

        if (!OperatingSystem.IsMacOS())
        {
            AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        }

        PropertyChanged += (_, e) =>
        {
            if (!OperatingSystem.IsMacOS())
                return;
            if (e.Property != WindowStateProperty)
                return;
            if (_fullscreenSuppressed)
                return;

            if (WindowState == WindowState.FullScreen)
            {
                _fullscreenSuppressed = true;
                Dispatcher.UIThread.Post(() =>
                {
                    WindowState = WindowState.Maximized;
                    _fullscreenSuppressed = false;
                    Console.WriteLine("[Window] Fullscreen is disabled on macOS; using maximized instead.");
                });
            }
        };
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_ignoreNextPointerPress)
            return;

        _ignoreNextPointerPress = false;
        e.Handled = true;
    }

    private void OnToggleSidebar(object? sender, RoutedEventArgs e)
    {
        _sidebarAutoCollapsed = false;
        SetSidebarCollapsed(!_isSidebarCollapsed);
    }

    private void ApplyResponsiveSidebar(bool force = false)
    {
        var shouldAutoCollapse = Bounds.Width < SidebarAutoCollapseWidth;
        if (!force && shouldAutoCollapse == _sidebarAutoCollapsed)
            return;

        _sidebarAutoCollapsed = shouldAutoCollapse;
        if (shouldAutoCollapse)
        {
            SetSidebarCollapsed(true);
            return;
        }

        // Expand back only when collapse was auto-triggered.
        if (_isSidebarCollapsed)
            SetSidebarCollapsed(false);
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        _isSidebarCollapsed = collapsed;

        if (RootGrid.ColumnDefinitions.Count >= 1)
            RootGrid.ColumnDefinitions[0].Width = new GridLength(collapsed ? 86 : 260);

        SidebarRoot.Padding = collapsed
            ? new Thickness(10, 12, 10, 10)
            : new Thickness(16, 18, 16, 14);
        SidebarTopSection.Margin = collapsed
            ? new Thickness(0, 4, 0, 10)
            : new Thickness(4, 10, 4, 16);
        NavButtonsPanel.Spacing = collapsed ? 12 : 4;
        NavButtonsPanel.Margin = collapsed
            ? new Thickness(0, 24, 0, 10)
            : new Thickness(0, 0, 0, 10);

        SidebarRoot.Classes.Set("compact", collapsed);
        ShellCompactBadge.IsVisible = collapsed;
        ShellBanner.IsVisible = !collapsed;
        NavigationHeader.IsVisible = !collapsed;
        SidebarDestinations.IsVisible = !collapsed;
        SidebarFooter.IsVisible = !collapsed;

        SidebarToggleGlyph.Text = collapsed ? "\uE70D" : "\uE700";
        SidebarToggleButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SidebarToggleButton.Margin = collapsed ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 0, 2);
        ToolTip.SetTip(
            SidebarToggleButton,
            LocalizationProvider.Service?.GetString(collapsed ? "Shell.SidebarExpand" : "Shell.SidebarCollapse")
            ?? (collapsed ? "Expand sidebar" : "Collapse sidebar"));
        NavDashboardText.IsVisible = !collapsed;
        NavProjectsText.IsVisible = !collapsed;
        NavBackupsText.IsVisible = !collapsed;
        NavSettingsText.IsVisible = !collapsed;

        var contentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var navButtonWidth = collapsed ? 48d : double.NaN;
        var navButtonHeight = collapsed ? 48d : double.NaN;
        var navButtonPadding = collapsed ? new Thickness(0) : new Thickness(10, 8);
        NavDashboardButton.HorizontalContentAlignment = contentAlignment;
        NavProjectsButton.HorizontalContentAlignment = contentAlignment;
        NavBackupsButton.HorizontalContentAlignment = contentAlignment;
        NavSettingsButton.HorizontalContentAlignment = contentAlignment;
        NavDashboardButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        NavProjectsButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        NavBackupsButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        NavSettingsButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        NavDashboardButton.Width = navButtonWidth;
        NavProjectsButton.Width = navButtonWidth;
        NavBackupsButton.Width = navButtonWidth;
        NavSettingsButton.Width = navButtonWidth;
        NavDashboardButton.Height = navButtonHeight;
        NavProjectsButton.Height = navButtonHeight;
        NavBackupsButton.Height = navButtonHeight;
        NavSettingsButton.Height = navButtonHeight;
        NavDashboardButton.Padding = navButtonPadding;
        NavProjectsButton.Padding = navButtonPadding;
        NavBackupsButton.Padding = navButtonPadding;
        NavSettingsButton.Padding = navButtonPadding;

        NavDashboardButton.Classes.Set("compact", collapsed);
        NavProjectsButton.Classes.Set("compact", collapsed);
        NavBackupsButton.Classes.Set("compact", collapsed);
        NavSettingsButton.Classes.Set("compact", collapsed);
        NavDashboardIconBadge.Classes.Set("compact", collapsed);
        NavProjectsIconBadge.Classes.Set("compact", collapsed);
        NavBackupsIconBadge.Classes.Set("compact", collapsed);
        NavSettingsIconBadge.Classes.Set("compact", collapsed);
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        DiagnosticsLogger.Record($"MainWindow closing. IsShuttingDown={App.IsShuttingDown}, IsCrashing={App.IsCrashing}.");
        if (App.IsShuttingDown)
            return;
        // Respect the RunInBackground behavior flag from AppConfig.
        // When enabled, pressing the close button should hide the window
        // and keep VaultSync running in the background.
        try
        {
            var config = AppConfigStore.Load();
            if (config.Behavior?.RunInBackground == true)
            {
                e.Cancel = true;
                IsForeground = false;
                Hide();
                NotifyRunningInBackground();
                DiagnosticsLogger.Record("MainWindow close intercepted; running in background.");
                return;
            }
        }
        catch
        {
            // If config load fails for any reason, fall back to normal close behavior.
        }

        // Default: allow the window to close normally.
        DiagnosticsLogger.Record("MainWindow close allowed to proceed.");
    }

    // -------- navigation handlers --------
    private void OnNavDashboard(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetDashboard();
    private void OnNavProjects (object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetPlaceholder("Projects", "Manage tracked Unity projects");
    private void OnNavBackups  (object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetPlaceholder("Backups", "Run and review snapshot jobs");
    private void OnNavSettings (object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetPlaceholder("Settings", "Theme, NAS paths, schedules");

    // -------- helpers --------
    private void SetDashboard()
    {
        SetHeader("Dashboard", "minimal boot");

        // Prefer the DashboardViewModel instance owned by AppViewModel, if available.
        object? vm = null;
        try
        {
            var dashboardProp = _appVm.GetType()
                                      .GetProperty("DashboardViewModel", BindingFlags.Instance | BindingFlags.Public);
            vm = dashboardProp?.GetValue(_appVm);
        }
        catch
        {
            // ignore and fall back to creating a new instance below
        }

        if (vm is null)
        {
            vm = new DashboardViewModel();
        }

        if (!TrySetCurrentView(vm))
        {
            // If AppViewModel lacks CurrentView, just show the VM directly.
            MainContent.Content = vm;
        }
    }

    private void SetPlaceholder(string title, string kicker)
    {
        SetHeader(title, kicker);

        // If CurrentView exists, set a plain string or the VM and let ViewLocator handle it.
        if (!TrySetCurrentView(new TextBlock { Text = $"{title} view coming soon...", Margin = new Thickness(16) }))
        {
            MainContent.Content = new TextBlock { Text = $"{title} view coming soon...", Margin = new Thickness(16) };
        }
    }

    private void SetHeader(string title, string kicker)
    {
        TrySetProperty(_appVm, "HeaderTitle", title);
        TrySetProperty(_appVm, "HeaderKicker", kicker);
    }

    private bool TrySetCurrentView(object value)
    {
        var prop = _appVm.GetType().GetProperty("CurrentView", BindingFlags.Instance | BindingFlags.Public);
        if (prop is { CanWrite: true })
        {
            prop.SetValue(_appVm, value);
            return true;
        }
        return false;
    }

    private static void TrySetProperty(object target, string name, object? value)
    {
        try
        {
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p is { CanWrite: true }) p.SetValue(target, value);
        }
        catch
        {
            // ignore - property is optional
        }
    }

    private static void NotifyRunningInBackground()
    {
        var title = Localized("Tray.Notification.BackgroundTitle", "VaultSync is still running");
        var message = Localized("Tray.Notification.BackgroundMessage", "VaultSync continues monitoring projects in the background.");
        GlobalNotificationCenter.Instance.Show(message, NotificationSeverity.Info, title);
        GlobalNotificationCenter.Instance.ShowSystem(message, NotificationSeverity.Info, title);
    }

    private static string Localized(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private bool TryDisableMacFullscreen()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            var selector = sel_registerName("setCollectionBehavior:");
            objc_msgSend(handle, selector, (nint)NSWindowCollectionBehaviorFullScreenNone);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Window] Failed to disable macOS fullscreen: {ex.Message}");
            return false;
        }
    }

    private const ulong NSWindowCollectionBehaviorFullScreenNone = 1UL << 9;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, nint arg1);
}
