using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VaultSync.Core.Config;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using VaultSync.UI.Views;

namespace VaultSync.UI;

public sealed partial class UpdaterApp : Application
{
    public static PatchApplyRequest? PendingRequest { get; private set; }

    public static void SetPendingRequest(PatchApplyRequest request)
    {
        PendingRequest = request;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var request = PendingRequest;
            if (request is null)
            {
                desktop.Shutdown();
                return;
            }

            InitializeLocalization();
            ApplyThemeFromConfig();

            var viewModel = new UpdaterViewModel(request);
            var window = new UpdaterWindow
            {
                DataContext = viewModel
            };

            viewModel.RequestClose += () => desktop.Shutdown();
            desktop.MainWindow = window;
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InitializeLocalization()
    {
        var localizationService = new LocalizationService();
        LocalizationProvider.Initialize(localizationService);

        try
        {
            var config = AppConfigStore.Load();
            if (!string.IsNullOrWhiteSpace(config.Advanced.Language))
            {
                localizationService.SetLanguage(config.Advanced.Language);
            }
        }
        catch
        {
            // Best effort: fall back to English.
        }
    }

    private void ApplyThemeFromConfig()
    {
        var config = AppConfigStore.Load();
        ThemeManager.ApplyAppearance(config.Appearance);
    }
}
