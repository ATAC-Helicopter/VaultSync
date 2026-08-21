using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
        private const string VisualStyleGlass = "Glass";
        private const string VisualStyleSolid = "Solid";

        private sealed record ThemePresetDefinition(string Id, string Description, ThemePaletteConfig Palette);

        private static readonly ThemePresetDefinition[] ThemePresets =
        {
            new(
                "vaultsync-midnight",
                "Default VaultSync dark palette with balanced contrast and blue accents.",
                new ThemePaletteConfig()),
            Preset(
                "aurora-glass",
                "Layered midnight glass with cool reflections and an aurora-blue accent.",
                "Aurora Glass",
                ThemeDark,
                ["#07111F", "#13233A", "#203A59", "#67D8FF", "#F7FBFF", "#B7C9DC", "#55E0B2", "#FFD06A", "#FF7D91"],
                VisualStyleGlass),
            Preset(
                "porcelain-glass",
                "Bright frosted surfaces with soft blue reflections and crisp readable text.",
                "Porcelain Glass",
                ThemeLight,
                ["#EAF1F8", "#F8FBFF", "#DCE8F4", "#2878E8", "#111A28", "#53677E", "#168A66", "#B87708", "#CF4055"],
                VisualStyleGlass),
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
                ["#000000", "#090B10", "#111621", "#4F8DFF", "#F5F8FF", "#AAB5CB", "#4DDAA6", "#FFC766", "#FF7676"]),
            Preset(
                "paper-ink",
                "A warm, low-glare light theme inspired by paper, graphite, and editorial layouts.",
                "Paper & Ink",
                ThemeLight,
                ["#F3EFE6", "#FFFCF5", "#E7DFD0", "#356A8A", "#211F1A", "#6A6258", "#287D62", "#A36B10", "#B84747"]),
            Preset(
                "neon-dusk",
                "Deep violet surfaces with electric cyan accents and restrained neon contrast.",
                "Neon Dusk",
                ThemeDark,
                ["#100C1E", "#19132A", "#29203E", "#64E8FF", "#FFF9FF", "#C3B4D7", "#5CE2AE", "#FFD066", "#FF719E"])
        };

        private static ThemePresetDefinition Preset(
            string id,
            string description,
            string name,
            string baseTheme,
            string[] colors,
            string visualStyle = VisualStyleSolid)
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
                    VisualStyle = visualStyle,
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

        public static ThemePaletteConfig NormalizeCustomTheme(ThemePaletteConfig palette)
        {
            ArgumentNullException.ThrowIfNull(palette);
            return NormalizePalette(palette);
        }

        public static void ApplyAppearance(AppearanceConfig appearance)
        {
            Application? app = Application.Current;
            if (app is null)
                return;

            ApplyThemeVariant(app, appearance.Theme, appearance.CustomTheme);
            ApplyPaletteOverrides(app, appearance.Theme, appearance.CustomTheme);
            ApplyWindowTransparency(
                app,
                string.Equals(appearance.Theme, ThemeCustom, StringComparison.OrdinalIgnoreCase)
                && string.Equals(appearance.CustomTheme?.VisualStyle, VisualStyleGlass, StringComparison.OrdinalIgnoreCase));
        }

        public static void ApplyTheme(string themeName)
        {
            Application? app = Application.Current;
            if (app is null)
                return;

            ApplyThemeVariant(app, themeName, null);
            if (!string.Equals(themeName, ThemeCustom, StringComparison.OrdinalIgnoreCase))
            {
                ClearPaletteOverrides(app);
                ApplyWindowTransparency(app, useGlass: false);
            }
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
            Color textOnAccent = ThemeColor.BestContrast(Color.Parse(palette.Accent));
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
            SetColorOverride(app, "VsTextOnAccentColor", textOnAccent);
            SetColorOverride(app, "VsSuccessColor", palette.Success);
            SetColorOverride(app, "VsWarningColor", palette.Warning);
            SetColorOverride(app, "VsDangerColor", palette.Danger);
            SetColorOverride(app, "VsSuccessSoftColor", WithAlpha(palette.Success, isLightBase ? 0.14 : 0.20));
            SetColorOverride(app, "VsWarningSoftColor", WithAlpha(palette.Warning, isLightBase ? 0.14 : 0.20));
            SetColorOverride(app, "VsDangerSoftColor", WithAlpha(palette.Danger, isLightBase ? 0.14 : 0.20));
            SetColorOverride(app, "VsInputBackgroundColor", inputBackground);
            SetColorOverride(app, "VsInputBorderColor", inputBorder);
            SetColorOverride(app, "VsInputBorderFocusedColor", palette.Accent);
            SetColorOverride(app, "VsDividerColor", divider);
            SetColorOverride(app, "VsShellBrandStartColor", shellStart);
            SetColorOverride(app, "VsShellBrandEndColor", shellEnd);
            SetColorOverride(app, "VsShellBrandTextColor", shellText);

            ApplyVisualStyleOverrides(app, palette, isLightBase);
        }

        private static void ClearPaletteOverrides(Application app)
        {
            foreach (string key in PaletteResourceKeys)
                app.Resources.Remove(key);

            foreach (string key in VisualStyleResourceKeys)
                app.Resources.Remove(key);
        }

        private static ThemePaletteConfig NormalizePalette(ThemePaletteConfig palette)
        {
            ThemePaletteConfig defaults = GetDefaultCustomTheme();
            string background = ThemeColor.NormalizeHex(palette.Background, defaults.Background);
            string surface = ThemeColor.NormalizeHex(palette.Surface, defaults.Surface);
            string surfaceAlt = ThemeColor.NormalizeHex(palette.SurfaceAlt, defaults.SurfaceAlt);
            return new ThemePaletteConfig
            {
                Name = string.IsNullOrWhiteSpace(palette.Name) ? defaults.Name : palette.Name.Trim(),
                BaseTheme = string.Equals(palette.BaseTheme, ThemeLight, StringComparison.OrdinalIgnoreCase) ? ThemeLight : ThemeDark,
                VisualStyle = string.Equals(palette.VisualStyle, VisualStyleGlass, StringComparison.OrdinalIgnoreCase)
                    ? VisualStyleGlass
                    : VisualStyleSolid,
                Background = background,
                Surface = surface,
                SurfaceAlt = surfaceAlt,
                Accent = ThemeColor.NormalizeHex(palette.Accent, defaults.Accent),
                TextPrimary = EnsureReadableText(
                    ThemeColor.NormalizeHex(palette.TextPrimary, defaults.TextPrimary),
                    [background, surface, surfaceAlt],
                    4.5),
                TextSecondary = EnsureReadableText(
                    ThemeColor.NormalizeHex(palette.TextSecondary, defaults.TextSecondary),
                    [background, surface, surfaceAlt],
                    3.0),
                Success = ThemeColor.NormalizeHex(palette.Success, defaults.Success),
                Warning = ThemeColor.NormalizeHex(palette.Warning, defaults.Warning),
                Danger = ThemeColor.NormalizeHex(palette.Danger, defaults.Danger)
            };
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

        private static string EnsureReadableText(string preferredHex, string[] backgroundHexes, double minimumRatio)
        {
            Color preferred = Color.Parse(preferredHex);
            Color[] backgrounds = backgroundHexes.Select(Color.Parse).ToArray();
            if (backgrounds.All(background => ThemeColor.ContrastRatio(preferred, background) >= minimumRatio))
                return preferredHex;

            Color best = new[] { Colors.White, Color.Parse("#11131A") }
                .OrderByDescending(candidate => backgrounds.Min(background => ThemeColor.ContrastRatio(candidate, background)))
                .First();
            return $"#{best.R:X2}{best.G:X2}{best.B:X2}";
        }


        private static void SetColorOverride(Application app, string key, string hex)
        {
            app.Resources[key] = Color.Parse(hex);
        }

        private static void SetColorOverride(Application app, string key, Color color)
        {
            app.Resources[key] = color;
        }

        private static void ApplyWindowTransparency(Application app, bool useGlass)
        {
            if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } mainWindow)
            {
                return;
            }

            mainWindow.TransparencyLevelHint = useGlass
                ?
                [
                    WindowTransparencyLevel.AcrylicBlur,
                    WindowTransparencyLevel.Blur,
                    WindowTransparencyLevel.Transparent,
                    WindowTransparencyLevel.None
                ]
                : [WindowTransparencyLevel.None];
        }

        private static void ApplyVisualStyleOverrides(
            Application app,
            ThemePaletteConfig palette,
            bool isLightBase)
        {
            foreach (string key in VisualStyleResourceKeys)
                app.Resources.Remove(key);

            if (!string.Equals(palette.VisualStyle, VisualStyleGlass, StringComparison.OrdinalIgnoreCase))
                return;

            Color background = Color.Parse(palette.Background);
            Color surface = Color.Parse(palette.Surface);
            Color surfaceAlt = Color.Parse(palette.SurfaceAlt);
            Color accent = Color.Parse(palette.Accent);
            Color whiteReflection = WithAlpha(Colors.White, isLightBase ? 0.72 : 0.18);
            Color softReflection = WithAlpha(Colors.White, isLightBase ? 0.36 : 0.10);
            Color glassEdge = isLightBase
                ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x54, 0xD9, 0xEE, 0xFF);
            Color glassEdgeSoft = isLightBase
                ? Color.FromArgb(0x72, 0x9B, 0xB0, 0xC5)
                : Color.FromArgb(0x38, 0xA8, 0xD8, 0xFA);

            IBrush backdrop = BackdropGradient(background, surfaceAlt, accent, isLightBase);
            IBrush navigationGlass = GlassGradient(
                whiteReflection,
                WithAlpha(surface, isLightBase ? 0.78 : 0.68),
                WithAlpha(Blend(accent, background, isLightBase ? 0.05 : 0.12), isLightBase ? 0.72 : 0.58));
            IBrush toolbarGlass = GlassGradient(
                softReflection,
                WithAlpha(surface, isLightBase ? 0.82 : 0.72),
                WithAlpha(background, isLightBase ? 0.74 : 0.62));
            IBrush floatingGlass = GlassGradient(
                whiteReflection,
                WithAlpha(surface, isLightBase ? 0.90 : 0.82),
                WithAlpha(surfaceAlt, isLightBase ? 0.82 : 0.72));
            IBrush contentSurface = new SolidColorBrush(
                WithAlpha(surface, isLightBase ? 0.96 : 0.94));
            IBrush contentRaised = new SolidColorBrush(
                WithAlpha(surfaceAlt, isLightBase ? 0.96 : 0.92));

            app.Resources["WindowBackground"] = backdrop;
            app.Resources["VsBackgroundBrush"] = app.Resources["WindowBackground"];
            app.Resources["ShellNavigationBrush"] = navigationGlass;
            app.Resources["ShellToolbarBrush"] = toolbarGlass;
            app.Resources["GlassFloatingBrush"] = floatingGlass;
            app.Resources["GlassControlBrush"] = GlassGradient(
                softReflection,
                WithAlpha(surfaceAlt, isLightBase ? 0.84 : 0.74),
                WithAlpha(surface, isLightBase ? 0.78 : 0.66));
            app.Resources["GlassRimBrush"] = new SolidColorBrush(glassEdge);
            app.Resources["GlassRimSoftBrush"] = new SolidColorBrush(glassEdgeSoft);
            app.Resources["GlassReflectionBrush"] = new SolidColorBrush(softReflection);
            app.Resources["GlassAmbientGlowBrush"] = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(WithAlpha(accent, isLightBase ? 0.14 : 0.20), 0),
                    new GradientStop(WithAlpha(accent, isLightBase ? 0.05 : 0.07), 0.46),
                    new GradientStop(WithAlpha(accent, 0), 1)
                }
            };
            app.Resources["CardBackgroundBrush"] = contentSurface;
            app.Resources["VsCardBrush"] = app.Resources["CardBackgroundBrush"];
            app.Resources["SurfaceCardBrush"] = app.Resources["CardBackgroundBrush"];
            app.Resources["CardRaisedBackgroundBrush"] = contentRaised;
            app.Resources["VsCardHighlightBrush"] = app.Resources["CardRaisedBackgroundBrush"];
            app.Resources["Surface0"] = toolbarGlass;
            app.Resources["Surface1"] = contentSurface;
            app.Resources["Surface2"] = contentRaised;
            app.Resources["Surface3"] = new SolidColorBrush(
                WithAlpha(surfaceAlt, isLightBase ? 0.98 : 0.95));
            app.Resources["ItemBg"] = new SolidColorBrush(
                WithAlpha(surface, isLightBase ? 0.92 : 0.88));
            app.Resources["CardSelectedBrush"] = GlassGradient(
                WithAlpha(accent, isLightBase ? 0.20 : 0.28),
                WithAlpha(surfaceAlt, isLightBase ? 0.94 : 0.86),
                WithAlpha(surface, isLightBase ? 0.90 : 0.78));
            app.Resources["BorderSoft"] = new SolidColorBrush(glassEdgeSoft);
            app.Resources["DividerBrush"] = new SolidColorBrush(
                isLightBase ? Color.FromArgb(0x84, 0x8D, 0xA2, 0xB8) : Color.FromArgb(0x5C, 0x9D, 0xC2, 0xDE));
            app.Resources["InputBackgroundBrush"] = new SolidColorBrush(
                WithAlpha(surface, isLightBase ? 0.94 : 0.88));
        }

        private static LinearGradientBrush GlassGradient(Color reflection, Color body, Color depth)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(reflection, 0),
                    new GradientStop(body, 0.12),
                    new GradientStop(body, 0.62),
                    new GradientStop(depth, 1)
                }
            };
        }

        private static LinearGradientBrush BackdropGradient(
            Color background,
            Color surfaceAlt,
            Color accent,
            bool isLightBase)
        {
            Color ambient = Blend(accent, background, isLightBase ? 0.08 : 0.20);
            Color depth = Blend(surfaceAlt, background, isLightBase ? 0.24 : 0.38);
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(WithAlpha(ambient, isLightBase ? 0.86 : 0.76), 0),
                    new GradientStop(WithAlpha(background, isLightBase ? 0.94 : 0.88), 0.42),
                    new GradientStop(WithAlpha(depth, isLightBase ? 0.86 : 0.78), 1)
                }
            };
        }

        private static Color WithAlpha(Color color, double opacity)
        {
            return Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0d, 1d) * 255d), color.R, color.G, color.B);
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
            "VsTextOnAccentColor",
            "VsSuccessColor",
            "VsWarningColor",
            "VsDangerColor",
            "VsSuccessSoftColor",
            "VsWarningSoftColor",
            "VsDangerSoftColor",
            "VsInputBackgroundColor",
            "VsInputBorderColor",
            "VsInputBorderFocusedColor",
            "VsDividerColor",
            "VsShellBrandStartColor",
            "VsShellBrandEndColor",
            "VsShellBrandTextColor"
        };

        private static readonly string[] VisualStyleResourceKeys =
        {
            "WindowBackground",
            "VsBackgroundBrush",
            "ShellNavigationBrush",
            "ShellToolbarBrush",
            "GlassFloatingBrush",
            "GlassControlBrush",
            "GlassRimBrush",
            "GlassRimSoftBrush",
            "GlassReflectionBrush",
            "GlassAmbientGlowBrush",
            "CardBackgroundBrush",
            "VsCardBrush",
            "SurfaceCardBrush",
            "CardRaisedBackgroundBrush",
            "VsCardHighlightBrush",
            "Surface0",
            "Surface1",
            "Surface2",
            "Surface3",
            "ItemBg",
            "CardSelectedBrush",
            "BorderSoft",
            "DividerBrush",
            "InputBackgroundBrush"
        };
    }
}
