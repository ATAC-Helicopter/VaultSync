using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupDiskSpaceTests
{
    [Theory]
    [InlineData(1_000, 250, 25d)]
    [InlineData(1_000, 0, 0d)]
    [InlineData(1_000, 1_000, 100d)]
    public void TryCalculateFreeSpacePercent_ValidCapacity_ReturnsBoundedPercentage(
        long totalBytes,
        long freeBytes,
        double expected)
    {
        bool result = BackupService.TryCalculateFreeSpacePercent(
            totalBytes,
            freeBytes,
            out double percentage);

        Assert.True(result);
        Assert.Equal(expected, percentage);
        Assert.InRange(percentage, 0d, 100d);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1_000, -1)]
    [InlineData(1_000, 1_001)]
    public void TryCalculateFreeSpacePercent_InvalidCapacity_IsRejected(
        long totalBytes,
        long freeBytes)
    {
        bool result = BackupService.TryCalculateFreeSpacePercent(
            totalBytes,
            freeBytes,
            out double percentage);

        Assert.False(result);
        Assert.Equal(0d, percentage);
    }
}
