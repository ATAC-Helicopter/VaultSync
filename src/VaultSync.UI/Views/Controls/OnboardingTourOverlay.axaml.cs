using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VaultSync.UI.ViewModels;
using System.Collections.Generic;
using Avalonia.Layout;

namespace VaultSync.UI.Views.Controls;

public partial class OnboardingTourOverlay : UserControl
{
    private OnboardingTourViewModel? _vm;
    private Control? _target;
    private readonly List<ScrollViewer> _scrollViewers = [];
    private readonly List<Control> _scrollContents = [];
    private readonly List<ItemsControl> _watchedLists = [];
    private bool _refreshQueued;

    public OnboardingTourOverlay()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        OverlayCanvas.SizeChanged += (_, _) => UpdatePosition();
        TourCard.SizeChanged += (_, _) => UpdatePosition();
        AttachedToVisualTree += (_, _) => ScheduleRefresh();
        DetachedFromVisualTree += (_, _) => ClearTarget();
    }
    private void ScheduleRefresh()
    {
        if (_refreshQueued)
            return;

        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            FindTarget();
            _target?.BringIntoView();
            UpdatePosition();
        }, DispatcherPriority.Loaded);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as OnboardingTourViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        ScheduleRefresh();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OnboardingTourViewModel.TargetControlName)
            or nameof(OnboardingTourViewModel.IsActive))
        {
            ScheduleRefresh();
        }
        else if (e.PropertyName is nameof(OnboardingTourViewModel.Title)
            or nameof(OnboardingTourViewModel.Body)
            or nameof(OnboardingTourViewModel.ActionText))
        {
            Dispatcher.UIThread.Post(UpdatePosition, DispatcherPriority.Loaded);
        }
    }

    private void ClearTarget()
    {
        foreach (ScrollViewer sv in _scrollViewers)
            sv.ScrollChanged -= OnScrollChanged;
        _scrollViewers.Clear();

        foreach (Control content in _scrollContents)
            content.SizeChanged -= OnLayoutSizeChanged;
        _scrollContents.Clear();

        if (_target is not null)
        {
            _target.DetachedFromVisualTree -= OnTargetDetached;
            _target.SizeChanged -= OnLayoutSizeChanged;
            _target.EffectiveViewportChanged -= OnTargetViewportChanged;
            _target = null;
        }

        foreach (ItemsControl list in _watchedLists)
        {
            list.ContainerPrepared -= OnContainerPrepared;
            list.PropertyChanged -= OnListPropertyChanged;
        }

        _watchedLists.Clear();

    }


    private void FindTarget()
    {
        ClearTarget();
        if (_vm is null || !_vm.IsActive)
            return;
        string? name = _vm.TargetControlName;
        if (string.IsNullOrWhiteSpace(name) || TopLevel.GetTopLevel(this) is not Window window)
            return;

        _target = window.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        if (_target is null)
        {
            foreach (ItemsControl list in window.GetVisualDescendants().OfType<ItemsControl>())
            {
                list.ContainerPrepared += OnContainerPrepared;
                list.PropertyChanged += OnListPropertyChanged;
                _watchedLists.Add(list);
            }
            return;
        }

        _target.SizeChanged += OnLayoutSizeChanged;
        _target.DetachedFromVisualTree += OnTargetDetached;
        _target.EffectiveViewportChanged += OnTargetViewportChanged;

        Visual? current = _target.GetVisualParent();
        while (current is not null)
        {
            if (current is ScrollViewer sv)
            {
                sv.ScrollChanged += OnScrollChanged;
                _scrollViewers.Add(sv);

                if (sv.Content is Control content)
                {
                    content.SizeChanged += OnLayoutSizeChanged;
                    _scrollContents.Add(content);
                }
            }
            current = current.GetVisualParent();
        }
    }

    private void OnLayoutSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePosition();
    private void OnTargetDetached(object? sender, VisualTreeAttachmentEventArgs e) => ScheduleRefresh();
    private void OnTargetViewportChanged(object? sender, EffectiveViewportChangedEventArgs e) => UpdatePosition();
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e) => ScheduleRefresh();
    private void OnListPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
            ScheduleRefresh();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdatePosition();

    private void UpdatePosition()
    {
        if (_vm is null || !_vm.IsActive)
            return;

        double canvasW = OverlayCanvas.Bounds.Width;
        double canvasH = OverlayCanvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0)
            return;

        const double margin = 16;
        const double gap = 12;
        double cardW = TourCard.Bounds.Width > 0 ? TourCard.Bounds.Width : 300;
        double cardH = TourCard.Bounds.Height > 0 ? TourCard.Bounds.Height : 180;

        Rect? targetRect = GetVisibleTargetRect(canvasH);
        if (targetRect is not Rect r)
        {
            TargetHighlight.IsVisible = false;
            Canvas.SetLeft(TourCard, Math.Max(margin, canvasW - cardW - margin));
            Canvas.SetTop(TourCard, Math.Max(margin, canvasH - cardH - margin));
            return;
        }

        TargetHighlight.IsVisible = true;
        Canvas.SetLeft(TargetHighlight, r.X);
        Canvas.SetTop(TargetHighlight, r.Y);
        TargetHighlight.Width = r.Width;
        TargetHighlight.Height = r.Height;

        (double cardX, double cardY) = PickCardPosition(r, canvasW, canvasH, cardW, cardH, margin, gap);
        Canvas.SetLeft(TourCard, cardX);
        Canvas.SetTop(TourCard, cardY);
    }

    private Rect? GetVisibleTargetRect(double canvasH)
    {
        if (_target is null || !_target.IsEffectivelyVisible)
            return null;

        Point? pos = _target.TranslatePoint(new Point(0, 0), OverlayCanvas);
        if (pos is null || _target.Bounds.Width <= 0 || _target.Bounds.Height <= 0)
            return null;

        double clipTop = 0;
        double clipBottom = canvasH;
        foreach (ScrollViewer sv in _scrollViewers)
        {
            if (sv.TranslatePoint(new Point(0, 0), OverlayCanvas) is not Point svPos)
                continue;

            clipTop = Math.Max(clipTop, svPos.Y);
            clipBottom = Math.Min(clipBottom, svPos.Y + sv.Bounds.Height);
        }

        double visibleTop = Math.Max(pos.Value.Y, clipTop);
        double visibleBottom = Math.Min(pos.Value.Y + _target.Bounds.Height, clipBottom);
        double visibleHeight = visibleBottom - visibleTop;

        if (visibleHeight < Math.Min(16, _target.Bounds.Height * 0.5))
            return null;

        return new Rect(pos.Value.X - 4, visibleTop - 4, _target.Bounds.Width + 8, visibleHeight + 8);
    }

    private static (double X, double Y) PickCardPosition(
        Rect r, double canvasW, double canvasH, double cardW, double cardH, double margin, double gap)
    {
        double maxX = Math.Max(margin, canvasW - cardW - margin);
        double maxY = Math.Max(margin, canvasH - cardH - margin);

        if (r.Right + gap + cardW <= canvasW - margin)
            return (r.Right + gap, Math.Clamp(r.Top, margin, maxY));

        if (r.Left - gap - cardW >= margin)
            return (r.Left - gap - cardW, Math.Clamp(r.Top, margin, maxY));

        if (r.Bottom + gap + cardH <= canvasH - margin)
            return (Math.Clamp(r.Left, margin, maxX), r.Bottom + gap);

        if (r.Top - gap - cardH >= margin)
            return (Math.Clamp(r.Left, margin, maxX), r.Top - gap - cardH);

        return (maxX, maxY);
    }
}
