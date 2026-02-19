using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.ComponentModel;

namespace VaultSync.UI.Views;

public partial class DashboardView : UserControl
{
    private INotifyPropertyChanged? _currentVmNotifier;

    public DashboardView()
    {
        InitializeComponent();
        ApplyChartTooltipStyle();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => DetachVmNotifier();
        StorageDonutChart.ActualThemeVariantChanged += (_, _) => ApplyChartTooltipStyle();
    }

    private void ApplyChartTooltipStyle()
    {
        var tooltipBackground = GetResourceColor("VsCardHighlightColor", Colors.Black);
        var tooltipText = GetResourceColor("VsTextPrimaryColor", Colors.White);

        StorageDonutChart.TooltipPosition = TooltipPosition.Auto;
        StorageDonutChart.TooltipTextSize = 13;
        StorageDonutChart.TooltipBackgroundPaint = new SolidColorPaint(ToSkColor(tooltipBackground));
        StorageDonutChart.TooltipTextPaint = new SolidColorPaint(ToSkColor(tooltipText));
    }

    private static Color GetResourceColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key, out var value) == true && value is Color color)
        {
            return color;
        }

        return fallback;
    }

    private static SKColor ToSkColor(Color color) =>
        new(color.R, color.G, color.B, color.A);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        DetachVmNotifier();
        if (DataContext is INotifyPropertyChanged notifier)
        {
            _currentVmNotifier = notifier;
            notifier.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, "StorageSeries", System.StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, "HasStorageSeries", System.StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            StorageDonutChart.InvalidateMeasure();
            StorageDonutChart.InvalidateVisual();
        });
    }

    private void DetachVmNotifier()
    {
        if (_currentVmNotifier is null)
        {
            return;
        }

        _currentVmNotifier.PropertyChanged -= OnVmPropertyChanged;
        _currentVmNotifier = null;
    }
}
