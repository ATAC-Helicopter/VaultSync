using System;
using Avalonia.Media;

namespace VaultSync.UI.Services;

internal static class ThemeColor
{
    private static readonly Color NearBlack = Color.Parse("#11131A");

    public static string NormalizeHex(string? value, string fallback)
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

    public static Color BestContrast(Color background) =>
        ContrastRatio(Colors.White, background) >= ContrastRatio(NearBlack, background)
            ? Colors.White
            : NearBlack;

    public static double ContrastRatio(Color first, Color second)
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
