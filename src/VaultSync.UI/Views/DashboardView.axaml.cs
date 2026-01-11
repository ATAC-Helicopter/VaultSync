using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace VaultSync.UI.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        ApplyChartTooltipStyle();
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
}
