using Avalonia;
using VaultSync.UI.Services;

namespace VaultSync.UI;

internal static class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        if (PatchInstallService.TryHandlePatchArgs(args))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont() // optional, ok to keep/remove
            .LogToTrace();
}
