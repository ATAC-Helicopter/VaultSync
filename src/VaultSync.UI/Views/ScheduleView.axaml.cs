using Avalonia.Controls;
using Avalonia;

namespace VaultSync.UI.Views;

public partial class ScheduleView : UserControl
{
    public ScheduleView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => UpdateResponsiveLayout();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    internal readonly record struct ResponsiveLayout(bool StackOptions, bool StackOverview);

    internal static ResponsiveLayout GetResponsiveLayout(double width) =>
        new(StackOptions: width < 760, StackOverview: width < 560);

    private void UpdateResponsiveLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        if (width <= 0)
            return;

        ResponsiveLayout layout = GetResponsiveLayout(width);
        ScheduleOptionsGrid.ColumnDefinitions = new ColumnDefinitions(layout.StackOptions ? "*" : "*,*");
        ScheduleOptionsGrid.RowDefinitions = new RowDefinitions(layout.StackOptions ? "Auto,Auto" : "Auto");
        Grid.SetColumn(QuietHoursCard, layout.StackOptions ? 0 : 1);
        Grid.SetRow(QuietHoursCard, layout.StackOptions ? 1 : 0);
        QuietHoursCard.Margin = layout.StackOptions ? new Thickness(0, 16, 0, 0) : default;

        ScheduleOverviewGrid.ColumnDefinitions = new ColumnDefinitions(layout.StackOverview ? "*" : "*,Auto");
        ScheduleOverviewGrid.RowDefinitions = new RowDefinitions(layout.StackOverview ? "Auto,Auto" : "Auto");
        Grid.SetColumn(NextRunPill, layout.StackOverview ? 0 : 1);
        Grid.SetRow(NextRunPill, layout.StackOverview ? 1 : 0);
        NextRunPill.Margin = layout.StackOverview ? new Thickness(0, 12, 0, 0) : default;
        NextRunPill.HorizontalAlignment = layout.StackOverview
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;
    }
}
