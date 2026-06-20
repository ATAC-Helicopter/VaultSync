using Avalonia;
using Avalonia.Controls;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class RecoveryView : UserControl
{
    public RecoveryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is RecoveryViewModel viewModel)
            _ = viewModel.RefreshAsync();
    }
}
