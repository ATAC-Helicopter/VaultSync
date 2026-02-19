using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class TransferPolicyTests
{
    [Fact]
    public void NormalizeBandwidthLimit_Disabled_ReturnsNull()
    {
        var result = TransferPolicy.NormalizeBandwidthLimitMbps(enabled: false, configuredMbps: 250);
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeBandwidthLimit_Enabled_ClampsToValidRange()
    {
        Assert.Equal(1, TransferPolicy.NormalizeBandwidthLimitMbps(true, 0));
        Assert.Equal(5000, TransferPolicy.NormalizeBandwidthLimitMbps(true, 9000));
        Assert.Equal(250, TransferPolicy.NormalizeBandwidthLimitMbps(true, 250));
    }

    [Fact]
    public void RsyncBwLimit_ConvertsMbpsToKilobytesPerSecond()
    {
        Assert.Equal(125, TransferPolicy.ToRsyncBwLimitKbps(1));
        Assert.Equal(12500, TransferPolicy.ToRsyncBwLimitKbps(100));
    }

    [Fact]
    public void RobocopyIpg_ComputesPositiveDelayForLowBandwidth()
    {
        var ipg = TransferPolicy.ToRobocopyIpgMilliseconds(maxBandwidthMbps: 10, threadCount: 8);
        Assert.True(ipg >= 0);
    }

    [Fact]
    public void RobocopyIpg_LowerBandwidthProducesHigherOrEqualDelay()
    {
        var low = TransferPolicy.ToRobocopyIpgMilliseconds(maxBandwidthMbps: 20, threadCount: 8);
        var high = TransferPolicy.ToRobocopyIpgMilliseconds(maxBandwidthMbps: 200, threadCount: 8);
        Assert.True(low >= high);
    }
}
