using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VaultSync.UI.Services;

namespace VaultSync.UI.Views.Controls;

public sealed class ContrastForegroundConverter : IValueConverter
{
    private static readonly IBrush LightForeground = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush DarkForeground = new ImmutableSolidColorBrush(Color.Parse("#11131A"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!TryGetColor(value, out Color background))
            return LightForeground;

        return ThemeColor.BestContrast(background) == Colors.White
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

}
