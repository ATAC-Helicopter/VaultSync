using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DestinationIdentityServiceTests
{
    [Fact]
    public void GetId_IsStableAcrossAliasChanges()
    {
        var first = new BackupDestination
        {
            Path = @"\\nas\share\vaultsync",
            Alias = "Home NAS",
            CredentialName = "vault",
            PreMounted = false
        };

        var second = new BackupDestination
        {
            Path = @"\\nas\share\vaultsync",
            Alias = "Renamed NAS",
            CredentialName = "vault",
            PreMounted = false
        };

        Assert.Equal(DestinationIdentityService.GetId(first), DestinationIdentityService.GetId(second));
    }

    [Fact]
    public void NormalizePreferredDestinationId_MapsLegacyAliasToStableId()
    {
        var destination = new BackupDestination
        {
            Path = @"D:\VaultSyncBackups",
            Alias = "USB",
            CredentialName = string.Empty
        };

        var normalized = DestinationIdentityService.NormalizePreferredDestinationId("USB", new[] { destination });

        Assert.Equal(DestinationIdentityService.GetId(destination), normalized);
    }

    [Fact]
    public void NormalizePreferredDestinationId_MapsLegacyPathToStableId()
    {
        var destination = new BackupDestination
        {
            Path = @"D:\VaultSyncBackups",
            Alias = "USB"
        };

        var normalized = DestinationIdentityService.NormalizePreferredDestinationId(@"D:\VaultSyncBackups", new[] { destination });

        Assert.Equal(DestinationIdentityService.GetId(destination), normalized);
    }

    [Fact]
    public void NormalizePreferredDestinationId_PreservesSpecialAllValue()
    {
        var normalized = DestinationIdentityService.NormalizePreferredDestinationId(Project.DestinationAllId, []);

        Assert.Equal(Project.DestinationAllId, normalized);
    }
}
