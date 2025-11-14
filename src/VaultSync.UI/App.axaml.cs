using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VaultSync.UI.ViewModels; // <-- ensure this matches the namespace above

namespace VaultSync.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Use the root AppViewModel so MainWindow can bind to CurrentPage
            desktop.MainWindow = new MainWindow
            {
                DataContext = new AppViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}