using Avalonia.Controls;
using Avalonia.Input;

namespace VaultSync.UI.Views;

public partial class TrayPanelWindow : Window
{
    public TrayPanelWindow()
    {
        InitializeComponent();
    }

    private void OnDragAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
