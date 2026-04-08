using System;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.Views.Controls;

public sealed class NotificationSeverityAccentConverter : IValueConverter
{
    private static readonly IBrush InfoBrush = new ImmutableSolidColorBrush(Color.Parse("#4C88FF"));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is NotificationSeverity severity
            ? severity switch
            {
                NotificationSeverity.Error => ErrorBrush,
                NotificationSeverity.Warning => WarningBrush,
                _ => InfoBrush
            }
            : InfoBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
