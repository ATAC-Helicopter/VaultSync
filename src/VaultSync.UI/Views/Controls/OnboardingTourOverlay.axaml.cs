using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views.Controls;

public partial class OnboardingTourOverlay : UserControl
{
    private Control? _target;
    private Control? _contentHost;
    private OnboardingTourViewModel? _vm;
    private CancellationTokenSource? _scrollCts;
    private DateTime _lastScrollAt;
    private string _lastScrollTarget = string.Empty;
    private double _lastScrollY = double.NaN;
    private bool _isScrolling;
    private bool _hasScrolledForTarget;
    private string _currentTargetName = string.Empty;

    public OnboardingTourOverlay()
    {
        InitializeComponent();
        CalloutPopup.PlacementTarget = this;

        DataContextChanged += (_, _) =>
        {
            _vm = DataContext as OnboardingTourViewModel;
            _target = null;
            if (_vm is not null)
            {
                _vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(OnboardingTourViewModel.TargetName) ||
                        e.PropertyName == nameof(OnboardingTourViewModel.IsActive))
                    {
                        _target = null;
                        UpdateTarget();
                        UpdateLayoutForTarget();
                    }
                };
            }
            UpdateTarget();
            UpdateLayoutForTarget();
        };

        LayoutUpdated += (_, _) => UpdateLayoutForTarget();
    }

    private void UpdateTarget()
    {
        if (_vm is null)
            return;

        var name = _vm.TargetName;
        if (string.IsNullOrWhiteSpace(name))
        {
            _target = null;
            return;
        }

        var root = (Visual?)TopLevel.GetTopLevel(this) ?? this;
        _contentHost = root.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => string.Equals(c.Name, "MainContent", StringComparison.Ordinal));

        _target = null;
        if (_contentHost is not null)
        {
            _target = _contentHost.FindControl<Control>(name);
            if (_target is null)
            {
                _target = _contentHost.GetVisualDescendants()
                    .OfType<Control>()
                    .FirstOrDefault(c =>
                        !this.IsVisualAncestorOf(c) &&
                        string.Equals(c.Name, name, StringComparison.Ordinal));
            }
        }
        else
        {
            _target = root.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(c =>
                    !this.IsVisualAncestorOf(c) &&
                    string.Equals(c.Name, name, StringComparison.Ordinal));
        }

        if (!string.Equals(_currentTargetName, name, StringComparison.Ordinal))
        {
            _currentTargetName = name;
            _lastScrollTarget = string.Empty;
            _lastScrollY = double.NaN;
            _hasScrolledForTarget = false;
        }
    }

    private void UpdateLayoutForTarget()
    {
        if (!IsVisible || _vm is null)
            return;

        if (_target is null || !_target.IsVisible)
        {
            UpdateTarget();
        }

        if (_target is null || !_target.IsVisible || _target.Bounds.Width <= 0 || _target.Bounds.Height <= 0)
        {
            PositionCalloutCenter();
            HighlightBorder.IsVisible = false;
            UpdateOverlayMask(null);
            return;
        }

        EnsureTargetInView();

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var origin = _target.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        var bounds = new Rect(origin, _target.Bounds.Size);

        const double pad = 16;
        var highlightRect = bounds.Inflate(pad);

        HighlightBorder.IsVisible = true;
        HighlightBorder.Width = highlightRect.Width;
        HighlightBorder.Height = highlightRect.Height;
        Canvas.SetLeft(HighlightBorder, highlightRect.X);
        Canvas.SetTop(HighlightBorder, highlightRect.Y);

        UpdateOverlayMask(highlightRect);
        PositionCallout(bounds, highlightRect, topLevel.Bounds.Size);
    }

    private void PositionCallout(Rect targetBounds, Rect highlightRect, Size containerSize)
    {
        const double margin = 12;
        const double centerClampPadding = 40;

        var calloutWidth = CalloutCard.Width;
        var calloutHeight = CalloutCard.Bounds.Height > 0 ? CalloutCard.Bounds.Height : 180;
        var contentBounds = GetContentBounds(containerSize);

        var left = contentBounds.Left + (contentBounds.Width * 0.35) - (calloutWidth / 2);
        var top = contentBounds.Bottom - calloutHeight - 32;

        var minX = contentBounds.Left + margin;
        var maxX = Math.Max(minX, contentBounds.Right - calloutWidth - margin);
        left = Math.Clamp(left, minX, maxX);

        var minY = Math.Max(contentBounds.Top + centerClampPadding, contentBounds.Top + margin);
        var maxY = Math.Max(minY, contentBounds.Bottom - calloutHeight - margin);
        top = Math.Clamp(top, minY, maxY);

        CalloutPopup.HorizontalOffset = left;
        CalloutPopup.VerticalOffset = top;
    }

    private void PositionCalloutCenter()
    {
        var size = Bounds.Size;
        var contentBounds = GetContentBounds(size);
        var calloutWidth = CalloutCard.Width;
        var calloutHeight = CalloutCard.Bounds.Height > 0 ? CalloutCard.Bounds.Height : 180;

        var left = Math.Max(contentBounds.Left + 20, contentBounds.Left + (contentBounds.Width * 0.35) - (calloutWidth / 2));
        var top = Math.Max(contentBounds.Top + 20, contentBounds.Bottom - calloutHeight - 32);

        CalloutPopup.HorizontalOffset = left;
        CalloutPopup.VerticalOffset = top;
    }

    private void UpdateOverlayMask(Rect? highlightRect)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        OverlayCanvas.Width = size.Width;
        OverlayCanvas.Height = size.Height;

        if (highlightRect is null)
        {
            SetOverlayRect(OverlayTop, new Rect(0, 0, size.Width, size.Height));
            SetOverlayRect(OverlayLeft, new Rect(0, 0, 0, 0));
            SetOverlayRect(OverlayRight, new Rect(0, 0, 0, 0));
            SetOverlayRect(OverlayBottom, new Rect(0, 0, 0, 0));
            return;
        }

        var clamped = ClampRectToBounds(highlightRect.Value, size);
        SetOverlayRect(OverlayTop, new Rect(0, 0, size.Width, clamped.Top));
        SetOverlayRect(OverlayBottom, new Rect(0, clamped.Bottom, size.Width, Math.Max(0, size.Height - clamped.Bottom)));
        SetOverlayRect(OverlayLeft, new Rect(0, clamped.Top, clamped.Left, clamped.Height));
        SetOverlayRect(OverlayRight, new Rect(clamped.Right, clamped.Top, Math.Max(0, size.Width - clamped.Right), clamped.Height));
    }

    private void EnsureTargetInView()
    {
        if (_target is null)
            return;

        var scrollViewer = ResolveScrollViewerForTarget(_target);
        if (scrollViewer is null || scrollViewer.Bounds.Height <= 0)
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastScrollAt).TotalMilliseconds < 650)
            return;

        if (_isScrolling)
            return;

        var origin = _target.TranslatePoint(new Point(0, 0), scrollViewer);
        if (origin is null)
            return;

        var targetRect = new Rect(origin.Value, _target.Bounds.Size).Inflate(12);
        var viewport = new Rect(0, 0, scrollViewer.Bounds.Width, scrollViewer.Bounds.Height);
        var viewportSafe = viewport.Deflate(24);

        var targetCenter = targetRect.Top + (targetRect.Height / 2);
        var viewportCenter = viewport.Top + (viewport.Height / 2);
        var centerDelta = Math.Abs(targetCenter - viewportCenter);

        if (viewportSafe.Contains(targetRect) && centerDelta < 24)
        {
            _hasScrolledForTarget = true;
            return;
        }

        var desiredY = ComputeTargetScrollY(scrollViewer, targetRect);
        var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var targetY = Math.Clamp(desiredY, 0, maxY);

        if (Math.Abs(targetY - scrollViewer.Offset.Y) < 1)
        {
            _hasScrolledForTarget = true;
            return;
        }

        if (_hasScrolledForTarget && viewportSafe.Contains(targetRect) && centerDelta < 36)
        {
            return;
        }

        var targetName = _target.Name ?? string.Empty;
        if (string.Equals(targetName, _lastScrollTarget, StringComparison.Ordinal) &&
            !double.IsNaN(_lastScrollY) &&
            Math.Abs(_lastScrollY - targetY) < 4)
        {
            return;
        }

        _lastScrollAt = now;
        _lastScrollTarget = targetName;
        _lastScrollY = targetY;
        _scrollCts?.Cancel();
        _scrollCts = new CancellationTokenSource();
        _ = AnimateScrollAsync(scrollViewer, targetY, _scrollCts.Token);
    }

    private static ScrollViewer? ResolveScrollViewerForTarget(Control target)
    {
        var candidates = target.GetVisualAncestors()
            .OfType<ScrollViewer>()
            .ToList();

        if (candidates.Count == 0)
            return null;

        var scrollable = candidates
            .Where(s => s.Extent.Height > (s.Viewport.Height + 1))
            .ToList();

        if (scrollable.Count == 0)
            return candidates.LastOrDefault();

        // Prefer the outermost scroll host so the highlighted area centers in the page.
        return scrollable.LastOrDefault();
    }

    private static double ComputeTargetScrollY(ScrollViewer scrollViewer, Rect targetRect)
    {
        var viewportHeight = scrollViewer.Bounds.Height;
        if (viewportHeight <= 0)
            return scrollViewer.Offset.Y;

        var safeMargin = 40;
        var safeHeight = Math.Max(0, viewportHeight - (safeMargin * 2));

        if (targetRect.Height >= safeHeight && safeHeight > 0)
        {
            return scrollViewer.Offset.Y + targetRect.Top - safeMargin;
        }

        var targetCenterInViewport = targetRect.Top + (targetRect.Height / 2);
        var viewportCenter = viewportHeight / 2;
        var deltaToCenter = targetCenterInViewport - viewportCenter;
        return scrollViewer.Offset.Y + deltaToCenter;
    }

    private async Task AnimateScrollAsync(ScrollViewer scrollViewer, double targetY, CancellationToken token)
    {
        if (scrollViewer is null)
            return;

        var start = scrollViewer.Offset;
        var startY = start.Y;
        var delta = targetY - startY;
        if (Math.Abs(delta) < 1)
            return;

        _isScrolling = true;
        try
        {
            const int steps = 12;
            const int delayMs = 16;
            for (var i = 1; i <= steps; i++)
            {
                if (token.IsCancellationRequested)
                    return;

                var t = i / (double)steps;
                var eased = 1 - Math.Pow(1 - t, 3);
                var y = startY + (delta * eased);
                scrollViewer.Offset = new Vector(start.X, y);
                await Task.Delay(delayMs, token);
            }

            if (!token.IsCancellationRequested)
            {
                scrollViewer.Offset = new Vector(start.X, targetY);
                _hasScrolledForTarget = true;
            }
        }
        finally
        {
            _isScrolling = false;
        }
    }

    private Rect GetContentBounds(Size containerSize)
    {
        if (_contentHost is null)
        {
            var root = (Visual?)TopLevel.GetTopLevel(this) ?? this;
            _contentHost = root.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(c => string.Equals(c.Name, "MainContent", StringComparison.Ordinal));
        }

        if (_contentHost is not null)
        {
            var origin = _contentHost.TranslatePoint(new Point(0, 0), this);
            if (origin is not null && _contentHost.Bounds.Width > 0 && _contentHost.Bounds.Height > 0)
            {
                return new Rect(origin.Value, _contentHost.Bounds.Size);
            }
        }

        return new Rect(0, 0, containerSize.Width, containerSize.Height);
    }

    private static Rect ClampRectToBounds(Rect rect, Size size)
    {
        var left = Math.Clamp(rect.Left, 0, size.Width);
        var top = Math.Clamp(rect.Top, 0, size.Height);
        var right = Math.Clamp(rect.Right, 0, size.Width);
        var bottom = Math.Clamp(rect.Bottom, 0, size.Height);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static void SetOverlayRect(Control overlay, Rect rect)
    {
        overlay.IsVisible = rect.Width > 0 && rect.Height > 0;
        if (!overlay.IsVisible)
            return;

        overlay.Width = rect.Width;
        overlay.Height = rect.Height;
        Canvas.SetLeft(overlay, rect.X);
        Canvas.SetTop(overlay, rect.Y);
    }
}
