using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;

namespace VaultSync.UI.Views.Controls
{
    public class StatusToBrushConverter : IValueConverter
    {
        private static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.Parse("#66C18A"));
        private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#E07B74"));
        private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#E0B35B"));
        private static readonly IBrush NeutralBrush = new ImmutableSolidColorBrush(Colors.Gray);

        private static readonly IBrush OkBackgroundBrush = MakeBackgroundBrush(OkBrush);
        private static readonly IBrush ErrorBackgroundBrush = MakeBackgroundBrush(ErrorBrush);
        private static readonly IBrush WarningBackgroundBrush = MakeBackgroundBrush(WarningBrush);
        private static readonly IBrush NeutralBackgroundBrush = MakeBackgroundBrush(NeutralBrush);

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
                return Brushes.Transparent;

            // Base colors
            IBrush brush;
            if (text.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                brush = OkBrush;
            else if (text.StartsWith("Reachable", StringComparison.OrdinalIgnoreCase))
                brush = OkBrush;
            else if (text.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                brush = ErrorBrush;
            else if (text.IndexOf("required", StringComparison.OrdinalIgnoreCase) >= 0)
                brush = ErrorBrush;
            else if (text.StartsWith("Low space", StringComparison.OrdinalIgnoreCase))
                brush = WarningBrush;
            else if (text.StartsWith("Not", StringComparison.OrdinalIgnoreCase))
                brush = ErrorBrush;
            else
                brush = NeutralBrush;

            var asBg = (parameter as string)?.Equals("bg", StringComparison.OrdinalIgnoreCase) == true;
            if (asBg)
            {
                // Soft translucent background
                return brush switch
                {
                    _ when ReferenceEquals(brush, OkBrush) => OkBackgroundBrush,
                    _ when ReferenceEquals(brush, WarningBrush) => WarningBackgroundBrush,
                    _ when ReferenceEquals(brush, ErrorBrush) => ErrorBackgroundBrush,
                    _ => NeutralBackgroundBrush
                };
            }

            return brush;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();

        private static IBrush MakeBackgroundBrush(IBrush source)
        {
            if (source is ImmutableSolidColorBrush solid)
            {
                var color = solid.Color;
                return new ImmutableSolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
            }

            return Brushes.Transparent;
        }
    }
}
