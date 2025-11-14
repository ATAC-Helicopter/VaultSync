using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VaultSync.UI.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}