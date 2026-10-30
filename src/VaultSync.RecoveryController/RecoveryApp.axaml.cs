using VaultSync.Core.Services;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace VaultSync.RecoveryController;

internal sealed partial class RecoveryApp : Application
{
    internal static Func<IRecoveryPresentation, CancellationToken, Task>? Runner { get; set; }
    internal static CancellationToken LifetimeToken { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new RecoveryWindow();
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try
                {
                    if (Runner is null) { window.ViewModel.Complete(LinuxRecoveryControllerClient.Result.Failed); return; }
                    await Runner(window.ViewModel, LifetimeToken);
                }
                catch (Exception) { window.ViewModel.Complete(LinuxRecoveryControllerClient.Result.Failed); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
