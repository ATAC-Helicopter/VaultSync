using System;
using Avalonia.Controls;
using Avalonia.Threading;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class MetadataSyncReviewWindow : Window
{
    public MetadataSyncReviewWindow()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MetadataSyncReviewViewModel vm)
        {
            vm.RequestClose += OnRequestClose;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (DataContext is MetadataSyncReviewViewModel vm)
        {
            vm.RequestClose -= OnRequestClose;
        }
    }

    private void OnRequestClose()
    {
        Dispatcher.UIThread.Post(Close);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Allow close via window manager; treat as cancel.
        if (DataContext is MetadataSyncReviewViewModel vm && !vm.Confirmed)
        {
            // nothing extra
        }
    }
}
