using Avalonia;
using System;

namespace VaultSync.UI;

internal static class Program
{
    // Initialization code. Don’t use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppBuilder starts.
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Do not remove; used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .LogToTrace();
}