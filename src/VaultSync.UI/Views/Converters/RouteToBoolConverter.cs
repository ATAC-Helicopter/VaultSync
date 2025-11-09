using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace VaultSync.UI.Views.Converters
{
    /// <summary>
    /// Returns true if CurrentRoute (string) equals ConverterParameter (string), case-insensitive.
    /// Usage in XAML:
    ///   IsChecked="{Binding CurrentRoute, Converter={StaticResource RouteToBool}, ConverterParameter=Dashboard}"
    /// </summary>
    public class RouteToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var current = value?.ToString();
            var target = parameter?.ToString();
            return string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter is string route)
                return route;
            return BindingOperations.DoNothing;
        }
    }
}