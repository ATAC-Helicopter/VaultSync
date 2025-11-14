using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Avalonia.Media; // <-- for Brush in legend + activity

namespace VaultSync.UI.ViewModels
{
    public class DashboardViewModel : ViewModelBase
    {
        // KPIs
        public int ProjectCount { get; } = 12;
        public string ProjectsHint { get; } = "3 active";
        public int SnapshotCount { get; } = 46;
        public string SnapshotsHint { get; } = "7 this week";
        public string StorageUsed { get; } = "1.2 TB";
        public string StorageHint { get; } = "of 4 TB total";

        // Search / actions (your RelayCommand expects Action<object?>)
        public string? SearchText { get; set; }
        public RelayCommand RefreshCommand { get; } = new RelayCommand(_ => { });
        public RelayCommand NewSnapshotCommand { get; } = new RelayCommand(_ => { });

        // Chart bindings
        public ISeries[] SnapshotSeries { get; }
        public Axis[] SnapshotXAxes { get; }
        public Axis[] SnapshotYAxes { get; }
        public string TotalSnapshotsWeek => _snapshots.Sum().ToString();

        // Donut bindings
        public ISeries[] StorageSeries { get; }
        public IEnumerable<LegendItem> StorageLegend { get; }

        // Activity
        public ObservableCollection<ActivityItem> ActivityItems { get; } = new()
        {
            new ActivityItem("Daily Snapshot", "Completed", "2h ago", Dot.Green),
            new ActivityItem("Manual Sync", "Finished successful", "Yesterday", Dot.Blue),
            new ActivityItem("Backup Validation", "No issues found", "2 days ago", Dot.Purple)
        };

        // Demo data (replace with real)
        private readonly string[] _days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        private readonly double[] _snapshots = { 8, 6, 12, 7, 1, 16, 12 };

        public DashboardViewModel()
        {
            // Colors for the line chart
            var accent = SKColor.Parse("#22CCFF");
            var accentFillTop = new SKColor(0x22, 0xCC, 0xFF, 64);
            var accentFillBot = new SKColor(0x22, 0xCC, 0xFF, 12);
            var grid = new SKColor(255, 255, 255, 28);
            var text = new SKColor(255, 255, 255, 170);
            var avgStroke = new SKColor(255, 255, 255, 110);

            // Main smoothed area line
            var line = new LineSeries<double>
            {
                Values = _snapshots,
                LineSmoothness = 1,
                Stroke = new SolidColorPaint(accent) { StrokeThickness = 3 },
                GeometrySize = 8,
                GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 2 },
                Fill = new LinearGradientPaint(new[] { accentFillTop, accentFillBot },
                                               new SKPoint(0, 0), new SKPoint(0, 1))
            };

            // Moving average (thin solid)
            var avgValues = MovingAverage(_snapshots, 3);
            var avg = new LineSeries<double>
            {
                Values = avgValues,
                LineSmoothness = 1,
                GeometrySize = 0,
                Fill = null,
                Stroke = new SolidColorPaint(avgStroke) { StrokeThickness = 2 }
            };

            SnapshotSeries = new ISeries[] { avg, line };

            SnapshotXAxes = new[]
            {
                new Axis
                {
                    Labels = _days,
                    TextSize = 12,
                    LabelsPaint = new SolidColorPaint(text),
                    SeparatorsPaint = new SolidColorPaint(grid) { StrokeThickness = 1 },
                    TicksPaint = null,
                    Padding = new LiveChartsCore.Drawing.Padding(8, 0, 8, 0)
                }
            };

            SnapshotYAxes = new[]
            {
                new Axis
                {
                    MinLimit = 0,
                    MinStep = 2,
                    TextSize = 12,
                    LabelsPaint = new SolidColorPaint(text),
                    SeparatorsPaint = new SolidColorPaint(grid) { StrokeThickness = 1 },
                    TicksPaint = null,
                    Padding = new LiveChartsCore.Drawing.Padding(0, 8, 0, 8)
                }
            };

           // Donut slice colors
var donutBlue  = SKColor.Parse("#4C8DFF");
var donutRed   = SKColor.Parse("#FF6B6B");
var donutGreen = SKColor.Parse("#6EE7B7");
var donutLilac = SKColor.Parse("#C4B5FD");

// Donut pie: hole via InnerRadius
StorageSeries = new ISeries[]
{
    new PieSeries<double>
    {
        Values      = new double[] { 50 },   // Projects 50%
        Name        = "Projects",
        InnerRadius = 90,                    // <<< makes it a donut
        Stroke      = null,
        Fill        = new SolidColorPaint(donutBlue)
    },
    new PieSeries<double>
    {
        Values      = new double[] { 25 },   // Snapshots 25%
        Name        = "Snapshots",
        InnerRadius = 90,
        Stroke      = null,
        Fill        = new SolidColorPaint(donutRed)
    },
    new PieSeries<double>
    {
        Values      = new double[] { 16 },   // Cache 16%
        Name        = "Cache",
        InnerRadius = 90,
        Stroke      = null,
        Fill        = new SolidColorPaint(donutGreen)
    },
    new PieSeries<double>
    {
        Values      = new double[] { 9 },    // Other 9%
        Name        = "Other",
        InnerRadius = 90,
        Stroke      = null,
        Fill        = new SolidColorPaint(donutLilac)
    }
};
            // Custom legend (Avalonia brushes for little dots/text)
            StorageLegend = new []
            {
                new LegendItem("Projects 50%",  new SolidColorBrush(Color.Parse("#5B8CFF"))),
                new LegendItem("Snapshots 25%", new SolidColorBrush(Color.Parse("#FF7B7B"))),
                new LegendItem("Cache 16%",     new SolidColorBrush(Color.Parse("#9AE6B4"))),
                new LegendItem("Other 9%",      new SolidColorBrush(Color.Parse("#C3B9FF")))
            };
        }

        private static double[] MovingAverage(IReadOnlyList<double> v, int window)
        {
            if (window <= 1) return v.ToArray();
            var r = new double[v.Count];
            for (var i = 0; i < v.Count; i++)
            {
                var start = Math.Max(0, i - (window - 1));
                var count = i - start + 1;
                double sum = 0;
                for (var j = start; j <= i; j++) sum += v[j];
                r[i] = sum / count;
            }
            return r;
        }

        // Bindables
        public record LegendItem(string Label, Brush Brush);

        public enum Dot { Green, Blue, Purple }

        public class ActivityItem
        {
            public ActivityItem(string title, string subtitle, string when, Dot dot)
            {
                Title = title; Subtitle = subtitle; When = when;
DotBrush = dot switch
{
    Dot.Green  => new SolidColorBrush(Color.Parse("#2ECC71")),
    Dot.Blue   => new SolidColorBrush(Color.Parse("#1ABCFE")),
    Dot.Purple => new SolidColorBrush(Color.Parse("#8E77FF")),
    _ => new SolidColorBrush(Colors.Gray)   // <-- was Brushes.Gray
};
            }
            public string Title { get; }
            public string Subtitle { get; }
            public string When { get; }
            public Brush DotBrush { get; }
        }
    }
}