using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VaultSync.UI.Infrastructure;

/// <summary>
/// Converts a bool to a char (e.g., show password when true => no masking).
/// Parameter provides the mask char (default '*').
/// </summary>
public class BooleanToCharConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        char maskChar = parameter is string s && s.Length > 0 ? s[0] : '*';
        if (value is bool show && show)
            return '\0'; // no mask
        return maskChar;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
