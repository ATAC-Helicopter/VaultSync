using Avalonia.Controls;
using Avalonia.Input;

namespace VaultSync.UI.Views
{
    public partial class BackupWidgetWindow : Window
    {
        public BackupWidgetWindow()
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
}
