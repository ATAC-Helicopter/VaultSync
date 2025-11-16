using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class DashboardView : UserControl
    {
        public DashboardView()
        {
            InitializeComponent();
            AttachedToVisualTree += OnAttachedToVisualTree;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is DashboardViewModel vm)
            {
                try
                {
                    await vm.RefreshAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[DashboardView] RefreshAsync failed: " + ex);
                }
            }
        }
    }
}