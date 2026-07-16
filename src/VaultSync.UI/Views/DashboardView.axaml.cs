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
    private const double StackedSectionsWidth = 900;
    private const double StackedActivityContentWidth = 680;
    private const double StackedStorageContentWidth = 560;
    private const double CompactStorageHeaderWidth = 500;
    private INotifyPropertyChanged? _currentVmNotifier;

    internal readonly record struct ResponsiveLayout(
        bool StackSections,
        bool StackActivityContent,
        bool StackStorageContent,
        bool CompactStorageHeader,
        double DonutHostSize);

    public DashboardView()
    {
        InitializeComponent();
        ApplyChartTooltipStyle();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => DetachVmNotifier();
        StorageDonutChart.ActualThemeVariantChanged += (_, _) => ApplyChartTooltipStyle();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private void ApplyChartTooltipStyle()
    {
        Color tooltipBackground = GetResourceColor("VsCardHighlightColor", Colors.Black);
        Color tooltipText = GetResourceColor("VsTextPrimaryColor", Colors.White);

        StorageDonutChart.TooltipPosition = TooltipPosition.Auto;
        StorageDonutChart.TooltipTextSize = 13;
        StorageDonutChart.TooltipBackgroundPaint = new SolidColorPaint(ToSkColor(tooltipBackground));
        StorageDonutChart.TooltipTextPaint = new SolidColorPaint(ToSkColor(tooltipText));
    }

    private static Color GetResourceColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key, out object? value) == true && value is Color color)
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

        ForceDonutRefresh();
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

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateResponsiveLayout();
        ForceDonutRefresh();
    }

    private void UpdateResponsiveLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        if (width <= 0)
            return;

        ResponsiveLayout layout = GetResponsiveLayout(width);
        ConfigureSectionGrid(
            ActivitySectionsGrid,
            RecentActivityCard,
            layout.StackSections,
            "1.65*,0.85*");
        ConfigureSectionGrid(
            StorageSectionsGrid,
            BackupStorageCard,
            layout.StackSections,
            "1.15*,0.85*");

        ConfigureContentGrid(
            WeeklyActivityContentGrid,
            WeeklyActivityChart,
            layout.StackActivityContent,
            "4.4*,7.6*");
        ConfigureContentGrid(
            StorageBreakdownGrid,
            StorageLegendList,
            layout.StackStorageContent,
            "Auto,*");

        bool compactStorageHeader = layout.CompactStorageHeader;
        StorageHeaderGrid.ColumnDefinitions = new ColumnDefinitions(compactStorageHeader ? "*" : "*,Auto");
        StorageHeaderGrid.RowDefinitions = new RowDefinitions(compactStorageHeader ? "Auto,Auto" : "Auto");
        Grid.SetColumn(StorageSortSelector, compactStorageHeader ? 0 : 1);
        Grid.SetRow(StorageSortSelector, compactStorageHeader ? 1 : 0);
        StorageSortSelector.Margin = compactStorageHeader ? new Thickness(0, 8, 0, 0) : default;
        StorageSortSelector.HorizontalAlignment = compactStorageHeader
            ? Avalonia.Layout.HorizontalAlignment.Stretch
            : Avalonia.Layout.HorizontalAlignment.Right;

        double donutHostSize = layout.DonutHostSize;
        double donutSize = donutHostSize - 10;
        StorageDonutHost.Width = donutHostSize;
        StorageDonutHost.Height = donutHostSize;
        StorageDonutTrack.Width = donutSize;
        StorageDonutTrack.Height = donutSize;
        StorageDonutTrack.CornerRadius = new CornerRadius(donutSize / 2);
        StorageDonutChart.Width = donutSize;
        StorageDonutChart.Height = donutSize;
    }

    internal static ResponsiveLayout GetResponsiveLayout(double width) =>
        new(
            StackSections: width < StackedSectionsWidth,
            StackActivityContent: width < StackedActivityContentWidth,
            StackStorageContent: width < StackedStorageContentWidth,
            CompactStorageHeader: width < CompactStorageHeaderWidth,
            DonutHostSize: width < 400 ? 220 : width < StackedStorageContentWidth ? 250 : 300);

    private static void ConfigureSectionGrid(
        Grid grid,
        Control secondaryCard,
        bool stacked,
        string wideColumns)
    {
        grid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : wideColumns);
        grid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        Grid.SetColumn(secondaryCard, stacked ? 0 : 1);
        Grid.SetRow(secondaryCard, stacked ? 1 : 0);
        secondaryCard.Margin = stacked ? new Thickness(0, 16, 0, 0) : default;
    }

    private static void ConfigureContentGrid(
        Grid grid,
        Control secondaryContent,
        bool stacked,
        string wideColumns)
    {
        grid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : wideColumns);
        grid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        Grid.SetColumn(secondaryContent, stacked ? 0 : 1);
        Grid.SetRow(secondaryContent, stacked ? 1 : 0);
        secondaryContent.Margin = stacked ? new Thickness(0, 12, 0, 0) : default;
    }

    private void ForceDonutRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            StorageDonutChart.InvalidateMeasure();
            StorageDonutChart.InvalidateVisual();
        }, DispatcherPriority.Render);

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            StorageDonutChart.InvalidateMeasure();
            StorageDonutChart.InvalidateVisual();
        }, DispatcherPriority.Background);
    }
}
