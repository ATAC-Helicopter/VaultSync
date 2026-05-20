using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class UiFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1{0}5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1099511627776, "1 TB")]
    public void FormatBytes_UsesSharedBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(WithDecimalSeparator(expected), UiFormat.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1536, "+1{0}5 KB")]
    [InlineData(-1536, "-1{0}5 KB")]
    [InlineData(0, "0 B")]
    public void FormatSignedBytes_AddsSignOnlyWhenNeeded(long bytes, string expected)
    {
        Assert.Equal(WithDecimalSeparator(expected), UiFormat.FormatSignedBytes(bytes));
    }

    [Fact]
    public void ExistingSnapshotFormatters_PreservePrecision()
    {
        string expected = WithDecimalSeparator("1{0}5 KB");
        Assert.Equal(expected, BackupSnapshotItem.FormatSize(1536));
        Assert.Equal(expected, ProjectSnapshotViewModel.FormatSize(1536));
    }

    [Fact]
    public void ProjectLatestSnapshotSizeDisplay_DoesNotPretendUnknownSizeIsZeroMegabytes()
    {
        var project = new ProjectItemViewModel
        {
            IsRegistered = true,
            LastSnapshot = new System.DateTime(2026, 5, 20, 10, 0, 0, System.DateTimeKind.Utc),
            SizeBytes = 0
        };

        Assert.Equal("Size unavailable", project.LatestSnapshotSizeDisplay);
    }

    private static string WithDecimalSeparator(string value) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            value,
            System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
}
