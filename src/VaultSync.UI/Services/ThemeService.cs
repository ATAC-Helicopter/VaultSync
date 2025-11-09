using Avalonia;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;

namespace VaultSync.UI.Services;

public static class ThemeService
{
    public static void SetDark(bool dark)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}