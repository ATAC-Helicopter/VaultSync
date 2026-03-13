using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using VaultSync.Core.Config;

namespace VaultSync.UI.Services
{
    public static class ThemeManager
    {
        private sealed record ThemePresetDefinition(string Id, string Name, string Description, ThemePaletteConfig Palette);

        private static readonly ThemePresetDefinition[] ThemePresets =
        {
            new(
                "vaultsync-midnight",
                "VaultSync Midnight",
                "The default dark VaultSync look with a cool blue accent.",
                new ThemePaletteConfig()),
            new(
                "studio-light",
                "Studio Light",
                "A clean bright theme with sharper contrast for daytime work.",
                new ThemePaletteConfig
                {
                    Name = "Studio Light",
                    BaseTheme = "Light",
                    Background = "#F4F5F9",
                    Surface = "#FFFFFF",
                    SurfaceAlt = "#E6E9F2",
                    Accent = "#2663FF",
                    TextPrimary = "#11131A",
                    TextSecondary = "#5C6275",
                    Success = "#32DFA0",
                    Warning = "#FFBF5F",
                    Danger = "#FF6A6A"
                }),
            new(
                "ember",
                "Ember",
                "Warm charcoal surfaces with a copper accent.",
                new ThemePaletteConfig
                {
                    Name = "Ember",
                    BaseTheme = "Dark",
                    Background = "#14110F",
                    Surface = "#201A16",
                    SurfaceAlt = "#2C241E",
                    Accent = "#FF8B4D",
                    TextPrimary = "#FFF4EB",
                    TextSecondary = "#D4BBA8",
                    Success = "#54D7A2",
                    Warning = "#F7C66B",
                    Danger = "#FF7B74"
                }),
            new(
                "fjord",
                "Fjord",
                "Deep slate blues with a crisp aqua accent.",
                new ThemePaletteConfig
                {
                    Name = "Fjord",
                    BaseTheme = "Dark",
                    Background = "#0D1620",
                    Surface = "#142131",
                    SurfaceAlt = "#1B2B3E",
                    Accent = "#4CC9F0",
                    TextPrimary = "#F4FAFF",
                    TextSecondary = "#AFC4D9",
                    Success = "#51D5AA",
                    Warning = "#FFCB65",
                    Danger = "#FF7A88"
                }),
            new(
                "forest",
                "Forest",
                "Muted green accents with grounded dark neutrals.",
                new ThemePaletteConfig
                {
                    Name = "Forest",
                    BaseTheme = "Dark",
                    Background = "#101611",
                    Surface = "#18211A",
                    SurfaceAlt = "#223026",
                    Accent = "#5AC88F",
                    TextPrimary = "#F5FFF7",
                    TextSecondary = "#B4CDB8",
                    Success = "#5CE2A1",
                    Warning = "#EFC56A",
                    Danger = "#FF7B79"
                }),
            new(
                "orchid",
                "Orchid",
                "A brighter creative theme with magenta-blue contrast.",
                new ThemePaletteConfig
                {
                    Name = "Orchid",
                    BaseTheme = "Dark",
                    Background = "#15111C",
                    Surface = "#21182B",
                    SurfaceAlt = "#2B2238",
                    Accent = "#B983FF",
                    TextPrimary = "#FFF7FF",
                    TextSecondary = "#CAB7DA",
                    Success = "#5ED7B4",
                    Warning = "#F8C86C",
                    Danger = "#FF82A8"
                })
        };

        public static IReadOnlyList<(string Id, string Name, string Description, ThemePaletteConfig Palette)> GetThemePresets()
        {
            var items = new List<(string, string, string, ThemePaletteConfig)>(ThemePresets.Length);
            foreach (var preset in ThemePresets)
                items.Add((preset.Id, preset.Name, preset.Description, preset.Palette.Clone()));
            return items;
        }

        public static ThemePaletteConfig GetDefaultCustomTheme() => ThemePresets[0].Palette.Clone();

        public static void ApplyAppearance(AppearanceConfig appearance)
        {
            var app = Application.Current;
            if (app is null)
                return;

            ApplyThemeVariant(app, appearance.Theme, appearance.CustomTheme);
            ApplyPaletteOverrides(app, appearance.Theme, appearance.CustomTheme);
        }

        public static void ApplyTheme(string themeName)
        {
            var app = Application.Current;
            if (app is null)
                return;

            ApplyThemeVariant(app, themeName, null);
            if (!string.Equals(themeName, "Custom", StringComparison.OrdinalIgnoreCase))
                ClearPaletteOverrides(app);
        }

        public static void ApplyCompactLayout(bool compact)
        {
            var app = Application.Current;
            if (app == null) return;

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

        private static void ApplyThemeVariant(Application app, string themeName, ThemePaletteConfig? customTheme)
        {
            app.RequestedThemeVariant = themeName switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                "Custom" when string.Equals(customTheme?.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase) => ThemeVariant.Light,
                "Custom" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }

        private static void ApplyPaletteOverrides(Application app, string themeName, ThemePaletteConfig? customTheme)
        {
            if (!string.Equals(themeName, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                ClearPaletteOverrides(app);
                return;
            }

            var palette = NormalizePalette(customTheme ?? GetDefaultCustomTheme());
            var accentSoft = WithAlpha(palette.Accent, palette.BaseTheme == "Light" ? 0.14 : 0.24);
            var textMuted = Blend(palette.TextSecondary, palette.Background, palette.BaseTheme == "Light" ? 0.45 : 0.60);
            var inputBackground = Blend(palette.SurfaceAlt, palette.Background, palette.BaseTheme == "Light" ? 0.45 : 0.25);
            var inputBorder = Blend(palette.SurfaceAlt, palette.TextSecondary, palette.BaseTheme == "Light" ? 0.35 : 0.28);
            var divider = Blend(palette.SurfaceAlt, palette.TextSecondary, palette.BaseTheme == "Light" ? 0.25 : 0.18);
            var shellStart = Blend(palette.Accent, palette.Background, palette.BaseTheme == "Light" ? 0.12 : 0.35);
            var shellEnd = Blend(palette.Accent, palette.SurfaceAlt, palette.BaseTheme == "Light" ? 0.20 : 0.45);
            var shellText = IsDark(palette.Surface) ? Colors.White : Color.Parse("#11131A");

            SetColorOverride(app, "VsBackgroundColor", palette.Background);
            SetColorOverride(app, "VsCardColor", palette.Surface);
            SetColorOverride(app, "VsCardHighlightColor", palette.SurfaceAlt);
            SetColorOverride(app, "VsTextPrimaryColor", palette.TextPrimary);
            SetColorOverride(app, "VsTextSecondaryColor", palette.TextSecondary);
            SetColorOverride(app, "VsTextMutedColor", textMuted);
            SetColorOverride(app, "VsAccentColor", palette.Accent);
            SetColorOverride(app, "VsAccentSoftColor", accentSoft);
            SetColorOverride(app, "VsSuccessColor", palette.Success);
            SetColorOverride(app, "VsWarningColor", palette.Warning);
            SetColorOverride(app, "VsDangerColor", palette.Danger);
            SetColorOverride(app, "VsInputBackgroundColor", inputBackground);
            SetColorOverride(app, "VsInputBorderColor", inputBorder);
            SetColorOverride(app, "VsInputBorderFocusedColor", palette.Accent);
            SetColorOverride(app, "VsDividerColor", divider);
            SetColorOverride(app, "VsShellBrandStartColor", shellStart);
            SetColorOverride(app, "VsShellBrandEndColor", shellEnd);
            SetColorOverride(app, "VsShellBrandTextColor", shellText);
        }

        private static void ClearPaletteOverrides(Application app)
        {
            foreach (var key in PaletteResourceKeys)
                app.Resources.Remove(key);
        }

        private static ThemePaletteConfig NormalizePalette(ThemePaletteConfig palette)
        {
            var defaults = GetDefaultCustomTheme();
            return new ThemePaletteConfig
            {
                Name = string.IsNullOrWhiteSpace(palette.Name) ? defaults.Name : palette.Name.Trim(),
                BaseTheme = string.Equals(palette.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark",
                Background = NormalizeHex(palette.Background, defaults.Background),
                Surface = NormalizeHex(palette.Surface, defaults.Surface),
                SurfaceAlt = NormalizeHex(palette.SurfaceAlt, defaults.SurfaceAlt),
                Accent = NormalizeHex(palette.Accent, defaults.Accent),
                TextPrimary = NormalizeHex(palette.TextPrimary, defaults.TextPrimary),
                TextSecondary = NormalizeHex(palette.TextSecondary, defaults.TextSecondary),
                Success = NormalizeHex(palette.Success, defaults.Success),
                Warning = NormalizeHex(palette.Warning, defaults.Warning),
                Danger = NormalizeHex(palette.Danger, defaults.Danger)
            };
        }

        private static string NormalizeHex(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            var candidate = value.Trim();
            if (!candidate.StartsWith("#", StringComparison.Ordinal))
                candidate = "#" + candidate;

            return Color.TryParse(candidate, out var color)
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : fallback;
        }

        private static Color Blend(string foregroundHex, string backgroundHex, double amount)
        {
            var foreground = Color.Parse(foregroundHex);
            var background = Color.Parse(backgroundHex);
            return Blend(foreground, background, amount);
        }

        private static Color Blend(Color foreground, Color background, double amount)
        {
            var alpha = Math.Clamp(amount, 0d, 1d);
            var red = (byte)Math.Round((foreground.R * alpha) + (background.R * (1d - alpha)));
            var green = (byte)Math.Round((foreground.G * alpha) + (background.G * (1d - alpha)));
            var blue = (byte)Math.Round((foreground.B * alpha) + (background.B * (1d - alpha)));
            return Color.FromRgb(red, green, blue);
        }

        private static Color WithAlpha(string hex, double opacity)
        {
            var color = Color.Parse(hex);
            return Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0d, 1d) * 255d), color.R, color.G, color.B);
        }

        private static bool IsDark(string hex)
        {
            var color = Color.Parse(hex);
            var luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255d;
            return luminance < 0.55;
        }

        private static void SetColorOverride(Application app, string key, string hex)
        {
            app.Resources[key] = Color.Parse(hex);
        }

        private static void SetColorOverride(Application app, string key, Color color)
        {
            app.Resources[key] = color;
        }

        private static readonly string[] PaletteResourceKeys =
        {
            "VsBackgroundColor",
            "VsCardColor",
            "VsCardHighlightColor",
            "VsTextPrimaryColor",
            "VsTextSecondaryColor",
            "VsTextMutedColor",
            "VsAccentColor",
            "VsAccentSoftColor",
            "VsSuccessColor",
            "VsWarningColor",
            "VsDangerColor",
            "VsInputBackgroundColor",
            "VsInputBorderColor",
            "VsInputBorderFocusedColor",
            "VsDividerColor",
            "VsShellBrandStartColor",
            "VsShellBrandEndColor",
            "VsShellBrandTextColor"
        };
    }
}
