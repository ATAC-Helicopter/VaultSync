using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VaultSync.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}