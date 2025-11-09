using System;
using Avalonia.Data.Converters;
using Avalonia;

namespace VaultSync.UI.Views.Converters;

public class BoolToWidthConverter : IValueConverter
{
    public static readonly BoolToWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string p && p.Contains(','))
        {
            var parts = p.Split(',');
            return flag ? double.Parse(parts[0]) : double.Parse(parts[1]);
        }
        return flag ? 200 : 60;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}