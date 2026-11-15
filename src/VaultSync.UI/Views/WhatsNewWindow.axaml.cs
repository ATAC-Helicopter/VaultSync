using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class WhatsNewWindow : Window
{
    private static void OnOpenCommunity(object? sender, RoutedEventArgs e)
    {
        try
        {
            SystemFileLauncher.OpenUri("https://discord.gg/sgP8UzeAQt");
        }
        catch (Exception)
        {
            // The external link is optional; missing browser must not break release notes.
        }
    }

    public WhatsNewWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is WhatsNewViewModel vm)
            {
                vm.CloseRequested += () => Close();
            }
        };
    }
}
