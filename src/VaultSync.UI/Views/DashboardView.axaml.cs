using Avalonia;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VaultSync.UI.Views;

public partial class DashboardView : UserControl
{
    private const double StackedSectionsWidth = 900;
    private const double StackedActivityContentWidth = 680;
    private const double StackedStorageContentWidth = 560;
    private const double CompactStorageHeaderWidth = 500;

    internal readonly record struct ResponsiveLayout(
        int OverviewColumns,
        int KpiColumns,
        bool StackSections,
        bool StackActivityContent,
        bool StackStorageContent,
        bool CompactStorageHeader,
        double DonutHostSize);

    public DashboardView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        double width = Bounds.Width > 0 ? Bounds.Width : Width;
        if (width <= 0)
            return;

        ResponsiveLayout layout = GetResponsiveLayout(width);
        ConfigureCardGrid(
            ProtectionOverviewGrid,
            [ProtectionStatusCard, RequiredActionCard, NextRunCard, KnownGoodCard],
            layout.OverviewColumns);
        ConfigureCardGrid(
            KpiGrid,
            [ProjectsKpiCard, BackupsKpiCard, StorageKpiCard],
            layout.KpiColumns);
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
            OverviewColumns: GetOverviewColumns(width),
            KpiColumns: width < 700 ? 1 : 3,
            StackSections: width < StackedSectionsWidth,
            StackActivityContent: width < StackedActivityContentWidth,
            StackStorageContent: width < StackedStorageContentWidth,
            CompactStorageHeader: width < CompactStorageHeaderWidth,
            DonutHostSize: GetDonutHostSize(width));

    private static int GetOverviewColumns(double width)
    {
        if (width < 520)
            return 1;

        return width < 1100 ? 2 : 4;
    }

    private static double GetDonutHostSize(double width)
    {
        if (width < 400)
            return 220;

        return width < StackedStorageContentWidth ? 250 : 300;
    }

    private static void ConfigureCardGrid(Grid grid, IReadOnlyList<Control> cards, int columnCount)
    {
        int columns = Math.Clamp(columnCount, 1, cards.Count);
        int rows = (int)Math.Ceiling(cards.Count / (double)columns);
        grid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns)));
        grid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows)));
        grid.RowSpacing = 12;

        for (int index = 0; index < cards.Count; index++)
        {
            Grid.SetColumn(cards[index], index % columns);
            Grid.SetRow(cards[index], index / columns);
        }
    }

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

}
