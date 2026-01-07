using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VaultSync.UI.Services;

namespace VaultSync.UI
{
    public partial class SettingsView : UserControl
    {
        private ScrollViewer? _scrollViewer;
        private double _pendingScrollOffset;
        private bool _restoreScrollPending;

        public SettingsView()
        {
            InitializeComponent();
            AttachedToVisualTree += OnAttachedToVisualTree;
            DetachedFromVisualTree += OnDetachedFromVisualTree;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _scrollViewer = this.FindControl<ScrollViewer>("SettingsScrollViewer");
            if (_scrollViewer != null)
            {
                _scrollViewer.LayoutUpdated += OnScrollViewerLayoutUpdated;
            }

            var localization = LocalizationProvider.Service;
            if (localization != null)
            {
                localization.LanguageChanged += OnLanguageChanged;
            }
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            var localization = LocalizationProvider.Service;
            if (localization != null)
            {
                localization.LanguageChanged -= OnLanguageChanged;
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.LayoutUpdated -= OnScrollViewerLayoutUpdated;
                _scrollViewer = null;
            }
        }

        private void OnLanguageChanged()
        {
            if (_scrollViewer == null)
                return;

            _pendingScrollOffset = _scrollViewer.Offset.Y;
            _restoreScrollPending = true;

            Dispatcher.UIThread.Post(ApplyPendingScroll, DispatcherPriority.Background);
        }

        private void OnScrollViewerLayoutUpdated(object? sender, EventArgs e)
        {
            ApplyPendingScroll();
        }

        private void ApplyPendingScroll()
        {
            if (!_restoreScrollPending || _scrollViewer == null)
                return;

            _restoreScrollPending = false;
            var current = _scrollViewer.Offset;
            _scrollViewer.Offset = new Vector(current.X, _pendingScrollOffset);
        }
    }
}
