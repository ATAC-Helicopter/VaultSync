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
        private int _restoreScrollAttempts;
        private DispatcherTimer? _restoreScrollTimer;

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

            LocalizationService? localization = LocalizationProvider.Service;
            if (localization != null)
            {
                localization.LanguageChanging += CaptureScrollOffset;
                localization.LanguageChanged += OnLanguageChanged;
            }
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            LocalizationService? localization = LocalizationProvider.Service;
            if (localization != null)
            {
                localization.LanguageChanging -= CaptureScrollOffset;
                localization.LanguageChanged -= OnLanguageChanged;
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.LayoutUpdated -= OnScrollViewerLayoutUpdated;
                _scrollViewer = null;
            }

            if (_restoreScrollTimer != null)
            {
                _restoreScrollTimer.Stop();
                _restoreScrollTimer = null;
            }
        }

        private void OnLanguageChanged()
        {
            if (_scrollViewer == null)
                return;

            _restoreScrollPending = true;
            _restoreScrollAttempts = 12;
            EnsureScrollRestoreTimer();
            ApplyPendingScroll(countAttempt: false);
        }

        private void CaptureScrollOffset()
        {
            if (_scrollViewer == null)
                return;

            _pendingScrollOffset = _scrollViewer.Offset.Y;
            _restoreScrollPending = true;
        }

        private void OnScrollViewerLayoutUpdated(object? sender, EventArgs e)
        {
            ApplyPendingScroll(countAttempt: false);
        }

        private void ApplyPendingScroll(bool countAttempt = true)
        {
            if (!_restoreScrollPending || _scrollViewer == null)
                return;

            Vector current = _scrollViewer.Offset;
            _scrollViewer.Offset = new Vector(current.X, _pendingScrollOffset);

            if (countAttempt && _restoreScrollAttempts > 0)
                _restoreScrollAttempts--;

            if (_restoreScrollAttempts > 0)
                return;

            _restoreScrollPending = false;
            _restoreScrollTimer?.Stop();
        }

        private void EnsureScrollRestoreTimer()
        {
            if (_restoreScrollTimer is not null)
            {
                _restoreScrollTimer.Stop();
            }
            else
            {
                _restoreScrollTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _restoreScrollTimer.Tick += (_, _) => ApplyPendingScroll(countAttempt: true);
            }

            _restoreScrollTimer.Start();
        }
    }
}
