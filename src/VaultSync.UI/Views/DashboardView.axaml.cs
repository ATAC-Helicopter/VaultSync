using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VaultSync.UI.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}