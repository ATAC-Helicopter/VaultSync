using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VaultSync.Core.Config;
using VaultSync.UI.ViewModels;
using VaultSync.UI.Views;

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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new AppViewModel()
            };
        }

        // Apply theme from stored config on startup
        ApplyThemeFromConfig();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Reads Appearance.Theme from AppConfig and sets the Avalonia theme variant.
    /// </summary>
    private void ApplyThemeFromConfig()
    {
        var config = AppConfigStore.Load();
        ApplyTheme(config.Appearance.Theme);
    }

    /// <summary>
    /// Called from SettingsViewModel when the user changes the theme.
    /// Also used at startup.
    /// </summary>
    public void ApplyTheme(string themeOption)
    {
        // Map our string option to Avalonia ThemeVariant
        RequestedThemeVariant = themeOption switch
        {
            "Light" => ThemeVariant.Light,
            "Dark"  => ThemeVariant.Dark,
            // "System" or anything unknown falls back to OS / default
            _       => ThemeVariant.Default
        };

        // Persist selection
        var config = AppConfigStore.Load();
        config.Appearance.Theme = themeOption;
        AppConfigStore.Save(config);
    }
}