using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class NetworkMountCleanupTests
{
    [Theory]
    [InlineData("/Volumes/Backup/Dev", "/Volumes/Backup")]
    [InlineData("/Volumes/Backup", "/Volumes/Backup")]
    [InlineData("/Volumes/Backup/Dev/../Other", "/Volumes/Backup")]
    [InlineData("/Volumes/BackupElsewhere/Dev", null)]
    [InlineData("/Volumes/Backup/../Other", null)]
    public void NestedDestinationResolvesOnlyToItsActualMountRoot(string destination, string expected)
    {
        Assert.Equal(expected, NetworkMountService.ResolveMacSmbMountRoot(destination, ["/Volumes/Backup"]));
    }

    [Fact]
    public void NestedMountUsesTheMostSpecificRoot()
    {
        Assert.Equal("/Volumes/Backup/Nested", NetworkMountService.ResolveMacSmbMountRoot(
            "/Volumes/Backup/Nested/Dev", ["/Volumes/Backup", "/Volumes/Backup/Nested"]));
    }
}
