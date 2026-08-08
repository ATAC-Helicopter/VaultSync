using Avalonia;
using Avalonia.Controls;

namespace VaultSync.UI.Views;

public partial class ScheduleView : UserControl
{
    public ScheduleView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => UpdateResponsiveLayout();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    internal readonly record struct ResponsiveLayout(
        bool StackMetrics,
        bool StackOperations,
        bool StackPolicy,
        bool StackOverview);

    internal static ResponsiveLayout GetResponsiveLayout(double width) => new(
        StackMetrics: width < 920,
        StackOperations: width < 900,
        StackPolicy: width < 760,
        StackOverview: width < 620);

    private void UpdateResponsiveLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        if (width <= 0)
            return;

        ResponsiveLayout layout = GetResponsiveLayout(width);
        ApplyMetricsLayout(layout.StackMetrics);
        ApplyTwoCardLayout(
            ScheduleOperationalGrid,
            ProjectCoverageCard,
            layout.StackOperations,
            spacing: 16);
        ApplyTwoCardLayout(
            SchedulePolicyGrid,
            QuietHoursPanel,
            layout.StackPolicy,
            spacing: 16);
        ApplyOverviewLayout(layout.StackOverview);
    }

    private void ApplyMetricsLayout(bool stacked)
    {
        StatusMetricsGrid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,*,*");
        StatusMetricsGrid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto,Auto" : "Auto");

        PositionMetric(CoverageMetricCard, stacked, 0);
        PositionMetric(DestinationMetricCard, stacked, 1);
        PositionMetric(PowerMetricCard, stacked, 2);
    }

    private static void PositionMetric(Control card, bool stacked, int index)
    {
        Grid.SetColumn(card, stacked ? 0 : index);
        Grid.SetRow(card, stacked ? index : 0);
        card.Margin = stacked && index > 0 ? new Thickness(0, 12, 0, 0) : default;
    }

    private static void ApplyTwoCardLayout(
        Grid grid,
        Control secondCard,
        bool stacked,
        double spacing)
    {
        grid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,*");
        grid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        Grid.SetColumn(secondCard, stacked ? 0 : 1);
        Grid.SetRow(secondCard, stacked ? 1 : 0);
        secondCard.Margin = stacked ? new Thickness(0, spacing, 0, 0) : default;
    }

    private void ApplyOverviewLayout(bool stacked)
    {
        ScheduleOverviewGrid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,Auto");
        ScheduleOverviewGrid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        Grid.SetColumn(NextRunPill, stacked ? 0 : 1);
        Grid.SetRow(NextRunPill, stacked ? 1 : 0);
        NextRunPill.Margin = stacked ? new Thickness(0, 14, 0, 0) : default;
        NextRunPill.HorizontalAlignment = stacked
            ? Avalonia.Layout.HorizontalAlignment.Stretch
            : Avalonia.Layout.HorizontalAlignment.Right;
    }
}
