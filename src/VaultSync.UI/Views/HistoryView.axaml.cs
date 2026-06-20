using Avalonia;
using Avalonia.Controls;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is HistoryViewModel viewModel)
            _ = viewModel.RefreshAsync();
    }
}
