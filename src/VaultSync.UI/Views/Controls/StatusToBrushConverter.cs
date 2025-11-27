using Avalonia.Data.Converters;
using Avalonia.Media;
using System;

namespace VaultSync.UI.Views.Controls
{
    public class StatusToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
                return Brushes.Transparent;

            // Base colors
            Color color;
            if (text.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                color = Color.Parse("#66C18A"); // green
            else if (text.StartsWith("Low space", StringComparison.OrdinalIgnoreCase))
                color = Color.Parse("#E0B35B"); // amber
            else if (text.StartsWith("Not", StringComparison.OrdinalIgnoreCase))
                color = Color.Parse("#E07B74"); // red
            else
                color = Colors.Gray;

            var asBg = (parameter as string)?.Equals("bg", StringComparison.OrdinalIgnoreCase) == true;
            if (asBg)
            {
                // Soft translucent background
                color = Color.FromArgb(40, color.R, color.G, color.B);
            }

            return new SolidColorBrush(color);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
