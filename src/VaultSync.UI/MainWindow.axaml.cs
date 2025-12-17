using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.Core.Config;

namespace VaultSync.UI;

public partial class MainWindow : Window
{
    private readonly AppViewModel _appVm;

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
        Opened += (_, _) => IsForeground = true;

        // Activated = user focused the window again.
        Activated += (_, _) => IsForeground = true;

        // Deactivated = window lost focus (background).
        Deactivated += (_, _) => IsForeground = false;

        // Closed = not foreground.
        Closed += (_, _) => IsForeground = false;

        // Intercept closing to optionally run in background instead of quitting.
        Closing += OnMainWindowClosing;
        // ------------------------------------------------------
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
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
                return;
            }
        }
        catch
        {
            // If config load fails for any reason, fall back to normal close behavior.
        }

        // Default: allow the window to close normally.
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
}
