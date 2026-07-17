using Avalonia;
using Avalonia.Controls;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class RecoveryView : UserControl
{
    public RecoveryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is RecoveryViewModel viewModel)
            _ = DetachedTask.RunAsync(viewModel.ActivateAsync, "activate-recovery-view");
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is RecoveryViewModel viewModel)
            viewModel.Deactivate();
    }
}
