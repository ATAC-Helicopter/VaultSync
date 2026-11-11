using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VaultSync.UI.Controls;

public sealed record StorageChartSegment(double Value, IBrush Brush);

/// <summary>Two static storage summaries; labels and tooltips live in the adjacent legend.</summary>
public sealed class StorageChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<StorageChartSegment>> SegmentsProperty =
        AvaloniaProperty.Register<StorageChart, IReadOnlyList<StorageChartSegment>>(nameof(Segments), []);
    public static readonly StyledProperty<bool> IsDonutProperty =
        AvaloniaProperty.Register<StorageChart, bool>(nameof(IsDonut));

    static StorageChart() => AffectsRender<StorageChart>(SegmentsProperty, IsDonutProperty);

    public IReadOnlyList<StorageChartSegment> Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public bool IsDonut
    {
        get => GetValue(IsDonutProperty);
        set => SetValue(IsDonutProperty, value);
    }

    internal readonly record struct Slice(double Start, double End, IBrush Brush);

    internal static IReadOnlyList<Slice> Layout(IReadOnlyList<StorageChartSegment> segments, bool donut)
    {
        StorageChartSegment[] valid = segments.Where(s => double.IsFinite(s.Value) && s.Value > 0).ToArray();
        if (valid.Length == 0)
            return [];

        // Scale first so even a collection of very large byte counts cannot overflow its total.
        double scale = donut ? valid.Max(s => s.Value) : 100;
        double total = donut ? valid.Sum(s => s.Value / scale) : 1;
        var slices = new List<Slice>(valid.Length);
        double offset = 0;
        foreach (StorageChartSegment segment in valid)
        {
            double end = Math.Min(1, offset + segment.Value / scale / total);
            if (end > offset)
                slices.Add(new Slice(offset, end, segment.Brush));
            offset = end;
        }
        return slices;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        foreach (Slice slice in Layout(Segments, IsDonut))
        {
            if (!IsDonut)
            {
                context.DrawRectangle(slice.Brush, null,
                    new Rect(slice.Start * Bounds.Width, 0, (slice.End - slice.Start) * Bounds.Width, Bounds.Height));
                continue;
            }

            double radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            context.DrawGeometry(slice.Brush, null, CreateRingSlice(center, radius, slice.Start, slice.End));
        }
    }

    internal static StreamGeometry CreateRingSlice(Point center, double radius, double start, double end)
    {
        double inner = radius * 0.64;
        var geometry = new StreamGeometry();
        using StreamGeometryContext path = geometry.Open();
        path.BeginFigure(OnCircle(center, radius, start), true);
        // Two arcs handle a full circle as well as ordinary wedges without coincident endpoints.
        double middle = (start + end) / 2;
        path.ArcTo(OnCircle(center, radius, middle), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        path.ArcTo(OnCircle(center, radius, end), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        path.LineTo(OnCircle(center, inner, end));
        path.ArcTo(OnCircle(center, inner, middle), new Size(inner, inner), 0, false, SweepDirection.CounterClockwise);
        path.ArcTo(OnCircle(center, inner, start), new Size(inner, inner), 0, false, SweepDirection.CounterClockwise);
        path.EndFigure(true);
        return geometry;
    }

    private static Point OnCircle(Point center, double radius, double fraction)
    {
        double angle = fraction * Math.Tau - Math.PI / 2;
        return new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
    }
}
