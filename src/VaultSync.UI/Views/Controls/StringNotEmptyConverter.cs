using System;
using Avalonia.Data.Converters;

namespace VaultSync.UI.Views.Controls
{
    public sealed class StringNotEmptyConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var s = value as string;
            return !string.IsNullOrWhiteSpace(s);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
