using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VaultSync.UI.Views.Controls;

public sealed class ContrastForegroundConverter : IValueConverter
{
    private static readonly IBrush LightForeground = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush DarkForeground = new ImmutableSolidColorBrush(Color.Parse("#11131A"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!TryGetColor(value, out Color background))
            return LightForeground;

        return ContrastRatio(Colors.White, background) >= ContrastRatio(Color.Parse("#11131A"), background)
            ? LightForeground
            : DarkForeground;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool TryGetColor(object? value, out Color color)
    {
        if (value is ISolidColorBrush solid)
        {
            color = solid.Color;
            return true;
        }

        if (value is string text && Color.TryParse(text, out color))
            return true;

        color = default;
        return false;
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
