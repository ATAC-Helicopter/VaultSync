using VaultSync.CLI.Commands;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DestinationCommandTests
{
    [Theory]
    [InlineData(true, null, "Reachable")]
    [InlineData(false, "", "Unreachable")]
    [InlineData(true, "Mounted primary", "Mounted primary")]
    [InlineData(false, "Mount failed", "Mount failed")]
    public void ResolveDestinationMessage_PreservesDetailsOrProvidesFallback(
        bool reachable,
        string message,
        string expected)
    {
        Assert.Equal(expected, DestinationCommand.ResolveDestinationMessage(reachable, message));
    }

    [Theory]
    [InlineData(false, true, "Configured")]
    [InlineData(true, true, "Reachable")]
    [InlineData(true, false, "Mount failed")]
    public void ResolveTableDetail_UsesReachabilityOnlyForTestedRows(
        bool test,
        bool reachable,
        string expected)
    {
        var row = new DestinationInfo("Primary", "/backup", "Active", reachable, reachable ? "Configured" : "Mount failed");

        Assert.Equal(expected, DestinationCommand.ResolveTableDetail(row, test));
    }
}
