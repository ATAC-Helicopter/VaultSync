using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views.Controls;

public partial class OnboardingTourOverlay : UserControl
{
    private Control? _target;
    private OnboardingTourViewModel? _vm;

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

        _target = this.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
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
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var origin = _target.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        var bounds = new Rect(origin, _target.Bounds.Size);

        const double pad = 6;
        var highlightRect = bounds.Inflate(pad);

        HighlightBorder.IsVisible = true;
        HighlightBorder.Width = highlightRect.Width;
        HighlightBorder.Height = highlightRect.Height;
        Canvas.SetLeft(HighlightBorder, highlightRect.X);
        Canvas.SetTop(HighlightBorder, highlightRect.Y);

        PositionCallout(bounds, highlightRect, topLevel.Bounds.Size);
    }

    private void PositionCallout(Rect targetBounds, Rect highlightRect, Size containerSize)
    {
        const double margin = 12;

        var calloutWidth = CalloutCard.Width;
        var calloutHeight = CalloutCard.Bounds.Height > 0 ? CalloutCard.Bounds.Height : 180;

        var rightSpace = containerSize.Width - highlightRect.Right - margin;
        var leftSpace = highlightRect.Left - margin;
        var belowSpace = containerSize.Height - highlightRect.Bottom - margin;

        double left;
        double top;

        if (rightSpace >= calloutWidth)
        {
            left = highlightRect.Right + margin;
            top = Math.Max(margin, highlightRect.Top - 6);
        }
        else if (leftSpace >= calloutWidth)
        {
            left = highlightRect.Left - margin - calloutWidth;
            top = Math.Max(margin, highlightRect.Top - 6);
        }
        else if (belowSpace >= calloutHeight)
        {
            left = Math.Clamp(highlightRect.Left, margin, containerSize.Width - calloutWidth - margin);
            top = highlightRect.Bottom + margin;
        }
        else
        {
            left = Math.Clamp(highlightRect.Left, margin, containerSize.Width - calloutWidth - margin);
            top = Math.Max(margin, highlightRect.Top - calloutHeight - margin);
        }

        CalloutPopup.HorizontalOffset = left;
        CalloutPopup.VerticalOffset = top;
    }

    private void PositionCalloutCenter()
    {
        var size = Bounds.Size;
        var calloutWidth = CalloutCard.Width;
        var calloutHeight = CalloutCard.Bounds.Height > 0 ? CalloutCard.Bounds.Height : 180;

        var left = Math.Max(20, (size.Width - calloutWidth) / 2);
        var top = Math.Max(20, (size.Height - calloutHeight) / 2);

        CalloutPopup.HorizontalOffset = left;
        CalloutPopup.VerticalOffset = top;
    }
}
