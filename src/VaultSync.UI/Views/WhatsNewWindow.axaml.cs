using Avalonia.Controls;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class WhatsNewWindow : Window
{
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
