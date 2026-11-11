using System;
using Avalonia.Media;
using VaultSync.UI.Controls;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class StorageChartTests
{
    [Fact]
    public void DonutUsesByteProportionsAndPreservesLegendOrderAndColors()
    {
        var slices = StorageChart.Layout([
            new(100, Brushes.Red), new(300, Brushes.Blue)], donut: true);

        Assert.Equal(2, slices.Count);
        Assert.Equal(0, slices[0].Start);
        Assert.Equal(0.25, slices[0].End);
        Assert.Equal(0.25, slices[1].Start);
        Assert.Equal(1, slices[1].End);
        Assert.Same(Brushes.Red, slices[0].Brush);
        Assert.Same(Brushes.Blue, slices[1].Brush);
    }

    [Fact]
    public void DiskBarLeavesFreeSpaceUnfilled()
    {
        var slices = StorageChart.Layout([
            new(20, Brushes.Red), new(30, Brushes.Blue)], donut: false);

        Assert.Equal(0.2, slices[0].End);
        Assert.Equal(0.2, slices[1].Start);
        Assert.Equal(0.5, slices[1].End);
    }

    [Fact]
    public void DiskBarClipsOvercommittedUsageWithoutDrawingOutsideBounds()
    {
        var slices = StorageChart.Layout([
            new(80, Brushes.Red), new(50, Brushes.Blue), new(10, Brushes.Green)], donut: false);

        Assert.Equal(2, slices.Count);
        Assert.Equal(0.8, slices[1].Start);
        Assert.Equal(1, slices[1].End);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyAndInvalidValuesProduceNoSlices(bool donut)
    {
        Assert.Empty(StorageChart.Layout([], donut));
        Assert.Empty(StorageChart.Layout([
            new(0, Brushes.Red), new(-1, Brushes.Red), new(double.NaN, Brushes.Red),
            new(double.PositiveInfinity, Brushes.Red)], donut));
    }

    [Fact]
    public void DonutNormalizesLargeValuesWithoutOverflowAndKeepsOneProjectAsFullRing()
    {
        var slices = StorageChart.Layout([
            new(double.MaxValue, Brushes.Red), new(double.MaxValue, Brushes.Blue)], donut: true);
        Assert.Equal(0.5, slices[0].End);
        Assert.Equal(1, slices[1].End);

        var single = Assert.Single(StorageChart.Layout([new(123, Brushes.Red)], donut: true));
        Assert.Equal(0, single.Start);
        Assert.Equal(1, single.End);
    }
}
