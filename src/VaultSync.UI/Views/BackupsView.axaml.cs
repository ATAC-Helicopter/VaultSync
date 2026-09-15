using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class BackupsView : UserControl
    {
        private const double SingleSummaryColumnWidth = 620;
        private const double ThreeSummaryColumnWidth = 1050;
        private BackupsViewModel? _viewModel;

        internal readonly record struct ResponsiveLayout(int SummaryColumns);

        public BackupsView()
        {
            AvaloniaXamlLoader.Load(this);
            DataContextChanged += OnDataContextChanged;
            AttachedToVisualTree += (_, _) => OnDataContextChanged(this, EventArgs.Empty);
            DetachedFromVisualTree += (_, _) => DetachViewModel();
            AttachedToVisualTree += (_, _) => UpdateResponsiveLayout();
            SizeChanged += (_, _) => UpdateResponsiveLayout();
            OnDataContextChanged(this, EventArgs.Empty);
        }

        internal static ResponsiveLayout GetResponsiveLayout(double width)
        {
            if (width < SingleSummaryColumnWidth)
                return new ResponsiveLayout(1);
            if (width < ThreeSummaryColumnWidth)
                return new ResponsiveLayout(2);
            return new ResponsiveLayout(3);
        }

        private void UpdateResponsiveLayout()
        {
            double width = Bounds.Width > 0 ? Bounds.Width : Width;
            if (width <= 0)
                return;

            Grid? summaryGrid = this.FindControl<Grid>("SummaryGrid");
            Control? totalCard = this.FindControl<Control>("TotalSnapshotsSummaryCard");
            Control? lastCard = this.FindControl<Control>("LastBackupSummaryCard");
            Control? storedCard = this.FindControl<Control>("StoredBackupSummaryCard");
            if (summaryGrid is null || totalCard is null || lastCard is null || storedCard is null)
                return;

            ConfigureSummaryGrid(
                summaryGrid,
                GetResponsiveLayout(width).SummaryColumns,
                [totalCard, lastCard, storedCard]);
        }

        private static void ConfigureSummaryGrid(
            Grid summaryGrid,
            int columnCount,
            IReadOnlyList<Control> cards)
        {
            int columns = Math.Clamp(columnCount, 1, cards.Count);
            int rows = (int)Math.Ceiling(cards.Count / (double)columns);
            summaryGrid.ColumnDefinitions = new ColumnDefinitions(
                string.Join(",", Enumerable.Repeat("*", columns)));
            summaryGrid.RowDefinitions = new RowDefinitions(
                string.Join(",", Enumerable.Repeat("Auto", rows)));

            for (int index = 0; index < cards.Count; index++)
            {
                Grid.SetColumn(cards[index], index % columns);
                Grid.SetRow(cards[index], index / columns);
                Grid.SetColumnSpan(cards[index], 1);
            }

            if (columns == 2)
                Grid.SetColumnSpan(cards[^1], 2);
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            DetachViewModel();

            _viewModel = DataContext as BackupsViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void DetachViewModel()
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BackupsViewModel.DiffFileContentLines))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("DiffContentScrollViewer");
                if (scrollViewer is not null)
                    scrollViewer.Offset = new Vector(0, 0);
            }, DispatcherPriority.Loaded);
        }

    }
}
