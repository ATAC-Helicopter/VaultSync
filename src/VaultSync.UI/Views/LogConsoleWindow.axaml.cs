using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class LogConsoleWindow : Window
    {
        private readonly LogConsoleViewModel _viewModel;
        private ScrollViewer? _scrollViewer;
        private bool _autoScroll = true;

        public LogConsoleWindow(LogConsoleViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            DataContext = _viewModel;

            if (_viewModel.Lines is INotifyCollectionChanged notifier)
            {
                notifier.CollectionChanged += OnLinesChanged;
            }
            Closed += OnClosed;
            Opened += OnOpened;
        }

        private void OnOpened(object? sender, System.EventArgs e)
        {
            if (this.FindControl<ListBox>("LogList") is { } list)
            {
                _scrollViewer = list.GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault();

                if (_scrollViewer is not null)
                {
                    _scrollViewer.ScrollChanged += OnScrollChanged;
                }
            }
        }

        private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_autoScroll)
                return;

            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollToEnd();
                return;
            }

            if (this.FindControl<ListBox>("LogList") is { } list && list.ItemCount > 0)
            {
                var last = list.Items[list.ItemCount - 1];
                list.ScrollIntoView(last);
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_scrollViewer is null)
                return;

            var maxY = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;
            _autoScroll = _scrollViewer.Offset.Y >= Math.Max(0, maxY - 4);
        }

        private void OnClosed(object? sender, System.EventArgs e)
        {
            if (_viewModel.Lines is INotifyCollectionChanged notifier)
            {
                notifier.CollectionChanged -= OnLinesChanged;
            }
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
            }
            Opened -= OnOpened;
            Closed -= OnClosed;
        }
    }
}
