using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia; 
using VaultSync.UI.ViewModels;

namespace VaultSync.UI;

public partial class MainWindow : Window
{
    private readonly object _appVm;

    public MainWindow()
    {
        InitializeComponent();

        // Use your existing AppViewModel if available; otherwise fall back to a simple one.
        _appVm = new AppViewModel();
        DataContext = _appVm;

        // Default view
        SetDashboard();
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
        var vm = new DashboardViewModel();
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
        if (!TrySetCurrentView(new TextBlock { Text = $"{title} view coming soon…", Margin = new Thickness(16) }))
        {
            MainContent.Content = new TextBlock { Text = $"{title} view coming soon…", Margin = new Thickness(16) };
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
        catch { /* ignore – property is optional */ }
    }
}