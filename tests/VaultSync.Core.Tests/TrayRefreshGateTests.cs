using VaultSync.UI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class TrayRefreshGateTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ReleaseTrayMenuRefreshGate_ReleasesOwnershipAndConsumesQueue(
        int queuedValue,
        bool expectedQueued)
    {
        int inFlight = 1;
        int queued = queuedValue;

        bool refreshQueued = App.ReleaseTrayMenuRefreshGate(ref inFlight, ref queued);

        Assert.Equal(expectedQueued, refreshQueued);
        Assert.Equal(0, inFlight);
        Assert.Equal(0, queued);
    }
}
