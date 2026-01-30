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
            app.Resources["CardPadding"]         = compact ? new Thickness(10)    : new Thickness(16);
            app.Resources["CardMargin"]          = compact ? new Thickness(4)     : new Thickness(8);
            app.Resources["CardCornerRadius"]    = compact ? new CornerRadius(12) : new CornerRadius(18);
            app.Resources["NavButtonPadding"]    = compact ? new Thickness(9, 6)  : new Thickness(12, 10);
            app.Resources["NavButtonMargin"]     = compact ? new Thickness(0, 3, 0, 0) : new Thickness(0, 6, 0, 0);
            app.Resources["MetricFontSize"]      = compact ? 26d : 36d;
            app.Resources["MetricMargin"]        = compact ? new Thickness(0, 3, 0, 1) : new Thickness(0, 6, 0, 2);
            app.Resources["MetricHintMargin"]    = compact ? new Thickness(0, 0, 0, 0) : new Thickness(0, 2, 0, 0);
            app.Resources["SectionTitleFontSize"]= compact ? 13d : 16d;
            app.Resources["PagePadding"]         = compact ? new Thickness(16, 12) : new Thickness(32, 24);
            app.Resources["PageStackMargin"]     = compact ? new Thickness(14) : new Thickness(24);
            app.Resources["SectionMarginBottom"] = compact ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 0, 12);
            app.Resources["ListItemMargin"]      = compact ? new Thickness(0, 3, 0, 0) : new Thickness(0, 8, 0, 0);
            app.Resources["ListItemMarginTight"] = compact ? new Thickness(0, 1, 0, 1) : new Thickness(0, 3, 0, 3);
            app.Resources["ListItemPadding"]     = compact ? new Thickness(10) : new Thickness(16);
            app.Resources["CardPaddingLarge"]    = compact ? new Thickness(16) : new Thickness(24);
            app.Resources["CardPaddingMedium"]   = compact ? new Thickness(12) : new Thickness(16);
            app.Resources["CardPaddingSmall"]    = compact ? new Thickness(8)  : new Thickness(12);
            app.Resources["InputPadding"]        = compact ? new Thickness(8, 5) : new Thickness(10, 8);
            app.Resources["ButtonTightPadding"]  = compact ? new Thickness(7, 4) : new Thickness(10, 6);
            app.Resources["ButtonPillPadding"]   = compact ? new Thickness(7, 3) : new Thickness(10, 4);
            app.Resources["SmallButtonPadding"]  = compact ? new Thickness(6, 3) : new Thickness(8, 4);
            app.Resources["SmallPillPadding"]    = compact ? new Thickness(7, 3) : new Thickness(10, 4);
            app.Resources["StatPillPadding"]     = compact ? new Thickness(6, 1) : new Thickness(8, 2);
            app.Resources["BackupTagPadding"]    = compact ? new Thickness(5, 1) : new Thickness(6, 2);
            app.Resources["BackupTagMargin"]     = compact ? new Thickness(3, 0, 0, 0) : new Thickness(4, 0, 0, 0);
            app.Resources["SummaryStatPadding"]  = compact ? new Thickness(6, 5) : new Thickness(10, 8);
            app.Resources["ChartItemMargin"]     = compact ? new Thickness(3, 0) : new Thickness(6, 0);
            app.Resources["ChartLabelMargin"]    = compact ? new Thickness(6, 2, 6, 0) : new Thickness(10, 4, 10, 0);
            app.Resources["PageRowSpacing"]      = compact ? 10d : 16d;
            app.Resources["ListRowSpacing"]      = compact ? 2d : 4d;
            app.Resources["ProjectIconSize"]     = compact ? 22d : 28d;
            app.Resources["ProjectIconRadius"]   = compact ? 11d : 14d;
            app.Resources["ProjectIconMargin"]   = compact ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 8, 0);
            app.Resources["PageTitleFontSize"]   = compact ? 19d : 22d;
            app.Resources["PageSubtitleFontSize"]= compact ? 11d : 12d;
            app.Resources["SectionHeaderFontSize"]= compact ? 12d : 14d;
        }
    }
}
