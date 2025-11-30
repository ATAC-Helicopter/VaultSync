using Avalonia;
using Avalonia.Styling;
using Avalonia.Media;

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

            // Core layout tokens used across styles
            app.Resources["CardPadding"]         = compact ? new Thickness(12)    : new Thickness(16);
            app.Resources["CardMargin"]          = compact ? new Thickness(6)     : new Thickness(8);
            app.Resources["CardCornerRadius"]    = compact ? new CornerRadius(14) : new CornerRadius(18);
            app.Resources["NavButtonPadding"]    = compact ? new Thickness(10, 8) : new Thickness(12, 10);
            app.Resources["NavButtonMargin"]     = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 6, 0, 0);
            app.Resources["MetricFontSize"]      = compact ? 30d : 36d;
            app.Resources["MetricMargin"]        = compact ? new Thickness(0, 4, 0, 2) : new Thickness(0, 6, 0, 2);
            app.Resources["MetricHintMargin"]    = compact ? new Thickness(0, 1, 0, 0) : new Thickness(0, 2, 0, 0);
            app.Resources["SectionTitleFontSize"]= compact ? 14d : 16d;
        }
    }
}
