using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class LogConsoleWindow : Window
    {
        private readonly LogConsoleViewModel _viewModel;
        private ScrollViewer? _scrollViewer;
        private ListBox? _logList;
        private bool _autoScroll = true;
        private DispatcherTimer? _scrollTimer;
        private bool _scrollPending;

        public LogConsoleWindow()
            : this(new LogConsoleViewModel(LogConsoleProvider.Service ?? new LogConsoleService()))
        {
        }

        public LogConsoleWindow(LogConsoleViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            DataContext = _viewModel;

            if (_viewModel.Lines is INotifyCollectionChanged notifier)
            {
                notifier.CollectionChanged += OnLinesChanged;
            }
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Closed += OnClosed;
            Opened += OnOpened;
            KeyDown += OnKeyDown;
        }

        private void OnOpened(object? sender, System.EventArgs e)
        {
            _viewModel.SetUiCaptureEnabled(true);
            if (OperatingSystem.IsMacOS())
                _viewModel.AutoScrollEnabled = false;
            _autoScroll = _viewModel.AutoScrollEnabled;
            if (this.FindControl<ListBox>("LogList") is { } list)
            {
                _logList = list;
                _scrollViewer = list.GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault();

                if (_scrollViewer is not null)
                {
                    _scrollViewer.ScrollChanged += OnScrollChanged;
                }
            }

            _scrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _scrollTimer.Tick += (_, _) => FlushPendingScroll();
        }

        private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_autoScroll)
                return;

            _scrollPending = true;
            if (_scrollTimer is not null && !_scrollTimer.IsEnabled)
                _scrollTimer.Start();
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_scrollViewer is null)
                return;

            var maxY = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;
            var shouldAutoScroll = _scrollViewer.Offset.Y >= Math.Max(0, maxY - 4);
            _autoScroll = shouldAutoScroll;

            if (_viewModel.AutoScrollEnabled != shouldAutoScroll)
                _viewModel.AutoScrollEnabled = shouldAutoScroll;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(LogConsoleViewModel.AutoScrollEnabled), StringComparison.Ordinal))
                return;

            _autoScroll = _viewModel.AutoScrollEnabled;
            if (_autoScroll)
            {
                _scrollPending = true;
                if (_scrollTimer is not null && !_scrollTimer.IsEnabled)
                    _scrollTimer.Start();
            }
        }

        private void OnClosed(object? sender, System.EventArgs e)
        {
            _viewModel.SetUiCaptureEnabled(false);
            if (_viewModel.Lines is INotifyCollectionChanged notifier)
            {
                notifier.CollectionChanged -= OnLinesChanged;
            }
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            KeyDown -= OnKeyDown;
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
            }
            _logList = null;
            if (_scrollTimer is not null)
            {
                _scrollTimer.Stop();
                _scrollTimer = null;
            }
            Opened -= OnOpened;
            Closed -= OnClosed;
        }

        private async void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.C)
                return;

            var modifiers = e.KeyModifiers;
            var isCopyGesture = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
            if (!isCopyGesture)
                return;

            if (await _viewModel.CopySelectedLineAsync())
                e.Handled = true;
        }

        private void FlushPendingScroll()
        {
            if (!_scrollPending)
            {
                _scrollTimer?.Stop();
                return;
            }

            _scrollPending = false;
            if (!_autoScroll)
                return;

            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollToEnd();
                return;
            }

            if (_logList is not null && _logList.ItemCount > 0)
            {
                var last = _logList.Items[_logList.ItemCount - 1];
                _logList.ScrollIntoView(last);
            }
        }
    }
}
