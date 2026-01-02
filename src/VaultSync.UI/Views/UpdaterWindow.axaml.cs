using System;
using Avalonia.Controls;
using Avalonia.Threading;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class UpdaterWindow : Window
{
    public UpdaterWindow()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is UpdaterViewModel vm)
        {
            vm.RequestClose += OnRequestClose;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (DataContext is UpdaterViewModel vm)
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
        if (DataContext is UpdaterViewModel vm && !vm.CanClose)
        {
            e.Cancel = true;
        }
    }

    private void OnLogTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (LogTextBox is null)
            return;

        LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
        LogTextBox.SelectionStart = LogTextBox.CaretIndex;
        LogTextBox.SelectionEnd = LogTextBox.CaretIndex;
    }
}
