using Avalonia;
using Avalonia.Controls;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class HistoryView : UserControl
{
    private const double CompactHeaderWidth = 840;
    private const double CompactFiltersWidth = 920;
    private const double StackedContentWidth = 1040;

    public HistoryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateResponsiveLayout();
        if (DataContext is HistoryViewModel viewModel)
            _ = viewModel.RefreshAsync();
    }

    private void UpdateResponsiveLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        if (width <= 0)
            return;

        bool compactHeader = width < CompactHeaderWidth;
        HistoryHeaderGrid.ColumnDefinitions = new ColumnDefinitions(compactHeader ? "*" : "*,Auto");
        HistoryHeaderGrid.RowDefinitions = new RowDefinitions(compactHeader ? "Auto,Auto" : "Auto");
        Grid.SetColumn(HistoryHeaderBadges, compactHeader ? 0 : 1);
        Grid.SetRow(HistoryHeaderBadges, compactHeader ? 1 : 0);
        HistoryHeaderBadges.Margin = compactHeader ? new Thickness(0, 10, 0, 0) : default;

        bool compactFilters = width < CompactFiltersWidth;
        HistoryFilterGrid.ColumnDefinitions = new ColumnDefinitions(compactFilters ? "*" : "*,Auto");
        HistoryFilterGrid.RowDefinitions = new RowDefinitions(compactFilters ? "Auto,Auto" : "Auto");
        Grid.SetColumn(HistoryFilterActions, compactFilters ? 0 : 1);
        Grid.SetRow(HistoryFilterActions, compactFilters ? 1 : 0);
        HistoryFilterActions.Margin = compactFilters ? new Thickness(0, 8, 0, 0) : default;
        HistoryFilterActions.HorizontalAlignment = compactFilters
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;

        bool stackContent = width < StackedContentWidth;
        HistoryContentGrid.ColumnDefinitions = new ColumnDefinitions(stackContent ? "*" : "*,360");
        HistoryContentGrid.RowDefinitions = new RowDefinitions(stackContent ? "1.15*,*" : "*");
        HistoryContentGrid.ColumnSpacing = stackContent ? 0 : 22;
        HistoryContentGrid.RowSpacing = stackContent ? 16 : 0;
        Grid.SetColumn(HistoryDetailPanel, stackContent ? 0 : 1);
        Grid.SetRow(HistoryDetailPanel, stackContent ? 1 : 0);
        Grid.SetRow(HistoryTimelinePanel, 0);
    }
}
