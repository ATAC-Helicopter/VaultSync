using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VaultSync.UI.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}