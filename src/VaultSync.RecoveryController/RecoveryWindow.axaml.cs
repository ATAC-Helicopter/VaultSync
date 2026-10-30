using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace VaultSync.RecoveryController;

internal sealed partial class RecoveryWindow : Window
{
    internal RecoveryViewModel ViewModel { get; }
    public RecoveryWindow()
    {
        AvaloniaXamlLoader.Load(this);
        ViewModel = new RecoveryViewModel(this);
        Closing += (_, e) => { if (!ViewModel.IsComplete) { e.Cancel = true; ViewModel.Cancel(); } };
    }
    private void PrimaryClick(object? sender, RoutedEventArgs e) => ViewModel.Accept();
    private void CancelClick(object? sender, RoutedEventArgs e) { if (ViewModel.IsComplete) Close(); else ViewModel.Cancel(); }
}
