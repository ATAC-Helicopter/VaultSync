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
        private const string ThemeDark = "Dark";
        private const string ThemeLight = "Light";
        private const string ThemeCustom = "Custom";

        private sealed record ThemePresetDefinition(string Id, string Description, ThemePaletteConfig Palette);

        private static readonly ThemePresetDefinition[] ThemePresets =
        {
            new(
                "vaultsync-midnight",
                "Default VaultSync dark palette with balanced contrast and blue accents.",
                new ThemePaletteConfig()),
            Preset(
                "studio-light",
                "Clean light workspace with crisp surfaces and a bright accent.",
                "Studio Light",
                ThemeLight,
                ["#F4F5F9", "#FFFFFF", "#E6E9F2", "#2663FF", "#11131A", "#5C6275", "#32DFA0", "#FFBF5F", "#FF6A6A"]),
            Preset(
                "ember",
                "Warm dark tones with orange accents for a softer night theme.",
                "Ember",
                ThemeDark,
                ["#14110F", "#201A16", "#2C241E", "#FF8B4D", "#FFF4EB", "#D4BBA8", "#54D7A2", "#F7C66B", "#FF7B74"]),
            Preset(
                "fjord",
                "Cool blue dark theme inspired by colder, calmer palettes.",
                "Fjord",
                ThemeDark,
                ["#0D1620", "#142131", "#1B2B3E", "#4CC9F0", "#F4FAFF", "#AFC4D9", "#51D5AA", "#FFCB65", "#FF7A88"]),
            Preset(
                "deep-blue",
                "Deeper blue night theme with stronger contrast and cooler surfaces.",
                "Deep Blue",
                ThemeDark,
                ["#09111B", "#0F1A29", "#16253A", "#5D8DFF", "#F4F8FF", "#A9BBD6", "#57D8AF", "#FFCA66", "#FF7B86"]),
            Preset(
                "forest",
                "Muted green palette built for long sessions and lower visual noise.",
                "Forest",
                ThemeDark,
                ["#101611", "#18211A", "#223026", "#5AC88F", "#F5FFF7", "#B4CDB8", "#5CE2A1", "#EFC56A", "#FF7B79"]),
            Preset(
                "orchid",
                "High-contrast violet palette with a brighter accent pop.",
                "Orchid",
                ThemeDark,
                ["#15111C", "#21182B", "#2B2238", "#B983FF", "#FFF7FF", "#CAB7DA", "#5ED7B4", "#F8C86C", "#FF82A8"]),
            Preset(
                "oled-black",
                "Pure black dark theme for OLED displays with bright blue highlights.",
                "OLED Black",
                ThemeDark,
                ["#000000", "#090B10", "#111621", "#4F8DFF", "#F5F8FF", "#AAB5CB", "#4DDAA6", "#FFC766", "#FF7676"])
        };

        private static ThemePresetDefinition Preset(
            string id,
            string description,
            string name,
            string baseTheme,
            string[] colors)
        {
            if (colors.Length != 9)
                throw new ArgumentException("Theme palette presets require exactly nine colors.", nameof(colors));

            return new ThemePresetDefinition(
                id,
                description,
                new ThemePaletteConfig
                {
                    Name = name,
                    BaseTheme = baseTheme,
                    Background = colors[0],
                    Surface = colors[1],
                    SurfaceAlt = colors[2],
                    Accent = colors[3],
                    TextPrimary = colors[4],
                    TextSecondary = colors[5],
                    Success = colors[6],
                    Warning = colors[7],
                    Danger = colors[8]
                });
        }

        public static IReadOnlyList<(string Id, string Description, ThemePaletteConfig Palette)> GetThemePresets()
        {
            var items = new List<(string, string, ThemePaletteConfig)>(ThemePresets.Length);
            foreach (ThemePresetDefinition preset in ThemePresets)
            {
                ThemePaletteConfig palette = preset.Palette.Clone();
                palette.Name = L($"Settings.Appearance.ThemePresets.{preset.Id}.Name", palette.Name);
                string description = L($"Settings.Appearance.ThemePresets.{preset.Id}.Description", preset.Description);
                items.Add((preset.Id, description, palette));
            }
            return items;
        }

        public static ThemePaletteConfig GetDefaultCustomTheme()
        {
            ThemePaletteConfig palette = ThemePresets[0].Palette.Clone();
            palette.Name = L("Settings.Appearance.ThemePresets.vaultsync-midnight.Name", palette.Name);
            return palette;
        }

        public static void ApplyAppearance(AppearanceConfig appearance)
        {
            Application? app = Application.Current;
            if (app is null)
                return;

            ApplyThemeVariant(app, appearance.Theme, appearance.CustomTheme);
            ApplyPaletteOverrides(app, appearance.Theme, appearance.CustomTheme);
        }

        public static void ApplyTheme(string themeName)
        {
            Application? app = Application.Current;
            if (app is null)
                return;

            ApplyThemeVariant(app, themeName, null);
            if (!string.Equals(themeName, ThemeCustom, StringComparison.OrdinalIgnoreCase))
                ClearPaletteOverrides(app);
        }

        public static void ApplyCompactLayout(bool compact)
        {
            Application? app = Application.Current;
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
                ThemeDark => ThemeVariant.Dark,
                ThemeLight => ThemeVariant.Light,
                ThemeCustom when string.Equals(customTheme?.BaseTheme, ThemeLight, StringComparison.OrdinalIgnoreCase) => ThemeVariant.Light,
                ThemeCustom => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }

        private static string L(string key, string fallback)
        {
            string? value = LocalizationProvider.Service?.GetString(key);
            return string.Equals(value, key, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value)
                ? fallback
                : value;
        }

        private static void ApplyPaletteOverrides(Application app, string themeName, ThemePaletteConfig? customTheme)
        {
            if (!string.Equals(themeName, ThemeCustom, StringComparison.OrdinalIgnoreCase))
            {
                ClearPaletteOverrides(app);
                return;
            }

            ThemePaletteConfig palette = NormalizePalette(customTheme ?? GetDefaultCustomTheme());
            bool isLightBase = string.Equals(palette.BaseTheme, ThemeLight, StringComparison.OrdinalIgnoreCase);
            Color accentSoft = WithAlpha(palette.Accent, isLightBase ? 0.14 : 0.24);
            Color textMuted = Blend(palette.TextSecondary, palette.Background, isLightBase ? 0.45 : 0.60);
            Color inputBackground = Blend(palette.SurfaceAlt, palette.Background, isLightBase ? 0.45 : 0.25);
            Color inputBorder = Blend(palette.SurfaceAlt, palette.TextSecondary, isLightBase ? 0.35 : 0.28);
            Color divider = Blend(palette.SurfaceAlt, palette.TextSecondary, isLightBase ? 0.25 : 0.18);
            Color shellStart = Blend(palette.Accent, palette.Background, isLightBase ? 0.12 : 0.35);
            Color shellEnd = Blend(palette.Accent, palette.SurfaceAlt, isLightBase ? 0.20 : 0.45);
            Color shellText = IsDark(palette.Surface) ? Colors.White : Color.Parse("#11131A");

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
            foreach (string key in PaletteResourceKeys)
                app.Resources.Remove(key);
        }

        private static ThemePaletteConfig NormalizePalette(ThemePaletteConfig palette)
        {
            ThemePaletteConfig defaults = GetDefaultCustomTheme();
            return new ThemePaletteConfig
            {
                Name = string.IsNullOrWhiteSpace(palette.Name) ? defaults.Name : palette.Name.Trim(),
                BaseTheme = string.Equals(palette.BaseTheme, ThemeLight, StringComparison.OrdinalIgnoreCase) ? ThemeLight : ThemeDark,
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

            string candidate = value.Trim();
            if (!candidate.StartsWith("#", StringComparison.Ordinal))
                candidate = "#" + candidate;

            return Color.TryParse(candidate, out Color color)
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
            double alpha = Math.Clamp(amount, 0d, 1d);
            byte red = (byte)Math.Round((foreground.R * alpha) + (background.R * (1d - alpha)));
            byte green = (byte)Math.Round((foreground.G * alpha) + (background.G * (1d - alpha)));
            byte blue = (byte)Math.Round((foreground.B * alpha) + (background.B * (1d - alpha)));
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
            double luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255d;
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
