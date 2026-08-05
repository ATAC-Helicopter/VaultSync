using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace VaultSync.UI.Views;

public partial class GuideView : UserControl
{
    public GuideView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => UpdateResponsiveLayout();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    internal readonly record struct ResponsiveLayout(
        bool StackHeader,
        int TopicColumns,
        int TermColumns);

    internal static ResponsiveLayout GetResponsiveLayout(double width) =>
        new(
            StackHeader: width < 720,
            TopicColumns: width < 820 ? 1 : 2,
            TermColumns: width < 620 ? 1 : width < 980 ? 2 : 3);

    private void UpdateResponsiveLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        if (width <= 0)
            return;

        ResponsiveLayout layout = GetResponsiveLayout(width);
        GuideHeaderGrid.ColumnDefinitions = new ColumnDefinitions(layout.StackHeader ? "*" : "*,Auto");
        GuideHeaderGrid.RowDefinitions = new RowDefinitions(layout.StackHeader ? "Auto,Auto" : "Auto");
        Grid.SetColumn(GuideHeaderActions, layout.StackHeader ? 0 : 1);
        Grid.SetRow(GuideHeaderActions, layout.StackHeader ? 1 : 0);
        GuideHeaderActions.Margin = layout.StackHeader ? new Thickness(0, 12, 0, 0) : default;
        GuideHeaderActions.HorizontalAlignment = layout.StackHeader
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;

        if (GuideTopicsItems.ItemsPanelRoot is UniformGrid topicGrid)
            topicGrid.Columns = layout.TopicColumns;
        if (GuideTermsItems.ItemsPanelRoot is UniformGrid termGrid)
            termGrid.Columns = layout.TermColumns;
    }
}
