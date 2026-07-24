using System;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using VaultSync.Core.Config;
using VaultSync.UI.Services;
using VaultSync.UI.Views.Controls;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ThemeManagerTests
{
    [Fact]
    public void ThemePresets_AreUniqueCompleteAndParseable()
    {
        var presets = ThemeManager.GetThemePresets();

        Assert.Equal(12, presets.Count);
        Assert.Equal(presets.Count, presets.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var preset in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Id));
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            Assert.False(string.IsNullOrWhiteSpace(preset.Palette.Name));
            Assert.Contains(preset.Palette.BaseTheme, new[] { "Dark", "Light" });
            Assert.Contains(preset.Palette.VisualStyle, new[] { "Solid", "Glass" });

            Assert.True(Color.TryParse(preset.Palette.Background, out _));
            Assert.True(Color.TryParse(preset.Palette.Surface, out _));
            Assert.True(Color.TryParse(preset.Palette.SurfaceAlt, out _));
            Assert.True(Color.TryParse(preset.Palette.Accent, out _));
            Assert.True(Color.TryParse(preset.Palette.TextPrimary, out _));
            Assert.True(Color.TryParse(preset.Palette.TextSecondary, out _));
            Assert.True(Color.TryParse(preset.Palette.Success, out _));
            Assert.True(Color.TryParse(preset.Palette.Warning, out _));
            Assert.True(Color.TryParse(preset.Palette.Danger, out _));

            Color[] surfaces =
            [
                Color.Parse(preset.Palette.Background),
                Color.Parse(preset.Palette.Surface),
                Color.Parse(preset.Palette.SurfaceAlt)
            ];
            Assert.All(surfaces, surface =>
                Assert.True(ContrastRatio(Color.Parse(preset.Palette.TextPrimary), surface) >= 4.5));
            Assert.All(surfaces, surface =>
                Assert.True(ContrastRatio(Color.Parse(preset.Palette.TextSecondary), surface) >= 3.0));
        }
    }

    [Fact]
    public void GlassPresets_AreExplicitAndRemainGlassWhenCloned()
    {
        var glassPresets = ThemeManager.GetThemePresets()
            .Where(preset => string.Equals(preset.Palette.VisualStyle, "Glass", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(new[] { "aurora-glass", "porcelain-glass" }, glassPresets.Select(preset => preset.Id));
        Assert.All(glassPresets, preset => Assert.Equal("Glass", preset.Palette.Clone().VisualStyle));
    }

    [Fact]
    public void NormalizeCustomTheme_CorrectsUnreadableTextAcrossEverySurface()
    {
        var palette = new ThemePaletteConfig
        {
            Background = "#FFFFFF",
            Surface = "#F8F8F8",
            SurfaceAlt = "#EEEEEE",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#F0F0F0"
        };

        ThemePaletteConfig normalized = ThemeManager.NormalizeCustomTheme(palette);

        Assert.Equal("#11131A", normalized.TextPrimary);
        Assert.Equal("#11131A", normalized.TextSecondary);
    }

    [Theory]
    [InlineData("#00D9FF", "#11131A")]
    [InlineData("#14213D", "#FFFFFF")]
    public void ContrastForegroundConverter_SelectsReadableForeground(string background, string expected)
    {
        var converter = new ContrastForegroundConverter();

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(
            converter.Convert(background, typeof(IBrush), null, CultureInfo.InvariantCulture));

        Assert.Equal(Color.Parse(expected), brush.Color);
    }

    private static double ContrastRatio(Color first, Color second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R))
            + (0.7152 * Linearize(color.G))
            + (0.0722 * Linearize(color.B));
    }
}
