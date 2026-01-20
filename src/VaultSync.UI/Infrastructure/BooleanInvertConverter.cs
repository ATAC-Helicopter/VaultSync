using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VaultSync.UI.Infrastructure
{
    public sealed class BooleanInvertConverter : IValueConverter
    {
        public static readonly BooleanInvertConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool flag ? !flag : value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool flag ? !flag : value;
        }
    }
}
