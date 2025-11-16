using Avalonia;
using Avalonia.Styling;

namespace VaultSync.UI.Services
{
    public static class ThemeManager
    {
        public static void ApplyTheme(string themeName)
        {
            var app = Application.Current;
            if (app == null) return;

            if (themeName == "Dark")
            {
                app.RequestedThemeVariant = ThemeVariant.Dark;
            }
            else if (themeName == "Light")
            {
                app.RequestedThemeVariant = ThemeVariant.Light;
            }
            else
            {
                // Follow System
                app.RequestedThemeVariant = ThemeVariant.Default;
            }
        }

        public static void ApplyCompactLayout(bool compact)
        {
            var app = Application.Current;
            if (app == null) return;

            // You can expand this later.
            app.Resources["GlobalPadding"] = compact ? new Thickness(6) : new Thickness(12);
            app.Resources["GlobalSpacing"] = compact ? 4.0 : 8.0;
        }
    }
}