using Avalonia;
using Avalonia.Controls;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class RecoveryView : UserControl
{
    private const double CompactHeaderWidth = 720;
    private const double SingleColumnKpiWidth = 620;
    private const double StackedContentWidth = 980;

    public RecoveryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateResponsiveLayout();
        if (DataContext is RecoveryViewModel viewModel)
            _ = DetachedTask.RunAsync(viewModel.ActivateAsync, "activate-recovery-view");
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is RecoveryViewModel viewModel)
            viewModel.Deactivate();
    }

    private void UpdateResponsiveLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        if (width <= 0)
            return;

        bool compactHeader = width < CompactHeaderWidth;
        RecoveryHeaderGrid.ColumnDefinitions = new ColumnDefinitions(compactHeader ? "*" : "*,Auto");
        RecoveryHeaderGrid.RowDefinitions = new RowDefinitions(compactHeader ? "Auto,Auto" : "Auto");
        Grid.SetColumn(RecoveryHeaderActions, compactHeader ? 0 : 1);
        Grid.SetRow(RecoveryHeaderActions, compactHeader ? 1 : 0);
        RecoveryHeaderActions.Margin = compactHeader ? new Thickness(0, 12, 0, 0) : default;
        RecoveryHeaderActions.HorizontalAlignment = compactHeader
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;

        bool singleColumnKpis = width < SingleColumnKpiWidth;
        RecoveryKpiGrid.ColumnDefinitions = new ColumnDefinitions(singleColumnKpis ? "*" : "1.3*,*,*,*");
        RecoveryKpiGrid.RowDefinitions = new RowDefinitions(singleColumnKpis ? "Auto,Auto,Auto,Auto" : "Auto");
        RecoveryKpiGrid.ColumnSpacing = singleColumnKpis ? 0 : 12;
        RecoveryKpiGrid.RowSpacing = singleColumnKpis ? 12 : 0;
        PositionKpi(RecoveryScoreCard, 0, singleColumnKpis);
        PositionKpi(RecoveryReadyCard, 1, singleColumnKpis);
        PositionKpi(RecoveryAttentionCard, 2, singleColumnKpis);
        PositionKpi(RecoveryRiskCard, 3, singleColumnKpis);

        bool stackContent = width < StackedContentWidth;
        RecoveryContentGrid.ColumnDefinitions = new ColumnDefinitions(stackContent ? "*" : "360,*");
        RecoveryContentGrid.RowDefinitions = new RowDefinitions(stackContent ? "Auto,Auto" : "Auto");
        RecoveryContentGrid.ColumnSpacing = stackContent ? 0 : 16;
        RecoveryContentGrid.RowSpacing = stackContent ? 16 : 0;
        Grid.SetColumn(RecoveryProjectsPanel, stackContent ? 0 : 1);
        Grid.SetRow(RecoveryProjectsPanel, stackContent ? 1 : 0);
    }

    private static void PositionKpi(Control card, int index, bool stacked)
    {
        Grid.SetColumn(card, stacked ? 0 : index);
        Grid.SetRow(card, stacked ? index : 0);
    }
}
