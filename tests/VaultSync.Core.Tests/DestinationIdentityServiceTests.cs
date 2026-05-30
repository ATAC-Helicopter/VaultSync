using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DestinationIdentityServiceTests
{
    [Fact]
    public void GetId_IsStableAcrossAliasChanges()
    {
        var first = new BackupDestinationBuilder()
            .WithPath(@"\\nas\share\vaultsync")
            .WithAlias("Home NAS")
            .WithCredentialName("vault")
            .Build();

        var second = new BackupDestinationBuilder()
            .WithPath(@"\\nas\share\vaultsync")
            .WithAlias("Renamed NAS")
            .WithCredentialName("vault")
            .Build();

        Assert.Equal(DestinationIdentityService.GetId(first), DestinationIdentityService.GetId(second));
    }

    [Fact]
    public void NormalizePreferredDestinationId_MapsLegacyAliasToStableId()
    {
        var destination = new BackupDestinationBuilder()
            .WithPath(@"D:\VaultSyncBackups")
            .WithAlias("USB")
            .Build();

        string normalized = DestinationIdentityService.NormalizePreferredDestinationId("USB", new[] { destination });

        Assert.Equal(DestinationIdentityService.GetId(destination), normalized);
    }

    [Fact]
    public void NormalizePreferredDestinationId_MapsLegacyPathToStableId()
    {
        var destination = new BackupDestinationBuilder()
            .WithPath(@"D:\VaultSyncBackups")
            .WithAlias("USB")
            .Build();

        string normalized = DestinationIdentityService.NormalizePreferredDestinationId(@"D:\VaultSyncBackups", new[] { destination });

        Assert.Equal(DestinationIdentityService.GetId(destination), normalized);
    }

    [Fact]
    public void NormalizePreferredDestinationId_PreservesSpecialAllValue()
    {
        string normalized = DestinationIdentityService.NormalizePreferredDestinationId(Project.DestinationAllId, []);

        Assert.Equal(Project.DestinationAllId, normalized);
    }
}
