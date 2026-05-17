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
        private TextBox? _logTextBox;
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
            _viewModel.SetClipboardProvider(() => Clipboard);

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
            if (this.FindControl<TextBox>("LogTextBox") is { } textBox)
            {
                _logTextBox = textBox;
                _scrollViewer = textBox.GetVisualDescendants()
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

            double maxY = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;
            bool shouldAutoScroll = _scrollViewer.Offset.Y >= Math.Max(0, maxY - 4);
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
            _logTextBox = null;
            if (_scrollTimer is not null)
            {
                _scrollTimer.Stop();
                _scrollTimer = null;
            }
            Opened -= OnOpened;
            Closed -= OnClosed;
            _viewModel.Dispose();
        }

        private async void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.C)
                return;

            KeyModifiers modifiers = e.KeyModifiers;
            bool isCopyGesture = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
            if (!isCopyGesture)
                return;

            if (e.Source is TextBox textBox && !string.IsNullOrEmpty(textBox.SelectedText))
                return;

            if (await _viewModel.CopySelectedLineAsync())
                e.Handled = true;
        }

        private async void OnCopySelectionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            string? selectedText = _logTextBox?.SelectedText;
            if (!string.IsNullOrEmpty(selectedText) && Clipboard is not null)
            {
                await Clipboard.SetTextAsync(selectedText);
                e.Handled = true;
                return;
            }

            _ = await _viewModel.CopySelectedLineAsync();
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

            if (_logTextBox is not null)
            {
                int end = _logTextBox.Text?.Length ?? 0;
                _logTextBox.CaretIndex = end;
                _logTextBox.SelectionStart = end;
                _logTextBox.SelectionEnd = end;
            }
        }
    }
}
